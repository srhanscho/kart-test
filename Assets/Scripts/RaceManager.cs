using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

public enum RacePhase { Lobby, Countdown, Racing, Results }

/// <summary>
/// Couch-race flow: lobby (phones + optional keyboard player, character and track picks),
/// countdown, race with split-screen cameras and CPU fill-ins, results, and a pause menu.
/// Owns the phone controller server.
/// The party LEADER is the connected player with the lowest slot (phone or keyboard). Only the
/// leader's phone may pick the track, pause (resume / restart / lobby / quit) and quit from the
/// lobby; the PC keyboard always has these host rights too.
/// </summary>
public class RaceManager : MonoBehaviour
{
    public const int MaxHumans = 4;

    public static readonly Color[] PlayerColors =
    {
        new Color(0.95f, 0.30f, 0.30f), new Color(0.30f, 0.60f, 1.00f),
        new Color(0.35f, 0.85f, 0.40f), new Color(1.00f, 0.80f, 0.20f)
    };

    /// <summary>Pause menu entries (same order on the TV and the leader's phone).</summary>
    /// <summary>Localization keys of the pause menu entries (Loc.T gives the text).</summary>
    public static readonly string[] PauseItems = { "pause.resume", "pause.restart", "pause.lobby", "pause.quit" };
    public const int PauseResume = 0, PauseRestart = 1, PauseLobby = 2, PauseQuit = 3;

    /// <summary>What "Quit game" does. Replaced by tests so the test process keeps running.</summary>
    public static System.Action QuitHandler = DefaultQuit;

    [SerializeField] KartController[] karts;
    [SerializeField] Transform[] gridSlots;
    [SerializeField] Camera[] playerCameras;
    [SerializeField] Camera overviewCamera;
    [SerializeField] Transform audioListener;
    [SerializeField] RaceTrack track;
    [SerializeField] TrackDefinition[] tracks;
    [SerializeField] Light sun;
    [SerializeField] CharacterRoster roster;
    [SerializeField] KeyboardKartInput keyboardInput;
    [SerializeField] int preferredPort = 8080;
    [SerializeField] int laps = 3;
    [SerializeField] float countdownSeconds = 3f;
    [SerializeField] float resultsDelay = 3f;
    [SerializeField] float lobbyDisconnectGrace = 8f;
    [SerializeField, Range(0f, 0.2f)] float rubberBandStrength = 0.08f;
    [SerializeField] float introSeconds = 3.8f;
    [SerializeField] float flyoverSeconds = 4.2f;

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
    public float CountdownRemaining => Phase != RacePhase.Countdown ? 0f : FlyoverActive ? countdownSeconds : Mathf.Max(0f, raceStartTime - Time.time);
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
    /// <summary>Karts taking part (CPU "Off" hides the unused ones during a race).</summary>
    public IReadOnlyList<KartController> AllKarts => activeKarts;
    readonly List<KartController> activeKarts = new List<KartController>();
    /// <summary>Karts in the current race (humans + CPUs); all karts outside a race.</summary>
    public int RacerCount => racers.Count > 0 ? racers.Count : karts.Length;

    // ---- CPU racers option (leader) -----------------------------------------------------------
    /// <summary>Lobby setting: number of CPU karts. -1 = fill the grid to 6 karts.</summary>
    public static readonly int[] CpuOptions = { 0, 2, 4, -1 };
    int cpuOption = 3;
    public int CpuOption => cpuOption;
    public int CpuSetting => CpuOptions[cpuOption];
    public string CpuLabel => CpuSetting < 0 ? Loc.T("cpu.fill") : CpuSetting == 0 ? Loc.T("cpu.off") : CpuSetting.ToString();

    public void CycleCpu(int direction)
    {
        if (Phase != RacePhase.Lobby) return;
        cpuOption = (cpuOption + direction + CpuOptions.Length) % CpuOptions.Length;
        GameAudio.Play(Sfx.UiMove);
        BroadcastCpu();
    }

    /// <summary>Test hook: CPU count (0, 2, 4 or -1 = fill).</summary>
    public void SetCpuForTest(int cpus)
    {
        int i = System.Array.IndexOf(CpuOptions, cpus);
        if (i >= 0 && Phase == RacePhase.Lobby) cpuOption = i;
        BroadcastCpu();
    }

    /// <summary>Game language (PC UI, and phones that have no local override). Saved in PlayerPrefs.</summary>
    public void SetLanguage(Lang lang)
    {
        if (lang == Loc.Current) return;
        Loc.Set(lang);
        GameAudio.Play(Sfx.UiMove);
        Broadcast("lang|" + Loc.Code);
    }

    void BroadcastCpu() => Broadcast($"cpu|{cpuOption}"); // phones show the option in their own language

    int CpuCountFor(int humans)
    {
        int free = karts.Length - humans;
        return CpuSetting < 0 ? free : Mathf.Min(CpuSetting, free);
    }

    /// <summary>Current race position (1 = leader) of a kart; 1 outside a race.</summary>
    public int PositionOf(KartController kart)
    {
        foreach (var r in racers) if (r.Kart == kart) return Mathf.Max(1, r.Position);
        return 1;
    }

    public void Configure(KartController[] kartList, Camera[] cameras, Camera overview, Transform listener,
        CharacterRoster characterRoster, KeyboardKartInput keyboard, TrackDefinition[] trackList, Light sunLight)
    {
        karts = kartList;
        playerCameras = cameras;
        overviewCamera = overview;
        audioListener = listener;
        roster = characterRoster;
        keyboardInput = keyboard;
        tracks = trackList;
        sun = sunLight;
        track = trackList[0].track;
        gridSlots = trackList[0].gridSlots;
        laps = trackList[0].laps;
    }

    // ============================================================================================
    // Lifecycle
    // ============================================================================================

    void OnEnable() => StartServer();
    void OnDisable() => StopServer();

    void OnApplicationQuit()
    {
        StopServer();
        Time.timeScale = 1f;
        AudioListener.pause = false;
    }

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
        Loc.Init();
        rng = new System.Random();
        foreach (var kart in karts)
        {
            var lap = kart.GetComponent<LapTracker>();
            lap.RaceFinished += OnRacerFinished;
            lap.LapStarted += OnLapStarted;
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (Paused)
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
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
        ActivateTrack(0);
        if (server != null) StartCoroutine(Preview.RenderThumbnails((i, png) => server?.SetFile($"/char/{i}.png", "image/png", png)));
        StartCoroutine(RenderTrackThumbnails());
        EnterLobby();
        if (!SkipIntroForTest && introSeconds > 0f) BeginIntro();
    }

    // ============================================================================================
    // Game start intro (once per session) and pre-race track flyover
    // ============================================================================================

    /// <summary>Tests that do not care about the intro set this before entering play mode.</summary>
    public static bool SkipIntroForTest;

    float introStart;
    public bool IntroActive { get; private set; }
    /// <summary>Seconds since the intro started (unscaled).</summary>
    public float IntroTime => Time.unscaledTime - introStart;
    public float IntroSeconds => IntroSecondsForTest > 0f ? IntroSecondsForTest : introSeconds;
    /// <summary>Tests lengthen the intro so a slow machine still sees it before skipping.</summary>
    public static float IntroSecondsForTest = -1f;
    public int IntrosPlayed { get; private set; }

    void BeginIntro()
    {
        IntroActive = true;
        IntrosPlayed++;
        introStart = Time.unscaledTime;
        GameAudio.Music(MusicState.None);
        GameAudio.Play(Sfx.Intro);
        Broadcast("intro|1");
    }

    /// <summary>Ends the intro (timer, any PC key or any button on the leader's phone).</summary>
    public void SkipIntro()
    {
        if (!IntroActive) return;
        IntroActive = false;
        if (ActiveTrack != null) overviewCamera.transform.SetPositionAndRotation(ActiveTrack.overviewPosition, ActiveTrack.overviewRotation);
        GameAudio.Music(MusicState.Lobby);
        Broadcast("intro|0");
    }

    /// <summary>Slow orbit of the lobby track under the logo.</summary>
    void TickIntro()
    {
        if (IntroTime >= IntroSeconds || (!Application.isBatchMode && Input.anyKeyDown))
        {
            SkipIntro();
            return;
        }
        TrackDefinition def = ActiveTrack;
        if (def == null) return;
        Bounds b = def.bounds;
        float span = Mathf.Max(b.size.x, b.size.z);
        float angle = -30f + IntroTime * 9f;
        Vector3 offset = Quaternion.Euler(0f, angle, 0f) * new Vector3(0f, span * 0.42f, -span * 0.62f);
        overviewCamera.transform.SetPositionAndRotation(b.center + offset, Quaternion.LookRotation(b.center - (b.center + offset)));
    }

    float flyoverStart, flyoverEnd;
    readonly List<Vector3> flyPositions = new List<Vector3>();
    readonly List<Quaternion> flyRotations = new List<Quaternion>();
    public bool FlyoverActive { get; private set; }
    public float FlyoverSeconds => flyoverSeconds;
    public int FlyoversPlayed { get; private set; }

    /// <summary>
    /// Camera path: high over the circuit, over its tightest corner and its highest point (jumps,
    /// hills), past the finish gate, then down behind the grid where P1's chase camera takes over.
    /// </summary>
    void BeginFlyover()
    {
        FlyoverActive = true;
        FlyoversPlayed++;
        flyoverStart = Time.time;
        flyoverEnd = Time.time + flyoverSeconds;
        SetupCameras(0);
        Preview?.SetActive(false);

        TrackDefinition def = ActiveTrack;
        Bounds b = def.bounds;
        float span = Mathf.Max(b.size.x, b.size.z);
        float tightS = 0f, tightR = float.MaxValue, highS = -1f, highY = 2f;
        for (float s = 20f; s < track.Length - 20f; s += 5f)
        {
            float r = track.RadiusAt(s);
            if (r < tightR) { tightR = r; tightS = s; }
            float y = track.PointAt(s).y;
            if (y > highY) { highY = y; highS = s; }
        }
        var highlights = new List<float> { tightS };
        if (highS > 0f && Mathf.Abs(highS - tightS) > 60f) highlights.Add(highS);
        highlights.Sort();

        flyPositions.Clear();
        flyRotations.Clear();
        void Key(Vector3 pos, Vector3 lookAt)
        {
            flyPositions.Add(pos);
            flyRotations.Add(Quaternion.LookRotation(lookAt - pos));
        }
        Key(b.center + new Vector3(-span * 0.35f, span * 0.45f, -span * 0.55f), b.center);
        foreach (float s in highlights)
        {
            Vector3 p = track.PointAt(s);
            Key(p - track.TangentAt(s) * 30f + track.RightAt(s) * 18f + Vector3.up * 22f, p);
        }
        Vector3 finish = track.PointAt(0f), finishDir = track.TangentAt(0f);
        Key(finish + finishDir * 40f + Vector3.up * 16f, finish);
        Transform lead = humanRacers.Count > 0 ? humanRacers[0].Kart.transform : karts[0].transform;
        Key(lead.position - lead.forward * 7.5f + Vector3.up * 3.2f, lead.position + lead.forward * 12f);

        var cam = overviewCamera;
        cam.rect = new Rect(0f, 0f, 1f, 1f);
        cam.gameObject.SetActive(true);
        GameAudio.Play(Sfx.Flyover);
        Broadcast($"flyover|1|{def.displayName}|{laps}");
    }

    public void SkipFlyover()
    {
        if (!FlyoverActive) return;
        flyoverEnd = Time.time;
    }

    void TickFlyover()
    {
        float t = Mathf.Clamp01((Time.time - flyoverStart) / Mathf.Max(0.01f, flyoverSeconds));
        int segments = flyPositions.Count - 1;
        float f = t * segments;
        int i = Mathf.Min(segments - 1, (int)f);
        float u = f - i;
        float e = u * u * (3f - 2f * u);
        Vector3 p0 = flyPositions[Mathf.Max(0, i - 1)], p1 = flyPositions[i], p2 = flyPositions[i + 1], p3 = flyPositions[Mathf.Min(segments, i + 2)];
        Vector3 pos = 0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u + (-p0 + 3f * p1 - 3f * p2 + p3) * u * u * u);
        overviewCamera.transform.SetPositionAndRotation(pos, Quaternion.Slerp(flyRotations[i], flyRotations[i + 1], e));
    }

    void EndFlyover()
    {
        FlyoverActive = false;
        raceStartTime = Time.time + countdownSeconds - 0.001f; // full 3-2-1 after the flyover (the epsilon keeps float error from showing a "4")
        lastCountValue = -1;
        if (ActiveTrack != null) overviewCamera.transform.SetPositionAndRotation(ActiveTrack.overviewPosition, ActiveTrack.overviewRotation);
        SetupCameras(humanRacers.Count);
        Broadcast("flyover|0");
    }

    void Update()
    {
        PumpServer();
        HandleKeyboard();

        if (Paused)
        {
            SendPhoneItems();
            return;
        }

        switch (Phase)
        {
            case RacePhase.Lobby:
                DropLostPlayers();
                if (IntroActive) TickIntro();
                else if (players.Count > 0 && players.All(p => p.Ready)) StartCountdown();
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
    // Tracks
    // ============================================================================================

    int selectedTrack;     // 0..Tracks.Count-1, or Tracks.Count = RANDOM
    int activeTrack = -1;  // the track that is enabled in the scene

    public IReadOnlyList<TrackDefinition> Tracks => tracks;
    /// <summary>Lobby card: 0..Tracks.Count-1, Tracks.Count = RANDOM.</summary>
    public int SelectedTrack => selectedTrack;
    public bool RandomSelected => selectedTrack >= tracks.Length;
    public int ActiveTrackIndex => activeTrack;
    public TrackDefinition ActiveTrack => activeTrack >= 0 ? tracks[activeTrack] : null;
    public bool ThumbnailsReady { get; private set; }
    public string TrackCardName(int index) => index >= tracks.Length ? Loc.T("lobby.random") : tracks[index].displayName;

    /// <summary>Enables one track (all others off), applies its sky/lighting and points the race logic at it.</summary>
    void ActivateTrack(int index)
    {
        index = Mathf.Clamp(index, 0, tracks.Length - 1);
        for (int i = 0; i < tracks.Length; i++)
            if (tracks[i].gameObject.activeSelf != (i == index)) tracks[i].gameObject.SetActive(i == index);
        activeTrack = index;
        TrackDefinition def = tracks[index];
        def.ApplyEnvironment(sun);
        DynamicGI.UpdateEnvironment();
        track = def.track;
        gridSlots = def.gridSlots;
        laps = def.laps;
        foreach (var kart in karts) kart.GetComponent<LapTracker>().Configure(def.checkpointCount, laps);
        ItemManager.Instance?.SetTrack(track);
        overviewCamera.transform.SetPositionAndRotation(def.overviewPosition, def.overviewRotation);
        Physics.SyncTransforms();
    }

    /// <summary>Leader phone / keyboard: next or previous track card (the last card is RANDOM).</summary>
    public void CycleTrack(int direction)
    {
        if (Phase != RacePhase.Lobby) return;
        int n = tracks.Length + 1;
        selectedTrack = ((selectedTrack + direction) % n + n) % n;
        if (!RandomSelected) ShowTrackInLobby(selectedTrack);
        GameAudio.Play(Sfx.UiMove);
        SendTrackToAll();
    }

    /// <summary>Test hook: select a track card directly (index == Tracks.Count selects RANDOM).</summary>
    public void SelectTrackForTest(int index)
    {
        if (Phase != RacePhase.Lobby) return;
        selectedTrack = Mathf.Clamp(index, 0, tracks.Length);
        if (!RandomSelected) ShowTrackInLobby(selectedTrack);
        SendTrackToAll();
    }

    void ShowTrackInLobby(int index)
    {
        if (index == activeTrack) return;
        ActivateTrack(index);
        for (int k = 0; k < karts.Length; k++) karts[k].Teleport(gridSlots[k].position, gridSlots[k].rotation);
    }

    /// <summary>Renders a top-down picture of every track (lobby card and phone image).</summary>
    System.Collections.IEnumerator RenderTrackThumbnails()
    {
        yield return null;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) yield break;
        var camGo = new GameObject("TrackThumbCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.enabled = false;
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.allowHDR = false;
        const int w = 480, h = 300;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        int keep = activeTrack;
        for (int i = 0; i < tracks.Length; i++)
        {
            ActivateTrack(i);
            Bounds b = tracks[i].bounds;
            cam.transform.SetPositionAndRotation(b.center + Vector3.up * 400f, Quaternion.Euler(90f, 0f, 0f));
            cam.orthographicSize = Mathf.Max(b.extents.z, b.extents.x * h / w) * 1.08f;
            cam.farClipPlane = 1000f;
            bool fog = RenderSettings.fog;
            RenderSettings.fog = false; // straight down through 400 m of fog would grey everything out
            cam.Render();
            RenderSettings.fog = fog;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false) { name = "TrackThumb_" + i };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prev;
            tracks[i].Thumbnail = tex;
            server?.SetFile($"/track/{i}.png", "image/png", tex.EncodeToPNG());
        }
        ActivateTrack(keep);
        if (Phase == RacePhase.Lobby)
            for (int k = 0; k < karts.Length; k++) karts[k].Teleport(gridSlots[k].position, gridSlots[k].rotation);
        cam.targetTexture = null;
        rt.Release();
        Destroy(camGo);
        ThumbnailsReady = true;
        SendTrackToAll();
    }

    void SendTrackToAll()
    {
        if (server == null) return;
        foreach (var p in players) SendTrack(p);
    }

    /// <summary>track|card|cards|name|length m|difficulty|laps (the last card is RANDOM: no picture).</summary>
    void SendTrack(Player p)
    {
        if (server == null || p.IsKeyboard || p.ConnectionId < 0) return;
        int n = tracks.Length + 1;
        if (RandomSelected) server.Send(p.ConnectionId, $"track|{selectedTrack}|{n}|RANDOM|0|0|0");
        else
        {
            TrackDefinition t = tracks[selectedTrack];
            server.Send(p.ConnectionId, $"track|{selectedTrack}|{n}|{t.displayName}|{t.Length:F0}|{t.difficulty}|{t.laps}");
        }
    }

    // ============================================================================================
    // Phases
    // ============================================================================================

    public void EnterLobby()
    {
        if (Paused) SetPaused(false, null);
        Phase = RacePhase.Lobby;
        phaseTime = Time.time;
        resultsAt = -1f;
        humanRacers.Clear();
        standings.Clear();
        LobbyQuitConfirm = false;
        foreach (var p in players) p.Ready = false;

        racers.Clear();
        activeKarts.Clear();
        for (int k = 0; k < karts.Length; k++)
        {
            KartController kart = karts[k];
            kart.gameObject.SetActive(true);
            activeKarts.Add(kart);
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
        foreach (var p in players)
        {
            SendPick(p);
            SendTrack(p);
        }
        BroadcastCpu();
        foreach (var w in waitingConnections.ToList()) Hello(w.Key, w.Value);
    }

    /// <summary>Starts the countdown with the current players (host / keyboard "force start").</summary>
    public void StartCountdown() => StartRace(false);

    /// <summary>
    /// Countdown on the active track. A restart keeps the track and every character (CPUs too);
    /// a new race resolves a RANDOM track card first.
    /// </summary>
    void StartRace(bool restart)
    {
        if (Phase != RacePhase.Lobby) return;
        if (players.Count == 0) AddKeyboardPlayer();
        if (!restart && RandomSelected) ActivateTrack(rng.Next(tracks.Length));

        var previousCpu = restart ? racers.Where(r => r.Human == null).Select(r => r.Character).ToList() : new List<int>();
        racers.Clear();
        humanRacers.Clear();
        finishCount = 0;
        resultsAt = -1f;

        // Humans take karts 0..n-1 (camera i follows kart i); CPUs take unpicked characters.
        List<Player> humans = players.OrderBy(p => p.Slot).ToList();
        var freeCharacters = Enumerable.Range(0, roster.Count).Where(c => humans.All(h => h.Character != c))
            .OrderBy(_ => rng.Next()).ToList();
        if (previousCpu.Count > 0 && previousCpu.All(c => humans.All(h => h.Character != c))) freeCharacters = previousCpu;

        bool headlights = ActiveTrack != null && ActiveTrack.headlights;
        int racing = humans.Count + CpuCountFor(humans.Count);
        activeKarts.Clear();
        for (int k = 0; k < karts.Length; k++)
        {
            bool inRace = k < racing;
            karts[k].gameObject.SetActive(inRace);
            if (!inRace) continue;
            activeKarts.Add(karts[k]);
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
            if (beam != null) beam.GetComponent<Light>().enabled = headlights && r.Human != null; // real lights only for humans at night
        }

        // Grid: CPUs on the front rows, humans behind them.
        var gridOrder = racers.Where(r => r.Human == null).Concat(racers.Where(r => r.Human != null)).ToList();
        for (int i = 0; i < gridOrder.Count; i++)
            gridOrder[i].Kart.Teleport(gridSlots[i].position, gridSlots[i].rotation);

        SetupCameras(humanRacers.Count);
        Preview?.SetActive(false);
        Podium?.Hide();

        Phase = RacePhase.Countdown;
        phaseTime = Time.time;
        raceStartTime = Time.time + countdownSeconds;
        lastCountValue = -1;
        FlyoverActive = false;
        Broadcast("phase|countdown");
        if (!restart && flyoverSeconds > 0f) BeginFlyover();
        UpdateStandings();
    }

    void TickCountdown()
    {
        if (FlyoverActive)
        {
            if (Time.time >= flyoverEnd) EndFlyover();
            else
            {
                TickFlyover();
                return;
            }
        }
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
            if (!r.Human.IsKeyboard) server?.Send(r.Human.ConnectionId, $"result|{r.FinishOrder + 1}|{racers.Count}");
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
                server?.Send(h.Human.ConnectionId, $"result|{h.Position}|{racers.Count}");
        Broadcast("phase|results");
    }

    // ============================================================================================
    // Pause menu (leader only) and quitting
    // ============================================================================================

    float timeScaleBeforePause = 1f;

    public bool Paused { get; private set; }
    /// <summary>"P1" (phone leader) or "HOST" (PC keyboard without a keyboard player).</summary>
    public string PausedBy { get; private set; }
    public int PauseSelection { get; private set; }
    /// <summary>Pause menu is asking "Quit game?" (selection 0 = NO, 1 = YES).</summary>
    public bool PauseQuitConfirm { get; private set; }
    public int ConfirmSelection { get; private set; }
    /// <summary>Lobby "Quit game?" prompt opened with Esc on the PC.</summary>
    public bool LobbyQuitConfirm { get; private set; }
    public int QuitRequests { get; private set; }
    public int PauseDenied { get; private set; }
    public bool CanPause => Phase == RacePhase.Countdown || Phase == RacePhase.Racing || Phase == RacePhase.Results;

    /// <summary>Lowest connected slot (phone or keyboard player).</summary>
    public Player Leader => players.Where(p => p.Connected).OrderBy(p => p.Slot).FirstOrDefault();
    bool IsLeader(Player p) => p != null && p == Leader;

    void SetPaused(bool paused, string by)
    {
        if (paused == Paused) return;
        Paused = paused;
        if (paused)
        {
            timeScaleBeforePause = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            PausedBy = by;
            PauseSelection = 0;
            PauseQuitConfirm = false;
            foreach (var p in players) p.Input.Clear();
        }
        else
        {
            Time.timeScale = timeScaleBeforePause;
            PauseQuitConfirm = false;
        }
        AudioManager.Instance?.SetPaused(paused);
        GameAudio.Play(paused ? Sfx.UiSelect : Sfx.UiBack);
        BroadcastPause();
    }

    void BroadcastPause()
    {
        if (server == null) return;
        if (!Paused)
        {
            Broadcast("pause|0");
            return;
        }
        Player leader = Leader;
        foreach (var p in players)
        {
            if (p.IsKeyboard || p.ConnectionId < 0) continue;
            server.Send(p.ConnectionId, $"pause|1|{PausedBy}|{(p == leader ? 1 : 0)}|{PauseSelection}|{(PauseQuitConfirm ? 1 : 0)}|{ConfirmSelection}");
        }
    }

    /// <summary>Pause request from a phone (null = PC keyboard). Only the leader may pause.</summary>
    public bool RequestPause(Player from)
    {
        if (!CanPause || Paused) return false;
        if (from != null && !from.IsKeyboard && !IsLeader(from))
        {
            PauseDenied++;
            if (from.ConnectionId >= 0) server?.Send(from.ConnectionId, "denied|pause");
            return false;
        }
        Player kb = players.FirstOrDefault(p => p.IsKeyboard);
        string by = from != null ? from.Label : kb != null ? kb.Label : "HOST";
        SetPaused(true, by);
        return true;
    }

    /// <summary>Menu navigation: -1 up, +1 down.</summary>
    public void PauseMove(int direction)
    {
        if (!Paused) return;
        if (PauseQuitConfirm) ConfirmSelection = direction > 0 ? 1 : 0;
        else PauseSelection = (PauseSelection + direction + PauseItems.Length) % PauseItems.Length;
        GameAudio.Play(Sfx.UiMove);
        BroadcastPause();
    }

    public void PauseSelect(int item = -1)
    {
        if (!Paused) return;
        if (PauseQuitConfirm)
        {
            if (item >= 0) ConfirmSelection = Mathf.Clamp(item, 0, 1);
            if (ConfirmSelection == 1) Quit();
            else PauseBack();
            return;
        }
        if (item >= 0) PauseSelection = Mathf.Clamp(item, 0, PauseItems.Length - 1);
        switch (PauseSelection)
        {
            case PauseResume:
                SetPaused(false, null);
                break;
            case PauseRestart:
                RestartRace();
                break;
            case PauseLobby:
                SetPaused(false, null);
                EnterLobby();
                break;
            case PauseQuit:
                PauseQuitConfirm = true;
                ConfirmSelection = 0; // NO first: quitting always needs a deliberate second choice
                GameAudio.Play(Sfx.UiSelect);
                BroadcastPause();
                break;
        }
    }

    /// <summary>Back / Esc inside the menu: leaves the quit prompt, otherwise resumes.</summary>
    public void PauseBack()
    {
        if (!Paused) return;
        if (PauseQuitConfirm)
        {
            PauseQuitConfirm = false;
            GameAudio.Play(Sfx.UiBack);
            BroadcastPause();
        }
        else SetPaused(false, null);
    }

    /// <summary>Same track, same characters, fresh countdown.</summary>
    public void RestartRace()
    {
        if (Phase == RacePhase.Lobby) return;
        SetPaused(false, null);
        Podium?.Hide();
        Phase = RacePhase.Lobby; // StartRace only runs from the lobby state; nothing is broadcast in between
        StartRace(true);
    }

    void Quit()
    {
        QuitRequests++;
        Debug.Log("[RaceManager] quit requested by the leader");
        QuitHandler?.Invoke();
    }

    static void DefaultQuit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
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
            server.Send(h.Human.ConnectionId, $"hud|{h.Position}|{racers.Count}|{h.Lap.DisplayLap}|{laps}");
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
                            lost.LostAt = Time.unscaledTime;
                            lost.Input.Clear();
                            SendJoinedToAll();
                            if (Paused) BroadcastPause(); // leadership may have moved
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
                if (player == null || parts.Length < 4) break;
                if (Paused) player.Input.Clear(); // inputs are ignored while paused
                else player.Input.Apply(ParseFloat(parts[1]), ParseFloat(parts[2]), parts[3] == "1", parts.Length >= 5 && parts[4] == "1");
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
                if (player != null && IsLeader(player) && !Paused)
                {
                    if (Phase == RacePhase.Lobby) StartCountdown();
                    else if (Phase == RacePhase.Results) EnterLobby();
                }
                break;
            case "track":
                if (player != null && parts.Length >= 2 && Phase == RacePhase.Lobby)
                {
                    if (IsLeader(player)) CycleTrack(parts[1] == "-1" ? -1 : 1);
                    else server?.Send(conn, "denied|track");
                }
                break;
            case "lang": // leader's LANGUAGE setting: the whole game (PC + phones that follow it)
                if (player != null && parts.Length >= 2 && Phase == RacePhase.Lobby)
                {
                    if (IsLeader(player)) SetLanguage(Loc.Parse(parts[1]));
                    else server?.Send(conn, "denied|lang");
                }
                break;
            case "cpu":
                if (player != null && parts.Length >= 2 && Phase == RacePhase.Lobby)
                {
                    if (IsLeader(player)) CycleCpu(parts[1] == "-1" ? -1 : 1);
                    else server?.Send(conn, "denied|cpu");
                }
                break;
            case "pause":
                if (player != null) RequestPause(player);
                break;
            case "menu":
                if (player == null || !Paused || parts.Length < 2) break;
                if (!IsLeader(player)) { server?.Send(conn, "denied|menu"); break; }
                switch (parts[1])
                {
                    case "up": PauseMove(-1); break;
                    case "down": PauseMove(1); break;
                    case "ok": PauseSelect(); break;
                    case "back": PauseBack(); break;
                    case "pick":
                        if (parts.Length >= 3 && int.TryParse(parts[2], out int item)) PauseSelect(item);
                        break;
                }
                break;
            case "skip": // any button on the leader's phone skips the intro / the flyover
                if (player == null || !IsLeader(player) || Paused) break;
                if (IntroActive) SkipIntro();
                else if (FlyoverActive) SkipFlyover();
                break;
            case "quit": // lobby EXIT button, confirmed on the phone
                if (player == null || Phase != RacePhase.Lobby || parts.Length < 2 || parts[1] != "yes") break;
                if (IsLeader(player)) Quit();
                else server?.Send(conn, "denied|quit");
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
                if (Paused) server.Send(conn, $"pause|1|{PausedBy}|0|0|0|0");
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
        SendTrack(player);
        server.Send(conn, $"cpu|{cpuOption}");
        server.Send(conn, "lang|" + Loc.Code);
        if (IntroActive) server.Send(conn, "intro|1");
        if (FlyoverActive) server.Send(conn, $"flyover|1|{ActiveTrack.displayName}|{laps}");
        if (Paused) BroadcastPause();
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

    /// <summary>Test hook: removes the idle test players (frees their slots).</summary>
    public void RemoveBotPlayersForTest()
    {
        foreach (var p in players.Where(x => x.IsTestBot).ToList()) RemovePlayer(p);
    }

    /// <summary>Test hook: the keyboard player leaves (like Backspace in the lobby).</summary>
    public void RemoveKeyboardPlayerForTest()
    {
        var kb = players.FirstOrDefault(p => p.IsKeyboard);
        if (kb != null && Phase == RacePhase.Lobby) RemovePlayer(kb);
    }

    /// <summary>Test hook: marks the keyboard and bot players READY (phones ready themselves).</summary>
    public void ReadyLocalPlayersForTest()
    {
        foreach (var p in players) if (p.IsKeyboard || p.IsTestBot) p.Ready = true;
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
            if (!p.Connected && Time.unscaledTime - p.LostAt > lobbyDisconnectGrace) RemovePlayer(p);
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

    /// <summary>joined|slot|colour|leader - the leader's phone shows the track arrows, START, PAUSE and EXIT.</summary>
    void SendJoinedToAll()
    {
        if (server == null) return;
        foreach (var p in players)
        {
            if (p.IsKeyboard || p.ConnectionId < 0) continue;
            string hex = "#" + ColorUtility.ToHtmlStringRGB(p.Color);
            server.Send(p.ConnectionId, $"joined|{p.Slot}|{hex}|{(IsLeader(p) ? 1 : 0)}");
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

    /// <summary>
    /// PC keys. Lobby: Enter join/ready, Left/Right (A/D) character, Q/E or Tab track, C CPU racers, L language,
    /// Backspace leave, Space start, Esc quit prompt. Race/results: Esc or P pause menu (Up/Down or W/S, Enter).
    /// </summary>
    void HandleKeyboard()
    {
        bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        bool space = Input.GetKeyDown(KeyCode.Space);
        bool esc = Input.GetKeyDown(KeyCode.Escape);
        Player kb = players.FirstOrDefault(p => p.IsKeyboard);

        if (Paused)
        {
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) PauseMove(-1);
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) PauseMove(1);
            if (PauseQuitConfirm && (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A))) PauseMove(-1);
            if (PauseQuitConfirm && (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D))) PauseMove(1);
            if (enter || space) PauseSelect();
            else if (esc || Input.GetKeyDown(KeyCode.P)) PauseBack();
            return;
        }

        if (IntroActive) return; // any key skips it (TickIntro)
        if (FlyoverActive && Input.anyKeyDown && !esc && !Input.GetKeyDown(KeyCode.P))
        {
            SkipFlyover();
            return;
        }

        switch (Phase)
        {
            case RacePhase.Lobby:
                if (LobbyQuitConfirm)
                {
                    if (enter) Quit();
                    if (esc || Input.GetKeyDown(KeyCode.Backspace))
                    {
                        LobbyQuitConfirm = false;
                        GameAudio.Play(Sfx.UiBack);
                    }
                    return;
                }
                if (esc)
                {
                    LobbyQuitConfirm = true;
                    GameAudio.Play(Sfx.UiSelect);
                    return;
                }
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
                if (Input.GetKeyDown(KeyCode.C)) CycleCpu(1);
                if (Input.GetKeyDown(KeyCode.L)) SetLanguage(Loc.Current == Lang.Es ? Lang.En : Lang.Es);
                if (Input.GetKeyDown(KeyCode.Q)) CycleTrack(-1);
                if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Tab)) CycleTrack(1);
                if (kb != null && (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))) RemovePlayer(kb);
                if (space) StartCountdown();
                break;
            case RacePhase.Countdown:
            case RacePhase.Racing:
                if (esc || Input.GetKeyDown(KeyCode.P)) RequestPause(null);
                break;
            case RacePhase.Results:
                if (esc || Input.GetKeyDown(KeyCode.P)) RequestPause(null);
                else if (enter || space) EnterLobby();
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
