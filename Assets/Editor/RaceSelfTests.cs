using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headless checks run by RaceSceneBuilder: phone server (HTTP, WebSocket
/// handshake/frames, clean shutdown) and an independent QR decode check.
/// Each returns report lines; failures are also logged as errors.
/// </summary>
public static class RaceSelfTests
{
    const string Tag = "[RaceSelfTests] ";

    public static string ServerSelfTest()
    {
        var log = new StringBuilder();
        var server = new PhoneControllerServer(ControllerPage.Html);
        try
        {
            if (!server.Start(8080))
            {
                Fail(log, "server did not start: " + server.LastError);
                return log.ToString();
            }
            int port = server.Port;
            log.AppendLine($"server: bound 0.0.0.0:{port}");

            // 1) Plain HTTP GET / returns the controller page.
            using (var http = new TcpClient())
            {
                http.Connect(IPAddress.Loopback, port);
                var s = http.GetStream();
                s.ReadTimeout = 3000;
                byte[] req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
                s.Write(req, 0, req.Length);
                string resp = ReadAll(s);
                bool ok = resp.StartsWith("HTTP/1.1 200") && resp.Contains("<html") && resp.Contains("new WebSocket");
                Check(log, ok, $"GET / -> {FirstLine(resp)}, {resp.Length} bytes");
            }

            // 2) RFC 6455 sample key must produce the sample accept value.
            Check(log, PhoneControllerServer.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
                "Sec-WebSocket-Accept matches RFC 6455 example");

            // 3) WebSocket upgrade, masked client frame in, server frame out.
            using (var ws = new TcpClient())
            {
                ws.Connect(IPAddress.Loopback, port);
                var s = ws.GetStream();
                s.ReadTimeout = 3000;
                byte[] hs = Encoding.ASCII.GetBytes("GET /ws HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\n" +
                                                    "Connection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
                s.Write(hs, 0, hs.Length);
                string head = ReadHeader(s);
                Check(log, head.StartsWith("HTTP/1.1 101") && head.Contains("s3pPLMBiTxaQ9kYGzzhZRbK+xOo="), "WebSocket upgrade -> " + FirstLine(head));

                byte[] frame = MaskedTextFrame("hello|selftest");
                s.Write(frame, 0, frame.Length);

                int connId = -1;
                string received = null;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3000 && received == null)
                {
                    while (server.TryDequeue(out var e))
                    {
                        if (e.Kind == PhoneControllerServer.EventKind.Connected) connId = e.ConnectionId;
                        if (e.Kind == PhoneControllerServer.EventKind.Message) received = e.Text;
                    }
                    Thread.Sleep(10);
                }
                Check(log, connId > 0 && received == "hello|selftest", $"client->server text frame (conn {connId}): '{received}'");

                server.Send(connId, "phase|lobby");
                byte[] h2 = ReadExact(s, 2);
                string text = h2 != null && h2[0] == 0x81 ? Encoding.UTF8.GetString(ReadExact(s, h2[1] & 0x7F)) : null;
                Check(log, text == "phase|lobby", $"server->client text frame: '{text}'");
            }

            // 4) Clean shutdown: port must be free again afterwards.
            var stopWatch = Stopwatch.StartNew();
            server.Stop();
            long stopMs = stopWatch.ElapsedMilliseconds;
            bool rebound;
            try
            {
                var probe = new TcpListener(IPAddress.Any, port);
                probe.Start();
                probe.Stop();
                rebound = true;
            }
            catch
            {
                rebound = false;
            }
            Check(log, !server.IsRunning && rebound, $"server stopped in {stopMs} ms, port {port} re-bindable={rebound}");
        }
        catch (Exception e)
        {
            Fail(log, "exception: " + e);
        }
        finally
        {
            server.Stop();
        }
        return log.ToString();
    }

    /// <summary>Encodes text, then decodes it back with separate logic (format BCH, unmask, RS syndromes).</summary>
    public static string QrSelfTest(string text)
    {
        var log = new StringBuilder();
        bool[,] m = QrCode.Encode(text);
        if (m == null)
        {
            Fail(log, "QR encode returned null");
            return log.ToString();
        }
        int size = m.GetLength(0);
        int version = (size - 17) / 4;

        // Format info (first copy), BCH-checked by brute force over all 32 valid codes.
        int raw = 0;
        for (int i = 0; i <= 5; i++) raw |= (m[i, 8] ? 1 : 0) << i;
        raw |= (m[7, 8] ? 1 : 0) << 6;
        raw |= (m[8, 8] ? 1 : 0) << 7;
        raw |= (m[8, 7] ? 1 : 0) << 8;
        for (int i = 9; i < 15; i++) raw |= (m[8, 14 - i] ? 1 : 0) << i;
        int format = -1;
        for (int d = 0; d < 32; d++)
        {
            int rem = d;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            if ((((d << 10) | rem) ^ 0x5412) == raw) format = d;
        }
        if (format < 0)
        {
            Fail(log, "QR format bits invalid");
            return log.ToString();
        }
        int ecl = format >> 3, mask = format & 7;

        // Function-module map rebuilt independently.
        var func = new bool[size, size];
        void Block(int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    if (x >= 0 && y >= 0 && x < size && y < size) func[y, x] = true;
        }
        Block(0, 0, 9, 9);
        Block(size - 8, 0, 8, 9);
        Block(0, size - 8, 9, 8);
        Block(6, 0, 1, size);
        Block(0, 6, size, 1);
        if (version >= 2)
        {
            int p = size - 7;
            Block(p - 2, p - 2, 5, 5);
        }

        // Read codewords in zig-zag order, unmasking.
        var bits = new List<bool>();
        bool upward = true;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                int y = upward ? size - 1 - vert : vert;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    if (func[y, x]) continue;
                    bool v = m[y, x] ^ MaskBit(mask, x, y);
                    bits.Add(v);
                }
            }
            upward = !upward;
        }
        int[] totals = { 0, 26, 44, 70, 100, 134, 172 };
        int[] eccs = { 0, 10, 16, 26, 18, 24, 16 };
        int[] blockCounts = { 0, 1, 1, 1, 2, 2, 4 };
        int total = totals[version], blocks = blockCounts[version], ecc = eccs[version];
        var codewords = new byte[total];
        for (int i = 0; i < total * 8; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        // De-interleave and verify RS syndromes are zero for every block.
        int dataPerBlock = (total - ecc * blocks) / blocks;
        bool rsOk = true;
        var data = new List<byte>();
        var blockData = new List<byte>[blocks];
        for (int b = 0; b < blocks; b++) blockData[b] = new List<byte>();
        for (int i = 0; i < dataPerBlock; i++) for (int b = 0; b < blocks; b++) blockData[b].Add(codewords[i * blocks + b]);
        for (int b = 0; b < blocks; b++)
        {
            var full = new List<byte>(blockData[b]);
            for (int i = 0; i < ecc; i++) full.Add(codewords[dataPerBlock * blocks + i * blocks + b]);
            for (int k = 0; k < ecc; k++)
            {
                int alpha = GfPow(k), syn = 0;
                foreach (byte c in full) syn = GfMul(syn, alpha) ^ c;
                if (syn != 0) rsOk = false;
            }
        }
        for (int b = 0; b < blocks; b++) data.AddRange(blockData[b]);

        // Parse byte-mode segment.
        int mode = data[0] >> 4;
        int length = ((data[0] & 0xF) << 4) | (data[1] >> 4);
        var payload = new byte[length];
        for (int i = 0; i < length; i++) payload[i] = (byte)(((data[1 + i] & 0xF) << 4) | (data[2 + i] >> 4));
        string decoded = Encoding.UTF8.GetString(payload);

        bool ok = ecl == 0 && mode == 4 && rsOk && decoded == text;
        Check(log, ok, $"QR v{version} ({size}x{size}) mask {mask} level {(ecl == 0 ? "M" : ecl.ToString())}: RS syndromes zero={rsOk}, decoded '{decoded}'");
        return log.ToString();
    }

    static bool MaskBit(int mask, int x, int y)
    {
        switch (mask)
        {
            case 0: return (x + y) % 2 == 0;
            case 1: return y % 2 == 0;
            case 2: return x % 3 == 0;
            case 3: return (x + y) % 3 == 0;
            case 4: return (x / 3 + y / 2) % 2 == 0;
            case 5: return x * y % 2 + x * y % 3 == 0;
            case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
            default: return ((x + y) % 2 + x * y % 3) % 2 == 0;
        }
    }

    static int GfMul(int a, int b)
    {
        int r = 0;
        while (b > 0)
        {
            if ((b & 1) != 0) r ^= a;
            a <<= 1;
            if ((a & 0x100) != 0) a ^= 0x11D;
            b >>= 1;
        }
        return r;
    }

    static int GfPow(int e)
    {
        int r = 1;
        for (int i = 0; i < e; i++) r = GfMul(r, 2);
        return r;
    }

    static byte[] MaskedTextFrame(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] mask = { 0x12, 0x34, 0x56, 0x78 };
        var frame = new byte[6 + payload.Length];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | payload.Length);
        Array.Copy(mask, 0, frame, 2, 4);
        for (int i = 0; i < payload.Length; i++) frame[6 + i] = (byte)(payload[i] ^ mask[i & 3]);
        return frame;
    }

    static string ReadAll(NetworkStream s)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        try
        {
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        catch { /* timeout */ }
        return sb.ToString();
    }

    static string ReadHeader(NetworkStream s)
    {
        var sb = new StringBuilder();
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            int b = s.ReadByte();
            if (b < 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    static byte[] ReadExact(NetworkStream s, int count)
    {
        var data = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Read(data, read, count - read);
            if (n <= 0) return null;
            read += n;
        }
        return data;
    }

    static string FirstLine(string s)
    {
        int i = s.IndexOf('\r');
        return i >= 0 ? s.Substring(0, i) : s;
    }

    static void Check(StringBuilder log, bool ok, string message)
    {
        log.AppendLine((ok ? "PASS " : "FAIL ") + message);
        if (!ok) Debug.LogError(Tag + "FAIL " + message);
    }

    static void Fail(StringBuilder log, string message) => Check(log, false, message);
}
