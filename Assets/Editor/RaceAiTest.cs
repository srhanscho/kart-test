using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>
/// Full race with 5 CPUs on one track (batchmode -executeMethod RaceAiTest.Run -kartTrack N):
/// the keyboard player drives on the CPU autopilot, the race runs at 4x speed until the results.
/// Checks every CPU finishes and logs lap times, rescues and crashes. Writes per-track screenshots
/// (grid, chase camera mid-race, top-down) to Logs/SmokeShots/track{N}_*.png.
/// </summary>
public static class RaceAiTest
{
    const string Tag = "[AiTest] ";
    const string ShotDir = "Logs/SmokeShots";

    static int stage, trackIndex, failures;
    static double stageStart;
    static RaceManager rm;
    static bool savedEnabled, chaseShot;
    static EnterPlayModeOptions savedOptions;
    static readonly StringBuilder report = new StringBuilder();

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
        failures = 0;
        chaseShot = false;
        report.Clear();
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
        catch (System.Exception e) { Check(false, "exception: " + e); Finish(); }
    }

    static void TickInternal()
    {
        switch (stage)
        {
            case 0:
                if (Elapsed > 90) { Check(false, "no lobby"); Finish(); return; }
                if (!EditorApplication.isPlaying) return;
                rm = Object.FindAnyObjectByType<RaceManager>();
                if (rm == null || rm.Phase != RacePhase.Lobby || !rm.ThumbnailsReady) return;
                rm.SelectTrackForTest(Mathf.Min(trackIndex, rm.Tracks.Count - 1));
                SaveThumbnail();
                rm.AddKeyboardPlayerForTest(true);
                Next();
                break;

            case 1: // flyover and countdown run normally
                if (rm.Phase == RacePhase.Countdown && !rm.FlyoverActive && Elapsed > 0.5 && !chaseShot)
                {
                    chaseShot = true;
                    Capture(rm.PlayerCamera(0), $"{ShotDir}/track{trackIndex + 1}_grid.png");
                }
                if (rm.Phase != RacePhase.Racing) return;
                chaseShot = false;
                rm.SetAutopilotForTest();
                Log($"track {rm.ActiveTrackIndex + 1} '{rm.ActiveTrack.displayName}': {rm.ActiveTrack.Length:F0} m, {rm.Laps} laps, {rm.Standings.Count} karts " +
                    $"({rm.Standings.Count(r => r.Human == null)} CPUs), keyboard kart on autopilot");
                Next();
                break;

            case 2: // real time for a mid-race chase shot, then fast forward
                if (!chaseShot && Elapsed > 9)
                {
                    chaseShot = true;
                    Capture(rm.PlayerCamera(0), $"{ShotDir}/track{trackIndex + 1}_chase.png");
                    Scenery($"{ShotDir}/track{trackIndex + 1}_scenery.png");
                    TopDown($"{ShotDir}/track{trackIndex + 1}_topdown.png");
                    Time.timeScale = 4f;
                }
                if (Elapsed > 420) { Check(false, "race did not reach results"); Dump(); Finish(); return; }
                // Results show when the human finishes; the CPUs keep driving until everyone is home.
                if (rm.Phase != RacePhase.Results || (rm.Standings.Any(r => !r.Lap.Finished) && Elapsed < 420)) return;
                Time.timeScale = 1f;
                Dump();
                var cpus = rm.Standings.Where(r => r.Human == null).ToList();
                var done = cpus.Where(r => r.Lap.Finished).ToList();
                int complete = cpus.Count(r => r.Lap.Finished || r.Lap.CurrentLap >= rm.Laps);
                float avg = done.Count > 0 ? done.Average(r => r.Lap.FinishTime) / rm.Laps : -1f;
                float best = cpus.Where(r => r.Lap.BestLap > 0f).Select(r => r.Lap.BestLap).DefaultIfEmpty(-1f).Min();
                Check(complete == cpus.Count,
                    $"RESULT track {rm.ActiveTrackIndex + 1} '{rm.ActiveTrack.displayName}': CPUs finished or on the final lap {complete}/{cpus.Count} " +
                    $"(finished {done.Count}), avg CPU lap {avg:F1} s, best CPU lap {best:F1} s, rescues {RescueService.Instance.RescueCount}, " +
                    $"crash spin-outs {rm.Standings.Sum(r => r.Items.Crashes)} (walls {rm.Standings.Sum(r => r.Kart.WallCrashes)}, " +
                    $"karts {rm.Standings.Sum(r => r.Kart.KartCrashes)}, hazards {rm.Standings.Sum(r => r.Kart.HazardCrashes)}), item uses {ItemManager.Instance.AiUses}");
                Finish();
                break;
        }
    }

    // ---- lobby track cards: one UI screenshot per card (-executeMethod RaceAiTest.LobbyCards) --------

    static int card, frames;
    static RenderTexture uiRt;

    public static void LobbyCards()
    {
        savedEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        savedOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorSceneManager.OpenScene("Assets/Scenes/Race.unity");
        RaceManager.SkipIntroForTest = true;
        card = -1;
        frames = 0;
        failures = 0;
        report.Clear();
        stageStart = EditorApplication.timeSinceStartup;
        EditorApplication.update += CardTick;
        EditorApplication.EnterPlaymode();
    }

    static void CardTick()
    {
        try
        {
            if (!EditorApplication.isPlaying) return;
            rm = Object.FindAnyObjectByType<RaceManager>();
            if (rm == null || !rm.ThumbnailsReady) return;
            var ui = rm.GetComponent<RaceUi>();
            var ps = ui.Document.panelSettings;
            if (card < 0)
            {
                rm.AddKeyboardPlayerForTest(false);
                card = 0;
                frames = 0;
            }
            if (frames == 0)
            {
                rm.SelectTrackForTest(card);
                uiRt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
                ps.targetTexture = uiRt;
                ps.clearColor = true;
                ps.colorClearValue = new Color(0, 0, 0, 0);
            }
            if (++frames < 6) return;
            var prev = RenderTexture.active;
            RenderTexture.active = uiRt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            ps.targetTexture = null;
            ps.clearColor = false;
            uiRt.Release();
            // Crop the track card (1920x1080 reference: x 60..620, y 695..935) and scale 2x for reading.
            float k = 1280f / 1920f;
            int x0 = (int)(50 * k), w = (int)(580 * k), yTop = (int)(685 * k), h = (int)(260 * k);
            var crop = new Texture2D(w * 2, h * 2, TextureFormat.RGB24, false);
            for (int y = 0; y < h * 2; y++)
                for (int x = 0; x < w * 2; x++)
                {
                    Color c = tex.GetPixel(x0 + x / 2, 720 - yTop - h + y / 2);
                    crop.SetPixel(x, y, Color.Lerp(new Color(0.05f, 0.06f, 0.1f), new Color(c.r, c.g, c.b), c.a > 0f ? 1f : 0f) + new Color(c.r, c.g, c.b) * 0f);
                }
            crop.Apply();
            Directory.CreateDirectory(ShotDir);
            File.WriteAllBytes($"{ShotDir}/lobby_card_{card + 1}.png", crop.EncodeToPNG());
            Log($"lobby card {card + 1}: '{ui.TrackCardText}' -> lobby_card_{card + 1}.png");
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(crop);
            card++;
            frames = 0;
            if (card > rm.Tracks.Count)
            {
                EditorApplication.update -= CardTick;
                EditorSettings.enterPlayModeOptionsEnabled = savedEnabled;
                EditorSettings.enterPlayModeOptions = savedOptions;
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError(Tag + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    static void Dump()
    {
        foreach (var r in rm.Standings)
            Log($"  {r.Position}. {r.Name,-26} lap {r.Lap.CurrentLap}/{rm.Laps} finished={r.Lap.Finished} time={(r.Lap.Finished ? r.Lap.FinishTime.ToString("F1") : "-")} best={r.Lap.BestLap:F1}");
    }

    static void SaveThumbnail()
    {
        var tex = rm.ActiveTrack.Thumbnail;
        if (tex == null) return;
        Directory.CreateDirectory(ShotDir);
        File.WriteAllBytes($"{ShotDir}/track{trackIndex + 1}_thumbnail.png", tex.EncodeToPNG());
    }

    /// <summary>High side view across the start straight (grandstands / pits / gate).</summary>
    static void Scenery(string file)
    {
        RaceTrack tr = rm.Track;
        Vector3 f = tr.PointAt(0f), dir = tr.TangentAt(0f), right = tr.RightAt(0f);
        var cam = new GameObject("SceneryCam").AddComponent<Camera>();
        cam.transform.position = f + right * 6f - dir * 70f + Vector3.up * 30f; // above the road, behind the grid
        cam.transform.LookAt(f + dir * 30f);
        cam.fieldOfView = 55f;
        cam.farClipPlane = 1500f;
        Capture(cam, file);
        Object.DestroyImmediate(cam.gameObject);
    }

    static void TopDown(string file)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        Bounds b = rm.ActiveTrack.bounds;
        var cam = new GameObject("TopDownCam").AddComponent<Camera>();
        cam.orthographic = true;
        cam.transform.SetPositionAndRotation(b.center + Vector3.up * 300f, Quaternion.Euler(90f, 0f, 0f));
        cam.orthographicSize = Mathf.Max(b.extents.z, b.extents.x * 720f / 1280f) * 1.08f;
        cam.farClipPlane = 700f;
        bool fog = RenderSettings.fog;
        RenderSettings.fog = false;
        Capture(cam, file);
        RenderSettings.fog = fog;
        Object.DestroyImmediate(cam.gameObject);
    }

    static void Capture(Camera cam, string file)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || cam == null) return;
        Directory.CreateDirectory(ShotDir);
        var rt = new RenderTexture(1280, 720, 24);
        RenderTexture prevTarget = cam.targetTexture;
        Rect prevRect = cam.rect;
        cam.targetTexture = rt;
        cam.rect = new Rect(0, 0, 1, 1);
        cam.Render();
        cam.targetTexture = prevTarget;
        cam.rect = prevRect;
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(file, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        rt.Release();
        Log("screenshot " + file);
    }

    static void Check(bool ok, string msg)
    {
        if (!ok) failures++;
        Log((ok ? "PASS " : "FAIL ") + msg);
    }

    static void Log(string msg)
    {
        report.AppendLine(msg);
        Debug.Log(Tag + msg);
    }

    static void Finish()
    {
        Time.timeScale = 1f;
        EditorApplication.update -= Tick;
        EditorSettings.enterPlayModeOptionsEnabled = savedEnabled;
        EditorSettings.enterPlayModeOptions = savedOptions;
        Log(failures == 0 ? "AI TEST PASSED" : $"AI TEST: {failures} FAILURE(S)");
        Debug.Log(Tag + "SUMMARY\n" + report);
        if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
    }
}
