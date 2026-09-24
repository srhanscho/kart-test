using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

/// <summary>
/// Minimal HTTP + WebSocket (RFC 6455) server on raw TcpListener, for phone
/// controllers on the LAN. All socket work happens on background threads;
/// the game thread polls TryDequeue() and calls Send()/Broadcast().
/// No Unity API is used here, so it is safe off the main thread.
/// </summary>
public sealed class PhoneControllerServer : IDisposable
{
    public enum EventKind { Connected, Message, Disconnected }

    public struct ServerEvent
    {
        public EventKind Kind;
        public int ConnectionId;
        public string Text;
    }

    const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    const int MaxMessageBytes = 4096;
    const int MaxHeaderBytes = 8192;

    readonly byte[] indexHtml;
    readonly ConcurrentQueue<ServerEvent> events = new ConcurrentQueue<ServerEvent>();
    readonly ConcurrentDictionary<string, (string type, byte[] body)> files =
        new ConcurrentDictionary<string, (string, byte[])>();
    readonly Dictionary<int, Connection> connections = new Dictionary<int, Connection>();
    readonly HashSet<TcpClient> rawClients = new HashSet<TcpClient>();
    readonly object gate = new object();

    TcpListener listener;
    Thread acceptThread;
    volatile bool running;
    int nextId;

    public int Port { get; private set; }
    public bool IsRunning => running;
    public string LastError { get; private set; }
    public int ConnectionCount { get { lock (gate) return connections.Count; } }

    public PhoneControllerServer(string html)
    {
        indexHtml = Encoding.UTF8.GetBytes(html);
    }

    /// <summary>Binds 0.0.0.0 on the first free port in [preferredPort, preferredPort + attempts).</summary>
    public bool Start(int preferredPort, int attempts = 10)
    {
        if (running) return true;
        for (int i = 0; i < attempts; i++)
        {
            int port = preferredPort + i;
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                listener = l;
                Port = port;
                running = true;
                acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "PhoneServer.Accept" };
                acceptThread.Start();
                return true;
            }
            catch (Exception e)
            {
                LastError = $"port {port}: {e.Message}";
            }
        }
        return false;
    }

    /// <summary>Stops listening, closes every socket and joins the accept thread.</summary>
    public void Stop()
    {
        if (!running && listener == null) return;
        running = false;
        try { listener?.Stop(); } catch { /* already closed */ }
        listener = null;

        Connection[] conns;
        TcpClient[] raws;
        lock (gate)
        {
            conns = new Connection[connections.Count];
            connections.Values.CopyTo(conns, 0);
            connections.Clear();
            raws = new TcpClient[rawClients.Count];
            rawClients.CopyTo(raws);
            rawClients.Clear();
        }
        foreach (var c in conns) c.Close();
        foreach (var r in raws) { try { r.Close(); } catch { /* ignore */ } }

        if (acceptThread != null && acceptThread != Thread.CurrentThread) acceptThread.Join(1000);
        acceptThread = null;
    }

    public void Dispose() => Stop();

    public bool TryDequeue(out ServerEvent e) => events.TryDequeue(out e);

    /// <summary>Serve extra static content, e.g. "/char/0.png".</summary>
    public void SetFile(string path, string contentType, byte[] body) => files[path] = (contentType, body);

    public void Send(int connectionId, string text)
    {
        Connection c;
        lock (gate) connections.TryGetValue(connectionId, out c);
        c?.SendText(text);
    }

    public void Broadcast(string text)
    {
        Connection[] conns;
        lock (gate)
        {
            conns = new Connection[connections.Count];
            connections.Values.CopyTo(conns, 0);
        }
        foreach (var c in conns) c.SendText(text);
    }

    // ------------------------------------------------------------------------------------------

    void AcceptLoop()
    {
        while (running)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; } // listener stopped
            if (!running) { client.Close(); break; }
            lock (gate) rawClients.Add(client);
            var t = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "PhoneServer.Client" };
            t.Start();
        }
    }

    void HandleClient(TcpClient client)
    {
        Connection conn = null;
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 2000;

            string header = ReadHttpHeader(stream);
            if (header == null) return;
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return;
            string path = requestLine[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            bool upgrade = headers.TryGetValue("Upgrade", out string up) &&
                           up.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            if (upgrade && headers.TryGetValue("Sec-WebSocket-Key", out string key))
            {
                string accept = ComputeAccept(key);
                WriteAscii(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                   $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                int id = Interlocked.Increment(ref nextId);
                conn = new Connection(id, client, stream);
                lock (gate)
                {
                    rawClients.Remove(client);
                    if (!running) { conn.Close(); return; }
                    connections[id] = conn;
                }
                events.Enqueue(new ServerEvent { Kind = EventKind.Connected, ConnectionId = id });
                stream.ReadTimeout = 10000; // phones heartbeat ~20 Hz
                ReadFrames(conn, stream);
            }
            else if (path == "/" || path == "/index.html")
            {
                WriteHttp(stream, "200 OK", "text/html; charset=utf-8", indexHtml);
            }
            else if (files.TryGetValue(path, out var file))
            {
                WriteHttp(stream, "200 OK", file.type, file.body);
            }
            else
            {
                WriteHttp(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("Not found"));
            }
        }
        catch
        {
            // Connection dropped, timed out or server stopping.
        }
        finally
        {
            if (conn != null)
            {
                bool removed;
                lock (gate) removed = connections.Remove(conn.Id);
                conn.Close();
                events.Enqueue(new ServerEvent { Kind = EventKind.Disconnected, ConnectionId = conn.Id });
                _ = removed;
            }
            else
            {
                lock (gate) rawClients.Remove(client);
                try { client.Close(); } catch { /* ignore */ }
            }
        }
    }

    void ReadFrames(Connection conn, NetworkStream stream)
    {
        var message = new List<byte>();
        int messageOpcode = 0;
        while (running)
        {
            byte[] head = ReadExact(stream, 2);
            if (head == null) return;
            bool fin = (head[0] & 0x80) != 0;
            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                byte[] ext = ReadExact(stream, 2);
                if (ext == null) return;
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                byte[] ext = ReadExact(stream, 8);
                if (ext == null) return;
                length = 0;
                for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
            }
            if (!masked || length > MaxMessageBytes) return; // clients must mask; keep frames small

            byte[] mask = ReadExact(stream, 4);
            byte[] payload = ReadExact(stream, (int)length);
            if (mask == null || payload == null) return;
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

            switch (opcode)
            {
                case 0x8: // close
                    conn.SendFrame(0x8, Array.Empty<byte>());
                    return;
                case 0x9: // ping
                    conn.SendFrame(0xA, payload);
                    break;
                case 0xA: // pong
                    break;
                case 0x0: // continuation
                case 0x1: // text
                case 0x2: // binary
                    if (opcode != 0) { messageOpcode = opcode; message.Clear(); }
                    message.AddRange(payload);
                    if (message.Count > MaxMessageBytes) return;
                    if (fin)
                    {
                        if (messageOpcode == 0x1)
                            events.Enqueue(new ServerEvent
                            {
                                Kind = EventKind.Message,
                                ConnectionId = conn.Id,
                                Text = Encoding.UTF8.GetString(message.ToArray())
                            });
                        message.Clear();
                    }
                    break;
                default:
                    return; // unknown opcode: drop connection
            }
        }
    }

    static string ReadHttpHeader(Stream stream)
    {
        var buffer = new List<byte>(512);
        while (buffer.Count < MaxHeaderBytes)
        {
            int b = stream.ReadByte();
            if (b < 0) return null;
            buffer.Add((byte)b);
            int n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
                return Encoding.ASCII.GetString(buffer.ToArray(), 0, n - 4);
        }
        return null;
    }

    static byte[] ReadExact(Stream stream, int count)
    {
        var data = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(data, read, count - read);
            if (n <= 0) return null;
            read += n;
        }
        return data;
    }

    public static string ComputeAccept(string key)
    {
        using (var sha1 = SHA1.Create())
            return Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
    }

    static void WriteAscii(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    static void WriteHttp(Stream stream, string status, string contentType, byte[] body)
    {
        WriteAscii(stream, $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                           "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    /// <summary>Server-to-client frames are never masked.</summary>
    static byte[] BuildFrame(int opcode, byte[] payload)
    {
        int headerLen = payload.Length < 126 ? 2 : 4;
        var frame = new byte[headerLen + payload.Length];
        frame[0] = (byte)(0x80 | opcode);
        if (payload.Length < 126) frame[1] = (byte)payload.Length;
        else
        {
            frame[1] = 126;
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)payload.Length;
        }
        Buffer.BlockCopy(payload, 0, frame, headerLen, payload.Length);
        return frame;
    }

    /// <summary>One WebSocket client; outgoing frames are written by a dedicated sender thread.</summary>
    sealed class Connection
    {
        public readonly int Id;
        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly BlockingCollection<byte[]> outbox = new BlockingCollection<byte[]>(256);
        volatile bool closed;

        public Connection(int id, TcpClient client, NetworkStream stream)
        {
            Id = id;
            this.client = client;
            this.stream = stream;
            new Thread(SendLoop) { IsBackground = true, Name = "PhoneServer.Send" }.Start();
        }

        public void SendText(string text)
        {
            if (text.Length > 60000) return;
            SendFrame(0x1, Encoding.UTF8.GetBytes(text));
        }

        public void SendFrame(int opcode, byte[] payload)
        {
            if (closed) return;
            try { outbox.TryAdd(BuildFrame(opcode, payload)); }
            catch (InvalidOperationException) { /* closing */ }
        }

        void SendLoop()
        {
            try
            {
                foreach (byte[] frame in outbox.GetConsumingEnumerable())
                    stream.Write(frame, 0, frame.Length);
            }
            catch
            {
                Close();
            }
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { outbox.CompleteAdding(); } catch { /* ignore */ }
            try { client.Close(); } catch { /* ignore */ }
        }
    }
}
