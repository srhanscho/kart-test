using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

public enum RacePhase { Lobby, Countdown, Racing, Results }

/// <summary>
/// Couch-race flow: lobby (phones + optional keyboard player, character picks),
/// countdown, race with split-screen cameras and CPU fill-ins, results.
/// Owns the phone controller server.
/// </summary>
public class RaceManager : MonoBehaviour
{
    public const int MaxHumans = 4;

    public static readonly Color[] PlayerColors =
    {
        new Color(0.95f, 0.30f, 0.30f), new Color(0.30f, 0.60f, 1.00f),
        new Color(0.35f, 0.85f, 0.40f), new Color(1.00f, 0.80f, 0.20f)
    };

    [SerializeField] KartController[] karts;
    [SerializeField] Transform[] gridSlots;
    [SerializeField] Camera[] playerCameras;
    [SerializeField] Camera overviewCamera;
    [SerializeField] Transform audioListener;
    [SerializeField] RaceTrack track;
    [SerializeField] CharacterRoster roster;
    [SerializeField] KeyboardKartInput keyboardInput;
    [SerializeField] int preferredPort = 8080;
    [SerializeField] int laps = 3;
    [SerializeField] float countdownSeconds = 3f;
    [SerializeField] float resultsDelay = 3f;
    [SerializeField] float lobbyDisconnectGrace = 8f;
    [SerializeField, Range(0f, 0.2f)] float rubberBandStrength = 0.08f;

    public class Player
    {
        public int Slot;
        public string Id;
        public bool IsKeyboard;
        public int ConnectionId = -1;
        public float LostAt;
        public int Character;
        public bool Ready;
        public readonly NetworkKartInput Input = new NetworkKartInput();
        public string LastItemMessage;
        public int LastHitCount;
        public bool IsTestBot;
        public bool Connected => IsKeyboard || IsTestBot || ConnectionId >= 0;
        public string Label => $"P{Slot + 1}";
        public Color Color => PlayerColors[Slot];
    }

    public class Racer
    {
        public int KartIndex;
        public KartController Kart;
        public LapTracker Lap;
        public KartItems Items;
        public Player Human;
        public AiKartInput Ai;
        public int Character;
        public float Skill = 1f;
        public float Progress;
        public int Position;
        public int FinishOrder = -1;
        public string Name;
    }

    readonly List<Player> players = new List<Player>();
    readonly Dictionary<int, Player> byConnection = new Dictionary<int, Player>();
    readonly Dictionary<int, string> waitingConnections = new Dictionary<int, string>(); // joined mid-race
    readonly List<Racer> racers = new List<Racer>();
    readonly List<Racer> standings = new List<Racer>();
    readonly List<Racer> humanRacers = new List<Racer>();
    PhoneControllerServer server;
    System.Random rng;
    float phaseTime, raceStartTime, resultsAt = -1f, nextPhoneHud;
    int lastCountValue, finishCount;

    public RacePhase Phase { get; private set; } = RacePhase.Lobby;
    public IReadOnlyList<Player> Players => players;
    public IReadOnlyList<Racer> Standings => standings;
    public IReadOnlyList<Racer> HumanRacers => humanRacers;
    public CharacterRoster Roster => roster;
    public LobbyPreview Preview { get; private set; }
    public string Url { get; private set; }
    public List<string> OtherUrls { get; } = new List<string>();
    public Texture2D QrTexture { get; private set; }
    public string ServerError { get; private set; }
    public bool ServerRunning => server != null && server.IsRunning;
    public int ServerPort => server?.Port ?? 0;
    public int Laps => laps;
    public int KartCount => karts.Length;
    public float CountdownRemaining => Phase == RacePhase.Countdown ? Mathf.Max(0f, raceStartTime - Time.time) : 0f;
    public float TimeSinceStart => Phase == RacePhase.Racing || Phase == RacePhase.Results ? Time.time - raceStartTime : 0f;
    public Camera PlayerCamera(int index) => playerCameras[index];
    public float PhaseStartTime => phaseTime;
    public static RaceManager Instance { get; private set; }
    public RaceTrack Track => track;
    /// <summary>Number of split-screen viewports currently shown.</summary>
    public int ViewportCount { get; private set; }
    public PodiumStage Podium { get; private set; }

    /// <summary>Test/screenshot hook: show only the first n human viewports.</summary>
    public void DebugSetViewports(int n) => SetupCameras(Mathf.Clamp(n, 0, humanRacers.Count));
    public IReadOnlyList<KartController> AllKarts => karts;

    /// <summary>Current race position (1 = leader) of a kart; 1 outside a race.</summary>
    public int PositionOf(KartController kart)
    {
        foreach (var r in racers) if (r.Kart == kart) return Mathf.Max(1, r.Position);
        return 1;
    }

    public void Configure(KartController[] kartList, Transform[] grid, Camera[] cameras, Camera overview,
        Transform listener, RaceTrack raceTrack, CharacterRoster characterRoster, KeyboardKartInput keyboard, int lapCount)
    {
        karts = kartList;
        gridSlots = grid;
        playerCameras = cameras;
        overviewCamera = overview;
        audioListener = listener;
        track = raceTrack;
        roster = characterRoster;
        keyboardInput = keyboard;
        laps = lapCount;
    }

    // ============================================================================================
    // Lifecycle
    // ============================================================================================

    void OnEnable() => StartServer();
    void OnDisable() => StopServer();
    void OnApplicationQuit() => StopServer();

    void StartServer()
    {
        if (server != null) return;
        server = new PhoneControllerServer(ControllerPage.Html);
        if (server.Start(preferredPort))
        {
            List<string> ips = LanAddress.GetCandidates();
            Url = $"http://{ips[0]}:{server.Port}/";
            OtherUrls.Clear();
            for (int i = 1; i < ips.Count; i++) OtherUrls.Add($"http://{ips[i]}:{server.Port}/");
            bool[,] modules = QrCode.Encode(Url);
            QrTexture = modules != null ? QrCode.ToTexture(modules) : null;
            ServerError = null;
            Debug.Log($"[RaceManager] Phone server listening on 0.0.0.0:{server.Port} -> {Url}");
        }
        else
        {
            ServerError = server.LastError;
            Debug.LogWarning("[RaceManager] Phone server failed to start: " + ServerError);
        }
    }

    void StopServer()
    {
        if (server == null) return;
        server.Stop();
        server = null;
        byConnection.Clear();
        waitingConnections.Clear();
        foreach (var p in players) p.ConnectionId = -1;
        Debug.Log("[RaceManager] Phone server stopped.");
    }

    void Awake()
    {
        Instance = this;
        rng = new System.Random();
        foreach (var kart in karts)
        {
            var lap = kart.GetComponent<LapTracker>();
            lap.Configure(lap.CheckpointCount, laps);
            lap.RaceFinished += OnRacerFinished;
            lap.LapStarted += OnLapStarted;
        }
    }

    void Start()
    {
        Preview = new GameObject("LobbyPreview").AddComponent<LobbyPreview>();
        Preview.transform.SetParent(transform, false);
        Preview.Init(roster, MaxHumans);
        Podium = new GameObject("PodiumStage").AddComponent<PodiumStage>();
        Podium.transform.SetParent(transform, false);
        Podium.Init();
        if (server != null) StartCoroutine(Preview.RenderThumbnails((i, png) => server?.SetFile($"/char/{i}.png", "image/png", png)));
        EnterLobby();
    }

    void Update()
    {
        PumpServer();
        HandleKeyboard();

        switch (Phase)
        {
            case RacePhase.Lobby:
                DropLostPlayers();
                if (players.Count > 0 && players.All(p => p.Ready)) StartCountdown();
                break;
            case RacePhase.Countdown:
                TickCountdown();
                break;
            case RacePhase.Racing:
                if (resultsAt > 0f && Time.time >= resultsAt) EnterResults();
                break;
            case RacePhase.Results:
                DropLostPlayers();
                break;
        }

        if (Phase == RacePhase.Racing || Phase == RacePhase.Results)
            foreach (var r in racers) RescueService.Instance?.Watch(r.Kart, track, false);

        if (Phase != RacePhase.Lobby)
        {
            TickAi();
            UpdateStandings();
            SendPhoneHud();
            SendPhoneItems();
        }
    }

    // ============================================================================================
    // Phases
    // ============================================================================================

    public void EnterLobby()
    {
        Phase = RacePhase.Lobby;
        phaseTime = Time.time;
        resultsAt = -1f;
        humanRacers.Clear();
        standings.Clear();
        foreach (var p in players) p.Ready = false;

        for (int k = 0; k < karts.Length; k++)
        {
            KartController kart = karts[k];
            kart.SetInput(null);
            kart.SetControlsLocked(true);
            kart.SpeedMultiplier = 1f;
            kart.GetComponent<LapTracker>().ResetForRace();
            kart.Teleport(gridSlots[k].position, gridSlots[k].rotation);
        }

        ResetItems();
        SetupCameras(0);
        Preview?.SetActive(true);
        Podium?.Hide();
        GameAudio.Music(MusicState.Lobby);
        Broadcast("phase|lobby");
        foreach (var p in players) SendPick(p);
        foreach (var w in waitingConnections.ToList()) Hello(w.Key, w.Value);
    }

    /// <summary>Starts the countdown with the current players (host / keyboard "force start").</summary>
    public void StartCountdown()
    {
        if (Phase != RacePhase.Lobby) return;
        if (players.Count == 0) AddKeyboardPlayer();

        racers.Clear();
        humanRacers.Clear();
        finishCount = 0;
        resultsAt = -1f;

        // Humans take karts 0..n-1 (camera i follows kart i); CPUs take unpicked characters.
        List<Player> humans = players.OrderBy(p => p.Slot).ToList();
        var freeCharacters = Enumerable.Range(0, roster.Count).Where(c => humans.All(h => h.Character != c))
            .OrderBy(_ => rng.Next()).ToList();

        for (int k = 0; k < karts.Length; k++)
        {
            var r = new Racer { KartIndex = k, Kart = karts[k], Lap = karts[k].GetComponent<LapTracker>(), Items = karts[k].GetComponent<KartItems>() };
            if (k < humans.Count)
            {
                r.Human = humans[k];
                r.Character = humans[k].Character;
                r.Name = $"{humans[k].Label} {roster[r.Character].displayName}";
                IKartInput input = humans[k].IsKeyboard ? keyboardInput : (IKartInput)humans[k].Input;
                r.Kart.SetInput(input);
                humanRacers.Add(r);
            }
            else
            {
                r.Character = freeCharacters[(k - humans.Count) % freeCharacters.Count];
                r.Name = "CPU " + roster[r.Character].displayName;
                r.Skill = Mathf.Lerp(0.93f, 0.99f, (float)rng.NextDouble());
                r.Ai = new AiKartInput(r.Kart, track, rng, r.Items);
                r.Kart.SetInput(r.Ai);
            }
            if (r.Items != null) r.Items.IsAi = r.Human == null;
            roster.ApplyTo(r.Kart, r.Character);
            r.Kart.SpeedMultiplier = 1f;
            r.Kart.SetControlsLocked(true);
            r.Lap.ResetForRace();
            racers.Add(r);
        }

        ResetItems();
        GameAudio.Music(MusicState.None);
        GameAudio.Play(Sfx.UiSelect);
        foreach (var r in racers)
        {
            var audio = r.Kart.GetComponent<KartAudio>();
            if (audio != null) audio.Human = r.Human != null;
            var beam = r.Kart.transform.Find("HeadlightBeam");
            if (beam != null) beam.GetComponent<Light>().enabled = r.Human != null; // real lights only for humans
        }

        // Grid: CPUs on the front rows, humans behind them.
        var gridOrder = racers.Where(r => r.Human == null).Concat(racers.Where(r => r.Human != null)).ToList();
        for (int i = 0; i < gridOrder.Count; i++)
            gridOrder[i].Kart.Teleport(gridSlots[i].position, gridSlots[i].rotation);

        SetupCameras(humanRacers.Count);
        Preview?.SetActive(false);

        Phase = RacePhase.Countdown;
        phaseTime = Time.time;
        raceStartTime = Time.time + countdownSeconds;
        lastCountValue = -1;
        Broadcast("phase|countdown");
        UpdateStandings();
    }

    void TickCountdown()
    {
        float remaining = raceStartTime - Time.time;
        int value = Mathf.CeilToInt(remaining);
        if (value != lastCountValue && value > 0)
        {
            lastCountValue = value;
            Broadcast("count|" + value);
            GameAudio.Play(Sfx.CountBeep); // same frame the UI number changes
        }
        if (remaining > 0f) return;
        GameAudio.Play(Sfx.CountGo);
        GameAudio.Music(MusicState.Race);

        Phase = RacePhase.Racing;
        phaseTime = Time.time;
        foreach (var r in racers)
        {
            r.Kart.SetControlsLocked(false);
            r.Lap.BeginRace(raceStartTime);
        }
        Broadcast("count|GO!");
        Broadcast("phase|race");
    }

    void OnRacerFinished(LapTracker lap)
    {
        Racer r = racers.FirstOrDefault(x => x.Lap == lap);
        if (r == null) return;
        r.FinishOrder = finishCount++;
        if (r.Human != null)
        {
            // Autopilot after the line, like the real thing.
            r.Ai = new AiKartInput(r.Kart, track, rng, r.Items);
            r.Kart.SetInput(r.Ai);
            if (!r.Human.IsKeyboard) server?.Send(r.Human.ConnectionId, $"result|You finished {Ordinal(r.FinishOrder + 1)}!");
            GameAudio.Play(r.FinishOrder == 0 ? Sfx.Finish1st : r.FinishOrder < 3 ? Sfx.FinishPodium : Sfx.FinishOther);
        }
        bool humansDone = humanRacers.All(h => h.Lap.Finished);
        bool allDone = racers.All(x => x.Lap.Finished);
        if ((humansDone || allDone) && resultsAt < 0f) resultsAt = Time.time + resultsDelay;
    }

    /// <summary>Lap sounds for humans; the music speeds up when any human starts the final lap.</summary>
    void OnLapStarted(LapTracker lap, int number)
    {
        Racer r = racers.FirstOrDefault(x => x.Lap == lap);
        if (r == null || r.Human == null || number < 2) return;
        if (number == laps)
        {
            GameAudio.Play(Sfx.FinalLap);
            GameAudio.Music(MusicState.FinalLap);
        }
        else GameAudio.Play(Sfx.LapComplete);
    }

    void EnterResults()
    {
        Phase = RacePhase.Results;
        phaseTime = Time.time;
        UpdateStandings();
        GameAudio.Music(MusicState.Lobby);
        Podium?.Show(roster, standings.Take(3).Select(x => x.Character).ToList());
        foreach (var h in humanRacers)
            if (h.Human != null && !h.Human.IsKeyboard)
                server?.Send(h.Human.ConnectionId, $"result|You finished {Ordinal(h.Position)}!");
        Broadcast("phase|results");
    }

    // ============================================================================================
    // Per-frame race logic
    // ============================================================================================

    void TickAi()
    {
        float bestHuman = float.MinValue;
        foreach (var h in humanRacers)
            if (!h.Lap.Finished) bestHuman = Mathf.Max(bestHuman, h.Progress);

        foreach (var r in racers)
        {
            if (r.Ai == null) continue;
            bool active = Phase == RacePhase.Racing || Phase == RacePhase.Results;
            r.Ai.Tick(Time.time, Time.deltaTime, active);

            float rubber = 1f;
            if (r.Human == null && bestHuman > float.MinValue && !r.Lap.Finished)
            {
                float gap = r.Progress - bestHuman; // metres ahead of the best human
                rubber = 1f - Mathf.Clamp(gap / 120f, -1f, 1f) * rubberBandStrength;
            }
            r.Kart.SpeedMultiplier = (r.Human == null ? r.Skill : 1f) * rubber;

            if (active && r.Ai.NeedsRespawn)
            {
                if (RescueService.Instance != null) RescueService.Instance.RescueNow(r.Kart, track);
                else Respawn(r);
                r.Ai.ClearStuck();
            }
        }
    }

    void Respawn(Racer r)
    {
        float s = track.Project(r.Kart.transform.position);
        Vector3 pos = track.PointAt(s) + Vector3.up * 0.5f;
        r.Kart.Teleport(pos, Quaternion.LookRotation(track.TangentAt(s)));
        r.Ai.ClearStuck();
    }

    /// <summary>Ranking: finished karts by finish order, then lap + distance along the lap.</summary>
    void UpdateStandings()
    {
        float length = track.Length;
        foreach (var r in racers)
        {
            if (r.FinishOrder >= 0)
            {
                r.Progress = 1e6f - r.FinishOrder;
                continue;
            }
            float s = track.Project(r.Kart.transform.position);
            int lap = r.Lap.CurrentLap;
            // Keep the geometric distance consistent with the trigger-based lap counter near the line.
            if (r.Lap.NextCheckpoint == 0 && s > length * 0.75f) s -= length;
            if (r.Lap.NextCheckpoint >= r.Lap.CheckpointCount && s < length * 0.25f) s += length;
            r.Progress = (lap - 1) * length + s;
        }
        standings.Clear();
        standings.AddRange(racers.OrderByDescending(r => r.Progress));
        for (int i = 0; i < standings.Count; i++) standings[i].Position = i + 1;
    }

    void SendPhoneHud()
    {
        if (server == null || Time.time < nextPhoneHud) return;
        nextPhoneHud = Time.time + 0.25f;
        foreach (var h in humanRacers)
        {
            if (h.Human == null || h.Human.IsKeyboard || h.Human.ConnectionId < 0) continue;
            server.Send(h.Human.ConnectionId, $"hud|{Ordinal(h.Position)}/{racers.Count}|{h.Lap.DisplayLap}/{laps}");
        }
    }

    /// <summary>Held-item state to each phone, only when it changes.</summary>
    void SendPhoneItems()
    {
        if (server == null) return;
        foreach (var h in humanRacers)
        {
            if (h.Human == null || h.Human.IsKeyboard || h.Human.ConnectionId < 0 || h.Items == null) continue;
            int hits = h.Items.HitsTaken + h.Items.Crashes;
            if (hits > h.Human.LastHitCount) server.Send(h.Human.ConnectionId, "buzz|250");
            h.Human.LastHitCount = hits;
            string msg = h.Items.IsRolling ? "item|roll|1" : $"item|{h.Items.Held.ToString().ToLowerInvariant()}|0";
            if (msg == h.Human.LastItemMessage) continue;
            h.Human.LastItemMessage = msg;
            server.Send(h.Human.ConnectionId, msg);
        }
    }

    void ResetItems()
    {
        RescueService.Instance?.ResetAll();
        ItemManager.Instance?.ClearAll();
        ItemBox.ResetAll();
        foreach (var kart in karts) kart.GetComponent<KartItems>()?.ResetItems();
        foreach (var p in players)
        {
            p.LastItemMessage = "item|none|0";
            if (!p.IsKeyboard && p.ConnectionId >= 0) server?.Send(p.ConnectionId, p.LastItemMessage);
        }
    }

    void SetupCameras(int humanCount)
    {
        ViewportCount = humanCount;
        for (int i = 0; i < playerCameras.Length; i++)
        {
            bool on = i < humanCount;
            playerCameras[i].gameObject.SetActive(on);
            if (!on) continue;
            playerCameras[i].rect = ViewportRect(i, humanCount);
            playerCameras[i].GetComponent<FollowCamera>().SetTarget(humanRacers[i].Kart.transform);
        }

        // Overview camera: full screen in the lobby, fills the empty quadrant with 3 players.
        bool overview = humanCount == 0 || humanCount == 3;
        overviewCamera.gameObject.SetActive(overview);
        overviewCamera.rect = humanCount == 3 ? new Rect(0.5f, 0f, 0.5f, 0.5f) : new Rect(0f, 0f, 1f, 1f);

        Transform listenerParent = humanCount > 0 ? playerCameras[0].transform : overviewCamera.transform;
        audioListener.SetParent(listenerParent, false);
        audioListener.localPosition = Vector3.zero;
    }

    /// <summary>1 = full, 2 = top/bottom, 3-4 = quadrants.</summary>
    public static Rect ViewportRect(int index, int count)
    {
        if (count <= 1) return new Rect(0f, 0f, 1f, 1f);
        if (count == 2) return index == 0 ? new Rect(0f, 0.5f, 1f, 0.5f) : new Rect(0f, 0f, 1f, 0.5f);
        float x = index % 2 == 0 ? 0f : 0.5f;
        float y = index < 2 ? 0.5f : 0f;
        return new Rect(x, y, 0.5f, 0.5f);
    }

    // ============================================================================================
    // Players, phones and keyboard
    // ============================================================================================

    void PumpServer()
    {
        if (server == null) return;
        while (server.TryDequeue(out var e))
        {
            switch (e.Kind)
            {
                case PhoneControllerServer.EventKind.Connected:
                    break; // wait for "hello|<id>"
                case PhoneControllerServer.EventKind.Disconnected:
                    waitingConnections.Remove(e.ConnectionId);
                    if (byConnection.TryGetValue(e.ConnectionId, out Player lost))
                    {
                        byConnection.Remove(e.ConnectionId);
                        if (lost.ConnectionId == e.ConnectionId)
                        {
                            lost.ConnectionId = -1;
                            lost.LostAt = Time.time;
                            lost.Input.Clear();
                            SendJoinedToAll();
                        }
                    }
                    break;
                case PhoneControllerServer.EventKind.Message:
                    HandleMessage(e.ConnectionId, e.Text);
                    break;
            }
        }
    }

    void HandleMessage(int conn, string text)
    {
        string[] parts = text.Split('|');
        byConnection.TryGetValue(conn, out Player player);

        switch (parts[0])
        {
            case "hello":
                if (parts.Length >= 2) Hello(conn, parts[1]);
                break;
            case "s":
                if (player != null && parts.Length >= 4)
                    player.Input.Apply(ParseFloat(parts[1]), ParseFloat(parts[2]), parts[3] == "1", parts.Length >= 5 && parts[4] == "1");
                break;
            case "pick":
                if (player != null && parts.Length >= 2 && Phase == RacePhase.Lobby && !player.Ready)
                    CyclePick(player, parts[1] == "-1" ? -1 : 1);
                break;
            case "ready":
                if (player != null && parts.Length >= 2 && Phase == RacePhase.Lobby)
                {
                    player.Ready = parts[1] == "1";
                    GameAudio.Play(player.Ready ? Sfx.UiReady : Sfx.UiBack);
                    SendPick(player);
                }
                break;
            case "start":
                if (player != null && IsHost(player))
                {
                    if (Phase == RacePhase.Lobby) StartCountdown();
                    else if (Phase == RacePhase.Results) EnterLobby();
                }
                break;
        }
    }

    void Hello(int conn, string id)
    {
        Player player = players.FirstOrDefault(p => !p.IsKeyboard && p.Id == id);
        if (player == null)
        {
            if (Phase != RacePhase.Lobby && Phase != RacePhase.Results)
            {
                waitingConnections[conn] = id;
                server.Send(conn, "wait");
                server.Send(conn, "phase|" + PhaseName());
                return;
            }
            player = CreatePlayer(id, false);
            if (player == null)
            {
                server.Send(conn, "full");
                return;
            }
        }
        waitingConnections.Remove(conn);
        if (player.ConnectionId >= 0 && player.ConnectionId != conn) byConnection.Remove(player.ConnectionId);
        player.ConnectionId = conn;
        player.LastItemMessage = null;
        byConnection[conn] = player;
        SendJoinedToAll();
        server.Send(conn, "phase|" + PhaseName());
        SendPick(player);
    }

    Player CreatePlayer(string id, bool keyboard)
    {
        if (players.Count >= MaxHumans) return null;
        int slot = Enumerable.Range(0, MaxHumans).First(s => players.All(p => p.Slot != s));
        var player = new Player { Slot = slot, Id = id, IsKeyboard = keyboard, Character = -1 };
        players.Add(player);
        GameAudio.Play(Sfx.UiSelect);
        player.Character = FirstFreeCharacter(slot * 3, player);
        Preview?.SetCharacter(slot, player.Character);
        return player;
    }

    void RemovePlayer(Player p)
    {
        GameAudio.Play(Sfx.UiBack);
        players.Remove(p);
        if (p.ConnectionId >= 0) byConnection.Remove(p.ConnectionId);
        Preview?.SetCharacter(p.Slot, -1);
        SendJoinedToAll();
    }

    /// <summary>Test hook: an extra READY player with an idle (network) input, to fill 3-4 player layouts.</summary>
    public void AddBotPlayerForTest()
    {
        var p = CreatePlayer("testbot-" + players.Count, false);
        if (p == null) return;
        p.IsTestBot = true;
        p.Ready = true;
    }

    /// <summary>Test hook: add the keyboard player (optionally already READY).</summary>
    public void AddKeyboardPlayerForTest(bool ready)
    {
        AddKeyboardPlayer();
        var kb = players.FirstOrDefault(p => p.IsKeyboard);
        if (kb != null) kb.Ready = ready;
    }

    void AddKeyboardPlayer()
    {
        if (players.Any(p => p.IsKeyboard)) return;
        CreatePlayer("keyboard", true);
        SendJoinedToAll();
    }

    void DropLostPlayers()
    {
        for (int i = players.Count - 1; i >= 0; i--)
        {
            Player p = players[i];
            if (!p.Connected && Time.time - p.LostAt > lobbyDisconnectGrace) RemovePlayer(p);
        }
    }

    int FirstFreeCharacter(int start, Player self)
    {
        for (int i = 0; i < roster.Count; i++)
        {
            int c = (start + i) % roster.Count;
            if (!TakenByOther(c, self)) return c;
        }
        return 0;
    }

    bool TakenByOther(int character, Player self) => players.Any(p => p != self && p.Character == character);

    void CyclePick(Player player, int direction)
    {
        GameAudio.Play(Sfx.UiMove);
        for (int step = 1; step < roster.Count; step++)
        {
            int c = ((player.Character + direction * step) % roster.Count + roster.Count) % roster.Count;
            if (TakenByOther(c, player)) continue;
            player.Character = c;
            break;
        }
        Preview?.SetCharacter(player.Slot, player.Character);
        SendPick(player);
    }

    bool IsHost(Player player)
    {
        Player host = players.Where(p => !p.IsKeyboard && p.ConnectionId >= 0).OrderBy(p => p.Slot).FirstOrDefault();
        return host == player;
    }

    void SendJoinedToAll()
    {
        if (server == null) return;
        foreach (var p in players)
        {
            if (p.IsKeyboard || p.ConnectionId < 0) continue;
            string hex = "#" + ColorUtility.ToHtmlStringRGB(p.Color);
            server.Send(p.ConnectionId, $"joined|{p.Slot}|{hex}|{(IsHost(p) ? 1 : 0)}");
        }
    }

    void SendPick(Player p)
    {
        if (server == null || p.IsKeyboard || p.ConnectionId < 0 || p.Character < 0) return;
        CharacterDefinition c = roster[p.Character];
        server.Send(p.ConnectionId, $"pick|{p.Character}|{(p.Ready ? 1 : 0)}|{c.displayName}|" +
                                    $"{CharacterDefinition.Bars(c.speed)}|{CharacterDefinition.Bars(c.acceleration)}|{CharacterDefinition.Bars(c.handling)}");
    }

    void Broadcast(string text) => server?.Broadcast(text);

    void HandleKeyboard()
    {
        bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        bool space = Input.GetKeyDown(KeyCode.Space);
        Player kb = players.FirstOrDefault(p => p.IsKeyboard);

        switch (Phase)
        {
            case RacePhase.Lobby:
                if (enter)
                {
                    if (kb == null) AddKeyboardPlayer();
                    else
                    {
                        kb.Ready = !kb.Ready;
                        GameAudio.Play(kb.Ready ? Sfx.UiReady : Sfx.UiBack);
                    }
                }
                if (kb != null && !kb.Ready)
                {
                    if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) CyclePick(kb, -1);
                    if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) CyclePick(kb, 1);
                }
                if (kb != null && (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))) RemovePlayer(kb);
                if (space) StartCountdown();
                break;
            case RacePhase.Countdown:
            case RacePhase.Racing:
                if (Input.GetKeyDown(KeyCode.Escape)) EnterLobby();
                break;
            case RacePhase.Results:
                if (enter || space) EnterLobby();
                break;
        }
    }

    string PhaseName() => Phase switch
    {
        RacePhase.Lobby => "lobby",
        RacePhase.Countdown => "countdown",
        RacePhase.Racing => "race",
        _ => "results"
    };

    static float ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    public static string Ordinal(int n)
    {
        if (n % 100 >= 11 && n % 100 <= 13) return n + "th";
        switch (n % 10)
        {
            case 1: return n + "st";
            case 2: return n + "nd";
            case 3: return n + "rd";
            default: return n + "th";
        }
    }

    // ============================================================================================
    // Test hooks (used by the editor smoke test)
    // ============================================================================================

    /// <summary>Hands every human kart to the CPU driver (used to finish a race in automated tests).</summary>
    public void SetAutopilotForTest()
    {
        foreach (var r in humanRacers)
        {
            r.Ai = new AiKartInput(r.Kart, track, rng, r.Items);
            r.Kart.SetInput(r.Ai);
        }
    }
}
