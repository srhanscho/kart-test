using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Reproduction for "sudden stops" and "stuck on the hill" (batchmode -executeMethod RaceStallTest.Run):
/// a HUMAN kart (keyboard player slot, scripted steering input through the normal IKartInput path,
/// CPUs removed) drives 2 laps at full throttle; every speed drop of more than 30% within 0.2 s
/// without braking is logged with the collider under the kart, recent contacts, spin-out/crash
/// state and TrackSurface info. Then hill climbs from standstill and from speed are checked.
/// </summary>
public static class RaceStallTest
{
    const string Tag = "[StallTest] ";

    /// <summary>Full-throttle driver that steers at the centerline; drift/hop/items off.</summary>
    class Autopilot : IKartInput
    {
        public KartController kart;
        public RaceTrack track;
        public float throttle = 1f;
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
        public float Throttle => throttle;
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
    static float lastEventTime = -10f;
    static int stalls, crashesAtStart, lapsAtStart;
    static float hillBaseS, hillTopY, maxY;
    static bool failed;
    static readonly StringBuilder report = new StringBuilder();
    static bool savedEnabled;
    static EnterPlayModeOptions savedOptions;

    public static void Run()
    {
        savedEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        savedOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorSceneManager.OpenScene("Assets/Scenes/Race.unity");
        stage = 0;
        stalls = 0;
        failed = false;
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
        catch (System.Exception e) { Debug.LogError(Tag + e); Finish(1); }
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
                lapsAtStart = lap.CurrentLap;
                Log($"driving 2 laps with the human kart '{rm.Roster[human.Character].displayName}' (keyboard slot, scripted input)");
                Next();
                break;

            case 2: // 2 laps, watch for stalls
                Watch();
                if (lap.CurrentLap >= lapsAtStart + 2 || lap.Finished)
                {
                    Log($"RESULT 2 laps: {stalls} stall events, crash spin-outs {TotalCrashes() - crashesAtStart} " +
                        $"(walls {kart.WallCrashes}, karts {kart.KartCrashes}, hazards {kart.HazardCrashes}), time {Elapsed:F1} s");
                    failed |= stalls > 0;
                    Next();
                    return;
                }
                if (Elapsed > 240) { Log($"RESULT 2 laps: TIMEOUT at lap {lap.CurrentLap}, {stalls} stall events"); failed = true; Next(); }
                break;

            case 3: // hill from standstill
                FindHill();
                PlaceOnTrack(hillBaseS - 6f);
                maxY = 0f;
                pilot.throttle = 1f;
                Next();
                break;

            case 4:
                Watch();
                maxY = Mathf.Max(maxY, kart.transform.position.y);
                if (Elapsed < 8) return;
                failed |= maxY <= hillTopY - 0.6f;
                Log($"RESULT hill from standstill: max height {maxY:F2} m of {hillTopY:F2} m -> {(maxY > hillTopY - 0.6f ? "CLIMBED" : "STUCK")}, " +
                    $"final speed {kart.ForwardSpeed:F1} m/s at s={track.Project(kart.transform.position):F0}");
                PlaceOnTrack(hillBaseS - 60f);
                maxY = 0f;
                Next();
                break;

            case 5:
                Watch();
                maxY = Mathf.Max(maxY, kart.transform.position.y);
                if (Elapsed < 8) return;
                Log($"RESULT hill from speed: max height {maxY:F2} m of {hillTopY:F2} m -> {(maxY > hillTopY - 0.6f ? "CLIMBED" : "STUCK")}");
                failed |= maxY <= hillTopY - 0.6f;
                Log(failed ? "STALL TEST FAILED" : "STALL TEST PASSED");
                Finish(failed ? 1 : 0);
                break;
        }
    }

    static int TotalCrashes() => kart.GetComponent<KartItems>()?.Crashes ?? 0;

    static void FindHill()
    {
        // First place where the centreline starts to rise noticeably.
        hillTopY = 0f;
        for (float s = 0f; s < track.Length; s += 1f) hillTopY = Mathf.Max(hillTopY, track.PointAt(s).y);
        for (float s = 0f; s < track.Length; s += 1f)
            if (track.PointAt(s).y < 0.3f && track.PointAt(s + 4f).y > 0.6f) { hillBaseS = s; break; }
        Log($"hill base at s={hillBaseS:F0} m, top height {hillTopY:F2} m");
    }

    static void PlaceOnTrack(float s)
    {
        Vector3 p = track.PointAt(s);
        if (Physics.Raycast(p + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore)) p.y = hit.point.y + 0.3f;
        kart.Teleport(p, Quaternion.LookRotation(track.TangentAt(s)));
        history.Clear();
    }

    /// <summary>Stall: speed fell by more than 30% within 0.2 s while throttle is full (no braking).</summary>
    static void Watch()
    {
        float now = Time.time;
        float speed = kart.ForwardSpeed;
        history.Enqueue((now, speed));
        while (history.Count > 0 && now - history.Peek().t > 0.2f) history.Dequeue();
        float peak = history.Max(h => h.speed);
        if (peak < 8f || speed > peak * 0.7f || now - lastEventTime < 0.5f || kart.ControlsLocked) return;
        lastEventTime = now;
        stalls++;

        Vector3 pos = kart.transform.position;
        string under = "nothing";
        if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f, ~0, QueryTriggerInteraction.Ignore))
        {
            var ts = hit.collider.GetComponent<TrackSurface>();
            under = $"{hit.collider.name} n={hit.normal:F2} trackSurface={(ts != null)} drivable={(ts != null && ts.IsDrivable(hit.triangleIndex))}";
        }
        var contacts = probe.Recent(0.25f)
            .GroupBy(h => (h.collider, n: new Vector3(Mathf.Round(h.normal.x * 10) / 10, Mathf.Round(h.normal.y * 10) / 10, Mathf.Round(h.normal.z * 10) / 10)))
            .Select(g => $"{g.Key.collider} n={g.Key.n} imp={g.Max(x => x.impulse):F0}{(g.Any(x => x.enter) ? " ENTER" : "")}")
            .Take(6);
        Log($"STALL #{stalls}: {peak:F1} -> {speed:F1} m/s at s={track.Project(pos):F0} pos={pos:F1} grounded={kart.IsGrounded} onRoad={kart.IsOnRoad} " +
            $"spinning={kart.IsSpinningOut} crashes={TotalCrashes() - crashesAtStart} lastImpact='{kart.LastImpact}' | under: {under} | contacts: {string.Join("; ", contacts)}");
    }

    static void Log(string msg)
    {
        report.AppendLine(msg);
        Debug.Log(Tag + msg);
    }

    static void Finish(int code)
    {
        EditorApplication.update -= Tick;
        EditorSettings.enterPlayModeOptionsEnabled = savedEnabled;
        EditorSettings.enterPlayModeOptions = savedOptions;
        Debug.Log(Tag + "SUMMARY\n" + report);
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
}
