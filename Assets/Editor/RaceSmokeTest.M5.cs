using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Milestone 5 part of the smoke test (runs after the first race is back in the lobby): track
/// selection from the leader phone, CPU racers off, a second non-leader phone, the pause menu
/// (deny / pause / frozen / restart / quit prompt / back to lobby), a 2-human race without CPUs
/// and a solo time trial.
/// </summary>
public static partial class RaceSmokeTest
{
    /// <summary>A second simulated phone (lobby / menu messages only, keep-alive pings).</summary>
    class SimPhone
    {
        public readonly ConcurrentQueue<string> Outbox = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        public readonly StringBuilder Log = new StringBuilder();
        volatile bool running;
        Thread thread;

        public void Start(int port, string id)
        {
            running = true;
            Outbox.Enqueue("hello|" + id);
            thread = new Thread(() => Loop(port)) { IsBackground = true, Name = "SmokePhone2" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            thread?.Join(2000);
        }

        public void Pump()
        {
            while (inbox.TryDequeue(out string m)) Log.AppendLine(m);
        }

        public bool Received(string text) => Log.ToString().Contains(text);

        void Loop(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    NetworkStream s = client.GetStream();
                    byte[] hs = Encoding.ASCII.GetBytes("GET /ws HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                                        "Sec-WebSocket-Key: c21va2V0ZXN0a2V5MTIzNQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
                    s.Write(hs, 0, hs.Length);
                    var header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n")) header.Append((char)s.ReadByte());
                    var buffer = new System.Collections.Generic.List<byte>();
                    var readBuf = new byte[4096];
                    var lastPing = DateTime.UtcNow;
                    while (running)
                    {
                        while (Outbox.TryDequeue(out string outMsg)) Send(s, outMsg);
                        if ((DateTime.UtcNow - lastPing).TotalMilliseconds >= 2000)
                        {
                            Send(s, "ping");
                            lastPing = DateTime.UtcNow;
                        }
                        while (client.Available > 0)
                        {
                            int n = s.Read(readBuf, 0, readBuf.Length);
                            for (int i = 0; i < n; i++) buffer.Add(readBuf[i]);
                        }
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
                            if ((buffer[0] & 0x0F) == 1) inbox.Enqueue(Encoding.UTF8.GetString(buffer.GetRange(head, len).ToArray()));
                            buffer.RemoveRange(0, head + len);
                        }
                        Thread.Sleep(5);
                    }
                    s.Write(new byte[] { 0x88, 0x80, 0, 0, 0, 0 }, 0, 6);
                }
            }
            catch (Exception e)
            {
                inbox.Enqueue("phone2 error: " + e.Message);
            }
        }
    }

    static SimPhone phone2;
    static int firstTrack, expectCard, quitCalls, quitsBefore, m5Step, denyBefore;
    static double m5Start;
    static bool introShot, flyoverShot, flyoverPhoneChecked;
    static double flyoverEndedAt = -1;
    static int[] humanChars;
    static int restartTrack;
    static Vector3[] frozenPos;
    static float frozenTime;
    static double resultsAt2 = -1, backAt = -1;

    static double M5Elapsed => EditorApplication.timeSinceStartup - m5Start;

    static void M5Next(string message)
    {
        Log("M5: " + message);
        m5Step++;
        m5Start = EditorApplication.timeSinceStartup;
    }

    static bool M5Timeout(double seconds, string what)
    {
        if (M5Elapsed <= seconds) return false;
        Check(false, "M5 timeout: " + what);
        m5Step = 99;
        return true;
    }

    /// <summary>Returns true when the milestone 5 checks are done.</summary>
    static bool TickM5()
    {
        string log1 = phoneLog.ToString();
        switch (m5Step)
        {
            case 0: // leader phone: next track card (skipping RANDOM so the race is deterministic)
            {
                int next = (rm.SelectedTrack + 1) % (rm.Tracks.Count + 1);
                expectCard = next == rm.Tracks.Count ? 0 : next;
                phoneOutbox.Enqueue("track|1");
                if (next == rm.Tracks.Count) phoneOutbox.Enqueue("track|1");
                M5Next($"leader phone -> next track (expect card {expectCard + 1})");
                return false;
            }

            case 1:
                if (M5Timeout(6, $"track switch (selected {rm.SelectedTrack})")) return false;
                if (rm.SelectedTrack != expectCard || rm.ActiveTrackIndex != expectCard || M5Elapsed < 0.5) return false;
                Check(log1.Contains($"track|{expectCard}|") && Ui.TrackCardText == rm.ActiveTrack.displayName.ToUpperInvariant(),
                    $"leader phone switched the track to '{rm.ActiveTrack.displayName}' ({rm.ActiveTrack.Length:F0} m): lobby card + phones updated, " +
                    $"phone still connected={rm.Players.Any(p => p.Id == "smoke-phone-1" && p.Connected)}");
                rm.RemoveBotPlayersForTest();
                phoneOutbox.Enqueue("cpu|1"); // FILL TO 6 -> OFF
                phone2 = new SimPhone();
                phone2.Start(serverPort, "smoke-phone-2");
                M5Next("CPU racers off, second phone joining");
                return false;

            case 2:
                if (M5Timeout(8, $"second phone / CPU off (players {rm.Players.Count}, cpu {rm.CpuLabel})")) return false;
                if (rm.Players.Count < 3 || rm.CpuSetting != 0 || !phone2.Received("joined|")) return false;
                if (M5Elapsed < 1) return false;
                Check(phone2.Received("joined|2|") && phone2.Log.ToString().Split('\n').Any(l => l.StartsWith("joined|2|") && l.TrimEnd().EndsWith("|0")),
                    "second phone joined as P3 (not the leader)");
                Check(log1.Contains("cpu|0|OFF") && phone2.Received("cpu|0|OFF") && Ui.CpuText.Contains("OFF"),
                    $"CPU racers OFF shown in the lobby ('{Ui.CpuText}') and on both phones");
                Check(phone2.Received($"track|{expectCard}|"), "second phone shows the chosen track");
                StartUiShot("ui_lobby_track_cpu_off.png");
                quitsBefore = rm.QuitRequests;
                phone2.Outbox.Enqueue("track|1");
                phone2.Outbox.Enqueue("cpu|1");
                phone2.Outbox.Enqueue("quit|yes");
                M5Next("non-leader phone tries track / CPU / quit");
                return false;

            case 3:
                if (M5Elapsed < 1.5) return false;
                Check(rm.SelectedTrack == expectCard && rm.CpuSetting == 0 && rm.QuitRequests == quitsBefore && quitCalls == 0
                      && phone2.Received("denied|track") && phone2.Received("denied|cpu") && phone2.Received("denied|quit"),
                    $"non-leader phone refused: track, CPU setting and quit unchanged (quit requests +{rm.QuitRequests - quitsBefore})");
                phoneOutbox.Enqueue("quit|yes"); // leader EXIT, confirmed on the phone -> quit hook (the test keeps running)
                M5Next("leader phone EXIT -> quit");
                return false;

            case 4:
                if (M5Timeout(4, "leader quit did not reach the quit hook")) return false;
                if (quitCalls < 1) return false;
                Check(rm.QuitRequests == quitsBefore + 1 && quitCalls == 1, $"leader phone EXIT in the lobby calls Quit (hook calls {quitCalls}; standalone: Application.Quit)");
                rm.ReadyLocalPlayersForTest();
                phoneOutbox.Enqueue("ready|1");
                phone2.Outbox.Enqueue("ready|1");
                M5Next("race 2: 3 humans, no CPUs");
                return false;

            case 5:
                if (M5Timeout(8, "race 2 did not start")) return false;
                if (rm.Phase != RacePhase.Countdown || !rm.FlyoverActive) return false;
                Check(rm.Standings.Count == 3 && rm.HumanRacers.Count == 3 && rm.AllKarts.Count == 3 && rm.AllKarts.All(k => k.gameObject.activeSelf)
                      && rm.ActiveTrackIndex == expectCard,
                    $"CPU off: {rm.Standings.Count} karts racing ({rm.HumanRacers.Count} humans) on '{rm.ActiveTrack.displayName}', {rm.KartCount - rm.AllKarts.Count} unused karts hidden");
                CheckGrid();
                humanChars = rm.HumanRacers.Select(r => r.Character).ToArray();
                restartTrack = rm.ActiveTrackIndex;
                phone2.Outbox.Enqueue("skip"); // not the leader: ignored
                M5Next("flyover playing; non-leader tries to skip");
                return false;

            case 6:
                if (M5Elapsed < 0.8) return false;
                Check(rm.FlyoverActive, "non-leader phone cannot skip the flyover");
                phoneOutbox.Enqueue("skip");
                M5Next("leader phone skips the flyover");
                return false;

            case 7:
                if (M5Timeout(1.5, "leader phone did not skip the flyover")) return false;
                if (rm.FlyoverActive) return false;
                Check(rm.Phase == RacePhase.Countdown && rm.CountdownRemaining > 2f && log1.Contains("flyover|0"),
                    $"flyover skipped by the leader phone after {Time.time - rm.PhaseStartTime:F1} s of {rm.FlyoverSeconds:F1} s: full 3-2-1 follows ({rm.CountdownRemaining:F1} s left)");
                M5Next("countdown after the skipped flyover");
                return false;

            case 8:
                if (M5Timeout(8, "race 2 did not reach GO")) return false;
                if (rm.Phase != RacePhase.Racing || M5Elapsed < 0.6) return false;
                denyBefore = rm.PauseDenied;
                phone2.Outbox.Enqueue("pause");
                M5Next("non-leader phone presses PAUSE");
                return false;

            case 9:
                if (M5Elapsed < 0.7) return false;
                Check(!rm.Paused && rm.PauseDenied == denyBefore + 1 && phone2.Received("denied|pause") && Time.timeScale > 0f,
                    $"non-leader pause request rejected (paused={rm.Paused}, denied={rm.PauseDenied - denyBefore})");
                phoneOutbox.Enqueue("pause");
                M5Next("leader phone presses PAUSE");
                return false;

            case 10:
                if (M5Timeout(2, "leader pause did not pause")) return false;
                if (!rm.Paused) return false;
                Check(Time.timeScale == 0f && AudioManager.Instance.Paused && AudioListener.pause && rm.PausedBy == "P1",
                    $"paused by {rm.PausedBy}: timeScale={Time.timeScale}, audio paused={AudioListener.pause}");
                frozenPos = rm.Standings.Select(r => r.Kart.transform.position).ToArray();
                frozenTime = rm.TimeSinceStart;
                M5Next("frozen for 1 s");
                return false;

            case 11:
                if (M5Elapsed < 1.0) return false;
                phone2.Pump();
                float moved = rm.Standings.Select((r, i) => Vector3.Distance(r.Kart.transform.position, frozenPos[i])).Max();
                Check(moved < 0.01f && Mathf.Approximately(rm.TimeSinceStart, frozenTime) && Ui.PauseMenuVisible,
                    $"game frozen while paused: karts moved {moved:F3} m, race clock {frozenTime:F2}->{rm.TimeSinceStart:F2} s, pause menu visible={Ui.PauseMenuVisible}");
                Check(log1.Contains("pause|1|P1|1|0|0") && phone2.Received("pause|1|P1|0|"),
                    "pause menu sent to the leader phone; the other phone shows 'PAUSED by P1'");
                StartUiShot("ui_pause.png");
                M5Next("pause menu screenshot");
                return false;

            case 12:
                if (M5Elapsed < 0.5) return false;
                phoneOutbox.Enqueue("menu|down");
                phoneOutbox.Enqueue("menu|ok"); // RESTART RACE
                M5Next("leader picks RESTART RACE");
                return false;

            case 13:
                if (M5Timeout(3, "restart did not happen")) return false;
                if (rm.Paused || rm.Phase != RacePhase.Countdown) return false;
                Check(Time.timeScale == 1f && !AudioListener.pause && rm.ActiveTrackIndex == restartTrack && !rm.FlyoverActive
                      && rm.HumanRacers.Select(r => r.Character).SequenceEqual(humanChars) && rm.Standings.All(r => r.Lap.CurrentLap == 0),
                    $"RESTART: fresh countdown on the same track with the same characters (timeScale {Time.timeScale})");
                M5Next("restarted race");
                return false;

            case 14:
                if (M5Timeout(8, "restarted race did not reach GO")) return false;
                if (rm.Phase != RacePhase.Racing || M5Elapsed < 0.3) return false;
                if (!rm.Paused)
                {
                    phoneOutbox.Enqueue("pause");
                    return false;
                }
                phoneOutbox.Enqueue("menu|pick|3"); // QUIT GAME -> confirmation
                M5Next("leader picks QUIT GAME");
                return false;

            case 15:
                if (M5Timeout(2, "quit confirmation not shown")) return false;
                if (!rm.PauseQuitConfirm || M5Elapsed < 0.3) return false;
                Check(quitCalls == 1 && rm.ConfirmSelection == 0 && log1.Contains("pause|1|P1|1|3|1|0"),
                    "QUIT GAME asks for confirmation first (NO preselected), nothing quit yet");
                phoneOutbox.Enqueue("menu|pick|0"); // NO
                M5Next("leader answers NO");
                return false;

            case 16:
                if (M5Timeout(2, "NO did not close the quit prompt")) return false;
                if (rm.PauseQuitConfirm || !rm.Paused) return false;
                Check(quitCalls == 1, "NO returns to the pause menu without quitting");
                phoneOutbox.Enqueue("menu|pick|3");
                phoneOutbox.Enqueue("menu|pick|1"); // YES
                M5Next("leader picks QUIT GAME -> YES");
                return false;

            case 17:
                if (M5Timeout(3, "YES did not call the quit hook")) return false;
                if (quitCalls < 2) return false;
                Check(quitCalls == 2, "pause menu QUIT GAME -> YES calls Quit (hook replaced by the test)");
                phoneOutbox.Enqueue("menu|back"); // leave the prompt
                phoneOutbox.Enqueue("menu|pick|2"); // BACK TO LOBBY
                M5Next("leader picks BACK TO LOBBY");
                return false;

            case 18:
                if (M5Timeout(3, "BACK TO LOBBY did not return to the lobby")) return false;
                if (rm.Phase != RacePhase.Lobby || rm.Paused) return false;
                if (backAt < 0) { backAt = M5Elapsed; return false; }
                if (M5Elapsed - backAt < 0.3) return false; // UI updates on the next frame
                Check(Time.timeScale == 1f && !AudioListener.pause && Ui.LobbyVisible && !Ui.PauseMenuVisible && rm.Players.Count == 3,
                    $"BACK TO LOBBY: lobby shown={Ui.LobbyVisible}, pause menu hidden={!Ui.PauseMenuVisible}, timeScale={Time.timeScale}, " +
                    $"audio paused={AudioListener.pause}, both phones + keyboard still in ({rm.Players.Count} players)");
                phone2.Stop(); // P3 leaves: phone + keyboard remain
                M5Next("second phone disconnects");
                return false;

            case 19:
                if (M5Timeout(15, $"disconnected phone was not dropped (players {rm.Players.Count})")) return false;
                if (rm.Players.Count != 2) return false;
                rm.ReadyLocalPlayersForTest();
                phoneOutbox.Enqueue("ready|1");
                M5Next("race 3: phone + keyboard, CPUs off");
                return false;

            case 20:
                if (M5Timeout(20, "race 3 did not reach GO")) return false;
                if (rm.Phase != RacePhase.Racing) return false;
                Check(rm.Standings.Count == 2 && rm.HumanRacers.Count == 2 && Ui.MinimapDots == 2,
                    $"2 humans, no CPUs: {rm.Standings.Count} karts, minimap dots={Ui.MinimapDots}, flyover played again={rm.FlyoversPlayed >= 3}");
                rm.SetAutopilotForTest();
                Time.timeScale = 5f;
                resultsAt2 = -1;
                M5Next("race 3 on autopilot (timeScale 5)");
                return false;

            case 21:
                if (M5Timeout(240, "race 3 did not reach results")) { Time.timeScale = 1f; return false; }
                if (rm.Phase != RacePhase.Results) return false;
                Time.timeScale = 1f;
                if (resultsAt2 < 0) { resultsAt2 = EditorApplication.timeSinceStartup; return false; }
                if (EditorApplication.timeSinceStartup - resultsAt2 < 1.2) return false;
                Check(Ui.ResultsVisible && Ui.ResultRows == 2 && rm.Podium.PerformerCount == 2 && rm.Standings.All(r => r.Lap.Finished),
                    $"2-human race without CPUs finished: results rows={Ui.ResultRows}, podium={rm.Podium.PerformerCount}, times " +
                    string.Join(" / ", rm.Standings.Select(r => $"{r.Name} {r.Lap.FinishTime:F1} s")));
                StartUiShot("ui_results_2p_no_cpu.png");
                M5Next("results (2 rows)");
                return false;

            case 22:
                if (M5Elapsed < 0.6) return false;
                phoneOutbox.Enqueue("start"); // leader: back to the lobby
                M5Next("back to the lobby for a time trial");
                return false;

            case 23:
                if (M5Timeout(5, "no lobby after results")) return false;
                if (rm.Phase != RacePhase.Lobby) return false;
                rm.RemoveKeyboardPlayerForTest();
                phoneOutbox.Enqueue("ready|1");
                M5Next("race 4: solo time trial (phone only, no CPUs)");
                return false;

            case 24:
                if (M5Timeout(10, "time trial did not start")) return false;
                if (rm.Phase != RacePhase.Countdown) return false;
                if (rm.FlyoverActive) phoneOutbox.Enqueue("skip");
                M5Next("time trial countdown");
                return false;

            case 25:
                if (M5Timeout(15, "time trial did not reach GO")) return false;
                if (rm.Phase != RacePhase.Racing) return false;
                Check(rm.Standings.Count == 1 && rm.HumanRacers.Count == 1, $"time trial: {rm.Standings.Count} kart on track");
                rm.SetAutopilotForTest();
                Time.timeScale = 5f;
                resultsAt2 = -1;
                M5Next("time trial on autopilot");
                return false;

            case 26:
                if (M5Timeout(240, "time trial did not reach results")) { Time.timeScale = 1f; return false; }
                if (rm.Phase != RacePhase.Results) return false;
                Time.timeScale = 1f;
                if (resultsAt2 < 0) { resultsAt2 = EditorApplication.timeSinceStartup; return false; }
                if (EditorApplication.timeSinceStartup - resultsAt2 < 1.2) return false;
                var solo = rm.Standings[0];
                Check(Ui.ResultsVisible && Ui.ResultRows == 1 && rm.Podium.PerformerCount == 1 && solo.Lap.Finished,
                    $"solo time trial finished: {solo.Lap.FinishTime:F1} s (best lap {solo.Lap.BestLap:F1} s), results rows={Ui.ResultRows}, podium={rm.Podium.PerformerCount}");
                StartUiShot("ui_results_time_trial.png");
                M5Next("done");
                return false;

            case 27:
                return M5Elapsed > 0.6;

            default: // a timeout happened
                Time.timeScale = 1f;
                return true;
        }
    }
}
