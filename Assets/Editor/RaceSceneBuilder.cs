using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;
using Random = System.Random;

/// <summary>
/// Generates Assets/Scenes/Race.unity: a closed-loop track built from Kenney
/// racing-kit road tiles (sizes measured from mesh data), checkpoints, finish
/// gate, centerline waypoints, starting grid, 6 karts, character roster
/// (Kenney mini characters driving racing/toy cars), split-screen cameras,
/// the RaceManager (lobby, phone server, CPU racers) and decoration.
/// Batchmode: -executeMethod RaceSceneBuilder.Build
/// </summary>
public static partial class RaceSceneBuilder
{
    const string RacingKit = "Assets/ThirdParty/Kenney/RacingKit/";
    const string ToyKit = "Assets/ThirdParty/Kenney/ToyCarKit/";
    const string MiniChars = "Assets/ThirdParty/Kenney/MiniCharacters/";
    const string RosterPath = "Assets/Generated/CharacterRoster.asset";
    const string ScenePath = "Assets/Scenes/Race.unity";
    const string GeneratedDir = "Assets/Generated";
    const string Tag = "[RaceSceneBuilder] ";

    static float RoadWidth = 12f;     // metres, full width of the track being built (set per track)
    const float KartWidth = 1.8f;     // metres
    const float PropScale = 1f;       // racing-kit props (grandstands, flags) are already in metres x10

    const int KartCount = 6;
    const float KartLength = 3.3f;    // collider length shared by all karts
    const float DriverHeight = 1.25f; // seated driver height in metres

    // Roster: driver model, kart model, stats (speed, accel, handling), UI colour, seat position along the
    // kart (fraction of length, + = forward) and how much of the seated driver shows above the cockpit.
    // Stat sums stay ~3.0.
    static readonly (string name, string driver, string kart, float spd, float acc, float han, string color, float seat, float visible)[] RosterTable =
    {
        ("Rusty Rocket",   "character-male-a",   "RacingKit/raceCarRed",            1.00f, 1.00f, 1.00f, "#E85550", -0.08f, 0.55f),
        ("Piper Piston",   "character-female-a", "RacingKit/raceCarGreen",          0.97f, 1.06f, 0.97f, "#4CC46A", -0.08f, 0.55f),
        ("Max Muffler",    "character-male-b",   "RacingKit/raceCarOrange",         1.05f, 0.96f, 0.99f, "#F39A3B", -0.08f, 0.55f),
        ("Luna Lugnut",    "character-female-b", "RacingKit/raceCarWhite",          0.97f, 0.98f, 1.05f, "#E8EEF4", -0.08f, 0.55f),
        ("Benny Burnout",  "character-male-c",   "ToyCarKit/vehicle-drag-racer",    1.07f, 0.97f, 0.94f, "#FF7A45", -0.08f, 0.55f),
        ("Dash Dynamo",    "character-male-d",   "ToyCarKit/vehicle-monster-truck", 0.95f, 1.09f, 0.96f, "#A778E8", 0.02f, 0.38f),
        ("Ruby Revs",      "character-female-c", "ToyCarKit/vehicle-racer-low",     1.02f, 0.97f, 1.03f, "#E8506E", -0.08f, 0.55f),
        ("Nova Nitro",     "character-female-d", "ToyCarKit/vehicle-racer",         1.03f, 1.00f, 0.98f, "#5AB0FF", -0.08f, 0.55f),
        ("Sally Skid",     "character-female-e", "ToyCarKit/vehicle-speedster",     1.06f, 0.94f, 1.00f, "#FFD447", -0.08f, 0.55f),
        ("Majo",           "character-female-f", "ToyCarKit/vehicle-suv",           0.96f, 1.04f, 1.01f, "#62D6C8", -0.08f, 0.55f),
        ("Otto Overdrive", "character-male-e",   "ToyCarKit/vehicle-truck",         0.97f, 1.07f, 0.96f, "#C7C7C7", 0.24f, 0.38f),
        ("Samuel",         "character-male-f",   "ToyCarKit/vehicle-vintage-racer", 0.98f, 0.98f, 1.05f, "#B98B5E", -0.08f, 0.55f),
    };

    struct Opening
    {
        public Vector3 pos; // root-local, unscaled
        public Vector3 dir; // outward, axis aligned
    }

    class PieceDef
    {
        public string name;
        public GameObject prefab;
        public Opening[] openings;
    }

    static StringBuilder buildLog;

    [MenuItem("Tools/Kart/Build Race Scene")]
    public static void Build()
    {
        try
        {
            BuildInternal();
        }
        catch (Exception e)
        {
            Debug.LogError(Tag + "FAILED: " + e + "\nPartial report:\n" + buildLog);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ============================================================================================
    // Track placement
    // ============================================================================================

    /// <summary>
    /// Places a piece so one of its connectors meets the cursor. Chooses yaw (multiple of 90) and which
    /// connector is the entry so the piece turns (+1 right / -1 left / 0 straight), shifts sideways
    /// (curve: +1 right / -1 left) and climbs (+1 up / -1 down / 0 level) as requested.
    /// Connectors carry height, so elevation changes chain exactly. Advances the cursor to the exit.
    /// </summary>
    static GameObject PlacePiece(PieceDef def, int turn, int shift, int climb, float scale, ref Vector3 cursor, ref Vector3 heading,
        Transform parent, string name)
    {
        Vector3 right = Vector3.Cross(Vector3.up, heading);
        for (int m = 0; m < 2; m++)
        for (int k = 0; k < 4; k++)
        {
            bool mirror = m == 1; // mirrored copy (negative X scale), e.g. an S-curve shifting the other way
            Quaternion rot = Quaternion.Euler(0f, 90f * k, 0f);
            for (int e = 0; e < 2; e++)
            {
                Opening entry = Mirror(def.openings[e], mirror), exit = Mirror(def.openings[1 - e], mirror);
                if (Vector3.Dot(rot * entry.dir, -heading) < 0.99f) continue;
                Vector3 exitDir = rot * exit.dir;
                float cross = Vector3.Cross(heading, exitDir).y; // > 0 means right turn in Unity
                if (turn == 0 && Vector3.Dot(exitDir, heading) < 0.99f) continue;
                if (turn > 0 && cross < 0.5f) continue;
                if (turn < 0 && cross > -0.5f) continue;
                Vector3 delta = rot * ((exit.pos - entry.pos) * scale);
                if (shift != 0 && Vector3.Dot(delta, right) * shift < 0.1f) continue;
                if (climb != 0 && delta.y * climb < 0.1f) continue;
                if (climb == 0 && Mathf.Abs(delta.y) > 0.1f) continue;

                Vector3 pos = cursor - rot * (entry.pos * scale);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(def.prefab);
                go.name = name;
                go.transform.SetParent(parent, false);
                go.transform.SetPositionAndRotation(pos, rot);
                go.transform.localScale = new Vector3(mirror ? -scale : scale, scale, scale);
                cursor = pos + rot * (exit.pos * scale);
                heading = SnapAxis(exitDir);
                return go;
            }
        }
        throw new Exception($"No orientation of {def.name} matches heading {heading}, turn {turn}, shift {shift}, climb {climb}");
    }

    /// <summary>
    /// One merged, welded collision mesh for the whole track. Per-piece colliders left two coincident
    /// vertical end faces (slab + wall ends) at every joint, and the kart's capsule caught on them at
    /// speed: a sudden full stop ("frenon") at seams, also at the hill joints. Here the joint-plane
    /// faces are dropped and shared vertices welded, so the road is one continuous surface.
    /// Submesh 0 = road/curbs/walls, submesh 1 = grass (off-road).
    /// </summary>
    static void BuildTrackCollision(Transform trackPieces, List<(Vector3 pos, Vector3 dir)> joints, string assetName, StringBuilder log)
    {
        const float planeEps = 0.03f, weld = 0.01f;
        var verts = new List<Vector3>();
        var index = new Dictionary<Vector3Int, int>();
        var tris = new[] { new List<int>(), new List<int>() };
        int total = 0, removed = 0;

        int Vert(Vector3 v)
        {
            var key = new Vector3Int(Mathf.RoundToInt(v.x / weld), Mathf.RoundToInt(v.y / weld), Mathf.RoundToInt(v.z / weld));
            if (!index.TryGetValue(key, out int i)) { i = verts.Count; verts.Add(v); index[key] = i; }
            return i;
        }

        bool OnJointPlane(Vector3 a, Vector3 b, Vector3 c)
        {
            foreach (var (pos, dir) in joints)
            {
                bool all = true;
                foreach (var v in new[] { a, b, c })
                {
                    Vector3 d = v - pos;
                    if (Mathf.Abs(Vector3.Dot(d, dir)) > planeEps || d.magnitude > RoadWidth * 1.2f) { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        foreach (var mf in trackPieces.GetComponentsInChildren<MeshFilter>())
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Matrix4x4 m = mf.transform.localToWorldMatrix;
            bool flip = m.determinant < 0f;
            Vector3[] mv = mesh.vertices;
            var world = new Vector3[mv.Length];
            for (int i = 0; i < mv.Length; i++) world[i] = m.MultiplyPoint3x4(mv[i]);
            Material[] mats = mf.GetComponent<MeshRenderer>()?.sharedMaterials ?? new Material[0];
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                bool grass = sm < mats.Length && mats[sm] != null && mats[sm].name.ToLowerInvariant().Contains("grass");
                int[] t = mesh.GetTriangles(sm);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    total++;
                    Vector3 a = world[t[i]], b = world[t[i + 1]], c = world[t[i + 2]];
                    if (OnJointPlane(a, b, c)) { removed++; continue; }
                    int ia = Vert(a), ib = Vert(b), ic = Vert(c);
                    if (ia == ib || ib == ic || ia == ic) { removed++; continue; }
                    var list = tris[grass ? 1 : 0];
                    if (flip) { list.Add(ia); list.Add(ic); list.Add(ib); }
                    else { list.Add(ia); list.Add(ib); list.Add(ic); }
                }
            }
        }

        var col = new Mesh { name = assetName, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        col.SetVertices(verts);
        col.subMeshCount = 2;
        col.SetTriangles(tris[0], 0);
        col.SetTriangles(tris[1], 1);
        col.RecalculateNormals();
        col.RecalculateBounds();
        EnsureFolder(GeneratedDir);
        string path = $"{GeneratedDir}/{assetName}.asset"; // one asset per track
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(col, path);

        var go = new GameObject("TrackCollision");
        go.transform.SetParent(trackPieces, false);
        go.isStatic = true;
        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = col;
        var surface = go.AddComponent<TrackSurface>();
        surface.offroadSubmeshes = new[] { false, true };
        log.AppendLine($"track collision: merged {total} triangles into {tris[0].Count / 3 + tris[1].Count / 3} " +
                       $"(removed {removed} joint-plane/degenerate), {verts.Count} welded vertices");
    }

    static Opening Mirror(Opening o, bool mirror) =>
        mirror ? new Opening { pos = new Vector3(-o.pos.x, o.pos.y, o.pos.z), dir = new Vector3(-o.dir.x, o.dir.y, o.dir.z) } : o;

    /// <summary>
    /// Toy-kit connectors: small clamp tabs (4 vertices, ~5% of the track width wide) sticking out of a
    /// bounding-box face. The joint plane sits one tab depth (10% of the width) inside the face.
    /// Returns joint centre (road bottom height) and outward direction.
    /// </summary>
    static Opening[] FindConnectors(List<Vector3> verts, float width)
    {
        Bounds b = BoundsOf(verts);
        float eps = width * 0.005f;
        var result = new List<Opening>();
        foreach (int axis in new[] { 0, 2 })
        {
            int other = axis == 0 ? 2 : 0;
            foreach (int sign in new[] { -1, 1 })
            {
                float face = sign < 0 ? b.min[axis] : b.max[axis];
                float lo = float.MaxValue, hi = float.MinValue, ylo = float.MaxValue, yhi = float.MinValue;
                int count = 0;
                foreach (var v in verts)
                {
                    if (Mathf.Abs(v[axis] - face) > eps) continue;
                    count++;
                    lo = Mathf.Min(lo, v[other]);
                    hi = Mathf.Max(hi, v[other]);
                    ylo = Mathf.Min(ylo, v.y);
                    yhi = Mathf.Max(yhi, v.y);
                }
                if (count < 3 || hi - lo > width * 0.15f || yhi - ylo > width * 0.1f) continue;
                var p = Vector3.zero;
                p[axis] = face - sign * 0.2f; // tab depth: 0.2 kit units on wide and narrow pieces
                p[other] = (lo + hi) * 0.5f;
                p.y = ylo;
                var d = Vector3.zero;
                d[axis] = sign;
                result.Add(new Opening { pos = p, dir = d });
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Racing-kit road tiles: the road meets a bounding-box face along one full tile unit (flat, at
    /// ground level). Returns joint centre (on the face, road bottom) and outward direction.
    /// </summary>
    static Opening[] FindTileConnectors(List<Vector3> verts, float unit)
    {
        Bounds b = BoundsOf(verts);
        float eps = unit * 0.002f;
        var result = new List<Opening>();
        foreach (int axis in new[] { 0, 2 })
        {
            int other = axis == 0 ? 2 : 0;
            foreach (int sign in new[] { -1, 1 })
            {
                float face = sign < 0 ? b.min[axis] : b.max[axis];
                float lo = float.MaxValue, hi = float.MinValue, ylo = float.MaxValue, yhi = float.MinValue;
                int count = 0;
                foreach (var v in verts)
                {
                    if (Mathf.Abs(v[axis] - face) > eps) continue;
                    count++;
                    lo = Mathf.Min(lo, v[other]);
                    hi = Mathf.Max(hi, v[other]);
                    ylo = Mathf.Min(ylo, v.y);
                    yhi = Mathf.Max(yhi, v.y);
                }
                if (count < 6 || Mathf.Abs(hi - lo - unit) > unit * 0.05f || yhi - ylo > unit * 0.1f) continue;
                var p = Vector3.zero;
                p[axis] = face;
                p[other] = (lo + hi) * 0.5f;
                p.y = ylo;
                var d = Vector3.zero;
                d[axis] = sign;
                result.Add(new Opening { pos = p, dir = d });
            }
        }
        return result.ToArray();
    }

    /// <summary>Intermediate centerline points of an S-curve piece (cosine-eased lateral shift).</summary>
    static void AddCurve(List<Vector3> points, Vector3 entry, Vector3 dir, Vector3 exit, int segments)
    {
        Vector3 right = Vector3.Cross(Vector3.up, dir);
        Vector3 delta = exit - entry;
        float forward = Vector3.Dot(delta, dir), lateral = Vector3.Dot(delta, right);
        for (int i = 1; i < segments; i++)
        {
            float t = i / (float)segments;
            points.Add(entry + dir * forward * t + right * lateral * (0.5f - 0.5f * Mathf.Cos(Mathf.PI * t)) + Vector3.up * delta.y * t);
        }
    }

    /// <summary>A support column under an elevated joint, stretched down to the ground.</summary>
    static void PlaceSupport(GameObject prefab, Transform parent, Vector3 joint, Vector3 heading, float scale)
    {
        Bounds b = BoundsOf(PrefabVertices(prefab));
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = "Support";
        go.transform.SetParent(parent, false);
        Quaternion rot = Quaternion.LookRotation(heading);
        var s = new Vector3(scale, Mathf.Max(0.1f, joint.y) / b.size.y, scale * 0.6f);
        go.transform.localScale = s;
        go.transform.SetPositionAndRotation(
            new Vector3(joint.x, 0f, joint.z) - rot * Vector3.Scale(new Vector3(b.center.x, b.min.y, b.center.z), s), rot);
    }

    static void CreateTrigger(string name, Vector3 pos, Vector3 dir, int index, bool finishLine, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos + Vector3.up * 2.5f, Quaternion.LookRotation(dir));
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(RoadWidth * 1.6f, 5f, 2f);
        go.AddComponent<Checkpoint>().Configure(index, finishLine);
    }

    static void CreateWall(Transform parent, Material mat, Vector3 pos, Vector3 size)
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Wall";
        wall.transform.SetParent(parent, false);
        wall.transform.position = pos;
        wall.transform.localScale = size;
        wall.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // ============================================================================================
    // Kart
    // ============================================================================================

    /// <summary>Adds intermediate points of a 90-degree corner arc (turn: +1 right, -1 left).</summary>
    static void AddArc(List<Vector3> points, Vector3 entry, Vector3 entryDir, Vector3 exit, int turn, int segments)
    {
        Vector3 right = Vector3.Cross(Vector3.up, entryDir) * turn;
        float radius = Vector3.Dot(exit - entry, entryDir);
        Vector3 centre = entry + right * radius;
        for (int i = 1; i < segments; i++)
        {
            float a = Mathf.PI * 0.5f * i / segments;
            points.Add(centre + (-right * Mathf.Cos(a) + entryDir * Mathf.Sin(a)) * radius + Vector3.up * (exit.y - entry.y) * i / segments);
        }
    }

    // ============================================================================================
    // Karts and characters
    // ============================================================================================

    /// <summary>Physics body + controller; the visible model comes from the character roster.</summary>
    static KartController BuildKartShell(string name, Transform parent, int checkpointCount)
    {
        var kart = new GameObject(name);
        kart.transform.SetParent(parent, false);
        var visual = new GameObject("Visual").transform;
        visual.SetParent(kart.transform, false);

        var rb = kart.AddComponent<Rigidbody>();
        rb.mass = 150f;
        rb.linearDamping = 0f;
        rb.angularDamping = 0.05f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        // Headlights: emissive lamps on every kart; a real spot light that RaceManager enables for humans only.
        var lampMat = AssetDatabase.LoadAssetAtPath<Material>(GeneratedDir + "/Headlight.mat");
        if (lampMat == null)
        {
            lampMat = new Material(Shader.Find("Standard"));
            lampMat.EnableKeyword("_EMISSION");
            lampMat.SetColor("_EmissionColor", new Color(1f, 0.95f, 0.8f) * 4f);
            EnsureFolder(GeneratedDir);
            AssetDatabase.CreateAsset(lampMat, GeneratedDir + "/Headlight.mat");
        }
        for (int side = -1; side <= 1; side += 2)
        {
            var lamp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lamp.name = "HeadlightLamp";
            Object.DestroyImmediate(lamp.GetComponent<Collider>());
            lamp.transform.SetParent(visual, false);
            lamp.transform.localPosition = new Vector3(0.5f * side, 0.55f, 1.55f);
            lamp.transform.localScale = Vector3.one * 0.22f;
            lamp.GetComponent<Renderer>().sharedMaterial = lampMat;
            lamp.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        var beam = new GameObject("HeadlightBeam");
        beam.transform.SetParent(kart.transform, false);
        beam.transform.localPosition = new Vector3(0f, 1.1f, 1.2f);
        beam.transform.localRotation = Quaternion.Euler(12f, 0f, 0f);
        var spot = beam.AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.range = 30f;
        spot.spotAngle = 70f;
        spot.intensity = 0.5f;
        spot.color = new Color(1f, 0.95f, 0.85f);
        spot.shadows = LightShadows.None;
        spot.enabled = false;

        var capsule = kart.AddComponent<CapsuleCollider>();
        capsule.direction = 2;
        capsule.radius = 0.45f;
        capsule.height = KartLength * 0.62f; // shorter than the body so it does not dig into slopes
        capsule.center = new Vector3(0f, capsule.radius, 0f);

        var controller = kart.AddComponent<KartController>();
        controller.Configure(visual);
        kart.AddComponent<LapTracker>().Configure(checkpointCount, 3); // per track at runtime
        kart.AddComponent<KartItems>();
        kart.AddComponent<KartAudio>();
        return controller;
    }

    static CharacterRoster BuildRoster(StringBuilder log)
    {
        ConfigureCharacterImports();
        var roster = AssetDatabase.LoadAssetAtPath<CharacterRoster>(RosterPath);
        if (roster == null)
        {
            EnsureFolder(GeneratedDir);
            roster = ScriptableObject.CreateInstance<CharacterRoster>();
            AssetDatabase.CreateAsset(roster, RosterPath);
        }

        var defs = new List<CharacterDefinition>();
        var seats = new List<(CharacterDefinition def, string path, float top, float z, float visible, float sign)>();
        foreach (var e in RosterTable)
        {
            ColorUtility.TryParseHtmlString(e.color, out Color ui);
            var def = new CharacterDefinition
            {
                displayName = e.name,
                uiColor = ui,
                speed = e.spd,
                acceleration = e.acc,
                handling = e.han
            };

            string kartPath = $"Assets/ThirdParty/Kenney/{e.kart}.fbx";
            def.kart = MeasureKart(Load(kartPath), e.seat, out float cockpitTop, out float seatZ, out string kartInfo);

            string driverPath = MiniChars + e.driver + ".fbx";
            Object[] driverAssets = AssetDatabase.LoadAllAssetsAtPath(driverPath);
            def.driveClip = driverAssets.OfType<AnimationClip>().FirstOrDefault(c => c.name == "drive")
                            ?? driverAssets.OfType<AnimationClip>().FirstOrDefault(c => c.name == "sit");
            def.driverAvatar = driverAssets.OfType<Avatar>().FirstOrDefault();
            def.driver = MeasureDriver(Load(driverPath), def.driveClip, cockpitTop, seatZ, e.visible, 0f, out string driverInfo, out float sign);
            seats.Add((def, driverPath, cockpitTop, seatZ, e.visible, sign));

            defs.Add(def);
            log.AppendLine($"  character '{e.name}': kart {e.kart} [{kartInfo}], driver {e.driver} [{driverInfo}], " +
                           $"clip={(def.driveClip != null ? def.driveClip.name : "NONE")}, avatar={(def.driverAvatar != null)}");
        }

        // All mini characters share one rig orientation: use the majority facing for everyone.
        float majority = seats.Sum(x => x.sign) >= 0f ? 1f : -1f;
        foreach (var seat in seats.Where(x => x.sign != majority))
        {
            seat.def.driver = MeasureDriver(Load(seat.path), seat.def.driveClip, seat.top, seat.z, seat.visible, majority, out string fixedInfo, out _);
            log.AppendLine($"  driver facing overridden to majority for '{seat.def.displayName}': {fixedInfo}");
        }

        roster.characters = defs.ToArray();
        EditorUtility.SetDirty(roster);
        AssetDatabase.SaveAssets();
        log.AppendLine($"roster: {defs.Count} characters saved to {RosterPath}");
        return roster;
    }

    /// <summary>Mini characters: generic rig with the "drive" take looping.</summary>
    static void ConfigureCharacterImports()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { MiniChars.TrimEnd('/') }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) continue;
            bool changed = false;
            if (mi.animationType != ModelImporterAnimationType.Generic)
            {
                mi.animationType = ModelImporterAnimationType.Generic;
                changed = true;
            }
            if (mi.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                changed = true;
            }
            if (!mi.importAnimation)
            {
                mi.importAnimation = true;
                changed = true;
            }
            ModelImporterClipAnimation[] clips = mi.clipAnimations.Length > 0 ? mi.clipAnimations : mi.defaultClipAnimations;
            foreach (var clip in clips)
            {
                if ((clip.name == "drive" || clip.name == "sit") && !clip.loopTime)
                {
                    clip.loopTime = true;
                    changed = true;
                }
            }
            if (changed)
            {
                mi.clipAnimations = clips;
                mi.SaveAndReimport();
            }
        }
    }

    /// <summary>Front = +Z (from wheel names, else the lower end), width = KartWidth, wheels on y = 0.</summary>
    static ModelPlacement MeasureKart(GameObject prefab, float seatFraction, out float cockpitTop, out float seatZ, out string info)
    {
        var inst = Object.Instantiate(prefab);
        inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        inst.transform.localScale = Vector3.one;
        List<Vector3> verts = CollectVertices(inst);
        List<Vector3> triangles = CollectTriangles(inst);
        Bounds b = BoundsOf(verts);
        int axis = b.size.x > b.size.z ? 0 : 2;

        Vector3 frontSum = Vector3.zero, backSum = Vector3.zero;
        int fronts = 0, backs = 0;
        foreach (var r in inst.GetComponentsInChildren<Renderer>())
        {
            string n = r.gameObject.name.ToLowerInvariant();
            if (n.StartsWith("wheel-f")) { frontSum += r.bounds.center; fronts++; }
            else if (n.StartsWith("wheel-b")) { backSum += r.bounds.center; backs++; }
        }

        var frontLocal = Vector3.zero;
        string method;
        if (fronts > 0 && backs > 0)
        {
            float d = (frontSum / fronts - backSum / backs)[axis];
            frontLocal[axis] = d >= 0f ? 1f : -1f;
            method = "wheels";
        }
        else
        {
            float band = b.size[axis] * 0.2f;
            float maxYLow = float.MinValue, maxYHigh = float.MinValue;
            foreach (var v in verts)
            {
                if (v[axis] < b.min[axis] + band) maxYLow = Mathf.Max(maxYLow, v.y);
                if (v[axis] > b.max[axis] - band) maxYHigh = Mathf.Max(maxYHigh, v.y);
            }
            frontLocal[axis] = maxYLow < maxYHigh ? -1f : 1f; // spoiler end is the rear
            method = "height";
        }
        Object.DestroyImmediate(inst);

        Quaternion rot = YawFromTo(frontLocal, Vector3.forward);
        float width = axis == 0 ? b.size.z : b.size.x;
        float scale = KartWidth / width;
        Vector3 pos = -(rot * (new Vector3(b.center.x, b.min.y, b.center.z) * scale));

        // Cockpit height: highest surface (triangles, not just vertices) over a central
        // patch slightly behind the middle, where the driver sits.
        float length = b.size[axis] * scale;
        seatZ = seatFraction * length;
        cockpitTop = 0f;
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            Vector3 a = rot * (triangles[i] * scale) + pos;
            Vector3 c1 = rot * (triangles[i + 1] * scale) + pos;
            Vector3 c2 = rot * (triangles[i + 2] * scale) + pos;
            for (int sx = -1; sx <= 1; sx++)
                for (int sz = 0; sz <= 4; sz++)
                {
                    float x = sx * KartWidth * 0.15f;
                    float z = Mathf.Lerp(seatZ - length * 0.22f, seatZ + length * 0.12f, sz / 4f);
                    if (HeightOnTriangle(a, c1, c2, x, z, out float y)) cockpitTop = Mathf.Max(cockpitTop, y);
                }
        }
        info = $"front={frontLocal:F0} via {method}, scale={scale:F2}, length={length:F2} m, cockpitTop={cockpitTop:F2} m";
        return new ModelPlacement { prefab = prefab, position = pos, rotation = rot, scale = scale };
    }

    /// <summary>Poses the driver with its drive clip, faces it forward and seats it so head + torso show.</summary>
    static ModelPlacement MeasureDriver(GameObject prefab, AnimationClip clip, float cockpitTop, float seatZ, float visible, float forcedSign,
        out string info, out float detectedSign)
    {
        var inst = Object.Instantiate(prefab);
        inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        inst.transform.localScale = Vector3.one;
        if (clip != null) clip.SampleAnimation(inst, 0f);

        var verts = new List<Vector3>();
        Matrix4x4 toRoot = inst.transform.worldToLocalMatrix;
        foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            Matrix4x4 m = toRoot * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            foreach (var v in baked.vertices) verts.Add(m.MultiplyPoint3x4(v));
            Object.DestroyImmediate(baked);
        }
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices) verts.Add(m.MultiplyPoint3x4(v));
        }
        if (verts.Count == 0) throw new Exception($"Driver {prefab.name} has no mesh vertices");
        Bounds b = BoundsOf(verts);

        Transform torso = inst.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "torso");
        Vector3 hips = torso != null ? inst.transform.InverseTransformPoint(torso.position) : b.center;
        Object.DestroyImmediate(inst);

        // Seated pose: the legs (and arms on the wheel) reach forward of the torso.
        detectedSign = (b.max.z - hips.z) >= (hips.z - b.min.z) ? 1f : -1f;
        float frontSign = forcedSign != 0f ? forcedSign : detectedSign;
        Quaternion rot = frontSign > 0f ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f);
        float scale = DriverHeight / b.size.y;
        Vector3 hipsPlaced = rot * (hips * scale);
        var pos = new Vector3(-hipsPlaced.x, cockpitTop - DriverHeight * (1f - visible) - b.min.y * scale, seatZ - hipsPlaced.z);

        info = $"posed size={b.size:F2}, front={(frontSign > 0 ? "+Z" : "-Z")}, scale={scale:F2}, seatY={pos.y:F2}";
        return new ModelPlacement { prefab = prefab, position = pos, rotation = rot, scale = scale };
    }

    /// <summary>Triangle corners (3 per triangle) of all meshes, in the root's local space.</summary>
    static List<Vector3> CollectTriangles(GameObject root)
    {
        var list = new List<Vector3>();
        Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
            Vector3[] v = mesh.vertices;
            foreach (int index in mesh.triangles) list.Add(m.MultiplyPoint3x4(v[index]));
        }
        return list;
    }

    /// <summary>Height of the triangle at (x, z) if that point lies inside its XZ projection.</summary>
    static bool HeightOnTriangle(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
    {
        y = 0f;
        float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (Mathf.Abs(d) < 1e-8f) return false;
        float w1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
        float w2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
        float w3 = 1f - w1 - w2;
        if (w1 < 0f || w2 < 0f || w3 < 0f) return false;
        y = w1 * a.y + w2 * b.y + w3 * c.y;
        return true;
    }

    // ============================================================================================
    // Night lighting, venue textures, toon conversion
    // ============================================================================================

    const string VenueDir = GeneratedDir + "/Venue";

    /// <summary>Saves a generated texture as a PNG asset with smooth, tiling import settings.</summary>
    static Texture2D SaveTexture(string name, Texture2D tex, bool repeat, bool mipmaps = true)
    {
        EnsureFolder(VenueDir);
        string path = $"{VenueDir}/{name}.png";
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), path), tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        if (!repeat) ti.wrapModeU = TextureWrapMode.Repeat; // sky wraps horizontally only
        ti.mipmapEnabled = mipmaps;
        ti.filterMode = FilterMode.Trilinear;
        ti.anisoLevel = 8;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.maxTextureSize = 4096;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static float Noise(float x, float y, int octaves)
    {
        float sum = 0f, amp = 0.5f, freq = 1f;
        for (int i = 0; i < octaves; i++)
        {
            sum += (Mathf.PerlinNoise(x * freq + 13.7f * i, y * freq + 7.3f * i) - 0.5f) * amp;
            freq *= 2f;
            amp *= 0.5f;
        }
        return sum;
    }

    static Texture2D GrassTexture()
    {
        const int n = 512;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color[n * n];
        var rng = new Random(11);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // Tileable noise: sample on a torus by blending wrapped coordinates.
                float u = x / (float)n, v = y / (float)n;
                float noise = Noise(u * 8f, v * 8f, 4) * (1 - u) * (1 - v) + Noise((u - 1) * 8f, v * 8f, 4) * u * (1 - v)
                            + Noise(u * 8f, (v - 1) * 8f, 4) * (1 - u) * v + Noise((u - 1) * 8f, (v - 1) * 8f, 4) * u * v;
                float stripe = (x / (n / 2)) % 2 == 0 ? 1.06f : 0.94f; // mowed stripes
                float blade = (float)rng.NextDouble() * 0.06f;
                var c = new Color(0.16f, 0.36f, 0.17f) * (stripe + noise * 0.5f + blade);
                c.a = 1f;
                px[y * n + x] = c;
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D GravelTexture()
    {
        const int n = 256;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var rng = new Random(5);
        var px = new Color[n * n];
        for (int i = 0; i < px.Length; i++)
        {
            float g = 0.8f + (float)rng.NextDouble() * 0.35f;
            float warm = (float)rng.NextDouble() * 0.06f;
            px[i] = new Color(0.52f * g + warm, 0.47f * g + warm * 0.5f, 0.38f * g, 1f);
        }
        for (int s = 0; s < 900; s++) // pebbles
        {
            int cx = rng.Next(n), cy = rng.Next(n), r = 1 + rng.Next(3);
            float shade = 0.6f + (float)rng.NextDouble() * 0.6f;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r)
                    {
                        int xx = (cx + dx + n) % n, yy = (cy + dy + n) % n;
                        px[yy * n + xx] = new Color(0.55f * shade, 0.52f * shade, 0.47f * shade, 1f);
                    }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D AsphaltTexture()
    {
        const int n = 512;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var rng = new Random(9);
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float g = 0.2f + (float)rng.NextDouble() * 0.05f + Noise(x / 60f, y / 60f, 3) * 0.05f;
                bool line = (x % 128 < 4) || (y > n - 8); // parking bays + a cross line
                px[y * n + x] = line ? new Color(0.85f, 0.85f, 0.8f, 1f) : new Color(g, g, g * 1.05f, 1f);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D SkyTexture()
    {
        const int w = 2048, h = 1024;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color[w * h];
        var rng = new Random(3);
        var horizon = new Color(0.13f, 0.17f, 0.32f);
        var zenith = new Color(0.01f, 0.02f, 0.07f);
        var ground = new Color(0.03f, 0.035f, 0.05f);
        for (int y = 0; y < h; y++)
        {
            float v = y / (float)(h - 1) * 2f - 1f; // -1 bottom .. 1 top
            Color c = v >= 0 ? Color.Lerp(horizon, zenith, Mathf.Pow(v, 0.55f)) : Color.Lerp(horizon, ground, Mathf.Clamp01(-v * 6f));
            for (int x = 0; x < w; x++) px[y * w + x] = c;
        }
        for (int s = 0; s < 2600; s++) // stars (denser and brighter higher up)
        {
            int x = rng.Next(w), y = h / 2 + 20 + rng.Next(h / 2 - 20);
            float b = Mathf.Pow((float)rng.NextDouble(), 3f) * 0.9f + 0.1f;
            var star = new Color(0.8f + 0.2f * b, 0.85f + 0.15f * b, 1f) * b;
            px[y * w + x] += star;
            if (b > 0.75f)
            {
                px[y * w + (x + 1) % w] += star * 0.4f;
                if (y + 1 < h) px[(y + 1) * w + x] += star * 0.4f;
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D PoolTexture()
    {
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                float a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a);
                px[y * n + x] = new Color(a, a, a, a);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Material SaveVenueMaterial(string name, Shader shader, Action<Material> setup)
    {
        EnsureFolder(VenueDir);
        string path = $"{VenueDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        setup(mat);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static GameObject FlatQuad(string name, Transform parent, Vector3 centre, Vector3 forward, float width, float length, Material mat)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = name;
        Object.DestroyImmediate(q.GetComponent<Collider>());
        q.transform.SetParent(parent, false);
        q.transform.position = centre;
        q.transform.rotation = Quaternion.LookRotation(Vector3.down, forward); // face up
        q.transform.localScale = new Vector3(width, length, 1f);
        var r = q.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return q;
    }

    static bool OverTrack(Vector3 p)
    {
        if (!Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore)) return false;
        return hit.collider.GetComponent<TrackSurface>() != null;
    }

    static void BuildNightVenue(Transform root, TrackDefinition def, RaceTrack track, GameObject ground, Vector3 groundSize,
        List<(Vector3 entry, Vector3 dir, Vector3 exit, int turn)> corners, StringBuilder log)
    {
        Physics.SyncTransforms();
        Shader lit = Shader.Find("Standard");
        Shader additive = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");

        // ---- Sky, moon, ambient, fog --------------------------------------------------------
        Texture2D sky = SaveTexture("NightSky", SkyTexture(), false, false);
        Material skyMat = SaveVenueMaterial("NightSky", Shader.Find("Skybox/Panoramic"), m =>
        {
            m.SetTexture("_MainTex", sky);
            m.SetFloat("_Exposure", 1f);
        });
        def.skybox = skyMat;
        def.sunEuler = new Vector3(38f, -40f, 0f); // moonlight
        def.sunColor = new Color(0.55f, 0.66f, 1f);
        def.sunIntensity = 0.38f;
        def.sunShadowStrength = 0.6f;
        def.ambientSky = new Color(0.25f, 0.29f, 0.46f);
        def.ambientEquator = new Color(0.17f, 0.18f, 0.26f);
        def.ambientGround = new Color(0.07f, 0.075f, 0.09f);
        def.fog = true;
        def.fogColor = new Color(0.05f, 0.07f, 0.14f);
        def.fogDensity = 0.0035f;
        def.headlights = true;

        // ---- Ground: mowed grass --------------------------------------------------------------
        Texture2D grass = SaveTexture("Grass", GrassTexture(), true);
        Material grassMat = SaveVenueMaterial("Grass", lit, m =>
        {
            m.mainTexture = grass;
            m.mainTextureScale = new Vector2(groundSize.x / 24f, groundSize.z / 24f);
            m.color = Color.white;
            m.SetFloat("_Glossiness", 0.05f);
        });
        ground.GetComponent<Renderer>().sharedMaterial = grassMat;

        var venue = new GameObject("Venue").transform;
        venue.SetParent(root);

        // ---- Gravel traps outside every corner -----------------------------------------------
        Texture2D gravel = SaveTexture("Gravel", GravelTexture(), true);
        Material gravelMat = SaveVenueMaterial("Gravel", lit, m =>
        {
            m.mainTexture = gravel;
            m.mainTextureScale = new Vector2(3f, 3f);
            m.SetFloat("_Glossiness", 0f);
        });
        int traps = 0;
        foreach (var c in corners)
        {
            Vector3 right = Vector3.Cross(Vector3.up, c.dir) * c.turn;
            float radius = Vector3.Dot(c.exit - c.entry, c.dir);
            Vector3 centre = c.entry + right * radius;
            Vector3 outward = (-right + c.dir).normalized;
            Vector3 p = centre + outward * (radius + RoadWidth * 0.5f + 9f);
            p.y = -0.015f;
            if (c.entry.y > 0.5f || OverTrack(p)) continue;
            FlatQuad("GravelTrap", venue, p, outward, 22f, 14f, gravelMat);
            traps++;
        }

        // ---- Paddock asphalt with painted bays, infield of the start straight ---------------------
        Texture2D asphalt = SaveTexture("Paddock", AsphaltTexture(), true);
        Material paddockMat = SaveVenueMaterial("Paddock", lit, m =>
        {
            m.mainTexture = asphalt;
            m.mainTextureScale = new Vector2(3f, 1f);
            m.SetFloat("_Glossiness", 0.15f);
        });
        Vector3 paddockCentre = new Vector3(track.RoadWidth * 0.5f + 20f, -0.012f, 64f);
        bool paddock = !OverTrack(paddockCentre + new Vector3(-14f, 0, -42f)) && !OverTrack(paddockCentre + new Vector3(14f, 0, 42f))
                       && !OverTrack(paddockCentre);
        if (paddock) FlatQuad("Paddock", venue, paddockCentre, Vector3.forward, 30f, 90f, paddockMat);

        // ---- Light posts: emissive heads, a few real spot lights, fake light pools --------------------
        GameObject postPrefab = Load(RacingKit + "lightPostLarge.fbx");
        Bounds pb = BoundsOf(PrefabVertices(postPrefab));
        Material lampMat = SaveVenueMaterial("LampHead", lit, m =>
        {
            m.color = new Color(1f, 0.95f, 0.8f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", new Color(1f, 0.85f, 0.6f) * 5f);
        });
        Texture2D poolTex = SaveTexture("LightPool", PoolTexture(), false);
        Material poolMat = SaveVenueMaterial("LightPool", additive, m =>
        {
            m.mainTexture = poolTex;
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.42f, 0.34f, 0.22f, 0.5f)); // legacy additive doubles the tint
            else m.color = new Color(1f, 0.82f, 0.55f, 0.3f);
        });
        var posts = new GameObject("LightPosts").transform;
        posts.SetParent(venue);
        int postCount = 0, realLights = 0, pools = 0;
        const int MaxRealLights = 8;
        for (float s = 10f; s < track.Length - 5f; s += 62f)
        {
            Vector3 onTrack = track.PointAt(s);
            if (onTrack.y > 0.6f) continue; // elevated section: no posts under it
            Vector3 tangent = track.TangentAt(s);
            Vector3 right = track.RightAt(s);
            float bend = Vector3.Cross(tangent, track.TangentAt(s + 20f)).y;
            int side = Mathf.Abs(bend) > 0.05f ? (bend > 0 ? -1 : 1) : (postCount % 2 == 0 ? -1 : 1); // outside of bends
            Vector3 foot = onTrack + right * side * (RoadWidth * 0.5f + 2.6f);
            foot.y = 0f;
            if (OverTrack(foot)) continue;
            Quaternion rot = Quaternion.LookRotation(-right * side); // arm towards the road
            var post = PlaceProp(postPrefab, posts, PropScale, foot, rot, "LightPost");
            Vector3 head = foot + Vector3.up * (pb.size.y * PropScale - 0.3f) - right * side * 0.6f;
            var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulb.name = "LampHead";
            Object.DestroyImmediate(bulb.GetComponent<Collider>());
            bulb.transform.SetParent(post.transform, true);
            bulb.transform.position = head;
            bulb.transform.localScale = Vector3.one * 0.9f / PropScale;
            bulb.GetComponent<Renderer>().sharedMaterial = lampMat;
            bulb.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Vector3 poolCentre = Surface(onTrack + right * side * 1.5f) + Vector3.up * 0.05f;
            FlatQuad("LightPool", posts, poolCentre, tangent, 24f, 24f, poolMat);
            pools++;

            if (realLights < MaxRealLights && postCount % 2 == 0)
            {
                var lightGo = new GameObject("PostLight");
                lightGo.transform.SetParent(post.transform, true);
                lightGo.transform.position = head - Vector3.up * 0.4f;
                lightGo.transform.rotation = Quaternion.LookRotation((poolCentre - head).normalized);
                var l = lightGo.AddComponent<Light>();
                l.type = LightType.Spot;
                l.range = 34f;
                l.spotAngle = 115f;
                l.intensity = 1.8f;
                l.color = new Color(1f, 0.88f, 0.7f);
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.Auto;
                realLights++;
            }
            postCount++;
        }

        // ---- Glow: item boxes, finish gate ------------------------------------------------------
        foreach (var box in root.GetComponentsInChildren<ItemBox>(true))
            FlatQuad("BoxGlow", box.transform, Surface(box.transform.position) + Vector3.up * 0.04f, Vector3.forward, 5f, 5f, poolMat);

        log.AppendLine($"night: sky + moon + trilight ambient + fog; {postCount} light posts, {realLights} real spot lights, {pools} light pools; " +
                       $"venue: grass texture, {traps} gravel traps, paddock={paddock}");
    }

    /// <summary>
    /// Converts every Standard material in the scene to the toon shaders (saved as assets).
    /// Track pieces, ground and venue surfaces get no outline; everything else is outlined.
    /// Item boxes, lamp heads and the finish gate keep/gain emission for bloom.
    /// </summary>
    static void ConvertSceneToToon(GameObject managerGo, StringBuilder log)
    {
        Shader toon = Shader.Find(ToonStyle.ToonShaderName);
        Shader outline = Shader.Find(ToonStyle.OutlineShaderName);
        Shader bloom = Shader.Find("Hidden/KartBloom");
        if (toon == null || outline == null || bloom == null) throw new Exception("Toon/bloom shaders not found");
        managerGo.AddComponent<ToonStyle>().Configure(toon, outline, bloom);

        const string dir = GeneratedDir + "/Toon";
        EnsureFolder(dir);
        var cache = new Dictionary<(Material, bool), Material>();
        int renderers = 0;
        var written = new HashSet<string>();
        // Sorted by hierarchy path so generated names are stable between builds (no orphaned copies).
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include)
                     .OrderBy(x => OrderKey(x.transform), StringComparer.Ordinal).ThenBy(x => x.GetType().Name, StringComparer.Ordinal))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer) continue;
            string path = PathOf(r.transform);
            bool surface = path.Contains("/Pieces/") || r.name == "Ground" || path.Contains("/Venue/") && !path.Contains("LightPost") || r.name == "Wall";
            bool glow = path.Contains("ItemBox_") || path.Contains("FinishGate");
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material src = mats[i];
                if (src == null) continue;
                bool fresh = !cache.ContainsKey((src, !surface));
                Material m = ToonStyle.Convert(src, !surface, toon, outline, cache);
                if (m == src) continue;
                if (fresh)
                {
                    if (glow)
                    {
                        m.SetColor("_EmissionColor", Color.white * (path.Contains("ItemBox_") ? 2.2f : 0.7f));
                        m.SetTexture("_EmissionMap", m.GetTexture("_MainTex"));
                    }
                    string file = $"{dir}/{Sanitize(m.name)}_{cache.Count}.mat";
                    AssetDatabase.CreateAsset(m, file);
                    written.Add(file);
                }
                mats[i] = m;
                changed = true;
            }
            if (changed)
            {
                r.sharedMaterials = mats;
                renderers++;
            }
        }
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { dir }))
        {
            string stale = AssetDatabase.GUIDToAssetPath(guid);
            if (!written.Contains(stale)) AssetDatabase.DeleteAsset(stale);
        }
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
        {
            var fx = cam.gameObject.AddComponent<BloomEffect>();
            fx.Init(bloom);
            cam.allowHDR = true;
        }
        log.AppendLine($"toon: {cache.Count} toon materials for {renderers} renderers; bloom on {Object.FindObjectsByType<BloomEffect>(FindObjectsInactive.Include).Length} cameras");
    }

    /// <summary>Unique, build-stable hierarchy key (root order + sibling indices).</summary>
    static string OrderKey(Transform t)
    {
        var sb = new StringBuilder();
        for (; t != null; t = t.parent)
            sb.Insert(0, (t.parent == null ? t.gameObject.scene.GetRootGameObjects().ToList().IndexOf(t.gameObject) : t.GetSiblingIndex()).ToString("D5") + "/");
        return sb.ToString();
    }

    static string PathOf(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (Transform p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
        return "/" + sb;
    }

    static string Sanitize(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace('(', '_').Replace(')', '_').Replace('+', '_').Replace(' ', '_');
    }

    // ============================================================================================
    // Audio and UI
    // ============================================================================================

    const string Kenney = "Assets/ThirdParty/Kenney/";

    static AudioManager.SfxEntry Sound(Sfx id, float volume, bool jingle, params string[] clips) => new AudioManager.SfxEntry
    {
        id = id,
        volume = volume,
        isJingle = jingle,
        clips = clips.Select(c =>
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Kenney}{c}.ogg");
            if (clip == null) throw new Exception("Missing audio clip: " + c);
            return clip;
        }).ToArray()
    };

    static void BuildAudio(GameObject host, StringBuilder log)
    {
        var entries = new[]
        {
            Sound(Sfx.Hop, 0.5f, false, "DigitalAudio/phaseJump1"),
            Sound(Sfx.Land, 0.6f, false, "ImpactSounds/impactSoft_medium_000"),
            Sound(Sfx.ItemBox, 0.7f, false, "InterfaceSounds/confirmation_001", "DigitalAudio/powerUp2"),
            Sound(Sfx.RouletteTick, 0.35f, false, "InterfaceSounds/tick_001"),
            Sound(Sfx.RouletteDing, 0.7f, false, "InterfaceSounds/bong_001"),
            Sound(Sfx.BananaDrop, 0.7f, false, "InterfaceSounds/drop_002"),
            Sound(Sfx.BananaSlip, 0.9f, false, "InterfaceSounds/scratch_003"),
            Sound(Sfx.RocketLaunch, 0.8f, false, "DigitalAudio/laser4"),
            Sound(Sfx.Explosion, 1f, false, "ImpactSounds/impactPunch_heavy_001", "DigitalAudio/spaceTrash3"),
            Sound(Sfx.ShieldUp, 0.7f, false, "DigitalAudio/phaserUp3"),
            Sound(Sfx.ShieldBreak, 0.8f, false, "InterfaceSounds/glass_005"),
            Sound(Sfx.Turbo, 0.8f, false, "DigitalAudio/powerUp7"),
            Sound(Sfx.MiniTurboReady, 0.4f, false, "DigitalAudio/phaserUp1"),
            Sound(Sfx.MiniTurboRelease, 0.7f, false, "DigitalAudio/powerUp11"),
            Sound(Sfx.WallCrash, 0.8f, false, "ImpactSounds/impactMetal_heavy_000", "ImpactSounds/impactMetal_heavy_002"),
            Sound(Sfx.SpinOut, 0.6f, false, "DigitalAudio/zapThreeToneDown"),
            Sound(Sfx.ObstacleHit, 0.8f, false, "ImpactSounds/impactPlate_heavy_000"),
            Sound(Sfx.Rescue, 0.8f, false, "DigitalAudio/phaserUp6", "DigitalAudio/highUp"),
            Sound(Sfx.LapComplete, 0.7f, true, "MusicJingles/jingles_NES03"),
            Sound(Sfx.FinalLap, 0.8f, true, "MusicJingles/jingles_STEEL02"),
            Sound(Sfx.Finish1st, 0.9f, true, "MusicJingles/jingles_NES10"),
            Sound(Sfx.FinishPodium, 0.8f, true, "MusicJingles/jingles_NES05"),
            Sound(Sfx.FinishOther, 0.8f, true, "MusicJingles/jingles_SAX03"),
            Sound(Sfx.UiMove, 0.5f, false, "InterfaceSounds/click_002"),
            Sound(Sfx.UiSelect, 0.6f, false, "InterfaceSounds/select_003"),
            Sound(Sfx.UiReady, 0.7f, false, "InterfaceSounds/confirmation_003"),
            Sound(Sfx.UiBack, 0.5f, false, "InterfaceSounds/back_002"),
            Sound(Sfx.Intro, 0.9f, true, "MusicJingles/jingles_NES00"),
            Sound(Sfx.Flyover, 0.7f, true, "MusicJingles/jingles_STEEL00"),
        };
        host.AddComponent<AudioManager>().Configure(entries);
        log.AppendLine($"audio: {entries.Length} Kenney sfx entries ({entries.Sum(e => e.clips.Length)} clips) + generated engine/screech/beeps/music");
    }

    static void BuildUi(GameObject host, StringBuilder log)
    {
        const string uiDir = GeneratedDir + "/UI";
        EnsureFolder(uiDir);

        // Default runtime theme (same content Unity writes when you create one from the menu).
        string tssPath = uiDir + "/KartTheme.tss";
        string tssFull = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), tssPath);
        if (!System.IO.File.Exists(tssFull))
        {
            System.IO.File.WriteAllText(tssFull, "@import url(\"unity-theme://default\");\n");
            AssetDatabase.ImportAsset(tssPath);
        }
        var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(tssPath);

        string panelPath = uiDir + "/RacePanel.asset";
        var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
        if (panel == null)
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(panel, panelPath);
        }
        panel.themeStyleSheet = theme;
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1920, 1080);
        panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panel.match = 0.5f;
        panel.targetTexture = null;
        EditorUtility.SetDirty(panel);
        AssetDatabase.SaveAssets();

        var doc = host.AddComponent<UIDocument>();
        doc.panelSettings = panel;
        var ui = host.AddComponent<RaceUi>();
        Font LoadFont(string file) => AssetDatabase.LoadAssetAtPath<Font>($"{Kenney}Fonts/{file}.ttf");
        Texture2D LoadTex(string file) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Kenney}UiPack/{file}.png");
        // Kenney Mini Square Mono: the only Kenney face with an unambiguous K and R (see Logs/SmokeShots/font_compare.png).
        Font font = LoadFont("Kenney Mini Square Mono");
        ui.Configure(font, font, font,
            LoadTex("panel_grey"), LoadTex("panel_yellow"), LoadTex("square_grey"), LoadTex("star"));
        log.AppendLine($"ui: UI Toolkit document (theme={(theme != null)}, font={(font != null ? font.name : "missing")}, sprites={(LoadTex("panel_grey") != null)})");
    }

    // ============================================================================================
    // Rescue helper, hazards, render quality
    // ============================================================================================

    static void BuildRescue(GameObject host, StringBuilder log)
    {
        string path = MiniChars + "character-male-f.fbx";
        GameObject pilot = Load(path);
        var probe = Object.Instantiate(pilot);
        Bounds b = RendererBounds(probe); // skinned mesh: use renderer bounds
        Object.DestroyImmediate(probe);
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
        AnimationClip idle = assets.OfType<AnimationClip>().FirstOrDefault(c => c.name == "idle");
        Avatar avatar = assets.OfType<Avatar>().FirstOrDefault();
        Material platform = SaveMaterial("RescuePlatform", new Color(0.95f, 0.95f, 0.98f));
        Material rotor = SaveMaterial("RescueRotor", new Color(0.2f, 0.22f, 0.26f));
        host.AddComponent<RescueService>().Configure(pilot, 1.1f / b.size.y, platform, rotor, idle, avatar);
        log.AppendLine($"rescue helper: {pilot.name} (idle clip={(idle != null)}), scale={1.1f / b.size.y:F2}");
    }

    static void BuildNightHazards(Transform root, RaceTrack track, StringBuilder log)
    {
        var parent = new GameObject("Hazards").transform;
        parent.SetParent(root);
        Material hazardMat = SaveMaterial("Hazard", new Color(1f, 0.72f, 0.1f));
        GameObject cone = Load(ToyKit + "item-cone.fbx");
        Bounds cb = BoundsOf(PrefabVertices(cone));
        float coneScale = 1.3f / cb.size.y;
        float L = track.Length;

        // Static cones: a slalom and a centre cone with a pair behind it.
        var coneSpots = new List<(float s, float lateral)>();
        float s1 = FindStraight(track, L * 0.38f);
        coneSpots.Add((s1, -2.8f));
        coneSpots.Add((s1 + 22f, 2.8f));
        coneSpots.Add((s1 + 44f, -2.8f));
        float s2 = FindStraight(track, L * 0.6f);
        coneSpots.Add((s2, 0f));
        coneSpots.Add((s2 + 30f, -3.3f));
        coneSpots.Add((s2 + 30f, 3.3f));
        int cones = 0;
        foreach (var spot in coneSpots)
        {
            if (track.RadiusAt(spot.s) < 200f) continue; // straights only
            Vector3 p = Surface(track.PointAt(spot.s) + track.RightAt(spot.s) * spot.lateral);
            var go = new GameObject($"Cone_{cones}");
            go.transform.SetParent(parent);
            go.transform.SetPositionAndRotation(p, Quaternion.LookRotation(track.TangentAt(spot.s)));
            var model = (GameObject)PrefabUtility.InstantiatePrefab(cone);
            model.transform.SetParent(go.transform, false);
            model.transform.localScale = Vector3.one * coneScale;
            model.transform.localPosition = -new Vector3(cb.center.x, cb.min.y, cb.center.z) * coneScale;
            var col = go.AddComponent<CapsuleCollider>();
            col.radius = Mathf.Max(cb.size.x, cb.size.z) * coneScale * 0.4f;
            col.height = cb.size.y * coneScale;
            col.center = new Vector3(0f, col.height * 0.5f, 0f);
            go.AddComponent<Hazard>().Configure(col.radius + 0.3f, Vector3.right, 0f, 1f);
            cones++;
        }

        // Moving hazard: a block sliding across the road.
        float s3 = FindStraight(track, L * 0.72f) + 6f;
        // Slides across the right half of the road only; the left half stays open.
        Vector3 bp = Surface(track.PointAt(s3) + track.RightAt(s3) * 2.6f) + Vector3.up * 0.8f;
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = "SlidingBlock";
        block.transform.SetParent(parent);
        block.transform.SetPositionAndRotation(bp, Quaternion.LookRotation(track.TangentAt(s3)));
        block.transform.localScale = new Vector3(2.2f, 1.6f, 2.2f);
        block.GetComponent<Renderer>().sharedMaterial = hazardMat;
        var rb = block.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        block.AddComponent<Hazard>().Configure(1.6f, track.RightAt(s3), 1.8f, 3.4f);

        log.AppendLine($"hazards: {cones} cones (s={s1:F0}, {s2:F0}), sliding block at s={s3:F0}");
    }

    static Vector3 Surface(Vector3 p)
    {
        Physics.SyncTransforms();
        if (Physics.Raycast(p + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point;
        return p;
    }

    /// <summary>MSAA 4x, soft high-res shadows and anisotropic filtering on every quality level.</summary>
    static void ApplyRenderQuality(StringBuilder log)
    {
        int current = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.antiAliasing = 4;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowResolution = ShadowResolution.High;
            QualitySettings.shadowDistance = 160f;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            QualitySettings.pixelLightCount = 6; // night track spot lights + human headlights
        }
        QualitySettings.SetQualityLevel(current, false);
        log.AppendLine($"render quality: MSAA 4x, soft shadows (high, 160 m) on {QualitySettings.names.Length} quality levels");
    }

    /// <summary>Kenney textures: smooth (bilinear + mipmaps + aniso), uncompressed.</summary>
    static void FixKenneyTextures(StringBuilder log)
    {
        int changed = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/ThirdParty/Kenney" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;
            if (KenneyTextureImport.Apply(ti))
            {
                ti.SaveAndReimport();
                changed++;
            }
        }
        log.AppendLine($"kenney textures re-imported with smooth settings: {changed}");
    }

    // ============================================================================================
    // Items
    // ============================================================================================

    /// <summary>Shared item manager (bananas, rockets, shields); the track is set when one is activated.</summary>
    static void BuildItemManager(GameObject host)
    {
        Material rocketMat = SaveMaterial("Rocket", new Color(0.9f, 0.2f, 0.2f));
        rocketMat.EnableKeyword("_EMISSION");
        rocketMat.SetColor("_EmissionColor", new Color(1f, 0.35f, 0.05f) * 3f);
        Material shieldMat = SaveMaterial("Shield", new Color(0.35f, 0.8f, 1f, 0.28f), "Legacy Shaders/Transparent/Diffuse");
        Material explosionMat = SaveMaterial("Explosion", new Color(1f, 0.6f, 0.1f, 0.85f), "Legacy Shaders/Transparent/Diffuse");
        Material trailMat = SaveMaterial("Trail", new Color(2.5f, 2.5f, 2.5f, 1f), "Sprites/Default"); // HDR tint: trails/sparks reach the bloom threshold

        GameObject banana = Load(ToyKit + "item-banana.fbx");
        Bounds bb = BoundsOf(PrefabVertices(banana));
        float bananaScale = 1.1f / Mathf.Max(bb.size.x, bb.size.z);
        Vector3 bananaOffset = -new Vector3(bb.center.x, bb.min.y, bb.center.z) * bananaScale;

        var itemsGo = new GameObject("Items");
        itemsGo.transform.SetParent(host.transform);
        itemsGo.AddComponent<ItemManager>().Configure(banana, bananaScale, bananaOffset, rocketMat, shieldMat, explosionMat, trailMat, null);
    }

    /// <summary>Item box rows (4 boxes across the road) on flat straight-ish spots of the track.</summary>
    static void BuildItemBoxes(Transform root, RaceTrack track, float[] fractions, StringBuilder log)
    {
        GameObject boxPrefab = Load(ToyKit + "item-box.fbx");
        Bounds xb = BoundsOf(PrefabVertices(boxPrefab));
        float boxScale = 1.4f / xb.size.y;
        var boxesRoot = new GameObject("ItemBoxes").transform;
        boxesRoot.SetParent(root);

        int rows = 0, boxes = 0, onRoad = 0;
        var rowInfo = new StringBuilder();
        foreach (float fraction in fractions)
        {
            float s = FindStraight(track, track.Length * fraction);
            Vector3 centre = track.PointAt(s);
            Vector3 right = track.RightAt(s);
            Quaternion rot = Quaternion.LookRotation(track.TangentAt(s));
            int col = 0;
            foreach (float lateral in new[] { -0.275f, -0.092f, 0.092f, 0.275f }.Select(f => f * track.RoadWidth))
            {
                var box = new GameObject($"ItemBox_{rows}_{col++}");
                box.transform.SetParent(boxesRoot);
                box.transform.SetPositionAndRotation(Surface(centre + right * lateral) + Vector3.up * 1.4f, rot);
                var trigger = box.AddComponent<SphereCollider>();
                trigger.isTrigger = true;
                trigger.radius = 1.1f;
                var visual = new GameObject("Visual").transform;
                visual.SetParent(box.transform, false);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(boxPrefab);
                model.transform.SetParent(visual, false);
                model.transform.localScale = Vector3.one * boxScale;
                model.transform.localPosition = -xb.center * boxScale;
                box.AddComponent<ItemBox>().Configure(visual);
                boxes++;

                Physics.SyncTransforms();
                if (Physics.Raycast(box.transform.position, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
                {
                    var ts = hit.collider.GetComponent<TrackSurface>();
                    if (ts != null && ts.IsDrivable(hit.triangleIndex)) onRoad++;
                }
            }
            rowInfo.Append($" s={s:F0}m@{centre:F0}");
            rows++;
        }
        log.AppendLine($"item boxes: {boxes} in {rows} rows ({onRoad} over road):{rowInfo}; box scale={boxScale:F2}");
        if (onRoad != boxes) Debug.LogWarning(Tag + "Some item boxes are not over the road!");
    }

    /// <summary>First distance at or after s where the centerline is straight and the road is
    /// flat (no bump/hill under it) for +-12 m.</summary>
    static float FindStraight(RaceTrack track, float s)
    {
        for (float d = 0f; d < track.Length; d += 2f)
        {
            float x = s + d;
            if (Vector3.Angle(track.TangentAt(x - 12f), track.TangentAt(x + 12f)) >= 1f) continue;
            float y0 = Surface(track.PointAt(x - 12f)).y, y1 = Surface(track.PointAt(x)).y, y2 = Surface(track.PointAt(x + 12f)).y;
            if (Mathf.Max(Mathf.Abs(y0 - y1), Mathf.Abs(y2 - y1)) > 0.3f) continue;
            return Mathf.Repeat(x, track.Length);
        }
        return s;
    }

    static List<Vector3> CollectVertices(GameObject root)
    {
        var list = new List<Vector3>();
        Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices) list.Add(m.MultiplyPoint3x4(v));
        }
        return list;
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    static GameObject PlaceProp(GameObject prefab, Transform parent, float scale, Vector3 groundPos, Quaternion rot, string name)
    {
        Bounds b = BoundsOf(PrefabVertices(prefab));
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * scale;
        go.transform.SetPositionAndRotation(
            groundPos - rot * (new Vector3(b.center.x, b.min.y, b.center.z) * scale), rot);
        return go;
    }

    /// <summary>Horizontal side (±X/±Z) whose outer band contains the tallest vertex.</summary>
    static Vector3 TallSide(List<Vector3> verts)
    {
        Bounds b = BoundsOf(verts);
        Vector3 best = Vector3.back;
        float bestY = float.MinValue;
        foreach (int axis in new[] { 0, 2 })
            foreach (int sign in new[] { -1, 1 })
            {
                float band = b.size[axis] * 0.25f;
                float maxY = float.MinValue;
                foreach (var v in verts)
                {
                    bool inBand = sign < 0 ? v[axis] < b.min[axis] + band : v[axis] > b.max[axis] - band;
                    if (inBand) maxY = Mathf.Max(maxY, v.y);
                }
                if (maxY > bestY)
                {
                    bestY = maxY;
                    best = Vector3.zero;
                    best[axis] = sign;
                }
            }
        return best;
    }

    static Quaternion YawFromTo(Vector3 from, Vector3 to) =>
        Quaternion.Euler(0f, Vector3.SignedAngle(Flat(from), Flat(to), Vector3.up), 0f);

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z).normalized;

    static Vector3 SnapAxis(Vector3 v) => new Vector3(Mathf.Round(v.x), 0f, Mathf.Round(v.z));

    static bool Overlaps(List<Bounds> list, Vector3 p, float pad)
    {
        foreach (var b in list)
            if (p.x > b.min.x - pad && p.x < b.max.x + pad && p.z > b.min.z - pad && p.z < b.max.z + pad)
                return true;
        return false;
    }

    /// <summary>All mesh vertices in the prefab root's local space (root at identity, scale 1).</summary>
    static List<Vector3> PrefabVertices(GameObject prefab)
    {
        var go = Object.Instantiate(prefab);
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.transform.localScale = Vector3.one;
        var list = new List<Vector3>();
        Matrix4x4 toRoot = go.transform.worldToLocalMatrix;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices) list.Add(m.MultiplyPoint3x4(v));
        }
        Object.DestroyImmediate(go);
        if (list.Count == 0) throw new Exception($"Prefab {prefab.name} has no mesh vertices");
        return list;
    }

    static Bounds BoundsOf(List<Vector3> pts)
    {
        var b = new Bounds(pts[0], Vector3.zero);
        foreach (var p in pts) b.Encapsulate(p);
        return b;
    }

    static Bounds RendererBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        var b = new Bounds(go.transform.position, Vector3.zero);
        for (int i = 0; i < renderers.Length; i++)
            if (i == 0) b = renderers[i].bounds; else b.Encapsulate(renderers[i].bounds);
        return b;
    }

    static string Describe(Opening[] openings)
    {
        var sb = new StringBuilder();
        foreach (var o in openings) sb.Append($"[pos {o.pos:F3} dir {o.dir:F0}] ");
        return sb.ToString();
    }

    static GameObject Load(string path)
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (go == null) throw new Exception("Missing asset: " + path);
        return go;
    }

    static void EnsureModelsReadable(string folder)
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetImporter.GetAtPath(path) is ModelImporter mi && !mi.isReadable)
            {
                mi.isReadable = true;
                mi.SaveAndReimport();
            }
        }
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }

    static Material SaveMaterial(string name, Color color, string shaderName = "Standard")
    {
        EnsureFolder(GeneratedDir);
        string path = $"{GeneratedDir}/{name}.mat";
        Shader shader = Shader.Find(shaderName);
        if (shader == null) throw new Exception("Shader not found: " + shaderName);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.color = color;
        mat.SetFloat("_Glossiness", 0.1f);
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
