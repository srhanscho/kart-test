using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Runtime UI built with UI Toolkit (no UXML): lobby, animated countdown, per-viewport race
/// HUD (position, lap / FINAL LAP banner, item slot with roulette, speed, wrong-way warning),
/// shared minimap with standings, and the results table over the podium camera.
/// Layout is in 1920x1080 reference pixels (PanelSettings scales it with the screen).
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class RaceUi : MonoBehaviour
{
    [SerializeField] Font titleFont;
    [SerializeField] Font bodyFont;
    [SerializeField] Font narrowFont;
    [SerializeField] Texture2D panelTexture;
    [SerializeField] Texture2D panelYellowTexture;
    [SerializeField] Texture2D squareTexture;
    [SerializeField] Texture2D starTexture;

    static readonly Color Gold = new Color(1f, 0.83f, 0.28f), Silver = new Color(0.85f, 0.88f, 0.93f), Bronze = new Color(0.9f, 0.57f, 0.29f);

    class Hud
    {
        public VisualElement root, itemBox, itemTile, info, posRow;
        public Label position, suffix, lap, speed, name, item, banner, wrongWay, centre;
        public int lastPosition = -1, lastLap = -1;
        public float punch, bannerUntil, bannerStart, wrongTimer;
    }

    class Card
    {
        public VisualElement root, header, stats, ready;
        public Label title, who, character, readyLabel, empty;
        public Image preview;
        public VisualElement[] bars = new VisualElement[15];
    }

    RaceManager rm;
    UIDocument doc;
    VisualElement root, lobby, race, results, countdownLayer, hudLayer, sidePanel;
    Label countdownLabel, urlLabel, otherUrls, serverError, readyStatus, resultsPrompt;
    Image qrImage;
    MinimapElement minimap;
    readonly List<Label> standingsLines = new List<Label>();
    readonly Hud[] huds = new Hud[RaceManager.MaxHumans];
    readonly Card[] cards = new Card[RaceManager.MaxHumans];
    VisualElement resultsTable;
    int lastCount = -1;
    float countPunchStart = -10f;
    RacePhase lastPhase = (RacePhase)(-1);
    bool built;

    // ---- read-only hooks for tests ----------------------------------------------------------------
    public bool Built => built;
    public string CountdownText => countdownLabel?.text;
    public bool CountdownVisible => countdownLayer != null && countdownLayer.style.display == DisplayStyle.Flex && !string.IsNullOrEmpty(countdownLabel.text);
    public string HudPosition(int i) => huds[i]?.position.text + huds[i]?.suffix.text;
    public string HudLap(int i) => huds[i]?.lap.text;
    public string HudItem(int i) => huds[i]?.item.text;
    public bool HudVisible(int i) => huds[i] != null && huds[i].root.style.display == DisplayStyle.Flex;
    public bool FinalLapBannerShown(int i) => huds[i] != null && huds[i].banner.text.Contains("FINAL");
    public int MinimapDots => minimap?.DotCount ?? 0;
    public int ResultRows => resultsTable?.childCount ?? 0;
    public bool LobbyVisible => lobby != null && lobby.style.display == DisplayStyle.Flex;
    public bool ResultsVisible => results != null && results.style.display == DisplayStyle.Flex;
    public int LobbyCardsFilled => cards.Count(c => c != null && c.character.text.Length > 0 && c.root.style.opacity.value > 0.9f);
    public UIDocument Document => doc;

    public void Configure(Font title, Font body, Font narrow, Texture2D panel, Texture2D panelYellow, Texture2D square, Texture2D star)
    {
        titleFont = title;
        bodyFont = body;
        narrowFont = narrow;
        panelTexture = panel;
        panelYellowTexture = panelYellow;
        squareTexture = square;
        starTexture = star;
    }

    void OnEnable()
    {
        doc = GetComponent<UIDocument>();
        rm = GetComponent<RaceManager>();
        Build();
    }

    // ================================================================================================
    // Building
    // ================================================================================================

    void Build()
    {
        root = doc.rootVisualElement;
        if (root == null) return;
        root.Clear();
        root.style.flexGrow = 1;
        root.pickingMode = PickingMode.Ignore;
        if (bodyFont != null) root.style.unityFontDefinition = FontDefinition.FromFont(bodyFont);

        hudLayer = Layer("hud-layer");
        BuildRace();
        countdownLayer = Layer("countdown-layer");
        countdownLabel = Text("", 260, Gold, titleFont, 10);
        countdownLabel.name = "countdown";
        countdownLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        countdownLabel.style.position = Position.Absolute;
        countdownLabel.style.left = 0; countdownLabel.style.right = 0;
        countdownLabel.style.top = new Length(30, LengthUnit.Percent);
        countdownLayer.Add(countdownLabel);
        lobby = Layer("lobby");
        BuildLobby();
        results = Layer("results");
        BuildResults();
        built = true;
    }

    VisualElement Layer(string name)
    {
        var v = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
        v.style.position = Position.Absolute;
        v.style.left = 0; v.style.right = 0; v.style.top = 0; v.style.bottom = 0;
        root.Add(v);
        return v;
    }

    Label Text(string text, float size, Color color, Font font = null, float outline = 4)
    {
        var l = new Label(text) { pickingMode = PickingMode.Ignore };
        l.style.fontSize = size;
        l.style.color = color;
        if (font != null) l.style.unityFontDefinition = FontDefinition.FromFont(font);
        l.style.unityTextOutlineColor = new Color(0.05f, 0.06f, 0.1f, 1f);
        // Thin outline + chunky drop shadow keeps pixel fonts readable.
        l.style.unityTextOutlineWidth = Mathf.Min(outline, 3f);
        if (outline > 0f && size >= 36f) l.style.textShadow = Shadow(Mathf.Clamp(size / 22f, 2f, 9f));
        l.style.marginLeft = l.style.marginRight = l.style.marginTop = l.style.marginBottom = 0;
        l.style.paddingLeft = l.style.paddingRight = l.style.paddingTop = l.style.paddingBottom = 0;
        return l;
    }

    VisualElement Panel(Texture2D texture, Color tint, int slice = 16)
    {
        var v = new VisualElement { pickingMode = PickingMode.Ignore };
        if (texture != null)
        {
            v.style.backgroundImage = new StyleBackground(texture);
            v.style.unitySliceLeft = v.style.unitySliceRight = v.style.unitySliceTop = slice;
            v.style.unitySliceBottom = slice + 4;
            v.style.unityBackgroundImageTintColor = tint;
        }
        else v.style.backgroundColor = tint;
        return v;
    }

    static void Abs(VisualElement v, float left, float top, float width, float height)
    {
        v.style.position = Position.Absolute;
        v.style.left = left; v.style.top = top; v.style.width = width; v.style.height = height;
    }

    // ---- lobby ------------------------------------------------------------------------------------

    void BuildLobby()
    {
        lobby.style.backgroundColor = new Color(0.05f, 0.06f, 0.1f, 0.78f);

        var title = Text("KART PARTY", 150, Gold, titleFont, 8);
        title.style.position = Position.Absolute;
        title.style.left = 0; title.style.right = 0; title.style.top = 20;
        title.style.unityTextAlign = TextAnchor.UpperCenter;
        lobby.Add(title);

        var join = Panel(panelTexture, new Color(1f, 1f, 1f, 0.95f));
        Abs(join, 60, 230, 560, 700);
        join.style.alignItems = Align.Center;
        join.style.paddingTop = 24;
        lobby.Add(join);
        var joinTitle = Text("SCAN TO JOIN", 44, new Color(0.15f, 0.17f, 0.24f), bodyFont, 0);
        join.Add(joinTitle);
        qrImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
        qrImage.style.width = 420; qrImage.style.height = 420; qrImage.style.marginTop = 16;
        join.Add(qrImage);
        urlLabel = Text("", 26, new Color(0.1f, 0.45f, 0.85f), bodyFont, 0);
        urlLabel.style.marginTop = 16;
        join.Add(urlLabel);
        otherUrls = Text("", 18, new Color(0.3f, 0.32f, 0.4f), narrowFont, 0);
        otherUrls.style.whiteSpace = WhiteSpace.Normal;
        otherUrls.style.marginTop = 6; otherUrls.style.width = 500; otherUrls.style.unityTextAlign = TextAnchor.UpperCenter;
        join.Add(otherUrls);
        serverError = Text("", 22, new Color(0.85f, 0.2f, 0.2f), bodyFont, 0);
        serverError.style.whiteSpace = WhiteSpace.Normal; serverError.style.width = 500;
        join.Add(serverError);
        var wifi = Text("Same Wi-Fi as this PC.\nTurn the phone sideways to drive.", 18, new Color(0.3f, 0.32f, 0.4f), narrowFont, 0);
        wifi.style.marginTop = 10;
        wifi.style.unityTextAlign = TextAnchor.UpperCenter;
        join.Add(wifi);

        for (int i = 0; i < cards.Length; i++)
        {
            float x = 680 + (i % 2) * 600, y = 230 + (i / 2) * 355;
            cards[i] = BuildCard(i, x, y);
        }

        readyStatus = Text("", 36, Color.white, bodyFont, 3);
        readyStatus.style.position = Position.Absolute;
        readyStatus.style.left = 0; readyStatus.style.right = 0; readyStatus.style.top = 950;
        readyStatus.style.unityTextAlign = TextAnchor.MiddleCenter;
        lobby.Add(readyStatus);
        var hints = Text("KEYBOARD: [ENTER] JOIN/READY  [LEFT/RIGHT] PICK  [BACKSPACE] LEAVE  [SPACE] START  [M] MUSIC  [F2] LOOK\n" +
                         "DRIVE: WASD/ARROWS  SPACE TAP = HOP, HOLD+STEER = DRIFT  E/SHIFT = ITEM  ESC = LOBBY",
            18, new Color(1f, 1f, 1f, 0.8f), narrowFont, 2);
        hints.style.position = Position.Absolute;
        hints.style.left = 0; hints.style.right = 0; hints.style.top = 1005;
        hints.style.unityTextAlign = TextAnchor.MiddleCenter;
        lobby.Add(hints);
    }

    Card BuildCard(int slot, float x, float y)
    {
        var c = new Card();
        Color pc = RaceManager.PlayerColors[slot];
        c.root = Panel(panelTexture, new Color(0.2f, 0.23f, 0.3f, 0.97f));
        Abs(c.root, x, y, 580, 335);
        lobby.Add(c.root);

        c.header = new VisualElement { pickingMode = PickingMode.Ignore };
        Abs(c.header, 10, 8, 560, 54);
        c.header.style.backgroundColor = pc;
        c.header.style.borderTopLeftRadius = c.header.style.borderTopRightRadius = 10;
        c.root.Add(c.header);
        c.title = Text($"P{slot + 1}", 40, Color.white, bodyFont, 3);
        Abs(c.title, 16, 4, 120, 50);
        c.header.Add(c.title);
        c.who = Text("", 26, Color.white, bodyFont, 3);
        Abs(c.who, 120, 12, 430, 40);
        c.header.Add(c.who);

        c.preview = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
        Abs(c.preview, 16, 70, 330, 210);
        c.preview.style.backgroundColor = new Color(0.12f, 0.14f, 0.19f);
        c.root.Add(c.preview);

        c.stats = new VisualElement { pickingMode = PickingMode.Ignore };
        Abs(c.stats, 362, 74, 200, 200);
        c.root.Add(c.stats);
        string[] labels = { "SPEED", "ACCEL", "HANDLING" };
        for (int s = 0; s < 3; s++)
        {
            var l = Text(labels[s], 20, new Color(1, 1, 1, 0.8f), narrowFont, 2);
            Abs(l, 0, s * 66, 200, 26);
            c.stats.Add(l);
            for (int b = 0; b < 5; b++)
            {
                var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                Abs(bar, b * 38, s * 66 + 30, 32, 16);
                bar.style.borderTopLeftRadius = bar.style.borderTopRightRadius = bar.style.borderBottomLeftRadius = bar.style.borderBottomRightRadius = 4;
                c.stats.Add(bar);
                c.bars[s * 5 + b] = bar;
            }
        }

        c.character = Text("", 34, Color.white, bodyFont, 3);
        Abs(c.character, 20, 284, 400, 44);
        c.root.Add(c.character);

        c.ready = Panel(panelYellowTexture, new Color(0.45f, 1f, 0.5f));
        Abs(c.ready, 400, 280, 160, 48);
        c.ready.style.alignItems = Align.Center; c.ready.style.justifyContent = Justify.Center;
        c.readyLabel = Text("READY!", 30, Color.white, bodyFont, 2);
        c.ready.Add(c.readyLabel);
        c.root.Add(c.ready);

        c.empty = Text("Open the URL on a phone\nto join", 26, new Color(1, 1, 1, 0.5f), bodyFont, 2);
        c.empty.style.position = Position.Absolute;
        c.empty.style.left = 20; c.empty.style.right = 20; c.empty.style.top = 130;
        c.empty.style.whiteSpace = WhiteSpace.Normal;
        c.empty.style.unityTextAlign = TextAnchor.MiddleCenter;
        c.root.Add(c.empty);
        return c;
    }

    // ---- race -------------------------------------------------------------------------------------

    void BuildRace()
    {
        race = hudLayer;
        for (int i = 0; i < huds.Length; i++) huds[i] = BuildHud(i);

        // Minimap: no panel, just the outlined track and dots; placed per layout in UpdateHuds.
        minimap = new MinimapElement { name = "minimap" };
        minimap.style.position = Position.Absolute;
        minimap.style.opacity = 0.9f;
        race.Add(minimap);

        // Tiny standings strip, only used with 3 players (in the empty quadrant).
        sidePanel = new VisualElement { name = "standings", pickingMode = PickingMode.Ignore };
        sidePanel.style.position = Position.Absolute;
        race.Add(sidePanel);
        for (int i = 0; i < 6; i++)
        {
            var l = Text("", 22, Color.white, bodyFont, 1);
            l.style.textShadow = Shadow(2f);
            l.style.height = 28;
            standingsLines.Add(l);
            sidePanel.Add(l);
        }
    }

    Hud BuildHud(int i)
    {
        var h = new Hud();
        h.root = new VisualElement { name = $"hud-{i}", pickingMode = PickingMode.Ignore };
        h.root.style.position = Position.Absolute;
        h.root.style.overflow = Overflow.Hidden;
        race.Add(h.root);

        // Top info column (outer corner): lap, name, item slot.
        h.info = new VisualElement { pickingMode = PickingMode.Ignore };
        h.info.style.position = Position.Absolute;
        h.root.Add(h.info);
        h.lap = Text("", 44, Color.white, bodyFont, 2);
        h.name = Text("", 26, Color.white, bodyFont, 1);
        h.itemBox = Panel(squareTexture, new Color(1f, 1f, 1f, 0.95f), 18);
        h.itemBox.style.marginTop = 10;
        h.itemTile = new VisualElement { pickingMode = PickingMode.Ignore };
        h.itemTile.style.flexGrow = 1;
        h.itemTile.style.justifyContent = Justify.Center;
        h.item = Text("", 24, new Color(0.08f, 0.08f, 0.12f), bodyFont, 0);
        h.item.style.unityTextAlign = TextAnchor.MiddleCenter;
        h.item.style.whiteSpace = WhiteSpace.Normal;
        h.itemTile.Add(h.item);
        h.itemBox.Add(h.itemTile);
        h.info.Add(h.lap);
        h.info.Add(h.name);
        h.info.Add(h.itemBox);

        // Position (bottom outer corner): big number + suffix on one baseline.
        h.posRow = new VisualElement { pickingMode = PickingMode.Ignore };
        h.posRow.style.position = Position.Absolute;
        h.posRow.style.flexDirection = FlexDirection.Row;
        h.posRow.style.alignItems = Align.FlexEnd;
        h.root.Add(h.posRow);
        h.position = Text("", 150, Gold, titleFont, 3);
        h.suffix = Text("", 60, Gold, titleFont, 2);
        h.suffix.style.marginBottom = 14;
        h.posRow.Add(h.position);
        h.posRow.Add(h.suffix);

        h.speed = Text("", 32, Color.white, bodyFont, 1);
        h.banner = Text("", 110, Gold, titleFont, 3);
        h.wrongWay = Text("WRONG WAY!", 90, new Color(1f, 0.25f, 0.2f), titleFont, 3);
        h.centre = Text("", 110, Gold, titleFont, 3);
        foreach (var l in new[] { h.speed, h.banner, h.wrongWay, h.centre })
        {
            l.style.position = Position.Absolute;
            l.style.left = 0; l.style.right = 0;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            h.root.Add(l);
        }
        foreach (var l in new[] { h.lap, h.name, h.position, h.suffix, h.speed, h.banner, h.wrongWay, h.centre })
            l.style.textShadow = Shadow(5f);
        return h;
    }

    static TextShadow Shadow(float offset) => new TextShadow
    {
        offset = new Vector2(offset, offset),
        blurRadius = 0f,
        color = new Color(0.03f, 0.04f, 0.08f, 0.85f)
    };

    /// <summary>Anchors an element to the left or right edge (the other side is released).</summary>
    static void Side(VisualElement v, bool right, float offset)
    {
        if (right)
        {
            v.style.right = offset;
            v.style.left = StyleKeyword.Auto;
        }
        else
        {
            v.style.left = offset;
            v.style.right = StyleKeyword.Auto;
        }
    }

    // ---- results ----------------------------------------------------------------------------------

    void BuildResults()
    {
        var panel = Panel(panelTexture, new Color(0.14f, 0.16f, 0.22f, 0.94f));
        Abs(panel, 1060, 140, 800, 800);
        results.Add(panel);
        var title = Text("RESULTS", 90, Gold, titleFont, 6);
        Abs(title, 0, 20, 800, 100);
        title.style.unityTextAlign = TextAnchor.MiddleCenter;
        panel.Add(title);
        resultsTable = new VisualElement { name = "results-table", pickingMode = PickingMode.Ignore };
        Abs(resultsTable, 30, 140, 740, 540);
        panel.Add(resultsTable);
        resultsPrompt = Text("START on player 1's phone or [Enter]: back to the lobby", 18, new Color(1, 1, 1, 0.85f), narrowFont, 1);
        Abs(resultsPrompt, 20, 700, 760, 60);
        resultsPrompt.style.whiteSpace = WhiteSpace.Normal;
        resultsPrompt.style.unityTextAlign = TextAnchor.MiddleCenter;
        panel.Add(resultsPrompt);
        var banner = Text("", 72, Gold, titleFont, 5);
        banner.name = "podium-banner";
        Abs(banner, 40, 60, 980, 200);
        banner.style.whiteSpace = WhiteSpace.Normal;
        banner.style.unityTextAlign = TextAnchor.UpperCenter;
        results.Add(banner);
    }

    // ================================================================================================
    // Per-frame update
    // ================================================================================================

    void Update()
    {
        if (!built || rm == null) return;
        RacePhase phase = rm.Phase;
        bool racing = phase == RacePhase.Countdown || phase == RacePhase.Racing || phase == RacePhase.Results;
        Show(lobby, phase == RacePhase.Lobby);
        Show(hudLayer, racing && phase != RacePhase.Results);
        Show(results, phase == RacePhase.Results);
        Show(countdownLayer, phase == RacePhase.Countdown || (phase == RacePhase.Racing && Time.time - rm.PhaseStartTime < 1f));
        if (phase != lastPhase)
        {
            lastPhase = phase;
            lastCount = -1;
            if (phase == RacePhase.Results) FillResults();
            foreach (var h in huds) { h.lastPosition = -1; h.lastLap = -1; h.bannerUntil = 0f; }
        }

        switch (phase)
        {
            case RacePhase.Lobby:
                UpdateLobby();
                break;
            case RacePhase.Countdown:
            case RacePhase.Racing:
                UpdateCountdown();
                UpdateHuds();
                UpdateMinimap();
                break;
            case RacePhase.Results:
                break;
        }
    }

    static void Show(VisualElement v, bool on) => v.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;

    void UpdateLobby()
    {
        qrImage.image = rm.QrTexture;
        urlLabel.text = rm.ServerRunning ? rm.Url : "";
        otherUrls.text = rm.OtherUrls.Count > 0 ? "Other addresses: " + string.Join("  ", rm.OtherUrls) : "";
        serverError.text = rm.ServerRunning ? "" : "Phone server not running:\n" + rm.ServerError;

        for (int slot = 0; slot < cards.Length; slot++)
        {
            Card c = cards[slot];
            RaceManager.Player p = rm.Players.FirstOrDefault(x => x.Slot == slot);
            bool joined = p != null;
            c.root.style.opacity = joined ? 1f : 0.55f;
            Show(c.empty, !joined);
            foreach (var v in new VisualElement[] { c.preview, c.stats, c.character, c.who }) Show(v, joined);
            Show(c.ready, joined && p.Ready);
            if (!joined)
            {
                c.character.text = "";
                continue;
            }
            CharacterDefinition def = rm.Roster[p.Character];
            c.who.text = p.IsKeyboard ? "Keyboard" : p.Connected ? "Phone" : "Phone (reconnecting...)";
            c.character.text = def.displayName.ToUpperInvariant();
            c.character.style.color = def.uiColor;
            c.preview.image = rm.Preview != null ? rm.Preview.GetTexture(slot) : null;
            int[] values = { CharacterDefinition.Bars(def.speed), CharacterDefinition.Bars(def.acceleration), CharacterDefinition.Bars(def.handling) };
            for (int s = 0; s < 3; s++)
                for (int b = 0; b < 5; b++)
                    c.bars[s * 5 + b].style.backgroundColor = b < values[s] ? RaceManager.PlayerColors[slot] : new Color(1, 1, 1, 0.15f);
            if (p.Ready)
            {
                float pulse = 1f + Mathf.Sin(Time.time * 6f) * 0.06f;
                c.ready.style.scale = new Scale(new Vector3(pulse, pulse, 1f));
            }
        }

        int ready = rm.Players.Count(x => x.Ready);
        readyStatus.text = rm.Players.Count == 0 ? "WAITING FOR PLAYERS..." : $"READY {ready}/{rm.Players.Count}  -  THE RACE STARTS WHEN EVERYONE IS READY";
    }

    void UpdateCountdown()
    {
        string text;
        Color color;
        if (rm.Phase == RacePhase.Countdown)
        {
            int value = Mathf.CeilToInt(rm.CountdownRemaining);
            text = Mathf.Max(1, value).ToString();
            color = value == 1 ? new Color(1f, 0.55f, 0.2f) : Gold;
            if (value != lastCount)
            {
                lastCount = value;
                countPunchStart = Time.time;
            }
        }
        else
        {
            text = "GO!";
            color = new Color(0.45f, 1f, 0.45f);
            if (lastCount != 0)
            {
                lastCount = 0;
                countPunchStart = Time.time;
            }
        }
        float t = Time.time - countPunchStart;
        float scale = Mathf.Lerp(2f, 1f, Mathf.Clamp01(t / 0.25f));
        countdownLabel.text = text;
        countdownLabel.style.color = color;
        countdownLabel.style.scale = new Scale(new Vector3(scale, scale, 1f));
        countdownLabel.style.opacity = Mathf.Clamp01(1.2f - t);
    }

    void UpdateHuds()
    {
        int count = rm.ViewportCount;
        var humans = rm.HumanRacers;
        for (int i = 0; i < huds.Length; i++)
        {
            Hud h = huds[i];
            bool on = i < count && i < humans.Count;
            Show(h.root, on);
            if (!on) continue;
            Rect r = rm.PlayerCamera(i).rect; // viewport rect (0..1, origin bottom-left)
            h.root.style.left = new Length(r.x * 100f, LengthUnit.Percent);
            h.root.style.top = new Length((1f - r.yMax) * 100f, LengthUnit.Percent);
            h.root.style.width = new Length(r.width * 100f, LengthUnit.Percent);
            h.root.style.height = new Length(r.height * 100f, LengthUnit.Percent);
            float s = Mathf.Lerp(0.55f, 1f, Mathf.InverseLerp(0.5f, 1f, r.height)) * (r.width < 0.9f ? 0.9f : 1f);
            // Right-column viewports mirror their corner elements to the outer (right) edge so the
            // centre of the screen stays free for the minimap.
            bool mirror = r.width < 0.9f && r.x >= 0.49f;
            UpdateHud(h, humans[i], s, mirror);
        }
        LayoutMinimap(count);
    }

    /// <summary>
    /// Minimap placement (1920x1080 reference): 1P bottom-right; 2P on the right edge across the
    /// divider; 3P large in the empty quadrant with a small standings strip; 4P small at the centre.
    /// </summary>
    void LayoutMinimap(int count)
    {
        float size, left, top;
        switch (count)
        {
            case 2:
                size = 250f; left = 1920f - size - 24f; top = 540f - size / 2f;
                break;
            case 3:
                size = 380f; left = 960f + 90f; top = 540f + (540f - size) / 2f;
                break;
            case 4:
                size = 190f; left = 960f - size / 2f; top = 540f - size / 2f;
                break;
            default:
                size = 260f; left = 1920f - size - 30f; top = 1080f - size - 30f;
                break;
        }
        Abs(minimap, left, top, size, size);
        bool strip = count == 3;
        sidePanel.style.display = strip ? DisplayStyle.Flex : DisplayStyle.None;
        if (strip) Abs(sidePanel, left + size + 30f, top + 20f, 1920f - (left + size + 30f) - 20f, size);
    }

    void UpdateHud(Hud h, RaceManager.Racer racer, float s, bool mirror)
    {
        const float pad = 24f;
        int pos = racer.Position;
        if (pos != h.lastPosition)
        {
            if (h.lastPosition > 0) h.punch = 1f;
            h.lastPosition = pos;
        }
        h.punch = Mathf.MoveTowards(h.punch, 0f, Time.deltaTime * 2.5f);
        Color pc = pos == 1 ? Gold : pos == 2 ? Silver : pos == 3 ? Bronze : Color.white;
        h.position.text = pos.ToString();
        h.suffix.text = RaceManager.Ordinal(pos).Substring(pos.ToString().Length).ToUpperInvariant();
        h.position.style.color = pc;
        h.suffix.style.color = pc;
        h.position.style.fontSize = 150 * s;
        h.suffix.style.fontSize = 60 * s;
        float shake = h.punch > 0f ? Mathf.Sin(Time.time * 60f) * 8f * h.punch : 0f;
        float scale = 1f + 0.35f * h.punch;
        h.posRow.style.scale = new Scale(new Vector3(scale, scale, 1f));
        h.posRow.style.transformOrigin = new TransformOrigin(new Length(mirror ? 100 : 0, LengthUnit.Percent), new Length(100, LengthUnit.Percent));
        Side(h.posRow, mirror, pad + shake);
        h.posRow.style.bottom = 6 * s;

        // Info column: lap, name, item slot.
        Side(h.info, mirror, pad);
        h.info.style.top = pad;
        h.info.style.alignItems = mirror ? Align.FlexEnd : Align.FlexStart;
        int lap = racer.Lap.DisplayLap;
        h.lap.text = $"LAP {lap}/{rm.Laps}";
        h.lap.style.fontSize = 44 * s;
        h.name.text = $"{racer.Human.Label} {rm.Roster[racer.Character].displayName.ToUpperInvariant()}";
        h.name.style.color = racer.Human.Color;
        h.name.style.fontSize = 24 * s;

        float box = 130 * s;
        h.itemBox.style.width = box;
        h.itemBox.style.height = box;
        KartItems items = racer.Items;
        ItemType show = items == null ? ItemType.None : items.IsRolling ? items.RouletteDisplay : items.Held;
        h.itemTile.style.marginLeft = h.itemTile.style.marginRight = 10 * s;
        h.itemTile.style.marginTop = 9 * s;
        h.itemTile.style.marginBottom = 16 * s;
        h.itemTile.style.backgroundColor = show == ItemType.None ? new Color(0.2f, 0.22f, 0.28f) : ItemTable.Tint(show);
        h.itemTile.style.borderTopLeftRadius = h.itemTile.style.borderTopRightRadius = h.itemTile.style.borderBottomLeftRadius = h.itemTile.style.borderBottomRightRadius = 10;
        h.item.text = show == ItemType.None ? "ITEM" : show == ItemType.Banana ? "BANANA" : ItemTable.Label(show);
        h.item.style.fontSize = (show == ItemType.None ? 20 : 22) * s;
        h.item.style.color = show == ItemType.None ? new Color(1, 1, 1, 0.35f) : new Color(0.08f, 0.08f, 0.12f);
        float roll = items != null && items.IsRolling ? 1f + Mathf.Abs(Mathf.Sin(Time.time * 20f)) * 0.08f : 1f;
        h.itemBox.style.scale = new Scale(new Vector3(roll, roll, 1f));

        if (lap != h.lastLap && rm.Phase == RacePhase.Racing && !racer.Lap.Finished)
        {
            if (h.lastLap > 0 || lap > 1)
            {
                bool final = lap == rm.Laps;
                h.banner.text = final ? "FINAL LAP!" : $"LAP {lap}";
                h.banner.style.color = final ? new Color(1f, 0.35f, 0.3f) : Gold;
                h.bannerStart = Time.time;
                h.bannerUntil = Time.time + (final ? 2.5f : 1.5f);
            }
            h.lastLap = lap;
        }
        bool bannerOn = Time.time < h.bannerUntil;
        Show(h.banner, bannerOn);
        if (bannerOn)
        {
            float bt = Time.time - h.bannerStart;
            h.banner.style.fontSize = 100 * s;
            h.banner.style.top = new Length(26, LengthUnit.Percent);
            h.banner.style.translate = new Translate(new Length(Mathf.Lerp(900f, 0f, Mathf.Clamp01(bt * 4f)) * s, LengthUnit.Pixel), 0);
            h.banner.style.opacity = Mathf.Clamp01((h.bannerUntil - Time.time) * 3f);
        }

        h.speed.text = $"{Mathf.Abs(racer.Kart.ForwardSpeed) * 3.6f:0} KM/H" + (racer.Kart.IsBoosting ? "  TURBO!" : racer.Kart.MiniTurboReady ? "  DRIFT *" : "");
        h.speed.style.fontSize = 30 * s;
        h.speed.style.bottom = pad;

        // Wrong way: facing against the track direction while moving.
        RaceTrack track = rm.Track;
        bool wrong = false;
        if (track != null && rm.Phase == RacePhase.Racing && !racer.Lap.Finished)
        {
            float sAlong = track.Project(racer.Kart.transform.position);
            float dot = Vector3.Dot(racer.Kart.transform.forward, track.TangentAt(sAlong));
            bool backwards = dot < -0.3f && Mathf.Abs(racer.Kart.ForwardSpeed) > 3f;
            h.wrongTimer = backwards ? h.wrongTimer + Time.deltaTime : 0f;
            wrong = h.wrongTimer > 0.8f;
        }
        Show(h.wrongWay, wrong && (int)(Time.time * 3f) % 2 == 0);
        h.wrongWay.style.fontSize = 80 * s;
        h.wrongWay.style.top = new Length(42, LengthUnit.Percent);

        bool finished = racer.Lap.Finished;
        Show(h.centre, finished);
        if (finished)
        {
            h.centre.text = $"FINISHED {RaceManager.Ordinal(racer.FinishOrder + 1).ToUpperInvariant()}!";
            h.centre.style.fontSize = 90 * s;
            h.centre.style.top = new Length(32, LengthUnit.Percent);
            h.centre.style.color = racer.FinishOrder == 0 ? Gold : Color.white;
        }
    }

    void UpdateMinimap()
    {
        if (rm.Track != null && minimap.userData == null)
        {
            minimap.SetTrack(rm.Track.Waypoints);
            minimap.userData = rm.Track;
        }
        minimap.SetKarts(rm.Standings.Select(r =>
            (r.Kart.transform.position, r.Human != null ? r.Human.Color : new Color(0.62f, 0.64f, 0.68f), r.Human != null)));
        if (sidePanel.style.display == DisplayStyle.None) return;
        var list = rm.Standings;
        for (int i = 0; i < standingsLines.Count; i++)
        {
            Label l = standingsLines[i];
            if (i >= list.Count) { l.text = ""; continue; }
            var r = list[i];
            l.text = $"{r.Position}. {(r.Human != null ? r.Human.Label : "CPU")} {rm.Roster[r.Character].displayName.ToUpperInvariant()}";
            l.style.color = r.Human != null ? r.Human.Color : new Color(0.85f, 0.86f, 0.9f);
        }
    }

    void FillResults()
    {
        resultsTable.Clear();
        var list = rm.Standings;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            Abs(row, 0, i * 88, 740, 80);
            row.style.backgroundColor = i % 2 == 0 ? new Color(1, 1, 1, 0.06f) : new Color(0, 0, 0, 0.12f);
            row.style.borderTopLeftRadius = row.style.borderTopRightRadius = row.style.borderBottomLeftRadius = row.style.borderBottomRightRadius = 8;
            Color pc = r.Position == 1 ? Gold : r.Position == 2 ? Silver : r.Position == 3 ? Bronze : Color.white;
            var pos = Text(RaceManager.Ordinal(r.Position), 44, pc, bodyFont, 3);
            Abs(pos, 14, 14, 110, 60);
            row.Add(pos);
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            Abs(swatch, 130, 22, 14, 38);
            swatch.style.backgroundColor = r.Human != null ? r.Human.Color : new Color(0.6f, 0.62f, 0.66f);
            row.Add(swatch);
            var name = Text(r.Name, 30, r.Human != null ? r.Human.Color : Color.white, bodyFont, 3);
            Abs(name, 158, 20, 330, 44);
            row.Add(name);
            string time = r.Lap.Finished ? Format(r.Lap.FinishTime) : "--:--.--";
            var t = Text(time, 30, Color.white, bodyFont, 3);
            Abs(t, 500, 8, 230, 40);
            t.style.unityTextAlign = TextAnchor.UpperRight;
            row.Add(t);
            var best = Text(r.Lap.BestLap > 0f ? "best " + Format(r.Lap.BestLap) : "", 20, new Color(1, 1, 1, 0.6f), narrowFont, 2);
            Abs(best, 500, 48, 230, 26);
            best.style.unityTextAlign = TextAnchor.UpperRight;
            row.Add(best);
            resultsTable.Add(row);
        }
        var banner = results.Q<Label>("podium-banner");
        var winner = list.FirstOrDefault();
        banner.text = winner != null ? $"WINNER\n{winner.Name.ToUpperInvariant()}" : "";
        banner.style.color = Gold;
    }

    static string Format(float t) => $"{(int)(t / 60f):00}:{t % 60f:00.00}";
}
