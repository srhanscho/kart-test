using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = System.Random;

/// <summary>
/// Scene assembly and the four circuits. Every track is generated into the one race scene under
/// its own root (Track_&lt;id&gt;) with a TrackDefinition; only the active track is enabled at runtime,
/// so phones, players and the RaceManager never reload.
/// Layout tokens: two-letter piece key + flags: R/L turn right/left, U/D climb up/down,
/// +/- S-curve shift right/left (e.g. "CLR", "HLU", "CV+", "RCRU").
/// </summary>
public static partial class RaceSceneBuilder
{
    enum Kit { Toy, Tile }
    enum Theme { Night, Sunset, Day, Neon }

    class TrackSpec
    {
        public string Id, Name, Twist;
        public int Difficulty = 1, Laps = 3;
        public Kit Kit;
        public string Folder;
        public (string key, string file)[] Pieces;
        /// <summary>Extra models placed with the same transform as a piece (tile-kit corner walls/curbs).</summary>
        public (string key, string file)[] Companions = new (string, string)[0];
        public string Layout;
        public int FinishAfter = 2;
        public float Width;               // metres across one kit unit (road incl. edges)
        public float DriveFraction = 1f;  // share of Width the AI may use
        public Theme Theme;
        public Color Accent;
        public float[] ItemRows = { 0.25f, 0.52f, 0.8f };
    }

    static readonly TrackSpec[] Specs =
    {
        new TrackSpec
        {
            Id = "night", Name = "Night Circuit", Difficulty = 2, Theme = Theme.Night, Accent = new Color(0.45f, 0.65f, 1f),
            Twist = "Floodlit toy circuit: hairpin, S-bends, a hill and a sliding block",
            Kit = Kit.Toy, Folder = ToyKit, Width = 12f,
            Pieces = new[]
            {
                ("ST", "track-striped-wide-straight"), ("BU", "track-striped-wide-straight-bump-up"),
                ("HL", "track-striped-wide-straight-hill-complete"), ("CS", "track-striped-wide-corner-small"),
                ("CL", "track-striped-wide-corner-large"), ("CV", "track-striped-wide-curve"),
            },
            Layout = "ST ST ST ST ST CLR ST BU ST CSR CSR ST CSL CV+ CV- CSL ST HLU ST HLD CLR ST ST CLR ST ST ST ST BU ST CLR ST",
        },
        new TrackSpec
        {
            Id = "sunset", Name = "Sunset Grand Prix", Difficulty = 1, Theme = Theme.Sunset, Accent = new Color(1f, 0.6f, 0.3f),
            Twist = "Wide and fast: long straights, sweepers, a hairpin to drift and a chicane to brake for",
            Kit = Kit.Tile, Folder = RacingKit, Width = 16f, DriveFraction = 0.8f,
            Pieces = new[]
            {
                ("ST", "roadStraight"), ("SL", "roadStraightLong"), ("CS", "roadCornerSmall"),
                ("CL", "roadCornerLarge"), ("CG", "roadCornerLarger"), ("CV", "roadCurved"),
            },
            Companions = new[]
            {
                ("CS", "roadCornerSmallWall"), ("CL", "roadCornerLargeWall"), ("CL", "roadCornerLargeBorderInner"),
                ("CG", "roadCornerLargerWall"), ("CG", "roadCornerLargerBorderInner"),
            },
            Layout = "SL SL SL SL SL SL CGR SL SL CGL SL CGR SL SL CLR CLR ST CLL SL SL SL SL CV+ CV- SL CSR CSL ST CGR SL SL SL ST ST CLR",
            ItemRows = new[] { 0.3f, 0.55f, 0.85f },
        },
        new TrackSpec
        {
            Id = "toybox", Name = "Toy Box Hills", Difficulty = 2, Theme = Theme.Day, Accent = new Color(1f, 0.55f, 0.15f),
            Twist = "Two storeys of track: a climbing corner and crests that throw you in the air",
            Kit = Kit.Toy, Folder = ToyKit, Width = 12f,
            Pieces = new[]
            {
                ("ST", "track-wide-straight"), ("BU", "track-wide-straight-bump-up"), ("BD", "track-wide-straight-bump-down"),
                ("HL", "track-wide-straight-hill-complete"), ("CS", "track-wide-corner-small"), ("CL", "track-wide-corner-large"),
                ("CV", "track-wide-curve"), ("RC", "track-wide-corner-large-ramp"),
            },
            Layout = "ST ST ST BU ST RCRU ST BD BU HLU ST CLR HLD BU HLD CV- CV+ ST ST ST ST CLR CV+ CV- ST ST ST CLR ST ST ST ST",
            ItemRows = new[] { 0.2f, 0.62f, 0.86f },
        },
        new TrackSpec
        {
            Id = "neon", Name = "Neon Night Loop", Difficulty = 3, Theme = Theme.Neon, Accent = new Color(1f, 0.3f, 0.9f),
            Twist = "Narrow and technical: esses, a zig-zag chicane and neon blocks sliding across the road",
            Kit = Kit.Toy, Folder = ToyKit, Width = 9f,
            Pieces = new[]
            {
                ("ST", "track-road-narrow-straight"), ("CS", "track-road-narrow-corner-small"),
                ("CL", "track-road-narrow-corner-large"), ("CV", "track-road-narrow-curve"),
            },
            Layout = "ST ST ST CSR CV+ CV- CSL ST CSR CLR ST ST CV- CV+ ST CSR CSL CSR ST ST CLR ST",
            ItemRows = new[] { 0.12f, 0.45f, 0.78f },
        },
    };

    // ============================================================================================
    // Scene
    // ============================================================================================

    static void BuildInternal()
    {
        var log = buildLog = new StringBuilder();
        EnsureModelsReadable("Assets/ThirdParty/Kenney");
        FixKenneyTextures(log);
        AssetDatabase.DeleteAsset(GeneratedDir + "/TrackCollision.asset"); // pre-milestone-5 single-track mesh

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var sunGo = GameObject.Find("Directional Light");
        sunGo.name = "Sun";
        var sun = sunGo.GetComponent<Light>();
        sun.shadows = LightShadows.Soft;

        // ---- Characters and karts (shared by every track) -----------------------------------------
        CharacterRoster roster = BuildRoster(log);
        var kartsRoot = new GameObject("Karts").transform;
        var karts = new KartController[KartCount];
        for (int k = 0; k < KartCount; k++)
        {
            karts[k] = BuildKartShell($"Kart_{k}", kartsRoot, 0);
            roster.ApplyTo(karts[k], k % roster.Count); // lobby placeholder, replaced at race start
        }

        // ---- Cameras, audio listener, race manager ---------------------------------------------------
        var overviewGo = GameObject.Find("Main Camera");
        if (overviewGo == null) overviewGo = new GameObject("Main Camera", typeof(Camera));
        overviewGo.name = "OverviewCamera";
        overviewGo.tag = "MainCamera";
        var oldListener = overviewGo.GetComponent<AudioListener>();
        if (oldListener != null) Object.DestroyImmediate(oldListener);
        var overview = overviewGo.GetComponent<Camera>();
        overview.fieldOfView = 50f;
        overview.farClipPlane = 2000f;
        var listener = new GameObject("AudioListener", typeof(AudioListener)).transform;
        listener.SetParent(overviewGo.transform, false);

        var playerCameras = new Camera[RaceManager.MaxHumans];
        for (int i = 0; i < playerCameras.Length; i++)
        {
            var camGo = new GameObject($"PlayerCamera_{i}", typeof(Camera), typeof(FollowCamera));
            playerCameras[i] = camGo.GetComponent<Camera>();
            playerCameras[i].fieldOfView = 65f;
            playerCameras[i].farClipPlane = 1500f;
            camGo.GetComponent<FollowCamera>().SetTarget(karts[i].transform);
            camGo.SetActive(false);
        }
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include)) cam.allowHDR = true;

        var managerGo = new GameObject("RaceManager");
        var keyboard = managerGo.AddComponent<KeyboardKartInput>();
        var manager = managerGo.AddComponent<RaceManager>();
        BuildAudio(managerGo, log);
        BuildUi(managerGo, log);
        BuildItemManager(managerGo);
        BuildRescue(managerGo, log);
        ApplyRenderQuality(log);

        // ---- Tracks ------------------------------------------------------------------------------------
        var defs = new List<TrackDefinition>();
        for (int i = 0; i < Specs.Length; i++)
        {
            TrackDefinition def = BuildTrack(Specs[i], i, karts, kartsRoot, log);
            def.gameObject.SetActive(false);
            defs.Add(def);
        }
        manager.Configure(karts, playerCameras, overview, listener, roster, keyboard, defs.ToArray(), sun);

        // Phone server + QR encoder self tests (no scene needed).
        log.Append(RaceSelfTests.ServerSelfTest());
        log.Append(RaceSelfTests.QrSelfTest("http://192.168.100.123:8080/"));

        // ---- Toon look (all tracks, inactive ones included) ----------------------------------------------
        ConvertSceneToToon(managerGo, log);

        // Saved state: track 1 active, its lighting in the scene, karts on its grid.
        TrackDefinition first = defs[0];
        first.gameObject.SetActive(true);
        first.ApplyEnvironment(sun);
        overviewGo.transform.SetPositionAndRotation(first.overviewPosition, first.overviewRotation);
        for (int k = 0; k < KartCount; k++) karts[k].transform.SetPositionAndRotation(first.gridSlots[k].position, first.gridSlots[k].rotation);

        log.AppendLine("tracks: " + string.Join("; ", defs.Select(d => $"{d.displayName} {d.Length:F0} m, {d.checkpointCount} checkpoints, difficulty {d.difficulty}")));

        // ---- Save ------------------------------------------------------------------------------
        EnsureFolder("Assets/Scenes");
        if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new Exception("SaveScene failed");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();

        Debug.Log(Tag + "Layout report:\n" + log);
        Debug.Log(Tag + "DONE. Scene saved to " + ScenePath);
    }

    // ============================================================================================
    // One track
    // ============================================================================================

    static TrackDefinition BuildTrack(TrackSpec spec, int index, KartController[] karts, Transform kartsRoot, StringBuilder log)
    {
        log.AppendLine($"==== track {index + 1}: {spec.Name} ({spec.Id}) ====");
        RoadWidth = spec.Width;
        kartsRoot.gameObject.SetActive(false); // keep build raycasts off the karts

        var root = new GameObject("Track_" + spec.Id).transform;
        var def = root.gameObject.AddComponent<TrackDefinition>();
        def.displayName = spec.Name;
        def.twist = spec.Twist;
        def.difficulty = spec.Difficulty;
        def.laps = spec.Laps;
        def.accent = spec.Accent;

        // ---- Measure pieces --------------------------------------------------------------------
        var pieces = new Dictionary<string, PieceDef>();
        float unit = 0f, scale = 1f;
        foreach (var (key, file) in spec.Pieces)
        {
            GameObject prefab = Load(spec.Folder + file + ".fbx");
            List<Vector3> verts = PrefabVertices(prefab);
            if (unit <= 0f)
            {
                Bounds sb = BoundsOf(verts); // first entry is a straight: its width defines the kit unit
                unit = Mathf.Min(sb.size.x, sb.size.z);
                scale = spec.Width / unit;
            }
            var pd = new PieceDef
            {
                name = key, prefab = prefab,
                openings = spec.Kit == Kit.Toy ? FindConnectors(verts, unit) : FindTileConnectors(verts, unit)
            };
            if (pd.openings.Length != 2) throw new Exception($"{prefab.name}: expected 2 connectors, found {pd.openings.Length}");
            pieces[key] = pd;
            log.AppendLine($"piece {key,-3} {prefab.name}: size={BoundsOf(verts).size:F2} connectors {Describe(pd.openings)}");
        }
        log.AppendLine($"kit={spec.Kit}, unit width={unit:F3}, scale={scale:F2} -> road {spec.Width} m wide");
        var companions = spec.Companions.Select(c => (c.key, prefab: Load(spec.Folder + c.file + ".fbx"))).ToList();
        GameObject supportPrefab = Load(ToyKit + (spec.Width < 10f ? "supports.fbx" : "supports-wide.fbx"));

        // ---- Lay out the circuit -------------------------------------------------------------------
        var trackPieces = new GameObject("Pieces").transform;
        trackPieces.SetParent(root);
        var supports = new GameObject("Supports").transform;
        supports.SetParent(root);

        Vector3 cursor = Vector3.zero, heading = Vector3.forward;
        Vector3 startCursor = cursor, startHeading = heading;
        var joints = new List<(Vector3 pos, Vector3 dir)> { (cursor, heading) };
        var checkpointCandidates = new List<(int piece, Vector3 pos, Vector3 dir)>();
        var pieceBounds = new List<Bounds>();
        (Vector3 pos, Vector3 dir) finish = default;
        var straightCenters = new List<(Vector3 pos, Vector3 dir)>();
        var centerline = new List<Vector3> { cursor };
        var corners = new List<(Vector3 entry, Vector3 dir, Vector3 exit, int turn)>();
        int finishWaypoint = 0, supportCount = 0, straightRun = 0;
        float maxHeight = 0f;
        string[] layout = spec.Layout.Split(' ');

        for (int i = 0; i < layout.Length; i++)
        {
            string token = layout[i];
            string key = token.Substring(0, 2);
            int turn = 0, shift = 0, climb = 0;
            foreach (char ch in token.Substring(2))
            {
                if (ch == 'R') turn = 1;
                else if (ch == 'L') turn = -1;
                else if (ch == 'U') climb = 1;
                else if (ch == 'D') climb = -1;
                else if (ch == '+') shift = 1;
                else if (ch == '-') shift = -1;
            }

            Vector3 entry = cursor, entryDir = heading;
            GameObject go = PlacePiece(pieces[key], turn, shift, climb, scale, ref cursor, ref heading, trackPieces, $"{i:00}_{token}");
            foreach (var c in companions.Where(c => c.key == key))
            {
                var extra = (GameObject)PrefabUtility.InstantiatePrefab(c.prefab);
                extra.name = c.prefab.name;
                extra.transform.SetParent(go.transform, false); // same transform (incl. mirroring) as the tile
            }
            pieceBounds.Add(RendererBounds(go));
            joints.Add((cursor, heading));
            maxHeight = Mathf.Max(maxHeight, cursor.y);

            if (turn != 0)
            {
                AddArc(centerline, entry, entryDir, cursor, turn, 6);
                corners.Add((entry, entryDir, cursor, turn));
            }
            if (shift != 0) AddCurve(centerline, entry, entryDir, cursor, 4);
            centerline.Add(cursor);

            if (i == spec.FinishAfter)
            {
                finish = (cursor, heading);
                finishWaypoint = centerline.Count - 1;
            }
            bool straight = turn == 0 && shift == 0 && climb == 0;
            if (i < 5 && straight) straightCenters.Add(((entry + cursor) * 0.5f, heading));

            // Checkpoints after every corner/curve and every third straight-ish piece.
            straightRun = turn != 0 || shift != 0 ? 0 : straightRun + 1;
            if (turn != 0 || shift != 0 || straightRun % 3 == 0) checkpointCandidates.Add((i, cursor, heading));

            // Support columns under elevated joints.
            if (cursor.y > 0.5f)
            {
                PlaceSupport(supportPrefab, supports, cursor, heading, scale);
                supportCount++;
            }
        }

        BuildTrackCollision(trackPieces, joints, "TrackCollision_" + spec.Id, log);
        float closureError = Vector3.Distance(cursor, startCursor);
        float headingError = Vector3.Angle(heading, startHeading);
        log.AppendLine($"pieces placed={layout.Length}, loop closure error={closureError:F4} m, heading error={headingError:F2} deg, " +
                       $"max elevation={maxHeight:F1} m, supports={supportCount}");
        if (closureError > 0.05f || headingError > 0.5f) Debug.LogWarning(Tag + $"{spec.Name}: track loop does not close cleanly!");

        Bounds trackBounds = pieceBounds[0];
        foreach (var b in pieceBounds) trackBounds.Encapsulate(b);
        log.AppendLine($"track bounds center={trackBounds.center:F1} size={trackBounds.size:F1}");

        int n = layout.Length;
        var checkpointPoses = checkpointCandidates
            .Where(c => c.piece != spec.FinishAfter)
            .OrderBy(c => (c.piece - spec.FinishAfter - 1 + n) % n)
            .Select(c => (c.pos, c.dir)).ToList();

        // ---- Ground and perimeter walls -------------------------------------------
        Material wallMat = SaveMaterial("Wall", new Color(0.75f, 0.75f, 0.78f));
        float margin = 45f;
        Vector3 groundSize = new Vector3(trackBounds.size.x + margin * 2f, 1f, trackBounds.size.z + margin * 2f);
        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        ground.transform.SetParent(root);
        ground.transform.position = new Vector3(trackBounds.center.x, -0.5f - 0.03f, trackBounds.center.z);
        ground.transform.localScale = groundSize;
        ground.GetComponent<Renderer>().sharedMaterial = SaveMaterial("Ground", new Color(0.38f, 0.62f, 0.32f));

        var walls = new GameObject("Walls").transform;
        walls.SetParent(root);
        float wallH = 3f, wallT = 1f;
        Vector3 gc = new Vector3(trackBounds.center.x, wallH * 0.5f, trackBounds.center.z);
        CreateWall(walls, wallMat, gc + Vector3.forward * groundSize.z * 0.5f, new Vector3(groundSize.x, wallH, wallT));
        CreateWall(walls, wallMat, gc - Vector3.forward * groundSize.z * 0.5f, new Vector3(groundSize.x, wallH, wallT));
        CreateWall(walls, wallMat, gc + Vector3.right * groundSize.x * 0.5f, new Vector3(wallT, wallH, groundSize.z));
        CreateWall(walls, wallMat, gc - Vector3.right * groundSize.x * 0.5f, new Vector3(wallT, wallH, groundSize.z));

        // ---- Checkpoints + finish line ----------------------------------------------
        var cpRoot = new GameObject("Checkpoints").transform;
        cpRoot.SetParent(root);
        for (int i = 0; i < checkpointPoses.Count; i++)
            CreateTrigger($"Checkpoint_{i}", checkpointPoses[i].pos, checkpointPoses[i].dir, i, false, cpRoot);
        CreateTrigger("FinishLine", finish.pos, finish.dir, -1, true, cpRoot);
        log.AppendLine($"checkpoints={checkpointPoses.Count}, finish at {finish.pos:F1} heading {finish.dir:F0}");

        // ---- Centerline waypoints (index 0 = finish line) ----------------------------------------
        centerline.RemoveAt(centerline.Count - 1); // closed loop: last point == first
        var waypoints = new Vector3[centerline.Count];
        for (int i = 0; i < centerline.Count; i++) waypoints[i] = centerline[(finishWaypoint + i) % centerline.Count];
        var raceTrack = root.gameObject.AddComponent<RaceTrack>();
        raceTrack.Configure(waypoints, RoadWidth * spec.DriveFraction);
        log.AppendLine($"waypoints={waypoints.Length}, centerline length={raceTrack.Length:F1} m, waypoint[0]={waypoints[0]:F1} (finish line)");

        // ---- Staggered 2-wide starting grid behind the finish line -------------------------------
        Vector3 finishRight = Vector3.Cross(Vector3.up, finish.dir);
        var gridRoot = new GameObject("Grid").transform;
        gridRoot.SetParent(root);
        var gridSlots = new Transform[KartCount];
        float gridLateral = Mathf.Min(2.3f, RoadWidth * spec.DriveFraction * 0.22f);
        for (int k = 0; k < KartCount; k++)
        {
            int row = k / 2, col = k % 2;
            float back = 7f + row * 7f + col * 3.5f;
            Vector3 p = finish.pos - finish.dir * back + finishRight * (col == 0 ? -gridLateral : gridLateral);
            Physics.SyncTransforms();
            if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit surface, 20f, ~0, QueryTriggerInteraction.Ignore))
                p.y = surface.point.y;
            p += Vector3.up * 0.3f;
            var slot = new GameObject($"GridSlot_{k}").transform;
            slot.SetParent(gridRoot);
            slot.SetPositionAndRotation(p, Quaternion.LookRotation(finish.dir));
            gridSlots[k] = slot;
        }
        Physics.SyncTransforms();
        int gridOnRoad = 0;
        foreach (var slot in gridSlots)
        {
            if (Physics.Raycast(slot.position + Vector3.up * 3f, Vector3.down, out RaycastHit gh, 10f, ~0, QueryTriggerInteraction.Ignore))
            {
                var ts = gh.collider.GetComponent<TrackSurface>();
                if (ts != null && ts.IsDrivable(gh.triangleIndex)) gridOnRoad++;
            }
        }
        log.AppendLine($"grid slots={KartCount}, on road={gridOnRoad}, first={gridSlots[0].position:F1}, last={gridSlots[KartCount - 1].position:F1}");
        if (gridOnRoad != KartCount) Debug.LogWarning(Tag + $"{spec.Name}: some grid slots are not on the road!");

        // ---- Items, hazards, venue ------------------------------------------------------------------
        BuildItemBoxes(root, raceTrack, spec.ItemRows, log);
        var ctx = new VenueContext
        {
            root = root, def = def, track = raceTrack, ground = ground, groundSize = groundSize, trackBounds = trackBounds,
            finish = finish, straightCenters = straightCenters, startHeading = startHeading, pieceBounds = pieceBounds,
            corners = corners, joints = joints, log = log, spec = spec,
        };
        switch (spec.Theme)
        {
            case Theme.Night:
                BuildNightHazards(root, raceTrack, log);
                BuildClassicDecoration(ctx, 45);
                BuildNightVenue(root, def, raceTrack, ground, groundSize, corners, log);
                break;
            case Theme.Sunset:
                BuildSunsetHazards(ctx);
                BuildSunsetVenue(ctx);
                break;
            case Theme.Day:
                BuildToyHazards(ctx);
                BuildToyVenue(ctx);
                break;
            case Theme.Neon:
                BuildNeonHazards(ctx);
                BuildNeonVenue(ctx);
                break;
        }

        // ---- Sanity checks -----------------------------------------------------------------
        Physics.SyncTransforms();
        int jointMisses = 0, jointOffroad = 0;
        foreach (var j in joints)
        {
            var hits = Physics.RaycastAll(j.pos + Vector3.up * 5f, Vector3.down, 10f, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.collider.GetComponent<Hazard>() == null).OrderBy(h => h.distance).ToList();
            if (hits.Count == 0) { jointMisses++; continue; }
            RaycastHit hit = hits[0];
            var ts = hit.collider.GetComponent<TrackSurface>();
            if (ts == null || !ts.IsDrivable(hit.triangleIndex)) jointOffroad++;
        }
        log.AppendLine($"centerline joints={joints.Count}, raycast misses={jointMisses}, not-on-road={jointOffroad}");

        // Drop test: the karts settle on this grid under gravity for 2 s and must rest on the road.
        kartsRoot.gameObject.SetActive(true);
        for (int k = 0; k < KartCount; k++) karts[k].transform.SetPositionAndRotation(gridSlots[k].position, gridSlots[k].rotation);
        Physics.SyncTransforms();
        var prevMode = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        for (int i = 0; i < 100; i++) Physics.Simulate(0.02f);
        Physics.simulationMode = prevMode;
        float maxDrift = 0f, minY = float.MaxValue, maxY = float.MinValue;
        for (int k = 0; k < KartCount; k++)
        {
            Vector3 settled = karts[k].transform.position;
            Vector3 slotPos = gridSlots[k].position;
            maxDrift = Mathf.Max(maxDrift, Vector2.Distance(new Vector2(settled.x, settled.z), new Vector2(slotPos.x, slotPos.z)));
            minY = Mathf.Min(minY, settled.y);
            maxY = Mathf.Max(maxY, settled.y);
            var body = karts[k].GetComponent<Rigidbody>();
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            karts[k].transform.SetPositionAndRotation(gridSlots[k].position, gridSlots[k].rotation);
        }
        log.AppendLine($"drop test ({KartCount} karts): settled y in [{minY:F3}, {maxY:F3}], max horizontal drift={maxDrift:F3} m");

        // ---- Definition ----------------------------------------------------------------------
        def.track = raceTrack;
        def.gridSlots = gridSlots;
        def.checkpointCount = checkpointPoses.Count;
        def.bounds = trackBounds;
        float span = Mathf.Max(trackBounds.size.x, trackBounds.size.z);
        def.overviewPosition = trackBounds.center + new Vector3(0f, span * 1.05f, -span * 0.75f);
        def.overviewRotation = Quaternion.LookRotation(trackBounds.center - def.overviewPosition);
        log.AppendLine($"track '{spec.Name}': length {raceTrack.Length:F0} m, twist: {spec.Twist}");
        return def;
    }

    class VenueContext
    {
        public Transform root;
        public TrackDefinition def;
        public RaceTrack track;
        public GameObject ground;
        public Vector3 groundSize;
        public Bounds trackBounds;
        public (Vector3 pos, Vector3 dir) finish;
        public List<(Vector3 pos, Vector3 dir)> straightCenters;
        public Vector3 startHeading;
        public List<Bounds> pieceBounds;
        public List<(Vector3 entry, Vector3 dir, Vector3 exit, int turn)> corners;
        public List<(Vector3 pos, Vector3 dir)> joints;
        public StringBuilder log;
        public TrackSpec spec;
        public Transform Deco(string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root);
            return t;
        }
    }

    // ============================================================================================
    // Shared decoration helpers
    // ============================================================================================

    /// <summary>Finish gate + checkered flags, grandstands along the start straight and trees (night circuit).</summary>
    static void BuildClassicDecoration(VenueContext c, int treeTarget)
    {
        var deco = c.Deco("Decoration");
        var obstacles = new List<Bounds>(c.pieceBounds);
        PlaceFinishGate(c, deco);
        Vector3 finishRight = Vector3.Cross(Vector3.up, c.finish.dir);
        GameObject flagPrefab = Load(RacingKit + "flagCheckers.fbx");
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 p = c.finish.pos + finishRight * s * (RoadWidth * 0.5f + 3f);
            PlaceProp(flagPrefab, deco, PropScale * 0.5f, p, YawFromTo(Vector3.forward, -c.finish.dir), "FlagCheckers");
        }
        PlaceGrandstands(c, deco, obstacles, Load(RacingKit + "grandStand.fbx"), -1);
        ScatterProps(c, deco, obstacles, new[] { Load(RacingKit + "treeLarge.fbx"), Load(RacingKit + "treeSmall.fbx") }, treeTarget, 6f, 11f, 1234, "Tree");
    }

    static void PlaceFinishGate(VenueContext c, Transform parent)
    {
        GameObject gatePrefab = Load(ToyKit + "gate-finish.fbx");
        Vector3 finishRight = Vector3.Cross(Vector3.up, c.finish.dir);
        Bounds gateB = BoundsOf(PrefabVertices(gatePrefab));
        bool gateAlongX = gateB.size.x >= gateB.size.z;
        float gateScale = RoadWidth * 1.15f / (gateAlongX ? gateB.size.x : gateB.size.z);
        PlaceProp(gatePrefab, parent, gateScale, c.finish.pos, YawFromTo(gateAlongX ? Vector3.right : Vector3.forward, finishRight), "FinishGate");
    }

    /// <summary>Grandstands beside the start straight facing the road (side -1 = left of travel).</summary>
    static void PlaceGrandstands(VenueContext c, Transform parent, List<Bounds> obstacles, GameObject standPrefab, int side, float targetWidth = 0f)
    {
        List<Vector3> standVerts = PrefabVertices(standPrefab);
        Bounds standB = BoundsOf(standVerts);
        Vector3 standFront = -TallSide(standVerts);
        float s = targetWidth > 0f ? targetWidth / Mathf.Max(standB.size.x, standB.size.z) : PropScale;
        Vector3 sideDir = Vector3.Cross(Vector3.up, c.startHeading) * side;
        float standDepth = Mathf.Min(standB.size.x, standB.size.z) * s;
        for (int i = 1; i < c.straightCenters.Count - 1; i++)
        {
            Vector3 p = c.straightCenters[i].pos + sideDir * (RoadWidth * 0.5f + 4f + standDepth * 0.5f);
            p.y = 0f;
            var stand = PlaceProp(standPrefab, parent, s, p, YawFromTo(standFront, -sideDir), "GrandStand");
            var box = stand.AddComponent<BoxCollider>();
            box.center = standB.center;
            box.size = standB.size;
            obstacles.Add(RendererBounds(stand));
        }
    }

    /// <summary>Random props off the track (ground level), each height in [minH, maxH] metres.</summary>
    static int ScatterProps(VenueContext c, Transform parent, List<Bounds> obstacles, GameObject[] prefabs, int target, float minH, float maxH,
        int seed, string name, float pad = 4f)
    {
        var rng = new Random(seed);
        int placed = 0;
        Bounds area = new Bounds(c.trackBounds.center, new Vector3(c.groundSize.x - 10f, 1f, c.groundSize.z - 10f));
        for (int attempt = 0; attempt < target * 14 && placed < target; attempt++)
        {
            var p = new Vector3(Mathf.Lerp(area.min.x, area.max.x, (float)rng.NextDouble()), 0f,
                                Mathf.Lerp(area.min.z, area.max.z, (float)rng.NextDouble()));
            if (Overlaps(obstacles, p, pad) || OverTrack(p)) continue;
            GameObject prefab = prefabs[rng.Next(prefabs.Length)];
            Bounds pb = BoundsOf(PrefabVertices(prefab));
            float h = Mathf.Lerp(minH, maxH, (float)rng.NextDouble());
            var go = PlaceProp(prefab, parent, h / pb.size.y, p, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f), name);
            Bounds rb = RendererBounds(go);
            if (Overlaps(obstacles, rb.center, Mathf.Max(rb.extents.x, rb.extents.z)))
            {
                Object.DestroyImmediate(go);
                continue;
            }
            obstacles.Add(rb);
            placed++;
        }
        return placed;
    }

    /// <summary>Static spin-out hazard built from a model (cone, pylon, block...).</summary>
    static GameObject PlaceHazardModel(Transform parent, GameObject prefab, float height, Vector3 pos, Quaternion rot, string name)
    {
        Bounds b = BoundsOf(PrefabVertices(prefab));
        float s = height / b.size.y;
        var go = new GameObject(name);
        go.transform.SetParent(parent);
        go.transform.SetPositionAndRotation(pos, rot);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        model.transform.SetParent(go.transform, false);
        model.transform.localScale = Vector3.one * s;
        model.transform.localPosition = -new Vector3(b.center.x, b.min.y, b.center.z) * s;
        var col = go.AddComponent<CapsuleCollider>();
        col.radius = Mathf.Max(b.size.x, b.size.z) * s * 0.4f;
        col.height = b.size.y * s;
        col.center = new Vector3(0f, col.height * 0.5f, 0f);
        go.AddComponent<Hazard>().Configure(col.radius + 0.3f, Vector3.right, 0f, 1f);
        return go;
    }

    /// <summary>Kinematic block that slides across the road (period in seconds, distance = amplitude).</summary>
    static GameObject PlaceSlidingBlock(Transform parent, Material mat, Vector3 pos, Vector3 axis, Vector3 forward, Vector3 size, float distance, float period, string name)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = name;
        block.transform.SetParent(parent);
        block.transform.SetPositionAndRotation(pos + Vector3.up * size.y * 0.5f, Quaternion.LookRotation(forward));
        block.transform.localScale = size;
        block.GetComponent<Renderer>().sharedMaterial = mat;
        var rb = block.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        block.AddComponent<Hazard>().Configure(Mathf.Max(size.x, size.z) * 0.75f, axis, distance, period);
        return block;
    }

    static GameObject GlowBox(Transform parent, string name, Vector3 centre, Quaternion rot, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent);
        go.transform.SetPositionAndRotation(centre, rot);
        go.transform.localScale = size;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    static Material Emissive(string name, Color color, float intensity) => SaveVenueMaterial(name, Shader.Find("Standard"), m =>
    {
        m.color = color;
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", color * intensity);
        m.SetFloat("_Glossiness", 0.2f);
    });

    static Material SkyMaterial(string name, Texture2D sky, float exposure = 1f) =>
        SaveVenueMaterial(name, Shader.Find("Skybox/Panoramic"), m =>
        {
            m.SetTexture("_MainTex", sky);
            m.SetFloat("_Exposure", exposure);
        });

    static Material GroundMaterial(string name, Texture2D tex, Vector3 groundSize, float tile, float gloss = 0.05f) =>
        SaveVenueMaterial(name, Shader.Find("Standard"), m =>
        {
            m.mainTexture = tex;
            m.mainTextureScale = new Vector2(groundSize.x / tile, groundSize.z / tile);
            m.color = Color.white;
            m.SetFloat("_Glossiness", gloss);
        });

    /// <summary>Outside point of a corner (centre of the arc, pushed out past the road edge).</summary>
    static Vector3 CornerOutside(VenueContext c, int i, float extra)
    {
        var k = c.corners[i];
        Vector3 right = Vector3.Cross(Vector3.up, k.dir) * k.turn;
        float radius = Vector3.Dot(k.exit - k.entry, k.dir);
        Vector3 centre = k.entry + right * radius;
        Vector3 outward = (-right + k.dir).normalized;
        Vector3 p = centre + outward * (radius + RoadWidth * 0.5f + extra);
        p.y = 0f;
        return p;
    }

    // ============================================================================================
    // Sunset Grand Prix
    // ============================================================================================

    static void BuildSunsetVenue(VenueContext c)
    {
        var def = c.def;
        def.skybox = SkyMaterial("SunsetSky", SaveTexture("SunsetSky", GradientSky(
            new Color(1f, 0.58f, 0.28f), new Color(0.78f, 0.36f, 0.42f), new Color(0.2f, 0.16f, 0.38f), new Color(0.22f, 0.14f, 0.12f),
            0, 0.35f, new Color(1f, 0.85f, 0.55f), 21), false, false));
        def.sunEuler = new Vector3(13f, -62f, 0f); // low warm sun, long shadows
        def.sunColor = new Color(1f, 0.68f, 0.42f);
        def.sunIntensity = 0.9f;
        def.sunShadowStrength = 0.85f;
        def.ambientSky = new Color(0.4f, 0.3f, 0.42f);
        def.ambientEquator = new Color(0.46f, 0.31f, 0.27f);
        def.ambientGround = new Color(0.16f, 0.12f, 0.11f);
        def.fog = true;
        def.fogColor = new Color(0.93f, 0.6f, 0.46f);
        def.fogDensity = 0.0021f;
        def.headlights = false;

        c.ground.GetComponent<Renderer>().sharedMaterial =
            GroundMaterial("SunsetGrass", SaveTexture("SunsetGrass", GrassTexture(new Color(0.3f, 0.38f, 0.16f), 17), true), c.groundSize, 24f);

        var venue = c.Deco("Venue");
        var deco = c.Deco("Decoration");
        var obstacles = new List<Bounds>(c.pieceBounds);

        // Start gantry + flags.
        GameObject overhead = Load(RacingKit + "overheadLights.fbx");
        Vector3 finishRight = Vector3.Cross(Vector3.up, c.finish.dir);
        Bounds ob = BoundsOf(PrefabVertices(overhead));
        bool alongX = ob.size.x >= ob.size.z;
        float os = RoadWidth * 1.25f / (alongX ? ob.size.x : ob.size.z);
        var gantry = PlaceProp(overhead, deco, os, c.finish.pos, YawFromTo(alongX ? Vector3.right : Vector3.forward, finishRight), "FinishGate");
        obstacles.Add(RendererBounds(gantry));
        GameObject flagPrefab = Load(RacingKit + "flagCheckers.fbx");
        for (int s = -1; s <= 1; s += 2)
            PlaceProp(flagPrefab, deco, PropScale * 0.6f, c.finish.pos + finishRight * s * (RoadWidth * 0.5f + 3f), YawFromTo(Vector3.forward, -c.finish.dir), "FlagCheckers");

        // Covered grandstands on the left of the start straight, pit garages on the right.
        PlaceGrandstands(c, venue, obstacles, Load(RacingKit + "grandStandCovered.fbx"), -1, 26f);
        GameObject garage = Load(RacingKit + "pitsGarage.fbx"), office = Load(RacingKit + "pitsOffice.fbx");
        Bounds gb = BoundsOf(PrefabVertices(garage));
        List<Vector3> garageVerts = PrefabVertices(garage);
        Vector3 garageFront = OpenSide(garageVerts); // the garage door: the side with the least geometry
        float gs = 9f / Mathf.Max(gb.size.x, gb.size.z);
        Vector3 pitSide = Vector3.Cross(Vector3.up, c.startHeading);
        int garages = 0;
        Vector3 startPos = c.straightCenters.First().pos, endPos = c.straightCenters.Last().pos;
        float runLen = Vector3.Distance(startPos, endPos);
        for (float d = 0f; d <= runLen; d += gb.size.x * gs * 1.02f)
        {
            Vector3 p = startPos + c.startHeading * d + pitSide * (RoadWidth * 0.5f + 12f);
            p.y = 0f;
            if (OverTrack(p)) continue;
            var g = PlaceProp(garages % 5 == 4 ? office : garage, venue, gs, p, YawFromTo(garageFront, -pitSide), "PitGarage");
            var box = g.AddComponent<BoxCollider>();
            box.center = gb.center;
            box.size = gb.size;
            obstacles.Add(RendererBounds(g));
            garages++;
        }
        // Pit lane apron in front of the garages.
        Texture2D asphalt = SaveTexture("Paddock", AsphaltTexture(), true);
        Material paddockMat = SaveVenueMaterial("Paddock", Shader.Find("Standard"), m =>
        {
            m.mainTexture = asphalt;
            m.mainTextureScale = new Vector2(3f, 1f);
            m.SetFloat("_Glossiness", 0.15f);
        });
        FlatQuad("PitApron", venue, (startPos + endPos) * 0.5f + pitSide * (RoadWidth * 0.5f + 5f) + Vector3.up * 0.01f - Vector3.up * startPos.y,
            c.startHeading, 9f, runLen + 20f, paddockMat);

        // Banner towers and billboards along the straights, barriers outside the corners.
        GameObject[] banners = { Load(RacingKit + "bannerTowerRed.fbx"), Load(RacingKit + "bannerTowerGreen.fbx") };
        GameObject billboard = Load(RacingKit + "billboard.fbx");
        int props = 0;
        var track = c.track;
        for (float s = 40f; s < track.Length - 20f; s += 70f)
        {
            if (track.RadiusAt(s) < 150f) continue;
            Vector3 right = track.RightAt(s);
            int side = props % 2 == 0 ? 1 : -1;
            Vector3 p = track.PointAt(s) + right * side * (RoadWidth * 0.5f + 7f);
            p.y = 0f;
            if (OverTrack(p) || Overlaps(obstacles, p, 5f)) continue;
            GameObject prefab = props % 3 == 2 ? billboard : banners[props % 2];
            Bounds pb = BoundsOf(PrefabVertices(prefab));
            var go = PlaceProp(prefab, deco, 8f / pb.size.y, p, YawFromTo(Vector3.forward, -right * side), props % 3 == 2 ? "Billboard" : "BannerTower");
            obstacles.Add(RendererBounds(go));
            props++;
        }
        GameObject[] barrierPrefabs = { Load(RacingKit + "barrierRed.fbx"), Load(RacingKit + "barrierWhite.fbx") };
        int barriers = 0;
        for (int i = 0; i < c.corners.Count; i++)
        {
            var k = c.corners[i];
            Vector3 rightDir = Vector3.Cross(Vector3.up, k.dir) * k.turn;
            float radius = Vector3.Dot(k.exit - k.entry, k.dir);
            Vector3 centre = k.entry + rightDir * radius;
            for (int a = 0; a <= 6; a++)
            {
                float ang = Mathf.PI * 0.5f * a / 6f;
                Vector3 radial = (-rightDir * Mathf.Cos(ang) + k.dir * Mathf.Sin(ang)).normalized;
                Vector3 p = centre + radial * (radius + RoadWidth * 0.5f + 11f);
                p.y = 0f;
                if (OverTrack(p)) continue; // (piece bounds cover the whole corner square: not a useful test here)
                GameObject prefab = barrierPrefabs[(a + i) % 2];
                Bounds bb = BoundsOf(PrefabVertices(prefab));
                var go = PlaceProp(prefab, deco, 1.2f / bb.size.y, p, Quaternion.LookRotation(Vector3.Cross(Vector3.up, radial)), "TyreBarrier");
                var box = go.AddComponent<BoxCollider>();
                box.center = bb.center;
                box.size = bb.size;
                barriers++;
            }
        }

        // Tents behind the pits, a few trees further out.
        int tents = ScatterProps(c, deco, obstacles, new[] { Load(RacingKit + "tent.fbx"), Load(RacingKit + "tentLong.fbx") }, 6, 4f, 5f, 99, "Tent");
        int trees = ScatterProps(c, deco, obstacles, new[] { Load(RacingKit + "treeLarge.fbx"), Load(RacingKit + "treeSmall.fbx") }, 30, 6f, 12f, 4321, "Tree");
        c.log.AppendLine($"sunset: low sun + gradient sky + warm fog; {garages} pit buildings, {props} banners/billboards, {barriers} tyre barriers, {tents} tents, {trees} trees");
    }

    static void BuildSunsetHazards(VenueContext c)
    {
        var parent = c.Deco("Hazards");
        GameObject pylon = Load(RacingKit + "pylon.fbx");
        var track = c.track;
        float s1 = FindStraight(track, track.Length * 0.64f);
        int n = 0;
        float lat = RoadWidth * 0.8f * 0.24f;
        foreach (var (ds, l) in new[] { (0f, -lat), (26f, lat), (52f, -lat) })
        {
            float s = s1 + ds;
            if (track.RadiusAt(s) < 200f) continue;
            PlaceHazardModel(parent, pylon, 1.3f, Surface(track.PointAt(s) + track.RightAt(s) * l), Quaternion.LookRotation(track.TangentAt(s)), $"Pylon_{n++}");
        }
        c.log.AppendLine($"hazards: {n} pylons (slalom at s={s1:F0})");
    }

    // ============================================================================================
    // Toy Box Hills
    // ============================================================================================

    static void BuildToyVenue(VenueContext c)
    {
        var def = c.def;
        def.skybox = SkyMaterial("DaySky", SaveTexture("DaySky", GradientSky(
            new Color(0.78f, 0.9f, 1f), new Color(0.55f, 0.76f, 0.98f), new Color(0.26f, 0.52f, 0.92f), new Color(0.5f, 0.55f, 0.6f),
            0, 0f, Color.white, 7, 0.55f), false, false));
        def.sunEuler = new Vector3(48f, -28f, 0f);
        def.sunColor = new Color(1f, 0.96f, 0.88f);
        def.sunIntensity = 0.8f;
        def.sunShadowStrength = 0.7f;
        def.ambientSky = new Color(0.4f, 0.46f, 0.58f);
        def.ambientEquator = new Color(0.38f, 0.37f, 0.35f);
        def.ambientGround = new Color(0.22f, 0.19f, 0.16f);
        def.fog = true;
        def.fogColor = new Color(0.74f, 0.85f, 0.98f);
        def.fogDensity = 0.0011f;
        def.headlights = false;

        c.ground.GetComponent<Renderer>().sharedMaterial = GroundMaterial("PlayMat", SaveTexture("PlayMat", PlayMatTexture(), true), c.groundSize, 48f, 0.12f);

        var deco = c.Deco("Decoration");
        var obstacles = new List<Bounds>(c.pieceBounds);
        foreach (Transform s in c.root.Find("Supports")) obstacles.Add(RendererBounds(s.gameObject));
        PlaceFinishGate(c, deco);

        // Giant toys around the room (scaled way up), toy trees and building blocks.
        GameObject[] giants =
        {
            Load(ToyKit + "vehicle-monster-truck.fbx"), Load(ToyKit + "vehicle-racer.fbx"), Load(ToyKit + "vehicle-drag-racer.fbx"),
            Load(ToyKit + "vehicle-truck.fbx"), Load(ToyKit + "wheel-large.fbx"), Load(ToyKit + "item-cone.fbx"),
            Load(ToyKit + "item-box.fbx"), Load(ToyKit + "item-coin-gold.fbx"),
        };
        int toys = ScatterProps(c, deco, obstacles, giants, 16, 7f, 14f, 777, "GiantToy", 6f);
        foreach (Transform t in deco)
            if (t.name == "GiantToy")
            {
                Bounds rb = RendererBounds(t.gameObject);
                var box = t.gameObject.AddComponent<BoxCollider>();
                box.center = t.InverseTransformPoint(rb.center);
                box.size = new Vector3(rb.size.x / t.lossyScale.x, rb.size.y / t.lossyScale.y, rb.size.z / t.lossyScale.z);
            }
        int trees = ScatterProps(c, deco, obstacles, new[] { Load(ToyKit + "tree.fbx"), Load(ToyKit + "tree-pine.fbx") }, 26, 7f, 13f, 555, "ToyTree");

        // Stacked building blocks (primitive cubes in bright colours).
        Color[] blockColors = { new Color(0.95f, 0.3f, 0.3f), new Color(0.3f, 0.6f, 1f), new Color(1f, 0.82f, 0.2f), new Color(0.35f, 0.8f, 0.4f) };
        var blockMats = blockColors.Select((col, i) => SaveVenueMaterial($"ToyBlock{i}", Shader.Find("Standard"), m => { m.color = col; m.SetFloat("_Glossiness", 0.35f); })).ToArray();
        var rng = new Random(31);
        int stacks = 0;
        for (int attempt = 0; attempt < 200 && stacks < 8; attempt++)
        {
            var p = new Vector3(Mathf.Lerp(c.trackBounds.min.x - 30f, c.trackBounds.max.x + 30f, (float)rng.NextDouble()), 0f,
                                Mathf.Lerp(c.trackBounds.min.z - 30f, c.trackBounds.max.z + 30f, (float)rng.NextDouble()));
            if (Overlaps(obstacles, p, 8f) || OverTrack(p) || !InsideGround(c, p, 6f)) continue;
            int height = 2 + rng.Next(3);
            float size = 4f;
            float yaw = (float)rng.NextDouble() * 90f;
            for (int h = 0; h < height; h++)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                b.name = "ToyBlock";
                b.transform.SetParent(deco);
                b.transform.SetPositionAndRotation(p + Vector3.up * (size * 0.5f + h * size), Quaternion.Euler(0f, yaw + h * 17f, 0f));
                b.transform.localScale = Vector3.one * size;
                b.GetComponent<Renderer>().sharedMaterial = blockMats[(stacks + h) % blockMats.Length];
            }
            obstacles.Add(new Bounds(p, new Vector3(size * 1.5f, 1f, size * 1.5f)));
            stacks++;
        }
        c.log.AppendLine($"toy box: day sky + play mat; {toys} giant toys, {trees} toy trees, {stacks} block stacks, supports under elevated track");
    }

    /// <summary>Horizontal side of a model's bounds with the fewest vertices close to it (an open front).</summary>
    static Vector3 OpenSide(List<Vector3> verts)
    {
        Bounds b = BoundsOf(verts);
        Vector3 best = Vector3.forward;
        int bestCount = int.MaxValue;
        foreach (int axis in new[] { 0, 2 })
            foreach (int sign in new[] { -1, 1 })
            {
                float face = sign < 0 ? b.min[axis] : b.max[axis], band = b.size[axis] * 0.04f;
                int count = verts.Count(v => Mathf.Abs(v[axis] - face) < band);
                if (count >= bestCount) continue;
                bestCount = count;
                best = Vector3.zero;
                best[axis] = sign;
            }
        return best;
    }

    static bool InsideGround(VenueContext c, Vector3 p, float pad) =>
        Mathf.Abs(p.x - c.trackBounds.center.x) < c.groundSize.x * 0.5f - pad && Mathf.Abs(p.z - c.trackBounds.center.z) < c.groundSize.z * 0.5f - pad;

    static void BuildToyHazards(VenueContext c)
    {
        var parent = c.Deco("Hazards");
        GameObject cone = Load(ToyKit + "item-cone.fbx");
        var track = c.track;
        int n = 0;
        // Cones on the ground-level back straight (never on crests: those are for flying).
        float s1 = FindStraight(track, track.Length * 0.72f);
        foreach (var (ds, l) in new[] { (0f, 0f), (24f, -3.3f), (24f, 3.3f) })
        {
            float s = s1 + ds;
            if (track.RadiusAt(s) < 200f) continue;
            PlaceHazardModel(parent, cone, 1.5f, Surface(track.PointAt(s) + track.RightAt(s) * l), Quaternion.LookRotation(track.TangentAt(s)), $"Cone_{n++}");
        }
        // A toy block sliding across the right half of the final straight.
        float s2 = FindStraight(track, track.Length * 0.93f);
        Material blockMat = SaveVenueMaterial("ToyBlockHazard", Shader.Find("Standard"), m => { m.color = new Color(0.3f, 0.6f, 1f); m.SetFloat("_Glossiness", 0.35f); });
        PlaceSlidingBlock(parent, blockMat, Surface(track.PointAt(s2) + track.RightAt(s2) * 2.6f), track.RightAt(s2), track.TangentAt(s2),
            new Vector3(2.2f, 1.8f, 2.2f), 1.8f, 3.6f, "SlidingToyBlock");
        c.log.AppendLine($"hazards: {n} cones (s={s1:F0}), sliding toy block at s={s2:F0}");
    }

    // ============================================================================================
    // Neon Night Loop
    // ============================================================================================

    static readonly Color NeonPink = new Color(1f, 0.2f, 0.85f), NeonCyan = new Color(0.1f, 0.9f, 1f), NeonYellow = new Color(1f, 0.85f, 0.15f);

    static void BuildNeonVenue(VenueContext c)
    {
        var def = c.def;
        def.skybox = SkyMaterial("NeonSky", SaveTexture("NeonSky", GradientSky(
            new Color(0.42f, 0.1f, 0.4f), new Color(0.12f, 0.04f, 0.2f), new Color(0.01f, 0.01f, 0.04f), new Color(0.02f, 0.015f, 0.04f),
            1800, 0.25f, new Color(1f, 0.25f, 0.7f), 13), false, false));
        def.sunEuler = new Vector3(40f, 55f, 0f);
        def.sunColor = new Color(0.55f, 0.45f, 1f);
        def.sunIntensity = 0.32f;
        def.sunShadowStrength = 0.55f;
        def.ambientSky = new Color(0.26f, 0.16f, 0.4f);
        def.ambientEquator = new Color(0.16f, 0.09f, 0.24f);
        def.ambientGround = new Color(0.05f, 0.04f, 0.08f);
        def.fog = true;
        def.fogColor = new Color(0.1f, 0.04f, 0.17f);
        def.fogDensity = 0.0045f;
        def.headlights = true;

        Texture2D grid = SaveTexture("NeonGrid", NeonGridTexture(), true);
        c.ground.GetComponent<Renderer>().sharedMaterial = SaveVenueMaterial("NeonGround", Shader.Find("Standard"), m =>
        {
            m.mainTexture = grid;
            m.mainTextureScale = new Vector2(c.groundSize.x / 16f, c.groundSize.z / 16f);
            m.color = Color.white;
            m.SetFloat("_Glossiness", 0.45f);
            m.EnableKeyword("_EMISSION");
            m.SetTexture("_EmissionMap", grid);
            m.SetColor("_EmissionColor", new Color(0.6f, 0.6f, 0.6f));
        });

        var venue = c.Deco("Venue");
        var deco = c.Deco("Decoration");
        var obstacles = new List<Bounds>(c.pieceBounds);
        PlaceFinishGate(c, deco);

        Material pink = Emissive("NeonPink", NeonPink, 2.6f), cyan = Emissive("NeonCyan", NeonCyan, 2.6f), yellow = Emissive("NeonYellow", NeonYellow, 2.2f);
        Material[] neon = { pink, cyan };
        var track = c.track;

        // Glowing strips along both road edges.
        int strips = 0;
        for (float s = 4f; s < track.Length - 4f; s += 9f)
        {
            Vector3 t = track.TangentAt(s), r = track.RightAt(s);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 p = track.PointAt(s) + r * side * (RoadWidth * 0.5f + 1.1f);
                p.y = 0.15f;
                if (OverTrack(p)) continue;
                GlowBox(venue, "NeonStrip", p, Quaternion.LookRotation(t), new Vector3(0.35f, 0.25f, 5.5f), neon[(side + 1) / 2]);
                strips++;
            }
        }

        // Neon arches over the track (posts outside the road, bar high above it).
        int arches = 0, lights = 0;
        float lastArch = -1000f;
        for (float s = 20f; s < track.Length - 10f; s += 8f)
        {
            if (s - lastArch < 90f) continue;
            Vector3 t = track.TangentAt(s), r = track.RightAt(s), p0 = track.PointAt(s);
            Material mat = neon[arches % 2];
            float half = RoadWidth * 0.5f + 1.8f, h = 8f;
            if (track.RadiusAt(s) < 150f || OverTrack(p0 + r * half) || OverTrack(p0 - r * half)) continue;
            lastArch = s;
            Quaternion rot = Quaternion.LookRotation(t);
            GlowBox(venue, "NeonArch", p0 + r * half + Vector3.up * h * 0.5f, rot, new Vector3(0.5f, h, 0.5f), mat);
            GlowBox(venue, "NeonArch", p0 - r * half + Vector3.up * h * 0.5f, rot, new Vector3(0.5f, h, 0.5f), mat);
            GlowBox(venue, "NeonArch", p0 + Vector3.up * h, rot, new Vector3(half * 2f + 0.5f, 0.5f, 0.5f), mat);
            if (lights < 6)
            {
                var lg = new GameObject("NeonLight");
                lg.transform.SetParent(venue);
                lg.transform.position = p0 + Vector3.up * (h - 1f);
                var l = lg.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = 26f;
                l.intensity = 2.2f;
                l.color = arches % 2 == 0 ? NeonPink : NeonCyan;
                l.shadows = LightShadows.None;
                lights++;
            }
            arches++;
        }

        // Light posts with coloured heads; city blocks with glowing windows around the circuit.
        GameObject post = Load(RacingKit + "lightPostModern.fbx");
        Bounds postB = BoundsOf(PrefabVertices(post));
        int posts = 0;
        for (float s = 50f; s < track.Length - 20f; s += 60f)
        {
            Vector3 r = track.RightAt(s);
            int side = posts % 2 == 0 ? 1 : -1;
            Vector3 foot = track.PointAt(s) + r * side * (RoadWidth * 0.5f + 3.5f);
            foot.y = 0f;
            if (OverTrack(foot) || Overlaps(obstacles, foot, 2f)) continue;
            var pgo = PlaceProp(post, deco, 7f / postB.size.y, foot, Quaternion.LookRotation(-r * side), "LightPost");
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "LampHead";
            Object.DestroyImmediate(head.GetComponent<Collider>());
            head.transform.SetParent(pgo.transform, true);
            head.transform.position = foot + Vector3.up * 7.2f;
            head.transform.localScale = Vector3.one * 0.8f / pgo.transform.lossyScale.x;
            head.GetComponent<Renderer>().sharedMaterial = posts % 3 == 2 ? yellow : neon[posts % 2];
            obstacles.Add(RendererBounds(pgo));
            posts++;
        }
        Texture2D windows = SaveTexture("NeonWindows", WindowsTexture(), true);
        Material windowMat = SaveVenueMaterial("NeonWindows", Shader.Find("Standard"), m =>
        {
            m.mainTexture = windows;
            m.color = Color.white;
            m.EnableKeyword("_EMISSION");
            m.SetTexture("_EmissionMap", windows);
            m.SetColor("_EmissionColor", new Color(1.6f, 1.6f, 1.6f));
        });
        var rng = new Random(71);
        int towers = 0;
        for (int attempt = 0; attempt < 400 && towers < 22; attempt++)
        {
            // Around the outside of the circuit, inside the perimeter walls.
            var p = new Vector3(Mathf.Lerp(c.trackBounds.min.x - 35f, c.trackBounds.max.x + 35f, (float)rng.NextDouble()), 0f,
                                Mathf.Lerp(c.trackBounds.min.z - 35f, c.trackBounds.max.z + 35f, (float)rng.NextDouble()));
            float w = 8f + (float)rng.NextDouble() * 8f, d = 8f + (float)rng.NextDouble() * 8f, h = 14f + (float)rng.NextDouble() * 30f;
            if (!InsideGround(c, p, Mathf.Max(w, d)) || Overlaps(obstacles, p, Mathf.Max(w, d) * 0.6f + 5f) || OverTrack(p)) continue;
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = "Tower";
            b.transform.SetParent(deco);
            b.transform.position = p + Vector3.up * h * 0.5f;
            b.transform.localScale = new Vector3(w, h, d);
            b.GetComponent<Renderer>().sharedMaterial = windowMat;
            var roofTrim = GlowBox(deco, "TowerTrim", p + Vector3.up * (h + 0.2f), Quaternion.identity, new Vector3(w + 0.4f, 0.4f, d + 0.4f), neon[towers % 2]);
            roofTrim.transform.SetParent(b.transform, true);
            obstacles.Add(new Bounds(p, new Vector3(w, 1f, d)));
            towers++;
        }
        c.log.AppendLine($"neon: night sky + purple fog + glowing grid floor; {strips} edge strips, {arches} arches, {lights} coloured lights, {posts} light posts, {towers} towers");
    }

    static void BuildNeonHazards(VenueContext c)
    {
        var parent = c.Deco("Hazards");
        Material mat = Emissive("NeonHazard", NeonYellow, 1.6f);
        var track = c.track;
        int n = 0;
        // Blocks sliding across the whole road with different rhythms: time the gap.
        // Each block sweeps two thirds of the road: time the gap or take the open lane (alternating side).
        foreach (var (fraction, period) in new[] { (0.3f, 3.2f), (0.6f, 2.6f), (0.86f, 3.8f) })
        {
            float s = FindStraight(track, track.Length * fraction) + 4f;
            float side = n % 2 == 0 ? 1f : -1f;
            Vector3 p = Surface(track.PointAt(s) + track.RightAt(s) * side * RoadWidth * 0.16f);
            PlaceSlidingBlock(parent, mat, p, track.RightAt(s), track.TangentAt(s), new Vector3(1.8f, 1.4f, 1.8f), RoadWidth * 0.2f, period, $"NeonBlock_{n++}");
        }
        c.log.AppendLine($"hazards: {n} neon blocks sliding across the road");
    }

    // ============================================================================================
    // Generated textures
    // ============================================================================================

    static Texture2D GrassTexture(Color baseColor, int seed)
    {
        const int n = 512;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color[n * n];
        var rng = new Random(seed);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = x / (float)n, v = y / (float)n;
                float noise = Noise(u * 8f, v * 8f, 4) * (1 - u) * (1 - v) + Noise((u - 1) * 8f, v * 8f, 4) * u * (1 - v)
                            + Noise(u * 8f, (v - 1) * 8f, 4) * (1 - u) * v + Noise((u - 1) * 8f, (v - 1) * 8f, 4) * u * v;
                float stripe = (x / (n / 2)) % 2 == 0 ? 1.06f : 0.94f;
                var c = baseColor * (stripe + noise * 0.6f + (float)rng.NextDouble() * 0.07f);
                c.a = 1f;
                px[y * n + x] = c;
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    /// <summary>Kids' play carpet: pastel squares with a soft grid line and a little fluff noise.</summary>
    static Texture2D PlayMatTexture()
    {
        const int n = 512, cells = 4;
        Color[] pastel =
        {
            new Color(0.98f, 0.72f, 0.72f), new Color(0.7f, 0.85f, 1f), new Color(1f, 0.93f, 0.62f), new Color(0.72f, 0.92f, 0.7f),
            new Color(0.85f, 0.75f, 1f), new Color(1f, 0.82f, 0.6f),
        };
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color[n * n];
        var rng = new Random(8);
        int cs = n / cells;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int cx = x / cs, cy = y / cs;
                Color c = pastel[(cx * 3 + cy * 5 + cx * cy) % pastel.Length];
                int lx = x % cs, ly = y % cs;
                bool edge = lx < 5 || ly < 5 || lx > cs - 6 || ly > cs - 6;
                if (edge) c *= 0.82f;
                c *= (0.94f + (float)rng.NextDouble() * 0.08f) * 0.85f;
                c.a = 1f;
                px[y * n + x] = c;
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    /// <summary>Near-black floor with glowing cyan/pink grid lines (also used as the emission map).</summary>
    static Texture2D NeonGridTexture()
    {
        const int n = 256;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color[n * n];
        var rng = new Random(4);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float g = 0.035f + (float)rng.NextDouble() * 0.02f;
                Color c = new Color(g, g * 0.9f, g * 1.3f, 1f);
                int dx = Mathf.Min(x % 128, 128 - x % 128), dy = Mathf.Min(y % 128, 128 - y % 128);
                if (dx < 2 || dy < 2) c = Color.Lerp(c, (x / 128 + y / 128) % 2 == 0 ? new Color(0.1f, 0.75f, 0.9f) : new Color(0.85f, 0.2f, 0.75f), 0.85f);
                else if (dx < 5 || dy < 5) c += new Color(0.04f, 0.05f, 0.08f);
                px[y * n + x] = c;
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    /// <summary>Dark facade with a grid of lit (warm/cyan/pink) and unlit windows.</summary>
    static Texture2D WindowsTexture()
    {
        const int n = 256;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color[n * n];
        var rng = new Random(12);
        Color[] lit = { new Color(1f, 0.85f, 0.55f), new Color(0.4f, 0.9f, 1f), new Color(1f, 0.45f, 0.9f) };
        var windowColor = new Color[8, 8];
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++)
                windowColor[i, j] = rng.NextDouble() < 0.45 ? lit[rng.Next(lit.Length)] * 0.8f : new Color(0.05f, 0.05f, 0.08f);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int cx = x / 32, cy = y / 32, lx = x % 32, ly = y % 32;
                bool window = lx > 6 && lx < 26 && ly > 8 && ly < 24;
                px[y * n + x] = window ? windowColor[cx, cy] : new Color(0.06f, 0.055f, 0.08f);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    /// <summary>
    /// Panoramic sky: horizon -> mid -> zenith gradient, darker below the horizon, optional stars,
    /// a warm glow band on the horizon (glowStrength) and soft clouds (cloudAmount).
    /// </summary>
    static Texture2D GradientSky(Color horizon, Color mid, Color zenith, Color ground, int stars, float glowStrength, Color glow, int seed, float cloudAmount = 0f)
    {
        const int w = 2048, h = 1024;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color[w * h];
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            float v = y / (float)(h - 1) * 2f - 1f; // -1 bottom .. 1 top
            Color c;
            if (v >= 0f)
            {
                float t = Mathf.Pow(v, 0.6f);
                c = t < 0.35f ? Color.Lerp(horizon, mid, t / 0.35f) : Color.Lerp(mid, zenith, (t - 0.35f) / 0.65f);
                c += glow * glowStrength * Mathf.Exp(-v * 18f);
            }
            else c = Color.Lerp(horizon * 0.8f, ground, Mathf.Clamp01(-v * 6f));
            for (int x = 0; x < w; x++) px[y * w + x] = c;
        }
        if (cloudAmount > 0f)
        {
            for (int y = h / 2; y < h; y++)
            {
                float v = (y - h / 2) / (float)(h / 2);
                float band = Mathf.Clamp01(1f - Mathf.Abs(v - 0.25f) * 3f);
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)w;
                    float cn = Noise(Mathf.Cos(u * Mathf.PI * 2f) * 3f + 10f, Mathf.Sin(u * Mathf.PI * 2f) * 3f + v * 9f, 4);
                    float a = Mathf.Clamp01((cn + 0.05f) * 4f) * band * cloudAmount;
                    px[y * w + x] = Color.Lerp(px[y * w + x], Color.white, a);
                }
            }
        }
        for (int s = 0; s < stars; s++)
        {
            int x = rng.Next(w), y = h / 2 + 40 + rng.Next(h / 2 - 40);
            float b = Mathf.Pow((float)rng.NextDouble(), 3f) * 0.9f + 0.1f;
            px[y * w + x] += new Color(0.9f, 0.85f, 1f) * b;
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}
