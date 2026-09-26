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
    // Track picker, CPU setting, pause menu, quit prompt, intro and flyover.
    VisualElement trackPanel, pauseLayer, pausePanel, quitLayer, introLayer, introFlash, introKart, flyoverLayer, flyCard;
    Image trackImage;
    Label resultsTitle, langLabel, trackName, trackInfo, trackTwist, trackIndex, cpuLabel, pauseTitle, pauseBy, pauseHint, introHint, flyName, flyLaps, flyTwist;
    readonly List<Image> trackStars = new List<Image>();
    readonly List<Label> pauseRows = new List<Label>();
    readonly List<Label> introLetters = new List<Label>();
    readonly List<VisualElement> sparks = new List<VisualElement>();
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
    public bool FinalLapBannerShown(int i) => huds[i] != null && huds[i].banner.text == Loc.T("race.final_lap");
    public int MinimapDots => minimap?.DotCount ?? 0;
    public int ResultRows => resultsTable?.childCount ?? 0;
    public bool LobbyVisible => lobby != null && lobby.style.display == DisplayStyle.Flex;
    public bool PauseMenuVisible => pauseLayer != null && pauseLayer.style.display == DisplayStyle.Flex;
    public bool IntroVisible => introLayer != null && introLayer.style.display == DisplayStyle.Flex;
    public bool FlyoverCardVisible => flyoverLayer != null && flyoverLayer.style.display == DisplayStyle.Flex;
    public string TrackCardText => trackName?.text;
    public string CpuText => cpuLabel?.text;
    public string LobbyStatusText => readyStatus?.text;
    public string PauseTitleText => pauseTitle?.text;
    public string ResultsTitleText => resultsTitle?.text;
    public string LanguageText => langLabel?.text;

    Label glyphSample;
    /// <summary>Test hook: a big sample line with every Spanish glyph, in the UI fonts.</summary>
    public void ShowGlyphSample(bool on)
    {
        if (glyphSample == null)
        {
            glyphSample = Text("ÁÉÍÓÚ ÑÜ áéíóú ñü ¡¿ º ° 1.º 2.º\n¡ÚLTIMA VUELTA! ¿SALIR? AÑO PINGÜINO", 64, Color.white, titleFont, 4);
            glyphSample.style.position = Position.Absolute;
            glyphSample.style.left = 40; glyphSample.style.right = 40; glyphSample.style.top = 380;
            glyphSample.style.backgroundColor = new Color(0.05f, 0.06f, 0.1f, 0.95f);
            glyphSample.style.unityTextAlign = TextAnchor.MiddleCenter;
            root.Add(glyphSample);
        }
        glyphSample.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
    }
    public string PauseSelectedText => pauseRows.FirstOrDefault(r => r.style.color == Gold)?.text;
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
        Loc.Init();
        Build();
        Loc.Changed += Rebuild;
    }

    void OnDisable() => Loc.Changed -= Rebuild;

    /// <summary>Language switched: every label is rebuilt in the new language.</summary>
    void Rebuild()
    {
        built = false;
        standingsLines.Clear();
        pauseRows.Clear();
        glyphSample = null;
        introLetters.Clear();
        sparks.Clear();
        trackStars.Clear();
        Build();
        lastPhase = (RacePhase)(-1);
    }

    /// <summary>"P1 Name" / "CPU Name" in the current language (character names are proper nouns).</summary>
    string DisplayName(RaceManager.Racer r) =>
        $"{(r.Human != null ? r.Human.Label : Loc.T("race.cpu"))} {rm.Roster[r.Character].displayName}";

    static string TrackTwist(TrackDefinition def)
    {
        string key = "twist." + def.id;
        string t = Loc.T(key);
        return t == key ? def.twist : t;
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
        flyoverLayer = Layer("flyover");
        BuildFlyover();
        introLayer = Layer("intro");
        BuildIntro();
        quitLayer = Layer("quit-confirm");
        BuildQuitConfirm();
        pauseLayer = Layer("pause");
        BuildPause();
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
        Abs(join, 60, 200, 560, 480);
        join.style.alignItems = Align.Center;
        join.style.paddingTop = 16;
        lobby.Add(join);
        var joinTitle = Text(Loc.T("lobby.scan"), 38, new Color(0.15f, 0.17f, 0.24f), bodyFont, 0);
        join.Add(joinTitle);
        qrImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
        qrImage.style.width = 290; qrImage.style.height = 290; qrImage.style.marginTop = 8;
        join.Add(qrImage);
        urlLabel = Text("", 26, new Color(0.1f, 0.45f, 0.85f), bodyFont, 0);
        urlLabel.style.marginTop = 8;
        join.Add(urlLabel);
        otherUrls = Text("", 18, new Color(0.3f, 0.32f, 0.4f), narrowFont, 0);
        otherUrls.style.whiteSpace = WhiteSpace.Normal;
        otherUrls.style.marginTop = 6; otherUrls.style.width = 500; otherUrls.style.unityTextAlign = TextAnchor.UpperCenter;
        join.Add(otherUrls);
        serverError = Text("", 22, new Color(0.85f, 0.2f, 0.2f), bodyFont, 0);
        serverError.style.whiteSpace = WhiteSpace.Normal; serverError.style.width = 500;
        join.Add(serverError);
        var wifi = Text(Loc.T("lobby.wifi"), 15, new Color(0.3f, 0.32f, 0.4f), narrowFont, 0);
        wifi.style.marginTop = 6;
        wifi.style.width = 520;
        wifi.style.whiteSpace = WhiteSpace.Normal;
        wifi.style.unityTextAlign = TextAnchor.UpperCenter;
        join.Add(wifi);

        BuildTrackPanel();

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
        var hints = Text(Loc.T("lobby.keys1") + "\n" + Loc.T("lobby.keys2"),
            18, new Color(1f, 1f, 1f, 0.8f), narrowFont, 2);
        hints.style.position = Position.Absolute;
        hints.style.left = 0; hints.style.right = 0; hints.style.top = 1005;
        hints.style.unityTextAlign = TextAnchor.MiddleCenter;
        lobby.Add(hints);
    }

    /// <summary>Lobby track card (picture, name, length, laps, difficulty) and the CPU racers setting.</summary>
    void BuildTrackPanel()
    {
        trackPanel = Panel(panelTexture, new Color(0.2f, 0.23f, 0.3f, 0.97f));
        Abs(trackPanel, 60, 695, 560, 240);
        lobby.Add(trackPanel);
        var header = Text(Loc.T("lobby.track"), 22, Gold, bodyFont, 2);
        Abs(header, 18, 8, 120, 28);
        trackPanel.Add(header);
        trackIndex = Text("", 16, new Color(1, 1, 1, 0.7f), narrowFont, 1);
        Abs(trackIndex, 150, 12, 392, 22);
        trackIndex.style.unityTextAlign = TextAnchor.UpperRight;
        trackPanel.Add(trackIndex);

        trackImage = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
        Abs(trackImage, 16, 44, 220, 138);
        trackImage.style.backgroundColor = new Color(0.1f, 0.12f, 0.16f);
        trackImage.style.borderTopLeftRadius = trackImage.style.borderTopRightRadius = trackImage.style.borderBottomLeftRadius = trackImage.style.borderBottomRightRadius = 8;
        trackPanel.Add(trackImage);

        // One line each (no wrapping): name, length + laps, stars, then the twist in a clipped box.
        trackName = Text("", 24, Color.white, bodyFont, 2);
        Abs(trackName, 250, 44, 296, 30);
        trackName.style.whiteSpace = WhiteSpace.NoWrap;
        trackName.style.overflow = Overflow.Hidden;
        trackPanel.Add(trackName);
        trackInfo = Text("", 17, new Color(1, 1, 1, 0.85f), narrowFont, 1);
        Abs(trackInfo, 250, 80, 296, 22);
        trackInfo.style.whiteSpace = WhiteSpace.NoWrap;
        trackPanel.Add(trackInfo);
        for (int i = 0; i < 3; i++)
        {
            var star = new Image { image = starTexture, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            Abs(star, 250 + i * 32, 106, 28, 28);
            trackPanel.Add(star);
            trackStars.Add(star);
        }
        trackTwist = Text("", 13, new Color(1, 1, 1, 0.7f), narrowFont, 0);
        Abs(trackTwist, 250, 140, 296, 46);
        trackTwist.style.whiteSpace = WhiteSpace.Normal;
        trackTwist.style.overflow = Overflow.Hidden;
        trackPanel.Add(trackTwist);

        cpuLabel = Text("", 17, Color.white, bodyFont, 2);
        Abs(cpuLabel, 16, 190, 528, 22);
        trackPanel.Add(cpuLabel);
        langLabel = Text("", 17, new Color(0.75f, 0.9f, 1f), bodyFont, 2);
        Abs(langLabel, 16, 213, 528, 22);
        trackPanel.Add(langLabel);
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
        string[] labels = { Loc.T("stat.speed"), Loc.T("stat.accel"), Loc.T("stat.handling") };
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
        c.readyLabel = Text(Loc.T("lobby.ready"), 30, Color.white, bodyFont, 2);
        c.ready.Add(c.readyLabel);
        c.root.Add(c.ready);

        c.empty = Text(Loc.T("lobby.empty"), 26, new Color(1, 1, 1, 0.5f), bodyFont, 2);
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
        h.wrongWay = Text(Loc.T("race.wrong_way"), 90, new Color(1f, 0.25f, 0.2f), titleFont, 3);
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
        var title = resultsTitle = Text(Loc.T("results.title"), 90, Gold, titleFont, 6);
        Abs(title, 0, 20, 800, 100);
        title.style.unityTextAlign = TextAnchor.MiddleCenter;
        panel.Add(title);
        resultsTable = new VisualElement { name = "results-table", pickingMode = PickingMode.Ignore };
        Abs(resultsTable, 30, 140, 740, 540);
        panel.Add(resultsTable);
        resultsPrompt = Text(Loc.T("results.prompt"), 18, new Color(1, 1, 1, 0.85f), narrowFont, 1);
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

    // ---- quit prompt, pause menu, intro, flyover --------------------------------------------------

    void BuildQuitConfirm()
    {
        quitLayer.style.backgroundColor = new Color(0.02f, 0.03f, 0.06f, 0.7f);
        var panel = Panel(panelTexture, new Color(0.16f, 0.18f, 0.25f, 0.98f));
        Abs(panel, 560, 380, 800, 300);
        quitLayer.Add(panel);
        var t = Text(Loc.T("quit.title"), 80, Gold, titleFont, 5);
        Abs(t, 0, 40, 800, 100);
        t.style.unityTextAlign = TextAnchor.MiddleCenter;
        panel.Add(t);
        var h = Text(Loc.T("quit.lobby_hint"), 24, Color.white, bodyFont, 2);
        Abs(h, 0, 190, 800, 40);
        h.style.unityTextAlign = TextAnchor.MiddleCenter;
        panel.Add(h);
        Show(quitLayer, false);
    }

    void BuildPause()
    {
        pauseLayer.style.backgroundColor = new Color(0.02f, 0.03f, 0.06f, 0.62f);
        pausePanel = Panel(panelTexture, new Color(0.15f, 0.17f, 0.24f, 0.97f));
        Abs(pausePanel, 610, 190, 700, 700);
        pauseLayer.Add(pausePanel);
        pauseTitle = Text(Loc.T("pause.title"), 110, Gold, titleFont, 6);
        Abs(pauseTitle, 0, 26, 700, 130);
        pauseTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
        pausePanel.Add(pauseTitle);
        pauseBy = Text("", 26, new Color(1, 1, 1, 0.8f), bodyFont, 2);
        Abs(pauseBy, 0, 150, 700, 34);
        pauseBy.style.unityTextAlign = TextAnchor.MiddleCenter;
        pausePanel.Add(pauseBy);
        for (int i = 0; i < RaceManager.PauseItems.Length; i++)
        {
            var row = Text("", 46, Color.white, bodyFont, 3);
            Abs(row, 60, 220 + i * 92, 580, 76);
            row.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.style.borderTopLeftRadius = row.style.borderTopRightRadius = row.style.borderBottomLeftRadius = row.style.borderBottomRightRadius = 14;
            pausePanel.Add(row);
            pauseRows.Add(row);
        }
        pauseHint = Text("", 18, new Color(1, 1, 1, 0.75f), narrowFont, 1);
        Abs(pauseHint, 20, 620, 660, 60);
        pauseHint.style.whiteSpace = WhiteSpace.Normal;
        pauseHint.style.unityTextAlign = TextAnchor.MiddleCenter;
        pausePanel.Add(pauseHint);
        Show(pauseLayer, false);
    }

    void UpdatePause()
    {
        bool on = rm.Paused;
        Show(pauseLayer, on);
        if (!on) return;
        bool confirm = rm.PauseQuitConfirm;
        pauseTitle.text = confirm ? Loc.T("quit.title") : Loc.T("pause.title");
        pauseBy.text = confirm ? Loc.T("pause.closes") : Loc.T("pause.by", rm.PausedBy == "HOST" ? Loc.T("pause.host") : rm.PausedBy);
        string[] items = confirm ? new[] { Loc.T("pause.no"), Loc.T("pause.yes") } : RaceManager.PauseItems.Select(k => Loc.T(k)).ToArray();
        int selected = confirm ? rm.ConfirmSelection : rm.PauseSelection;
        float pulse = 1f + Mathf.Sin(Time.unscaledTime * 6f) * 0.03f; // animates while the game is frozen
        for (int i = 0; i < pauseRows.Count; i++)
        {
            Label row = pauseRows[i];
            bool used = i < items.Length;
            Show(row, used);
            if (!used) continue;
            bool sel = i == selected;
            row.text = sel ? $"> {items[i]} <" : items[i];
            row.style.color = sel ? Gold : Color.white;
            row.style.backgroundColor = sel ? new Color(1f, 1f, 1f, 0.1f) : new Color(0, 0, 0, 0);
            row.style.scale = new Scale(sel ? new Vector3(pulse, pulse, 1f) : Vector3.one);
        }
        pauseHint.text = Loc.T("pause.hint", Loc.T(confirm ? "pause.hint_back" : "pause.hint_resume"));
    }

    static readonly string IntroWord = "KART PARTY";

    void BuildIntro()
    {
        introLayer.style.backgroundColor = new Color(0.02f, 0.03f, 0.08f, 0.35f);
        var row = new VisualElement { pickingMode = PickingMode.Ignore };
        row.style.position = Position.Absolute;
        row.style.left = 0; row.style.right = 0; row.style.top = 300; row.style.height = 240;
        row.style.flexDirection = FlexDirection.Row;
        row.style.justifyContent = Justify.Center;
        row.style.alignItems = Align.FlexEnd;
        introLayer.Add(row);
        foreach (char ch in IntroWord)
        {
            var l = Text(ch.ToString(), 190, Gold, titleFont, 8);
            l.style.width = ch == ' ' ? 70 : 128;
            l.style.unityTextAlign = TextAnchor.LowerCenter;
            l.style.transformOrigin = new TransformOrigin(new Length(50, LengthUnit.Percent), new Length(100, LengthUnit.Percent));
            row.Add(l);
            introLetters.Add(l);
        }

        // A little kart (body, cockpit, wheels) in the player colours that zooms under the logo.
        introKart = new VisualElement { pickingMode = PickingMode.Ignore };
        Abs(introKart, -300, 560, 220, 90);
        var body = new VisualElement { pickingMode = PickingMode.Ignore };
        Abs(body, 10, 30, 200, 40);
        body.style.backgroundColor = RaceManager.PlayerColors[0];
        Round(body, 16);
        introKart.Add(body);
        var cockpit = new VisualElement { pickingMode = PickingMode.Ignore };
        Abs(cockpit, 80, 6, 70, 34);
        cockpit.style.backgroundColor = new Color(0.95f, 0.95f, 1f);
        Round(cockpit, 14);
        introKart.Add(cockpit);
        var helmet = new VisualElement { pickingMode = PickingMode.Ignore };
        Abs(helmet, 96, -14, 34, 34);
        helmet.style.backgroundColor = Gold;
        Round(helmet, 17);
        introKart.Add(helmet);
        foreach (float x in new[] { 26f, 150f })
        {
            var wheel = new VisualElement { pickingMode = PickingMode.Ignore };
            Abs(wheel, x, 52, 40, 40);
            wheel.style.backgroundColor = new Color(0.08f, 0.08f, 0.1f);
            Round(wheel, 20);
            introKart.Add(wheel);
        }
        introLayer.Add(introKart);
        for (int i = 0; i < 24; i++)
        {
            var s = new VisualElement { pickingMode = PickingMode.Ignore };
            Abs(s, -100, 0, 10, 10);
            Round(s, 5);
            introLayer.Add(s);
            sparks.Add(s);
        }

        introHint = Text(Loc.T("intro.press"), 30, Color.white, bodyFont, 3);
        introHint.style.position = Position.Absolute;
        introHint.style.left = 0; introHint.style.right = 0; introHint.style.top = 760;
        introHint.style.unityTextAlign = TextAnchor.MiddleCenter;
        introLayer.Add(introHint);

        introFlash = new VisualElement { pickingMode = PickingMode.Ignore };
        introFlash.style.position = Position.Absolute;
        introFlash.style.left = 0; introFlash.style.right = 0; introFlash.style.top = 0; introFlash.style.bottom = 0;
        introFlash.style.backgroundColor = Color.white;
        introLayer.Add(introFlash);
        Show(introLayer, false);
    }

    static void Round(VisualElement v, float r) =>
        v.style.borderTopLeftRadius = v.style.borderTopRightRadius = v.style.borderBottomLeftRadius = v.style.borderBottomRightRadius = r;

    /// <summary>Unscaled-time logo animation: letters drop in with squash and stretch, a flash as the
    /// last one lands, a kart zooms across leaving sparks, then everything fades into the lobby.</summary>
    void UpdateIntro()
    {
        bool on = rm.IntroActive;
        Show(introLayer, on);
        if (!on) return;
        float t = rm.IntroTime, total = rm.IntroSeconds;
        for (int i = 0; i < introLetters.Count; i++)
        {
            float lt = t - 0.15f - i * 0.07f;
            Label l = introLetters[i];
            if (lt < 0f)
            {
                l.style.opacity = 0f;
                continue;
            }
            l.style.opacity = 1f;
            // Fall (0..0.28 s), then a damped bounce.
            float y, sx = 1f, sy = 1f;
            if (lt < 0.28f)
            {
                float f = lt / 0.28f;
                y = -520f * (1f - f * f);
                sy = 1.25f; sx = 0.85f; // stretched while falling
            }
            else
            {
                float b = lt - 0.28f;
                y = -Mathf.Abs(Mathf.Sin(b * 9f)) * 60f * Mathf.Exp(-b * 5f);
                float squash = Mathf.Exp(-b * 9f);
                sy = 1f - 0.35f * squash;
                sx = 1f + 0.3f * squash;
            }
            l.style.translate = new Translate(0, y);
            l.style.scale = new Scale(new Vector3(sx, sy, 1f));
            l.style.color = Color.Lerp(Color.white, Gold, Mathf.Clamp01(lt * 3f));
        }
        float landed = 0.15f + (introLetters.Count - 1) * 0.07f + 0.28f;
        introFlash.style.opacity = Mathf.Clamp01(0.7f - Mathf.Abs(t - landed) * 4f);

        // Kart zooms across (1.3 s .. 2.4 s) with sparks behind the rear wheel.
        float kt = Mathf.InverseLerp(1.3f, 2.4f, t);
        float kx = Mathf.Lerp(-320f, 2100f, kt * kt * (3f - 2f * kt));
        introKart.style.left = kx;
        introKart.style.rotate = new Rotate(Mathf.Sin(t * 30f) * 2f);
        for (int i = 0; i < sparks.Count; i++)
        {
            float age = (t * 40f + i * 1.7f) % 12f / 12f; // 0..1 lifetime, staggered
            float born = kx - age * 420f;
            bool alive = kt > 0f && kt < 1f && born > -100f;
            var s = sparks[i];
            s.style.display = alive ? DisplayStyle.Flex : DisplayStyle.None;
            if (!alive) continue;
            s.style.left = born + 20f;
            s.style.top = 640f - age * 60f * ((i % 3) + 1) * 0.5f + (i % 2 == 0 ? 6f : -4f);
            s.style.opacity = 1f - age;
            s.style.backgroundColor = i % 3 == 0 ? new Color(1f, 0.95f, 0.5f) : i % 3 == 1 ? new Color(1f, 0.6f, 0.15f) : new Color(0.5f, 0.85f, 1f);
        }
        introHint.style.opacity = t > 1f ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f) : 0f;
        introLayer.style.opacity = Mathf.Clamp01((total - t) / 0.4f);
    }

    void BuildFlyover()
    {
        flyCard = Panel(panelTexture, new Color(0.14f, 0.16f, 0.22f, 0.9f));
        Abs(flyCard, 60, 820, 900, 190);
        flyoverLayer.Add(flyCard);
        flyName = Text("", 70, Gold, titleFont, 5);
        Abs(flyName, 30, 14, 860, 90);
        flyCard.Add(flyName);
        flyLaps = Text("", 30, Color.white, bodyFont, 2);
        Abs(flyLaps, 34, 102, 400, 40);
        flyCard.Add(flyLaps);
        flyTwist = Text("", 18, new Color(1, 1, 1, 0.75f), narrowFont, 1);
        Abs(flyTwist, 34, 142, 840, 40);
        flyTwist.style.whiteSpace = WhiteSpace.Normal;
        flyCard.Add(flyTwist);
        var skip = Text(Loc.T("fly.skip"), 18, new Color(1, 1, 1, 0.7f), narrowFont, 2);
        Abs(skip, 1400, 40, 480, 30);
        skip.style.unityTextAlign = TextAnchor.UpperRight;
        flyoverLayer.Add(skip);
        Show(flyoverLayer, false);
    }

    void UpdateFlyover()
    {
        bool on = rm.Phase == RacePhase.Countdown && rm.FlyoverActive;
        Show(flyoverLayer, on);
        if (!on || rm.ActiveTrack == null) return;
        TrackDefinition def = rm.ActiveTrack;
        flyName.text = def.displayName.ToUpperInvariant();
        flyName.style.color = def.accent;
        flyLaps.text = Loc.T("fly.laps", rm.Laps, def.Length.ToString("F0"));
        flyTwist.text = TrackTwist(def);
        float t = Time.time - rm.PhaseStartTime;
        flyCard.style.translate = new Translate(Mathf.Lerp(-1000f, 0f, Mathf.Clamp01(t * 3f)), 0);
    }

    void UpdateTrackPanel()
    {
        int n = rm.Tracks.Count;
        int sel = rm.SelectedTrack;
        bool random = rm.RandomSelected;
        TrackDefinition def = random ? null : rm.Tracks[sel];
        trackIndex.text = Loc.T("lobby.track_keys", sel + 1, n + 1);
        trackName.text = random ? Loc.T("lobby.random") : def.displayName.ToUpperInvariant();
        trackName.style.fontSize = trackName.text.Length > 13 ? 19 : 24; // long names stay on one line
        trackName.style.color = random ? Color.white : def.accent;
        trackInfo.text = random ? Loc.T("lobby.random_info") : Loc.T("lobby.track_info", def.Length.ToString("F0"), def.laps);
        trackTwist.text = random ? Loc.T("lobby.random_twist") : TrackTwist(def);
        trackImage.image = def != null ? def.Thumbnail : null;
        for (int i = 0; i < trackStars.Count; i++)
        {
            Show(trackStars[i], !random);
            trackStars[i].tintColor = def != null && i < def.difficulty ? Gold : new Color(1, 1, 1, 0.18f);
        }
        cpuLabel.text = Loc.T("lobby.cpu", rm.CpuLabel);
        langLabel.text = Loc.T("lobby.lang");
    }

    // ================================================================================================
    // Per-frame update
    // ================================================================================================

    void Update()
    {
        if (!built || rm == null) return;
        RacePhase phase = rm.Phase;
        bool racing = phase == RacePhase.Countdown || phase == RacePhase.Racing || phase == RacePhase.Results;
        bool flyover = phase == RacePhase.Countdown && rm.FlyoverActive;
        Show(lobby, phase == RacePhase.Lobby && !rm.IntroActive);
        Show(hudLayer, racing && phase != RacePhase.Results && !flyover);
        Show(results, phase == RacePhase.Results);
        Show(countdownLayer, (phase == RacePhase.Countdown && !flyover) || (phase == RacePhase.Racing && Time.time - rm.PhaseStartTime < 1f));
        Show(quitLayer, phase == RacePhase.Lobby && rm.LobbyQuitConfirm);
        UpdatePause();
        UpdateIntro();
        UpdateFlyover();
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
        UpdateTrackPanel();
        qrImage.image = rm.QrTexture;
        urlLabel.text = rm.ServerRunning ? rm.Url : "";
        otherUrls.text = rm.OtherUrls.Count > 0 ? Loc.T("lobby.other", string.Join("  ", rm.OtherUrls)) : "";
        serverError.text = rm.ServerRunning ? "" : Loc.T("lobby.server_off", rm.ServerError);

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
            c.who.text = Loc.T(p.IsKeyboard ? "lobby.keyboard" : p.Connected ? "lobby.phone" : "lobby.phone_lost");
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
        readyStatus.text = rm.Players.Count == 0 ? Loc.T("lobby.waiting") : Loc.T("lobby.ready_status", ready, rm.Players.Count);
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
            text = Loc.T("race.go");
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
        h.suffix.text = Loc.OrdinalSuffix(pos).ToUpperInvariant();
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
        h.lap.text = Loc.T("race.lap", lap, rm.Laps);
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
        h.item.text = show == ItemType.None ? Loc.T("race.item") : ItemTable.Label(show);
        h.item.style.fontSize = (show == ItemType.None ? 20 : 22) * s;
        h.item.style.color = show == ItemType.None ? new Color(1, 1, 1, 0.35f) : new Color(0.08f, 0.08f, 0.12f);
        float roll = items != null && items.IsRolling ? 1f + Mathf.Abs(Mathf.Sin(Time.time * 20f)) * 0.08f : 1f;
        h.itemBox.style.scale = new Scale(new Vector3(roll, roll, 1f));

        if (lap != h.lastLap && rm.Phase == RacePhase.Racing && !racer.Lap.Finished)
        {
            if (h.lastLap > 0 || lap > 1)
            {
                bool final = lap == rm.Laps;
                h.banner.text = final ? Loc.T("race.final_lap") : Loc.T("race.lap_banner", lap);
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

        h.speed.text = Loc.T("race.speed", (Mathf.Abs(racer.Kart.ForwardSpeed) * 3.6f).ToString("0")) + (racer.Kart.IsBoosting ? "  " + Loc.T("race.turbo") : racer.Kart.MiniTurboReady ? "  " + Loc.T("race.drift") : "");
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
            h.centre.text = Loc.T("race.finished", Loc.Ordinal(racer.FinishOrder + 1).ToUpperInvariant());
            h.centre.style.fontSize = 90 * s;
            h.centre.style.top = new Length(32, LengthUnit.Percent);
            h.centre.style.color = racer.FinishOrder == 0 ? Gold : Color.white;
        }
    }

    void UpdateMinimap()
    {
        if (rm.Track != null && minimap.userData != (object)rm.Track)
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
            l.text = $"{r.Position}. {(r.Human != null ? r.Human.Label : Loc.T("race.cpu"))} {rm.Roster[r.Character].displayName.ToUpperInvariant()}";
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
            var pos = Text(Loc.Ordinal(r.Position), 44, pc, bodyFont, 3);
            Abs(pos, 14, 14, 110, 60);
            row.Add(pos);
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            Abs(swatch, 130, 22, 14, 38);
            swatch.style.backgroundColor = r.Human != null ? r.Human.Color : new Color(0.6f, 0.62f, 0.66f);
            row.Add(swatch);
            string shown = DisplayName(r).ToUpperInvariant();
            var name = Text(shown, shown.Length > 16 ? 22 : 30, r.Human != null ? r.Human.Color : Color.white, bodyFont, 3);
            Abs(name, 158, shown.Length > 16 ? 26 : 20, 330, 44);
            name.style.overflow = Overflow.Hidden;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            row.Add(name);
            string time = r.Lap.Finished ? Format(r.Lap.FinishTime) : "--:--.--";
            var t = Text(time, 30, Color.white, bodyFont, 3);
            Abs(t, 500, 8, 230, 40);
            t.style.unityTextAlign = TextAnchor.UpperRight;
            row.Add(t);
            var best = Text(r.Lap.BestLap > 0f ? Loc.T("results.best", Format(r.Lap.BestLap)) : "", 20, new Color(1, 1, 1, 0.6f), narrowFont, 2);
            Abs(best, 500, 48, 230, 26);
            best.style.unityTextAlign = TextAnchor.UpperRight;
            row.Add(best);
            resultsTable.Add(row);
        }
        var banner = results.Q<Label>("podium-banner");
        var winner = list.FirstOrDefault();
        banner.text = winner == null ? "" : Loc.T(list.Count == 1 ? "results.time_trial" : "results.winner", DisplayName(winner).ToUpperInvariant());
        banner.style.color = Gold;
    }

    static string Format(float t) => $"{(int)(t / 60f):00}:{t % 60f:00.00}";
}
