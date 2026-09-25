using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Drivability check per track (batchmode -executeMethod RaceStallTest.Run [-kartTrack N], N = 1..4):
/// a HUMAN kart (keyboard player slot, scripted input through the normal IKartInput path, CPUs and
/// hazards removed) drives 2 laps. The autopilot steers at the centerline and uses full throttle
/// unless a corner needs it to lift or brake. Logged:
///  * STALL - speed fell by more than 30% within 0.2 s while on full throttle and not touching a wall
///    (the seam bug); wall impacts are counted separately,
///  * jumps - every airborne phase longer than 0.25 s and whether the landing kept the speed,
///  * rescues / falls, lap time.
/// Then a climb from standstill and from speed at the first rise, if the track has one.
/// </summary>
public static class RaceStallTest
{
    const string Tag = "[StallTest] ";

    /// <summary>Centerline driver with a corner-speed limit; drift/hop/items off.</summary>
    class Autopilot : IKartInput
    {
        public KartController kart;
        public RaceTrack track;
        public bool forceFull; // hill tests: always full throttle
        public float lastLift = -10f;

        public float Steer
        {
            get
            {
                Transform t = kart.transform;
                float s = track.Project(t.position);
                float look = 7f + Mathf.Abs(kart.ForwardSpeed) * 0.45f;
                Vector3 local = t.InverseTransformPoint(track.PointAt(s + look));
                return Mathf.Clamp(Mathf.Atan2(local.x, Mathf.Max(local.z, 0.5f)) * Mathf.Rad2Deg / 18f, -1f, 1f);
            }
        }

        public float Throttle
        {
            get
            {
                if (forceFull) return 1f;
                float speed = kart.ForwardSpeed;
                float s = track.Project(kart.transform.position);
                float desired = kart.MaxSpeed;
                for (float d = 2f; d <= 14f + Mathf.Abs(speed) * 1.3f; d += 3f)
                {
                    float cornerSpeed = Mathf.Max(7f, track.RadiusAt(s + d) * 1.3f);
                    desired = Mathf.Min(desired, Mathf.Sqrt(cornerSpeed * cornerSpeed + 2f * 14f * Mathf.Max(0f, d - 3f)));
                }
                float error = desired - speed;
                float throttle = error > 1f ? 1f : error > -1.5f ? 0.4f : -0.5f;
                if (throttle < 1f) lastLift = Time.time;
                return throttle;
            }
        }

        public bool Drift => false;
        public bool UseItem => false;
        public bool Hop => false;
    }

    static int stage;
    static double stageStart;
    static RaceManager rm;
    static KartController kart;
    static LapTracker lap;
    static ContactProbe probe;
    static Autopilot pilot;
    static RaceTrack track;
    static readonly Queue<(float t, float speed)> history = new Queue<(float, float)>();
    static float lastEventTime = -10f, lapStart;
    static int stalls, wallImpacts, crashesAtStart, rescuesAtStart, trackIndex;
    static int jumps, badLandings;
    static float maxAir, airStartSpeed, landCheckAt = -1f, landSpeed;
    static bool wasAir;
    static float hillBaseS, hillTopY, maxY;
    static bool hasHill, failed;
    static readonly StringBuilder report = new StringBuilder();
    static bool savedEnabled;
    static EnterPlayModeOptions savedOptions;

    public static void Run()
    {
        trackIndex = 0;
        string[] args = System.Environment.GetCommandLineArgs();
        int at = System.Array.IndexOf(args, "-kartTrack");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int n)) trackIndex = Mathf.Max(0, n - 1);

        savedEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        savedOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorSceneManager.OpenScene("Assets/Scenes/Race.unity");
        RaceManager.SkipIntroForTest = true;
        stage = 0;
        stalls = wallImpacts = jumps = badLandings = 0;
        maxAir = 0f;
        wasAir = false;
        landCheckAt = -1f;
        failed = false;
        report.Clear();
        history.Clear();
        stageStart = EditorApplication.timeSinceStartup;
        EditorApplication.update += Tick;
        EditorApplication.EnterPlaymode();
    }

    static double Elapsed => EditorApplication.timeSinceStartup - stageStart;

    static void Next()
    {
        stage++;
        stageStart = EditorApplication.timeSinceStartup;
    }

    static void Tick()
    {
        try { TickInternal(); }
        catch (System.Exception e) { Debug.LogError(Tag + e); failed = true; Finish(1); }
    }

    static void TickInternal()
    {
        switch (stage)
        {
            case 0:
                if (Elapsed > 90) { Debug.LogError(Tag + "no lobby"); Finish(1); return; }
                if (!EditorApplication.isPlaying) return;
                rm = Object.FindAnyObjectByType<RaceManager>();
                if (rm == null || rm.Phase != RacePhase.Lobby) return;
                rm.SelectTrackForTest(Mathf.Min(trackIndex, rm.Tracks.Count - 1));
                rm.AddKeyboardPlayerForTest(true); // the only player: auto start
                Next();
                break;

            case 1:
                if (rm.Phase != RacePhase.Racing) return;
                var human = rm.HumanRacers[0];
                kart = human.Kart;
                lap = human.Lap;
                track = rm.Track;
                // Keep the track empty so every stop is the track's fault, not a collision with a CPU.
                foreach (var r in rm.Standings) if (r.Human == null) r.Kart.gameObject.SetActive(false);
                foreach (var h in Hazard.All.ToList()) h.gameObject.SetActive(false); // obstacles are meant to stop you
                pilot = new Autopilot { kart = kart, track = track };
                kart.SetInput(pilot);
                probe = kart.gameObject.AddComponent<ContactProbe>();
                crashesAtStart = TotalCrashes();
                rescuesAtStart = RescueService.Instance != null ? RescueService.Instance.RescueCount : 0;
                lapStart = -1f;
                Log($"track {rm.ActiveTrackIndex + 1} '{rm.ActiveTrack.displayName}' ({track.Length:F0} m): driving 2 laps with the human kart " +
                    $"'{rm.Roster[human.Character].displayName}' (keyboard slot, scripted input)");
                Next();
                break;

            case 2: // 2 full laps (lap 1 starts at the first line crossing), watch for stalls and jumps
                Watch();
                if (lapStart < 0f && lap.CurrentLap >= 1) lapStart = Time.time;
                if (lap.CurrentLap >= 3 || lap.Finished)
                {
                    int rescues = (RescueService.Instance != null ? RescueService.Instance.RescueCount : 0) - rescuesAtStart;
                    Log($"RESULT 2 laps: {stalls} stall events, {wallImpacts} wall impacts, crash spin-outs {TotalCrashes() - crashesAtStart} " +
                        $"(walls {kart.WallCrashes}, karts {kart.KartCrashes}, hazards {kart.HazardCrashes}), rescues {rescues}, " +
                        $"time {Time.time - lapStart:F1} s ({(Time.time - lapStart) / 2f:F1} s/lap)");
                    Log($"RESULT jumps: {jumps} airborne phases > 0.25 s, longest {maxAir:F2} s, bad landings {badLandings}");
                    failed |= stalls > 0 || badLandings > 0 || rescues > 0;
                    Next();
                    return;
                }
                if (Elapsed > 400) { Log($"RESULT 2 laps: TIMEOUT at lap {lap.CurrentLap}, {stalls} stall events"); failed = true; Next(); }
                break;

            case 3: // hill from standstill
                hasHill = FindHill();
                if (!hasHill)
                {
                    Log("RESULT hill: flat track, no climb to test");
                    Finish(failed ? 1 : 0);
                    return;
                }
                pilot.forceFull = true;
                PlaceOnTrack(hillBaseS - 6f);
                maxY = -100f;
                Next();
                break;

            case 4:
                Watch();
                maxY = Mathf.Max(maxY, kart.transform.position.y);
                if (Elapsed < 20 && maxY <= hillTopY - 0.6f) return;
                failed |= maxY <= hillTopY - 0.6f;
                Log($"RESULT hill from standstill: max height {maxY:F2} m of {hillTopY:F2} m after {Elapsed:F1} s -> {(maxY > hillTopY - 0.6f ? "CLIMBED" : "STUCK")}, " +
                    $"final speed {kart.ForwardSpeed:F1} m/s at s={track.Project(kart.transform.position):F0}");
                PlaceOnTrack(hillBaseS - 60f);
                maxY = -100f;
                Next();
                break;

            case 5:
                Watch();
                maxY = Mathf.Max(maxY, kart.transform.position.y);
                if (Elapsed < 20 && maxY <= hillTopY - 0.6f) return;
                failed |= maxY <= hillTopY - 0.6f;
                Log($"RESULT hill from speed: max height {maxY:F2} m of {hillTopY:F2} m after {Elapsed:F1} s -> {(maxY > hillTopY - 0.6f ? "CLIMBED" : "STUCK")}");
                Finish(failed ? 1 : 0);
                break;
        }
    }

    static int TotalCrashes() => kart.GetComponent<KartItems>()?.Crashes ?? 0;

    /// <summary>First rise of the centreline and the height it reaches before coming down again.</summary>
    static bool FindHill()
    {
        for (float s = 0f; s < track.Length; s += 1f)
        {
            float y0 = track.PointAt(s).y;
            if (track.PointAt(s + 4f).y - y0 < 0.5f) continue;
            hillBaseS = s;
            hillTopY = y0;
            for (float d = 0f; d < 200f; d += 1f)
            {
                float y = track.PointAt(s + d).y;
                if (y < hillTopY - 0.5f) break;
                hillTopY = Mathf.Max(hillTopY, y);
            }
            Log($"hill base at s={hillBaseS:F0} m (y {y0:F1}), top height {hillTopY:F2} m");
            return true;
        }
        return false;
    }

    static void PlaceOnTrack(float s)
    {
        Vector3 p = track.PointAt(s);
        if (Physics.Raycast(p + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore)) p.y = hit.point.y + 0.3f;
        kart.Teleport(p, Quaternion.LookRotation(track.TangentAt(s)));
        history.Clear();
    }

    /// <summary>Stall: speed fell by more than 30% within 0.2 s on full throttle, without a wall hit.</summary>
    static void Watch()
    {
        float now = Time.time;
        float speed = kart.ForwardSpeed;
        TrackJumps(now, speed);
        history.Enqueue((now, speed));
        while (history.Count > 0 && now - history.Peek().t > 0.2f) history.Dequeue();
        float peak = history.Max(h => h.speed);
        if (peak < 8f || speed > peak * 0.7f || now - lastEventTime < 0.5f || kart.ControlsLocked) return;
        if (!pilot.forceFull && now - pilot.lastLift < 0.4f) return; // lifting / braking for a corner
        lastEventTime = now;

        var recent = probe.Recent(0.25f).ToList();
        bool wall = recent.Any(h => Mathf.Abs(h.normal.y) <= 0.5f && h.impulse > 1f);
        Vector3 pos = kart.transform.position;
        string under = "nothing";
        if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f, ~0, QueryTriggerInteraction.Ignore))
        {
            var ts = hit.collider.GetComponent<TrackSurface>();
            under = $"{hit.collider.name} n={hit.normal:F2} trackSurface={(ts != null)} drivable={(ts != null && ts.IsDrivable(hit.triangleIndex))}";
        }
        var contacts = recent
            .GroupBy(h => (h.collider, n: new Vector3(Mathf.Round(h.normal.x * 10) / 10, Mathf.Round(h.normal.y * 10) / 10, Mathf.Round(h.normal.z * 10) / 10)))
            .Select(g => $"{g.Key.collider} n={g.Key.n} imp={g.Max(x => x.impulse):F0}{(g.Any(x => x.enter) ? " ENTER" : "")}")
            .Take(6);
        string what = wall ? "WALL" : "STALL";
        if (wall) wallImpacts++;
        else stalls++;
        Log($"{what}: {peak:F1} -> {speed:F1} m/s at s={track.Project(pos):F0} pos={pos:F1} grounded={kart.IsGrounded} onRoad={kart.IsOnRoad} " +
            $"spinning={kart.IsSpinningOut} crashes={TotalCrashes() - crashesAtStart} lastImpact='{kart.LastImpact}' | under: {under} | contacts: {string.Join("; ", contacts)}");
    }

    /// <summary>Airborne phases: a landing is bad if the kart lost more than 40% of its take-off speed within 1 s.</summary>
    static void TrackJumps(float now, float speed)
    {
        bool air = !kart.IsGrounded;
        if (air && !wasAir)
        {
            airStart = now;
            airStartSpeed = speed;
        }
        if (!air && wasAir)
        {
            float duration = now - airStart;
            if (duration > 0.25f)
            {
                jumps++;
                maxAir = Mathf.Max(maxAir, duration);
                landCheckAt = now + 1f;
                landSpeed = airStartSpeed;
                Log($"jump: {duration:F2} s airborne at s={track.Project(kart.transform.position):F0}, take-off {airStartSpeed:F1} m/s, touch-down {speed:F1} m/s");
            }
        }
        if (landCheckAt > 0f && now >= landCheckAt)
        {
            landCheckAt = -1f;
            if (landSpeed > 8f && speed < landSpeed * 0.6f && pilot.lastLift < now - 1.2f)
            {
                badLandings++;
                Log($"BAD LANDING: {landSpeed:F1} -> {speed:F1} m/s one second after touch-down at s={track.Project(kart.transform.position):F0}");
            }
        }
        wasAir = air;
    }

    static float airStart;

    static void Log(string msg)
    {
        report.AppendLine(msg);
        Debug.Log(Tag + msg);
    }

    static void Finish(int code)
    {
        if (failed) code = 1;
        EditorApplication.update -= Tick;
        EditorSettings.enterPlayModeOptionsEnabled = savedEnabled;
        EditorSettings.enterPlayModeOptions = savedOptions;
        Log(code == 0 ? "STALL TEST PASSED" : "STALL TEST FAILED");
        Debug.Log(Tag + "SUMMARY\n" + report);
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
