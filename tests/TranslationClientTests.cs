using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PotPlayerAiSubtitle;

// Isolated loopback HTTP tests: no production settings, credentials or model requests.
internal static class TranslationClientTests
{
    private const string Answer = "{\"translations\":[{\"id\":1,\"zh\":\"你好\"}]}";
    private static readonly List<SubtitleCue> Cues = new List<SubtitleCue> { new SubtitleCue { Id = 1, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "こんにちは" } };
    private static readonly SubtitleScene Scene = new SubtitleScene { Index = 0, StartCueIndex = 0, EndCueIndex = 0 };
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string Reply(string finish, string content)
    {
        return AtomicJson.Serialize(new Dictionary<string, object> {
            { "choices", new object[] { new Dictionary<string, object> {
                { "finish_reason", finish }, { "message", new Dictionary<string, object> {
                    { "content", content }, { "reasoning_content", "private-reasoning-do-not-log" } } } } } },
            { "usage", new Dictionary<string, object> { { "completion_tokens", 4096 },
                { "completion_tokens_details", new Dictionary<string, object> { { "reasoning_tokens", 4096 } } } } }
        });
    }
    private static DeepSeekClient Client(Server server, string tier, bool thinking)
    {
        AppConfig config = AppConfig.CreateDefault();
        config.ApiBaseUrl = server.Url; config.Model = "loopback-test";
        config.TranslationQuality = tier; config.EnableThinking = thinking;
        return new DeepSeekClient(config, "test-secret-do-not-log", config.EnableThinking);
    }
    private static TranslationResult Translate(DeepSeekClient client, CancellationToken token)
    { return client.TranslateScene(Cues, Scene, 0, new Dictionary<string, string>(), token); }
    private static void Request(Server server, int index, int budget, bool thinking)
    {
        var request = server.Requests[index];
        Check(Convert.ToInt32(request["max_tokens"]) == budget, "Wrong output budget");
        Check((string)((Dictionary<string, object>)request["thinking"])["type"] == (thinking ? "enabled" : "disabled"), "Wrong thinking policy");
    }
    private static void Failure<T>(Action action, string contains) where T : Exception
    {
        try { action(); } catch (T ex) { Check(ex.Message.Contains(contains), "Missing error diagnosis"); return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    public static int Main()
    {
        try
        {
            StoragePaths.Ensure();
            foreach (string tier in new[] { "fast", "quality" }) foreach (bool preference in new[] { false, true })
            {
                using (Server server = new Server(Reply("stop", Answer)))
                {
                    Check(Translate(Client(server, tier, preference), CancellationToken.None).Translations["1"] == "你好", "Translation lost");
                    server.Complete(); bool effective = tier == "quality" && preference;
                    Request(server, 0, effective ? 16384 : 4096, effective);
                }
            }
            Console.WriteLine("PASS all four tier/thinking combinations use the expected wire policy and budget");
            foreach (bool thinking in new[] { false, true })
            {
                // Even syntactically valid content must not be accepted when finish_reason=length.
                using (Server server = new Server(Reply("length", thinking ? "" : Answer), Reply("stop", Answer)))
                {
                    Check(Translate(Client(server, "quality", thinking), CancellationToken.None).Translations.Count == 1, "Recovery failed");
                    server.Complete(); Request(server, 0, thinking ? 16384 : 4096, thinking);
                    Request(server, 1, thinking ? 32768 : 8192, thinking);
                }
            }
            Console.WriteLine("PASS empty reasoning-only and nonempty truncated answers recover once without changing thinking");
            using (Server server = new Server(Reply("length", ""), Reply("length", "")))
            {
                Failure<TranslationOutputLimitException>(delegate { Translate(Client(server, "quality", true), CancellationToken.None); }, "预算上限");
                server.Complete(); Check(server.Requests.Count == 2, "Output retries are not bounded");
            }
            Console.WriteLine("PASS exhausted output budget stops after two requests with an actionable error");
            using (Server server = new Server(Reply("stop", "")))
            {
                Failure<InvalidDataException>(delegate { Translate(Client(server, "fast", true), CancellationToken.None); }, "未标记为输出截断");
                server.Complete(); Check(server.Requests.Count == 1, "Unmarked empty response incorrectly grows budget");
            }
            using (Server server = new Server(Reply("content_filter", Answer)))
            {
                Failure<InvalidDataException>(delegate { Translate(Client(server, "fast", false), CancellationToken.None); }, "未正常完成");
                server.Complete();
            }
            using (Server server = new Server("{\"choices\":[{\"finish_reason\":\"stop\"}]}"))
            {
                Failure<InvalidDataException>(delegate { Translate(Client(server, "fast", false), CancellationToken.None); }, "未返回译文");
                server.Complete();
            }
            Console.WriteLine("PASS empty, incomplete and non-stop responses cannot masquerade as successful translations");
            using (Server server = new Server(Reply("stop", "{\"translations\":[]}")))
            {
                Failure<InvalidDataException>(delegate { Translate(Client(server, "fast", false), CancellationToken.None); }, "漏译");
                server.Complete();
            }
            Console.WriteLine("PASS strict cue completeness remains enforced");
            using (Server server = new Server()) using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Failure<OperationCanceledException>(delegate { Translate(Client(server, "quality", true), cancel.Token); }, "");
                Check(server.Requests.Count == 0, "Pre-cancelled request used the network");
            }
            using (Server server = new Server((string)null)) using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Thread trigger = new Thread(delegate() { if (server.Received.WaitOne(5000)) cancel.Cancel(); }) { IsBackground = true };
                trigger.Start();
                Failure<OperationCanceledException>(delegate { Translate(Client(server, "quality", true), cancel.Token); }, "");
                Check(trigger.Join(5000), "Cancellation trigger hung");
                Check(server.Requests.Count == 1, "Cancelled request was retried");
            }
            Console.WriteLine("PASS cancellation before send and during a thinking request is immediate and never retried");
            string log = File.ReadAllText(Path.Combine(StoragePaths.Logs, "worker.log"));
            Check(!log.Contains("private-reasoning-do-not-log") && !log.Contains("test-secret-do-not-log") && !log.Contains("こんにちは"), "Sensitive text leaked to diagnostics");
            Check(log.Contains("reasoning_tokens=4096") && log.Contains("finish_reason=length"), "Missing safe diagnostics");
            Console.WriteLine("PASS diagnostics retain token evidence without credentials, source text or reasoning");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }

    internal sealed class Server : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Thread thread;
        private readonly ManualResetEvent release = new ManualResetEvent(false);
        public readonly ManualResetEvent Received = new ManualResetEvent(false);
        public readonly List<Dictionary<string, object>> Requests = new List<Dictionary<string, object>>();
        private Exception error;
        public string Url { get; private set; }
        public Server(params string[] replies)
        {
            listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(delegate()
            {
                try
                {
                    foreach (string reply in replies)
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.ReadTimeout = 5000;
                        StringBuilder header = new StringBuilder();
                        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        { int b = stream.ReadByte(); if (b < 0) throw new EndOfStreamException(); header.Append((char)b); }
                        int length = 0;
                        foreach (string line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                        if (header.ToString().IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                        { byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); stream.Write(interim, 0, interim.Length); }
                        byte[] body = new byte[length]; int offset = 0;
                        while (offset < length) { int read = stream.Read(body, offset, length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
                        Requests.Add((Dictionary<string, object>)AtomicJson.DeserializeObject(Encoding.UTF8.GetString(body)));
                        Received.Set();
                        if (reply == null) { release.WaitOne(5000); continue; }
                        byte[] data = Encoding.UTF8.GetBytes(reply);
                        byte[] responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + data.Length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(responseHeader, 0, responseHeader.Length); stream.Write(data, 0, data.Length);
                    }
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            thread.Start();
        }
        public void Complete() { Check(thread.Join(5000), "Loopback server hung"); if (error != null) throw error; }
        public void Dispose() { release.Set(); listener.Stop(); thread.Join(5000); release.Dispose(); Received.Dispose(); }
    }
}
