using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

/// <summary>
/// End-to-end play-mode smoke test (batchmode, no -quit):
///   -executeMethod RaceSmokeTest.Run
/// A simulated phone joins over a real WebSocket, picks + readies (auto start),
/// drives through an item box (roulette + held item sent to the phone), fires
/// a rocket with its ITEM button; banana, shield and turbo are exercised on CPU
/// karts; then all karts race 3 laps on autopilot (CPUs using items), results
/// appear, the phone returns everyone to the lobby, play mode exits and the
/// server port must be free again.
/// </summary>
public static class RaceSmokeTest
{
    const string Tag = "[RaceSmokeTest] ";
    const string ShotDir = "Logs/SmokeShots"; // Temp/ is wiped when the editor exits

    static int stage;
    static double stageStart;
    static int failures;
    static RaceManager rm;
    static Vector3 humanStart;
    static int serverPort;
    static bool shotTaken;

    // Simulated phone.
    static Thread phoneThread;
    static volatile bool phoneRunning, phoneDriving;
    static readonly ConcurrentQueue<string> phoneOutbox = new ConcurrentQueue<string>();
    static readonly ConcurrentQueue<string> phoneInbox = new ConcurrentQueue<string>();
    static readonly StringBuilder phoneLog = new StringBuilder();

    public static void Run()
    {
        // Keep static test state across entering play mode; restored in Finish().
        savedOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        savedOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
        EditorSceneManager.OpenScene("Assets/Scenes/Race.unity");
        stage = 0;
        stageStart = EditorApplication.timeSinceStartup;
        EditorApplication.update += Tick;
        Log("entering play mode");
        EditorApplication.EnterPlaymode();
    }

    static double Elapsed => EditorApplication.timeSinceStartup - stageStart;

    static void Next(string message)
    {
        Log(message);
        stage++;
        stageStart = EditorApplication.timeSinceStartup;
    }

    static void Tick()
    {
        try
        {
            TickInternal();
        }
        catch (Exception e)
        {
            Check(false, "exception in smoke test: " + e);
            Finish();
        }
    }

    static void TickInternal()
    {
        while (phoneInbox.TryDequeue(out string msg)) phoneLog.AppendLine(msg);
        if (pendingShot != null)
        {
            if (--pendingFrames > 0) return;
            FinishUiShot();
            return;
        }
        if (perfFramesLeft > 0)
        {
            if (perfSkip-- <= 0)
            {
                perfSum += Time.unscaledDeltaTime;
                perfMax = Mathf.Max(perfMax, Time.unscaledDeltaTime);
                perfFramesLeft--;
            }
            if (perfFramesLeft == 0)
            {
                float avg = perfSum / PerfFrames * 1000f;
                Log($"PERF {perfLabel}: player-loop frame {avg:F2} ms, worst {perfMax * 1000f:F1} ms over {PerfFrames} frames (batchmode, no present); " +
                    $"render benchmark 1920x1080 all viewports: {RenderBenchmark():F2} ms/frame, mode {ToonStyle.Instance?.Mode}");
                perfDone?.Invoke();
            }
            return;
        }
        if (phoneItemUntil > 0 && EditorApplication.timeSinceStartup > phoneItemUntil)
        {
            phoneItem = false;
            phoneItemUntil = 0;
        }
        if (phoneHopUntil > 0 && EditorApplication.timeSinceStartup > phoneHopUntil)
        {
            phoneHop = false;
            phoneHopUntil = 0;
        }

        switch (stage)
        {
            case 0: // play mode up, lobby ready, server running
                if (Elapsed > 90) { Check(false, "timeout waiting for lobby"); Finish(); return; }
                if (!EditorApplication.isPlaying) return;
                rm = Object.FindAnyObjectByType<RaceManager>();
                if (rm == null || rm.Phase != RacePhase.Lobby || !rm.ServerRunning) return;
                serverPort = rm.ServerPort;
                Check(true, $"lobby up, server on port {serverPort}, url {rm.Url}, QR={(rm.QrTexture != null ? rm.QrTexture.width + "px" : "none")}");
                StartPhone(serverPort);
                phoneOutbox.Enqueue("hello|smoke-phone-1");
                phoneOutbox.Enqueue("pick|1");
                Next("phone joined and picked");
                break;

            case 1: // phone joined -> add a READY keyboard player (P2), check the lobby UI, then the phone readies
                if (Elapsed > 20) { Check(false, "race did not auto-start when all players were ready"); Finish(); return; }
                if (!keyboardAdded)
                {
                    if (rm.Players.Count < 1) return;
                    rm.AddKeyboardPlayerForTest(true);
                    rm.AddBotPlayerForTest(); // P3, P4: idle READY players so 3P/4P layouts can be checked
                    rm.AddBotPlayerForTest();
                    keyboardAdded = true;
                    return;
                }
                if (!lobbyUiChecked)
                {
                    if (Elapsed < 1.5) return; // previews rendered, cards laid out
                    Ui = rm.GetComponent<RaceUi>();
                    var am = AudioManager.Instance;
                    Check(Ui != null && Ui.Built && Ui.LobbyVisible && Ui.LobbyCardsFilled == 4,
                        $"lobby UI: built={Ui != null && Ui.Built}, visible={Ui?.LobbyVisible}, filled player cards={Ui?.LobbyCardsFilled}");
                    Check(am != null && am.Music == MusicState.Lobby && am.MusicPlaying,
                        $"lobby music: state={am?.Music}, playing={am?.MusicPlaying}");
                    lobbyUiChecked = true;
                    StartUiShot("ui_lobby.png");
                    return;
                }
                if (!phoneReadied)
                {
                    phoneOutbox.Enqueue("ready|1");
                    phoneReadied = true;
                    return;
                }
                if (rm.Phase != RacePhase.Countdown) return;
                var human = rm.HumanRacers[0];
                humanStart = human.Kart.transform.position;
                Check(rm.HumanRacers.Count == 4 && rm.Standings.Count == rm.KartCount,
                    $"countdown started: humans={rm.HumanRacers.Count} (phone + keyboard + 2 idle test players), karts={rm.Standings.Count}, P1 drives '{rm.Roster[human.Character].displayName}'");
                CheckGrid();
                Next("countdown");
                break;

            case 2: // GO -> phone holds throttle
                if (Elapsed > 10) { Check(false, "countdown never reached GO"); Finish(); return; }
                if (!countdownChecked && rm.Phase == RacePhase.Countdown && Elapsed > 0.4)
                {
                    countdownChecked = true;
                    Check(Ui.CountdownVisible && (Ui.CountdownText == "3" || Ui.CountdownText == "2"),
                        $"countdown UI shows '{Ui.CountdownText}', beeps so far={AudioManager.Instance.PlayedCount(Sfx.CountBeep)}");
                    StartUiShot("ui_countdown.png");
                    return;
                }
                if (rm.Phase != RacePhase.Racing) return;
                phoneDriving = true;
                Next("racing, phone holds GAS");
                break;

            case 3:
                if (!shotTaken && Elapsed > 1.5)
                {
                    TakeShots("race");
                    shotTaken = true;
                    CheckShaders();
                    var style = ToonStyle.Instance;
                    Check(style != null && style.Mode == VisualMode.ToonBloom, $"visual mode default: {style?.Mode}");
                    if (style != null)
                    {
                        style.SetMode(VisualMode.Plain);
                        Capture(rm.PlayerCamera(0), $"{ShotDir}/race_chasecam_plain.png");
                        style.SetMode(VisualMode.ToonBloom);
                        Capture(rm.PlayerCamera(0), $"{ShotDir}/race_chasecam_toon_bloom.png");
                    }
                }
                if (!raceUiChecked && Elapsed > 2.0)
                {
                    raceUiChecked = true;
                    var am = AudioManager.Instance;
                    Check(AudioManager.Instance.PlayedCount(Sfx.CountBeep) == 3 && am.PlayedCount(Sfx.CountGo) == 1,
                        $"countdown audio: {am.PlayedCount(Sfx.CountBeep)} beeps + {am.PlayedCount(Sfx.CountGo)} GO");
                    Check(am.Music == MusicState.Race && am.MusicPlaying, $"race music: state={am.Music}, playing={am.MusicPlaying}");
                    var audios = rm.Standings.Select(r => r.Kart.GetComponent<KartAudio>()).ToList();
                    Check(audios.All(a => a != null && a.EnginePlaying && a.Engine.clip != null),
                        $"engine sounds playing on {audios.Count(a => a != null && a.EnginePlaying)}/{audios.Count} karts, pitches " +
                        string.Join(" ", audios.Select(a => a != null ? a.EnginePitch.ToString("F2") : "-")) +
                        $", human karts 2D={audios.Where((a, i) => rm.Standings[i].Human != null).All(a => a.Engine.spatialBlend == 0f)}");
                    Check(Ui.HudVisible(0) && Ui.HudVisible(1) && !string.IsNullOrEmpty(Ui.HudPosition(0)) && Ui.HudLap(0).StartsWith("LAP") && Ui.MinimapDots == rm.KartCount,
                        $"race HUD: P1 '{Ui.HudPosition(0)}' '{Ui.HudLap(0)}' item '{Ui.HudItem(0)}', P2 '{Ui.HudPosition(1)}', minimap dots={Ui.MinimapDots}");
                    rm.DebugSetViewports(1);
                    StartPerf("1P", () => StartUiShot("ui_race_1p.png"));
                    return;
                }
                if (raceUiChecked && viewportShots < 4)
                {
                    viewportShots++;
                    int n = viewportShots + 1; // 2, 3, 4
                    if (n <= 4)
                    {
                        rm.DebugSetViewports(n);
                        if (n == 2 || n == 4) CheckViewportIsolation(n);
                        if (n == 4) StartPerf("4P", () => StartUiShot("ui_race_4p.png"));
                        else StartUiShot($"ui_race_{n}p.png");
                        return;
                    }
                }
                if (Elapsed < 3) return;
                float moved = Vector3.Distance(humanStart, rm.HumanRacers[0].Kart.transform.position);
                Check(moved > 15f, $"phone throttle moved P1 kart {moved:F1} m in 3 s");
                // Drive P1 into an item box row.
                var box = Object.FindObjectsByType<ItemBox>().Where(b => b.Available).OrderBy(b => b.name).FirstOrDefault();
                Check(box != null, $"item boxes in scene: {Object.FindObjectsByType<ItemBox>().Length}");
                if (box == null) { Finish(); return; }
                P1.Items.ResetItems();
                Place(P1.Kart, box.transform.position - box.transform.forward * 14f, box.transform.rotation);
                boxRowS = Track.Project(box.transform.position);
                pickupsBefore = ItemBox.Pickups;
                Next($"P1 placed 14 m before {box.name}");
                break;

            case 4: // box -> roulette
                if (Elapsed > 5) { Check(false, "P1 did not pick up an item box"); Finish(); return; }
                if (!P1.Items.IsRolling && P1.Items.Held == ItemType.None) return;
                Check(ItemBox.Pickups > pickupsBefore, $"item box pickup started the roulette (pickups {pickupsBefore}->{ItemBox.Pickups})");
                Next("roulette spinning");
                break;

            case 5: // roulette -> item
                if (Elapsed > 3) { Check(false, "roulette never granted an item"); Finish(); return; }
                if (P1.Items.IsRolling || P1.Items.Held == ItemType.None) return;
                Check(true, $"roulette granted '{P1.Items.Held}' to P1 (position {rm.PositionOf(P1.Kart)})");
                Next("roulette done");
                break;

            case 6: // rocket fired from the phone at a CPU ahead
                if (Elapsed < 0.6) return; // let the phone receive the item message
                Check(phoneLog.ToString().Contains("item|roll|1") && phoneLog.ToString().Contains($"item|{P1.Items.Held.ToString().ToLowerInvariant()}|0"),
                    $"phone received roulette + held item '{P1.Items.Held}'");
                P1.Items.GiveItem(ItemType.Rocket);
                victim = Cpu(0);
                {
                    RaceTrack tr = Track;
                    float sv = tr.Project(P1.Kart.transform.position) + 28f;
                    Place(victim.Kart, tr.PointAt(sv), Quaternion.LookRotation(tr.TangentAt(sv)));
                }
                hitsBefore = victim.Items.HitsTaken;
                othersHitBefore = rm.Standings.Where(r => r != P1).Sum(r => r.Items.HitsTaken + r.Items.HitsBlocked);
                phoneItem = true;
                phoneItemUntil = EditorApplication.timeSinceStartup + 0.3;
                rocketShot = false;
                Next($"phone ITEM pressed (rocket) at {victim.Name} 28 m ahead");
                break;

            case 7:
                if (!rocketShot && ItemManager.Instance.Rockets.Count > 0 && Elapsed > 0.15)
                {
                    var rk = ItemManager.Instance.Rockets[0];
                    if (rk != null) ShotAt(rk.transform.position - rk.transform.forward * 6f + Vector3.up * 2.5f, rk.transform.position, "item_rocket.png");
                    rocketShot = true;
                }
                if (victim.Items.HitsTaken > hitsBefore)
                {
                    Check(P1.Items.Held == ItemType.None, "phone ITEM button fired the rocket");
                    Check(victim.Kart.IsSpinningOut, $"rocket hit {victim.Name}: spinning out");
                    Next("rocket ok");
                    break;
                }
                // The rocket homes on the nearest kart ahead; another kart may cut in front of the target.
                if (ItemManager.Instance.Rockets.Count == 0 && Elapsed > 0.5 &&
                    rm.Standings.Where(r => r != P1).Sum(r => r.Items.HitsTaken + r.Items.HitsBlocked) > othersHitBefore)
                {
                    Check(P1.Items.Held == ItemType.None, "phone ITEM button fired the rocket");
                    Check(true, "rocket hit a kart ahead (another kart got in front of the intended target)");
                    Next("rocket ok");
                    break;
                }
                if (Elapsed > 4) { Check(false, $"rocket did not hit {victim.Name} (P1 held={P1.Items.Held}, rockets={ItemManager.Instance.UsesOf(ItemType.Rocket)})"); Next("rocket failed"); }
                break;

            case 8: // banana on P1's line (P1 drives straight on the phone's GAS)
                victim = P1;
                victim.Items.ResetItems();
                hitsBefore = victim.Items.HitsTaken;
                {
                    RaceTrack tr = Track;
                    float L = tr.Length;
                    Place(P1.Kart, tr.PointAt(L - 90f), Quaternion.LookRotation(tr.TangentAt(L - 90f)));
                    Vector3 bananaPos = tr.PointAt(L - 62f);
                    ItemManager.Instance.SpawnBananaAt(bananaPos, Quaternion.LookRotation(tr.TangentAt(L - 62f)), null);
                    ShotAt(bananaPos + tr.RightAt(L - 62f) * 5f + Vector3.up * 2.5f - tr.TangentAt(L - 62f) * 6f, bananaPos, "item_banana.png");
                }
                Next("banana dropped 28 m ahead of P1");
                break;

            case 9:
                if (victim.Items.HitsTaken > hitsBefore)
                {
                    Check(victim.Kart.IsSpinningOut, $"banana hit {victim.Name}: spinning out");
                    Next("banana ok");
                    break;
                }
                if (Elapsed > 4) { Check(false, $"banana was not hit by {victim.Name}"); Next("banana failed"); }
                break;

            case 10: // shield absorbs a banana
                victim = Cpu(2);
                hitsBefore = victim.Items.HitsTaken;
                blockedBefore = victim.Items.HitsBlocked;
                victim.Items.GiveItem(ItemType.Shield);
                victim.Items.UseHeldItem();
                Check(victim.Items.ShieldActive, $"shield raised on {victim.Name}");
                Next("shield up");
                break;

            case 11:
                if (!shieldShot)
                {
                    if (Elapsed < 0.3) return; // let the bubble appear for the screenshot
                    Transform vt = victim.Kart.transform;
                    ShotAt(vt.position + vt.right * 6f + vt.forward * 4f + Vector3.up * 2.5f, vt.position + Vector3.up, "item_shield.png");
                    shieldShot = true;
                    if (!victim.Items.ShieldActive) // consumed by a bump in the pack meanwhile: raise it again
                    {
                        blockedBefore = victim.Items.HitsBlocked;
                        hitsBefore = victim.Items.HitsTaken;
                        victim.Items.GiveItem(ItemType.Shield);
                        victim.Items.UseHeldItem();
                    }
                    // Banana contact itself is covered by the banana test; here the hit is applied directly.
                    Check(!victim.Items.Hit(ItemType.Banana), "banana hit on the shielded kart reported as absorbed");
                }
                if (victim.Items.HitsBlocked > blockedBefore)
                {
                    Check(victim.Items.HitsTaken == hitsBefore && !victim.Items.ShieldActive, $"shield absorbed the banana ({victim.Name} not hit, shield consumed)");
                    Next("shield ok");
                    break;
                }
                if (Elapsed > 4) { Check(false, $"shielded {victim.Name} never touched the banana (taken {victim.Items.HitsTaken - hitsBefore})"); Next("shield failed"); }
                break;

            case 12:
            case 13:
                Next("(turbo is tested on P1 right after the hop)");
                break;

            case 14: // HOP over a banana (phone HOP button, timed by the test)
                P1.Items.ResetItems();
                {
                    RaceTrack tr = Track;
                    float L = tr.Length;
                    Place(P1.Kart, tr.PointAt(L - 90f), Quaternion.LookRotation(tr.TangentAt(L - 90f)));
                    hopBanana = ItemManager.Instance.SpawnBananaAt(tr.PointAt(L - 45f), Quaternion.LookRotation(tr.TangentAt(L - 45f)), null);
                    hopTangent = tr.TangentAt(L - 45f);
                }
                hitsBefore = P1.Items.HitsTaken;
                hopSent = false;
                maxAir = 0f;
                ShotObstacles();
                Next("P1 placed 45 m before a banana, phone holds GAS");
                break;

            case 15:
            {
                if (Elapsed > 10) { Check(false, "hop test timed out"); Next("hop failed"); break; }
                if (hopBanana == null) // the banana was hit
                {
                    Check(false, $"P1 hit the banana instead of hopping over it (hop sent={hopSent} at {hopSpeedAt:F1} m/s, max air {maxAir:F2} s)");
                    Next("hop failed");
                    break;
                }
                Vector3 p = P1.Kart.transform.position;
                float d = Vector3.Dot(hopBanana.transform.position - p, hopTangent);
                float v = Mathf.Max(1f, P1.Kart.ForwardSpeed);
                maxAir = Mathf.Max(maxAir, P1.Kart.AirTime);
                if (!hopSent && d <= 1.9f + 0.1f * v)
                {
                    phoneHop = true;
                    phoneHopUntil = EditorApplication.timeSinceStartup + 0.12;
                    hopSent = true;
                    hopSpeedAt = v;
                }
                if (d < -6f)
                {
                    Check(P1.Items.HitsTaken == hitsBefore && maxAir > 0.2f,
                        $"phone HOP cleared the banana at {hopSpeedAt:F1} m/s (air time {maxAir:F2} s, hits {P1.Items.HitsTaken - hitsBefore})");
                    // Turbo on the same straight.
                    turboBase = P1.Kart.MaxSpeed;
                    turboPeak = 0f;
                    P1.Items.GiveItem(ItemType.Turbo);
                    P1.Items.UseHeldItem();
                    turboStart = EditorApplication.timeSinceStartup;
                    Next("hop ok, turbo used by P1");
                }
                break;
            }

            case 16: // turbo check, then wall crash
                if (EditorApplication.timeSinceStartup - turboStart < 1.2)
                {
                    turboPeak = Mathf.Max(turboPeak, P1.Kart.ForwardSpeed);
                    return;
                }
                if (!turboChecked)
                {
                    Check(turboPeak > turboBase * 1.05f, $"turbo pushed P1 to {turboPeak:F1} m/s (normal top {turboBase:F1}), FOV kick + boost trail active while boosting");
                    turboChecked = true;
                    stageStart = EditorApplication.timeSinceStartup;
                }
                if (!crashSetup)
                {
                    RaceTrack tr = Track;
                    float s = tr.Length - 60f; // main straight (before the grid): walls parallel to the track
                    P1.Items.ResetItems();
                    Vector3 right = tr.RightAt(s);
                    Place(P1.Kart, tr.PointAt(s) - right * 1.5f, Quaternion.LookRotation(right));
                    P1.Kart.Body.linearVelocity = right * 22f;
                    crashesBefore = P1.Items.Crashes;
                    crashSetup = true;
                    return;
                }
                if (P1.Items.Crashes > crashesBefore)
                {
                    Check(P1.Kart.IsSpinningOut, $"wall impact at speed -> spin-out (crashes {crashesBefore}->{P1.Items.Crashes})");
                    Next("crash ok");
                    break;
                }
                if (Elapsed > 3)
                {
                    RaceTrack tr = Track;
                    Vector3 kp = P1.Kart.transform.position;
                    float sk = tr.Project(kp);
                    float lat = Vector3.Dot(kp - tr.PointAt(sk), tr.RightAt(sk));
                    Check(false, $"driving into the wall at 22 m/s did not trigger a crash spin-out (last impact: {P1.Kart.LastImpact}; last contact: {P1.Kart.LastContact}; " +
                                 $"now lateral {lat:F1} m, y {kp.y:F1}, speed {P1.Kart.ForwardSpeed:F1}, spinning {P1.Kart.IsSpinningOut}, locked {P1.Kart.ControlsLocked})");
                    Next("crash failed");
                }
                break;

            case 17: // rescue: one CPU off the track on the grass, one fallen below the ground
            {
                RaceTrack tr = Track;
                rescueA = Cpu(0);
                rescueB = Cpu(1);
                if (rescueB == rescueA) rescueB = Cpu(2);
                rescuesBefore = RescueService.Instance.RescueCount;
                float s = boxRowS;
                Place(rescueA.Kart, tr.PointAt(s) + tr.RightAt(s) * 28f, Quaternion.LookRotation(tr.TangentAt(s)), false);
                rescueB.Kart.Teleport(tr.PointAt(s + 30f) + Vector3.down * 12f, Quaternion.LookRotation(tr.TangentAt(s + 30f)));
                helperShot = false;
                Next($"{rescueA.Name} dropped on the grass, {rescueB.Name} below the ground");
                break;
            }

            case 18:
            {
                if (!helperShot && RescueService.Instance.LastHelper != null && Elapsed > 1.2)
                {
                    Vector3 hp = RescueService.Instance.LastHelper.transform.position;
                    ShotAt(hp + new Vector3(9f, 3f, 9f), hp + Vector3.down * 1.5f, "rescue_helper.png");
                    helperShot = true;
                }
                bool done = RescueService.Instance.RescueCount >= rescuesBefore + 2
                            && !RescueService.Instance.IsRescuing(rescueA.Kart) && !RescueService.Instance.IsRescuing(rescueB.Kart);
                if (done && Elapsed > 1)
                {
                    RaceTrack tr = Track;
                    foreach (var r in new[] { rescueA, rescueB })
                    {
                        Vector3 p = r.Kart.transform.position;
                        float off = Vector3.Distance(new Vector3(p.x, 0f, p.z), Flat(tr.PointAt(tr.Project(p))));
                        Check(off < 6f && p.y > -1f, $"rescued {r.Name}: back on the track ({off:F1} m from centerline, y={p.y:F1})");
                    }
                    Next("rescue ok");
                    break;
                }
                if (Elapsed > 15) { Check(false, $"rescue did not complete (count {RescueService.Instance.RescueCount - rescuesBefore})"); Next("rescue failed"); }
                break;
            }

            case 19:
                phoneDriving = false; // phone goes silent (keep-alive pings only)
                Next("phone silent");
                break;

            case 20:

                if (Elapsed < 1.5) return;
                var input = rm.Players[0].Input;
                Check(input.Throttle == 0f && input.Steer == 0f, "silent phone -> NetworkKartInput timed out to zero input");
                int aiMoving = rm.Standings.Count(r => r.Human == null && r.Kart.ForwardSpeed > 5f);
                int cpuCount = rm.Standings.Count(r => r.Human == null);
                Check(aiMoving >= cpuCount - 1, $"CPU karts driving: {aiMoving}/{cpuCount} above 5 m/s");
                rm.SetAutopilotForTest();
                aiUsesBefore = ItemManager.Instance.AiUses;
                usesBefore = ItemTable.Items.Select(t => ItemManager.Instance.UsesOf(t)).ToArray();
                Time.timeScale = 5f;
                Next("autopilot for P1, timeScale 5");
                break;

            case 21: // wait for results
                if (Elapsed > 300) { Check(false, "race did not reach results"); DumpStandings(); Finish(); return; }
                if (AudioManager.Instance.Music == MusicState.FinalLap && AudioManager.Instance.MusicPitch > 1.1f) sawFinalLapMusic = true;
                if (Ui.FinalLapBannerShown(0) || Ui.FinalLapBannerShown(1)) sawFinalLapBanner = true;
                if (rm.Phase != RacePhase.Results) return;
                if (resultsSeenAt < 0) { resultsSeenAt = EditorApplication.timeSinceStartup; Time.timeScale = 1f; return; }
                if (EditorApplication.timeSinceStartup - resultsSeenAt < 1.2) return;
                if (!resultsShot)
                {
                    resultsShot = true;
                    Check(Ui.ResultsVisible && Ui.ResultRows == rm.KartCount && rm.Podium.Showing && rm.Podium.PerformerCount == 3,
                        $"results UI: visible={Ui.ResultsVisible}, rows={Ui.ResultRows}, podium camera={rm.Podium.Showing}, podium characters={rm.Podium.PerformerCount}");
                    Check(sawFinalLapMusic && sawFinalLapBanner, $"final lap: music sped up={sawFinalLapMusic}, FINAL LAP banner={sawFinalLapBanner}");
                    var am = AudioManager.Instance;
                    var counts = new[] { Sfx.ItemBox, Sfx.RouletteTick, Sfx.RouletteDing, Sfx.RocketLaunch, Sfx.Explosion, Sfx.BananaDrop, Sfx.BananaSlip,
                        Sfx.ShieldUp, Sfx.ShieldBreak, Sfx.Turbo, Sfx.Hop, Sfx.WallCrash, Sfx.SpinOut, Sfx.Rescue, Sfx.LapComplete, Sfx.FinalLap,
                        Sfx.Finish1st, Sfx.FinishPodium, Sfx.FinishOther, Sfx.UiSelect, Sfx.UiReady, Sfx.UiMove };
                    string summary = string.Join(", ", counts.Select(c => $"{c}={am.PlayedCount(c)}"));
                    string[] mustPlay = { "ItemBox", "RouletteDing", "RocketLaunch", "Explosion", "BananaSlip", "ShieldUp", "Turbo", "Hop", "WallCrash", "Rescue", "FinalLap", "UiReady" };
                    Check(mustPlay.All(n => am.PlayedCount((Sfx)Enum.Parse(typeof(Sfx), n)) > 0) &&
                          am.PlayedCount(Sfx.Finish1st) + am.PlayedCount(Sfx.FinishPodium) + am.PlayedCount(Sfx.FinishOther) > 0,
                        "sfx played: " + summary);
                    StartUiShot("ui_results_podium.png");
                    return;
                }
                Time.timeScale = 1f;
                DumpStandings();
                int finished = rm.Standings.Count(r => r.Lap.Finished);
                Check(rm.HumanRacers[0].Lap.Finished && finished >= 1, $"results reached, finished karts={finished}/{rm.KartCount}");
                int aiUses = ItemManager.Instance.AiUses - aiUsesBefore;
                var perType = ItemTable.Items.Select((t, i) => $"{t}={ItemManager.Instance.UsesOf(t) - usesBefore[i]}");
                int typesUsed = ItemTable.Items.Where((t, i) => ItemManager.Instance.UsesOf(t) - usesBefore[i] > 0).Count();
                int hits = rm.Standings.Sum(r => r.Items.HitsTaken), blocks = rm.Standings.Sum(r => r.Items.HitsBlocked);
                Check(aiUses >= 3 && typesUsed >= 2, $"CPU item use during the race: {aiUses} uses ({string.Join(", ", perType)}), hits={hits}, blocked={blocks}, box pickups={ItemBox.Pickups}");
                var cpusDone = rm.Standings.Where(r => r.Human == null).ToList();
                int cpuComplete = cpusDone.Count(r => r.Lap.Finished || r.Lap.CurrentLap >= rm.Laps);
                var finishedCpus = cpusDone.Where(r => r.Lap.Finished).ToList();
                float avgLap = finishedCpus.Count > 0 ? finishedCpus.Average(r => r.Lap.FinishTime) / rm.Laps : -1f;
                float bestLap = cpusDone.Where(r => r.Lap.BestLap > 0f).Select(r => r.Lap.BestLap).DefaultIfEmpty(-1f).Min();
                int crashes = rm.Standings.Sum(r => r.Items.Crashes);
                Log("hazard hits: " + string.Join(", ", Hazard.All.Select(h => $"{h.name}={h.Hits}")));
                string crashKinds = $"walls {rm.Standings.Sum(r => r.Kart.WallCrashes)}, karts {rm.Standings.Sum(r => r.Kart.KartCrashes)}, hazards {rm.Standings.Sum(r => r.Kart.HazardCrashes)}";
                Check(cpuComplete == cpusDone.Count,
                    $"CPUs reached the final lap or finished: {cpuComplete}/{cpusDone.Count}; avg CPU lap {avgLap:F1} s, best lap {bestLap:F1} s, " +
                    $"track {Track.Length:F0} m, rescues={RescueService.Instance.RescueCount}, crash spin-outs={crashes} ({crashKinds})");
                phoneOutbox.Enqueue("start"); // host phone: back to lobby
                Next("results -> phone START");
                break;

            case 22:
                if (Elapsed > 10) { Check(false, "phone START did not return to lobby"); Finish(); return; }
                if (rm.Phase != RacePhase.Lobby) return;
                Check(true, "back in lobby");
                TakeShots("lobby");
                TopDownShot();
                CheckThumbnails();
                string log = phoneLog.ToString();
                foreach (string expect in new[] { "joined|0|", "pick|", "phase|countdown", "count|3", "phase|race", "hud|", "item|roll|1", "buzz|", "result|", "phase|results", "phase|lobby" })
                    Check(log.Contains(expect), $"phone received '{expect}'");
                Next("stopping play mode");
                StopPhone();
                EditorApplication.ExitPlaymode();
                break;

            case 23:
                if (EditorApplication.isPlaying) return;
                if (Elapsed < 1) return;
                bool free;
                try
                {
                    var probe = new TcpListener(IPAddress.Any, serverPort);
                    probe.Start();
                    probe.Stop();
                    free = true;
                }
                catch { free = false; }
                Check(free, $"after leaving play mode port {serverPort} is free (server thread shut down)");
                Finish();
                break;
        }
    }

    // ---- item test helpers ---------------------------------------------------------------------

    static int pickupsBefore, hitsBefore, blockedBefore, aiUsesBefore;
    static int[] usesBefore;
    static float turboBase, turboPeak;
    static bool rocketShot, shieldShot;
    static RaceManager.Racer victim;
    static volatile bool phoneItem;
    static double phoneItemUntil;

    static RaceManager.Racer P1 => rm.HumanRacers[0];
    static RaceUi Ui;
    const int PerfFrames = 90;
    static int perfFramesLeft, perfSkip;
    static float perfSum, perfMax;
    static string perfLabel;
    static Action perfDone;

    /// <summary>Renders every active camera (with their viewport rects and image effects) into a
    /// 1920x1080 target 30 times and forces a GPU sync; returns milliseconds per frame.</summary>
    static float RenderBenchmark()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return -1f;
        var cams = Object.FindObjectsByType<Camera>().Where(c => c.enabled && c.gameObject.activeInHierarchy && c.targetTexture == null).OrderBy(c => c.depth).ToList();
        var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.DefaultHDR);
        var probe = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int frames = 30;
        for (int f = 0; f < frames; f++)
        {
            foreach (var cam in cams)
            {
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;
            }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false); // waits for the GPU
            RenderTexture.active = prev;
        }
        float ms = (float)sw.Elapsed.TotalMilliseconds / frames;
        rt.Release();
        Object.DestroyImmediate(probe);
        return ms;
    }

    static void StartPerf(string label, Action done)
    {
        perfLabel = label;
        perfDone = done;
        perfFramesLeft = PerfFrames;
        perfSkip = 10; // let the layout change settle
        perfSum = perfMax = 0f;
    }

    /// <summary>
    /// Split-screen regression check: each player camera clears to its own marker colour (drawing
    /// nothing), all cameras render with their image effects into one target, and the centre of
    /// every viewport must show that camera's colour (a full-screen blit would overwrite the others).
    /// </summary>
    static void CheckViewportIsolation(int n)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        Color[] markers = { Color.red, Color.green, Color.blue, Color.magenta };
        var saved = new System.Collections.Generic.List<(Camera cam, CameraClearFlags flags, Color bg, int mask)>();
        for (int i = 0; i < n; i++)
        {
            Camera c = rm.PlayerCamera(i);
            saved.Add((c, c.clearFlags, c.backgroundColor, c.cullingMask));
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = markers[i];
            c.cullingMask = 0;
        }
        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.DefaultHDR);
        foreach (var cam in Object.FindObjectsByType<Camera>().Where(c => c.enabled && c.gameObject.activeInHierarchy && c.targetTexture == null).OrderBy(c => c.depth))
        {
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
        }
        var img = ReadBack(rt);
        rt.Release();
        foreach (var s in saved)
        {
            s.cam.clearFlags = s.flags;
            s.cam.backgroundColor = s.bg;
            s.cam.cullingMask = s.mask;
        }
        var report = new StringBuilder();
        bool ok = true;
        for (int i = 0; i < n; i++)
        {
            Rect r = rm.PlayerCamera(i).rect;
            Color px = img.GetPixel((int)((r.x + r.width * 0.5f) * img.width), (int)((r.y + r.height * 0.5f) * img.height));
            Color want = markers[i];
            bool match = Mathf.Abs(px.r - want.r) < 0.35f && Mathf.Abs(px.g - want.g) < 0.35f && Mathf.Abs(px.b - want.b) < 0.35f;
            ok &= match;
            report.Append($" P{i + 1}@({r.x:F1},{r.y:F1})={(match ? "own colour" : $"WRONG {px}")}");
        }
        Object.DestroyImmediate(img);
        Check(ok, $"split screen {n}P: each viewport shows its own camera (bloom on):{report}");
    }

    /// <summary>Fails if any renderer uses a missing/unsupported (pink) shader.</summary>
    static void CheckShaders()
    {
        var bad = new System.Collections.Generic.List<string>();
        int materials = 0, toon = 0;
        foreach (var r in Object.FindObjectsByType<Renderer>())
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { bad.Add(r.name + ": <null material>"); continue; }
                materials++;
                if (m.shader == null || !m.shader.isSupported || m.shader.name.Contains("InternalError")) bad.Add($"{r.name}: {m.name} ({m.shader?.name})");
                if (m.shader != null && m.shader.name.StartsWith("Kart/Toon")) toon++;
            }
        var bloom = ToonStyle.Instance != null ? ToonStyle.Instance.BloomShader : null;
        Check(bad.Count == 0 && bloom != null && bloom.isSupported,
            $"shaders: {materials} materials in use, {toon} toon, bloom supported={bloom != null && bloom.isSupported}, unsupported={bad.Count} " + string.Join("; ", bad.Take(5)));
    }
    static int othersHitBefore;
    static bool keyboardAdded, lobbyUiChecked, phoneReadied, countdownChecked, raceUiChecked, resultsShot;
    static int viewportShots;
    static bool sawFinalLapMusic, sawFinalLapBanner;
    static double resultsSeenAt = -1;

    // ---- screenshots including the UI Toolkit overlay ---------------------------------------------
    // The panel is redirected into a RenderTexture for a few frames, then composited over the
    // cameras' image (each camera renders into its own viewport rect).
    static string pendingShot;
    static int pendingFrames;
    static RenderTexture uiTarget;

    static void StartUiShot(string file)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Ui == null) return;
        var ps = Ui.Document.panelSettings;
        uiTarget = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        ps.targetTexture = uiTarget;
        ps.clearColor = true;
        ps.colorClearValue = new Color(0f, 0f, 0f, 0f);
        pendingShot = file;
        pendingFrames = 4;
    }

    static void FinishUiShot()
    {
        var ps = Ui.Document.panelSettings;
        var ui = ReadBack(uiTarget);
        ps.targetTexture = null;
        ps.clearColor = false;
        uiTarget.Release();

        var scene = new RenderTexture(1280, 720, 24);
        foreach (var cam in Object.FindObjectsByType<Camera>().Where(c => c.enabled && c.gameObject.activeInHierarchy && c.targetTexture == null).OrderBy(c => c.depth))
        {
            cam.targetTexture = scene;
            cam.Render();
            cam.targetTexture = null;
        }
        var img = ReadBack(scene);
        scene.Release();

        Color[] a = img.GetPixels(), b = ui.GetPixels();
        int uiPixels = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float alpha = b[i].a;
            if (alpha > 0.01f) uiPixels++;
            a[i] = new Color(a[i].r * (1 - alpha) + b[i].r, a[i].g * (1 - alpha) + b[i].g, a[i].b * (1 - alpha) + b[i].b, 1f); // UI is premultiplied
        }
        img.SetPixels(a);
        Directory.CreateDirectory(ShotDir);
        File.WriteAllBytes($"{ShotDir}/{pendingShot}", img.EncodeToPNG());
        Log($"UI screenshot {pendingShot}: {uiPixels * 100f / a.Length:F1}% of pixels covered by UI");
        Object.DestroyImmediate(img);
        Object.DestroyImmediate(ui);
        pendingShot = null;
    }

    static Texture2D ReadBack(RenderTexture rt)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        return tex;
    }
    static RaceTrack Track => Object.FindAnyObjectByType<RaceTrack>();
    static BananaPeel hopBanana;
    static Vector3 hopTangent;
    static bool hopSent, crashSetup, helperShot, turboChecked;
    static double turboStart;
    static float maxAir, hopSpeedAt, boxRowS;
    static int crashesBefore, rescuesBefore;
    static RaceManager.Racer rescueA, rescueB;
    static volatile bool phoneHop;
    static double phoneHopUntil;

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    /// <summary>Point on the kart's current lane, the given distance further along the track.</summary>
    static Vector3 AheadOnTrack(KartController kart, float distance)
    {
        RaceTrack tr = Track;
        Vector3 p = kart.transform.position;
        float s = tr.Project(p);
        float lateral = Vector3.Dot(p - tr.PointAt(s), tr.RightAt(s));
        return tr.PointAt(s + distance) + tr.RightAt(s + distance) * lateral;
    }

    static void ShotObstacles()
    {
        var block = Hazard.All.FirstOrDefault(h => h.Moving);
        if (block != null)
        {
            Vector3 bp = block.transform.position;
            RaceTrack tr = Track;
            float s = tr.Project(bp);
            ShotAt(bp - tr.TangentAt(s) * 16f + Vector3.up * 5f, bp, "obstacles_block.png");
        }
        var cone = Hazard.All.FirstOrDefault(h => !h.Moving);
        if (cone != null)
        {
            Vector3 cp = cone.transform.position;
            RaceTrack tr = Track;
            float s = tr.Project(cp);
            ShotAt(cp - tr.TangentAt(s) * 14f + Vector3.up * 4f, cp + tr.TangentAt(s) * 10f, "obstacles_cones.png");
        }
    }

    /// <summary>Orthographic top-down view of the whole circuit.</summary>
    static void TopDownShot()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        var surfaces = Object.FindObjectsByType<TrackSurface>();
        if (surfaces.Length == 0) return;
        Bounds b = surfaces[0].GetComponent<Renderer>().bounds;
        foreach (var t in surfaces) b.Encapsulate(t.GetComponent<Renderer>().bounds);
        var cam = new GameObject("TopDownCam").AddComponent<Camera>();
        cam.orthographic = true;
        cam.transform.position = b.center + Vector3.up * 200f;
        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        cam.orthographicSize = Mathf.Max(b.extents.z, b.extents.x * 720f / 1280f) * 1.08f;
        cam.farClipPlane = 500f;
        Directory.CreateDirectory(ShotDir);
        Capture(cam, $"{ShotDir}/track_overview.png");
        Object.DestroyImmediate(cam.gameObject);
    }

    /// <summary>The n-th CPU racer (by kart index) that is not currently spinning out.</summary>
    static RaceManager.Racer Cpu(int n)
    {
        var cpus = rm.Standings.Where(r => r.Human == null).OrderBy(r => r.KartIndex).ToList();
        var r0 = cpus[n % cpus.Count];
        if (!r0.Kart.IsSpinningOut) return r0;
        return cpus.FirstOrDefault(r => !r.Kart.IsSpinningOut) ?? r0;
    }

    static void Place(KartController kart, Vector3 position, Quaternion rotation, bool snapToSurface = true)
    {
        if (Physics.Raycast(position + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore)
            && hit.collider.GetComponentInParent<KartController>() == null)
            position.y = hit.point.y + 0.3f;
        else position.y = 0.6f;
        kart.Teleport(position, rotation);
        var ai = rm.Standings.FirstOrDefault(r => r.Kart == kart)?.Ai;
        ai?.ClearStuck();
    }

    static void ShotAt(Vector3 from, Vector3 lookAt, string file)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        Directory.CreateDirectory(ShotDir);
        var cam = new GameObject("ShotCam").AddComponent<Camera>();
        cam.transform.position = from;
        cam.transform.LookAt(lookAt);
        cam.fieldOfView = 55f;
        Capture(cam, $"{ShotDir}/{file}");
        Object.DestroyImmediate(cam.gameObject);
    }

    /// <summary>Phone picker images: GET /char/{i}.png for every character (rendered at lobby start).</summary>
    static void CheckThumbnails()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        int ok = 0;
        for (int i = 0; i < rm.Roster.Count; i++)
        {
            byte[] png = HttpGet(serverPort, $"/char/{i}.png");
            if (png != null && png.Length > 8 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G')
            {
                ok++;
                File.WriteAllBytes($"{ShotDir}/char_{i:00}.png", png);
            }
        }
        Check(ok == rm.Roster.Count, $"phone thumbnails served over HTTP: {ok}/{rm.Roster.Count}");
    }

    static byte[] HttpGet(int port, string path)
    {
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, port);
            var s = client.GetStream();
            s.ReadTimeout = 3000;
            byte[] req = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n");
            s.Write(req, 0, req.Length);
            var ms = new MemoryStream();
            var buf = new byte[8192];
            try
            {
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            }
            catch { /* timeout */ }
            byte[] all = ms.ToArray();
            for (int i = 0; i + 3 < all.Length; i++)
                if (all[i] == '\r' && all[i + 1] == '\n' && all[i + 2] == '\r' && all[i + 3] == '\n')
                {
                    if (!Encoding.ASCII.GetString(all, 0, i).StartsWith("HTTP/1.1 200")) return null;
                    return all.Skip(i + 4).ToArray();
                }
            return null;
        }
    }

    static void CheckGrid()
    {
        int onRoad = 0;
        foreach (var r in rm.Standings)
        {
            // Skip the kart's own collider: look for the first track surface below it.
            var hits = Physics.RaycastAll(r.Kart.transform.position + Vector3.up * 2f, Vector3.down, 5f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits.OrderBy(h => h.distance))
            {
                var ts = hit.collider.GetComponent<TrackSurface>();
                if (ts == null) continue;
                if (ts.IsDrivable(hit.triangleIndex)) onRoad++;
                break;
            }
        }
        Check(onRoad == rm.KartCount, $"grid: {onRoad}/{rm.KartCount} karts on road");
    }

    static void DumpStandings()
    {
        var sb = new StringBuilder("standings:\n");
        foreach (var r in rm.Standings)
            sb.AppendLine($"  {r.Position}. {r.Name,-28} lap {r.Lap.CurrentLap}/{rm.Laps} finished={r.Lap.Finished} " +
                          $"time={(r.Lap.Finished ? r.Lap.FinishTime.ToString("F2") : "-")} best={r.Lap.BestLap:F2} speedMul={r.Kart.SpeedMultiplier:F2}");
        Log(sb.ToString());
    }

    // ---- screenshots (only when a GPU device exists, i.e. without -nographics) --------------------

    static void TakeShots(string label)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        Directory.CreateDirectory(ShotDir);
        if (label == "race")
        {
            Capture(rm.PlayerCamera(0), $"{ShotDir}/race_chasecam.png");
            var kart = rm.HumanRacers[0].Kart.transform;
            var tmp = new GameObject("ShotCam").AddComponent<Camera>();
            tmp.transform.position = kart.position + kart.right * 4.5f + kart.forward * 3f + Vector3.up * 1.6f;
            tmp.transform.LookAt(kart.position + Vector3.up * 0.9f);
            tmp.fieldOfView = 50f;
            Capture(tmp, $"{ShotDir}/race_kart_side.png");
            Object.DestroyImmediate(tmp.gameObject);
        }
        else
        {
            Texture preview = rm.Preview != null ? rm.Preview.GetTexture(0) : null;
            if (preview is RenderTexture rt) Save(rt, $"{ShotDir}/lobby_preview_p1.png");
            var overview = Object.FindObjectsByType<Camera>().FirstOrDefault(c => c.name == "OverviewCamera");
            if (overview != null) Capture(overview, $"{ShotDir}/lobby_overview.png");
        }
        Log("screenshots written to " + Path.GetFullPath(ShotDir));
    }

    static void Capture(Camera cam, string file)
    {
        var rt = new RenderTexture(1280, 720, 24);
        RenderTexture prevTarget = cam.targetTexture;
        Rect prevRect = cam.rect;
        cam.targetTexture = rt;
        cam.rect = new Rect(0, 0, 1, 1);
        cam.Render();
        cam.targetTexture = prevTarget;
        cam.rect = prevRect;
        Save(rt, file);
        rt.Release();
    }

    static void Save(RenderTexture rt, string file)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(file, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }

    // ---- simulated phone -----------------------------------------------------------------------

    static void StartPhone(int port)
    {
        phoneRunning = true;
        phoneThread = new Thread(() => PhoneLoop(port)) { IsBackground = true, Name = "SmokePhone" };
        phoneThread.Start();
    }

    static void StopPhone()
    {
        phoneRunning = false;
        phoneThread?.Join(2000);
    }

    static void PhoneLoop(int port)
    {
        try
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                NetworkStream s = client.GetStream();
                byte[] hs = Encoding.ASCII.GetBytes("GET /ws HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                                    "Sec-WebSocket-Key: c21va2V0ZXN0a2V5MTIzNA==\r\nSec-WebSocket-Version: 13\r\n\r\n");
                s.Write(hs, 0, hs.Length);
                var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n")) header.Append((char)s.ReadByte());
                phoneInbox.Enqueue("handshake: " + header.ToString().Split('\r')[0]);

                var buffer = new System.Collections.Generic.List<byte>();
                var readBuf = new byte[4096];
                var lastState = DateTime.MinValue;
                var lastPing = DateTime.UtcNow;
                string lastSent = "";
                while (phoneRunning)
                {
                    while (phoneOutbox.TryDequeue(out string outMsg)) Send(s, outMsg);
                    string state = $"s|0|1|{(phoneHop ? 1 : 0)}|{(phoneItem ? 1 : 0)}";
                    if (phoneDriving && (state != lastSent || (DateTime.UtcNow - lastState).TotalMilliseconds >= 50))
                    {
                        Send(s, state);
                        lastSent = state;
                        lastState = DateTime.UtcNow;
                    }
                    else if (!phoneDriving && (DateTime.UtcNow - lastPing).TotalMilliseconds >= 2000)
                    {
                        Send(s, "ping"); // like the real page: keep-alive while not driving
                        lastPing = DateTime.UtcNow;
                    }
                    while (client.Available > 0)
                    {
                        int n = s.Read(readBuf, 0, readBuf.Length);
                        for (int i = 0; i < n; i++) buffer.Add(readBuf[i]);
                    }
                    // Parse unmasked server frames (payloads < 126 bytes or 16-bit length).
                    while (buffer.Count >= 2)
                    {
                        int len = buffer[1] & 0x7F, head = 2;
                        if (len == 126)
                        {
                            if (buffer.Count < 4) break;
                            len = (buffer[2] << 8) | buffer[3];
                            head = 4;
                        }
                        if (buffer.Count < head + len) break;
                        if ((buffer[0] & 0x0F) == 1) phoneInbox.Enqueue(Encoding.UTF8.GetString(buffer.GetRange(head, len).ToArray()));
                        buffer.RemoveRange(0, head + len);
                    }
                    Thread.Sleep(5);
                }
                s.Write(new byte[] { 0x88, 0x80, 0, 0, 0, 0 }, 0, 6); // masked close
            }
        }
        catch (Exception e)
        {
            phoneInbox.Enqueue("phone error: " + e.Message);
        }
    }

    static void Send(NetworkStream s, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] mask = { 1, 2, 3, 4 };
        var frame = new byte[6 + payload.Length];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | payload.Length);
        Array.Copy(mask, 0, frame, 2, 4);
        for (int i = 0; i < payload.Length; i++) frame[6 + i] = (byte)(payload[i] ^ mask[i & 3]);
        s.Write(frame, 0, frame.Length);
    }

    // ---------------------------------------------------------------------------------------------

    static void Check(bool ok, string message)
    {
        if (!ok) failures++;
        if (ok) Debug.Log(Tag + "PASS " + message);
        else Debug.LogError(Tag + "FAIL " + message);
    }

    static void Log(string message) => Debug.Log(Tag + message);

    static bool savedOptionsEnabled;
    static EnterPlayModeOptions savedOptions;

    static void Finish()
    {
        EditorSettings.enterPlayModeOptionsEnabled = savedOptionsEnabled;
        EditorSettings.enterPlayModeOptions = savedOptions;
        EditorApplication.update -= Tick;
        StopPhone();
        Time.timeScale = 1f;
        Debug.Log(Tag + "phone log:\n" + phoneLog);
        Debug.Log(Tag + (failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)"));
        if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
    }
}
