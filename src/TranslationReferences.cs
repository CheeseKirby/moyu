using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class ReferenceEvidence
    {
        public string Id { get; set; }
        public string Term { get; set; }
        public string Url { get; set; }
        public string Text { get; set; }
        public string RetrievedUtc { get; set; }
        public string Kind { get; set; }
        public List<int> CueIds { get; set; }
    }
    internal sealed class ReferenceQuery
    {
        public string Query { get; set; }
        public string Status { get; set; }
    }
    internal sealed class ReferenceReport
    {
        public string Signature { get; set; }
        public int SearchAttempts { get; set; }
        public double ActiveSeconds { get; set; }
        public string Warning { get; set; }
        public bool LocalLoaded { get; set; }
        public List<ReferenceEvidence> Evidence { get; set; }
        public List<ReferenceQuery> Queries { get; set; }
        public ReferenceReport() { Evidence = new List<ReferenceEvidence>(); Queries = new List<ReferenceQuery>(); }
    }
    internal sealed class TranslationReferences
    {
        public readonly ReferenceReport Report;
        private readonly AppConfig config;
        private readonly string file;
        private readonly IList<SubtitleCue> cues;
        internal Func<string, string, CancellationToken, string> Fetch;
        internal Func<string> ReadKey;
        public TranslationReferences(AppConfig config, string directory, IList<SubtitleCue> cues)
        {
            this.config = config; this.cues = cues;
            file = Path.Combine(directory, "translation-references.json");
            string signature = TranslationCache.Hash(AtomicJson.Serialize(new object[] { "references-1", config.SourceLanguage,
                config.EnableWebReference && config.WebPrivacyAccepted, TranslationCache.FileHash(config.ReferenceSubtitlePath),
                cues.Select(c => new object[] { c.Id, c.Start.Ticks, c.End.Ticks, c.Text }).ToArray() }));
            Report = AtomicJson.Read<ReferenceReport>(file, null);
            if (File.Exists(file) && (Report == null || Report.Signature != signature || Report.Evidence == null || Report.Queries == null))
                throw new InvalidDataException("参考记录损坏或输入已改变，已停止以免重复搜索。");
            if (Report == null) Report = new ReferenceReport { Signature = signature };
            Fetch = SafeReferenceHttp.Get; ReadKey = CredentialStore.ReadSearchKey;
        }
        public void Prepare(IDictionary<string, string> glossary, CancellationToken cancellation)
        {
            if (!Report.LocalLoaded)
            {
                if (!string.IsNullOrWhiteSpace(config.ReferenceSubtitlePath))
                    {
                        if (new FileInfo(config.ReferenceSubtitlePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("参考字幕超过4MB上限");
                        var local = SrtFile.Read(config.ReferenceSubtitlePath);
                        if (local.Count > 10000 || cues.Count > 10000) throw new InvalidDataException("参考对齐最多支持10000条字幕");
                        Report.Evidence.AddRange(Align(cues, local));
                    }
                Report.LocalLoaded = true; Save();
            }
            if (!config.EnableWebReference) return;
            if (!config.WebPrivacyAccepted) { Report.Warning = "联网参考未获隐私确认，未发送查询"; Save(); return; }
            foreach (string term in glossary.Keys.Where(t => t.Length >= 2 && t.Length <= 60)
                .OrderByDescending(t => cues.Count(c => QualityPolicy.Contains(c.Text, t))).ThenBy(t => t, StringComparer.Ordinal).Take(Math.Max(1, QualityPolicy.SearchLimit(config) / 2)))
            { cancellation.ThrowIfCancellationRequested(); Search(term, cancellation); }
        }
        public void Search(string term, CancellationToken cancellation)
        {
            if (!config.EnableWebReference || !config.WebPrivacyAccepted || string.IsNullOrWhiteSpace(term) || term.Length > 100
                || !cues.Any(c => QualityPolicy.Contains(c.Text, term))) return;
            string query = Regex.Replace(term, @"\s+", " ").Trim() + " " + config.SourceLanguage + " meaning 中文 词义";
            if (Report.Queries.Any(q => q.Query == query)) return;
            if (Report.SearchAttempts >= QualityPolicy.SearchLimit(config) || (!QualityPolicy.IsIntensive(config) && Report.ActiveSeconds >= 30))
            { Report.Warning = "参考查证已到预算上限，未查证项不自动采用"; Save(); return; }
            string key = ReadKey();
            if (string.IsNullOrWhiteSpace(key)) { Report.Warning = "未配置 Brave Search 密钥，联网参考不可用"; Save(); return; }
            var record = new ReferenceQuery { Query = query, Status = "interrupted" };
            Report.Queries.Add(record); Report.SearchAttempts++; Save();
            var timer = Stopwatch.StartNew();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(QualityPolicy.IsIntensive(config) ? 30000 : Math.Max(1, (int)((30 - Report.ActiveSeconds) * 1000)));
                try
                {
                    string json = Fetch("https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query) + "&count=5", key, timeout.Token);
                    var root = AtomicJson.DeserializeObject(json) as Dictionary<string, object>;
                    var web = root != null && root.ContainsKey("web") ? root["web"] as Dictionary<string, object> : null;
                    object[] results = web != null && web.ContainsKey("results") ? web["results"] as object[] : null;
                    if (results != null) foreach (var row in results.OfType<Dictionary<string, object>>().Take(2))
                    {
                        string url = row.ContainsKey("url") ? Convert.ToString(row["url"], CultureInfo.InvariantCulture) : "";
                        try
                        {
                            string page = Fetch(url, null, timeout.Token);
                            string text = Extract(page);
                            if (text.Length < 50 || !QualityPolicy.Contains(text, term)) continue;
                            int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                            string excerpt = text.Substring(Math.Max(0, at - 350), Math.Min(1500, text.Length - Math.Max(0, at - 350)));
                            Report.Evidence.Add(new ReferenceEvidence { Id = TranslationCache.Hash(url + excerpt).Substring(0, 16),
                                Term = term, Url = url, Text = excerpt, Kind = "web-unverified", RetrievedUtc = DateTime.UtcNow.ToString("o"), CueIds = new List<int>() });
                            Save();
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { /* A failed page is not evidence. Do not persist response bodies/errors. */ }
                    }
                    record.Status = "completed";
                    if (!Report.Evidence.Any(e => e.Term == term)) Report.Warning = "部分查询未取得可核对正文，不以搜索摘要改译";
                }
                catch (OperationCanceledException) { record.Status = "interrupted"; cancellation.ThrowIfCancellationRequested(); Report.Warning = "参考查证超时，保留已有证据"; }
                catch (Exception) { record.Status = "failed"; Report.Warning = "参考服务不可用或返回异常，保留已有译文"; }
                finally { Report.ActiveSeconds += timer.Elapsed.TotalSeconds; Save(); }
            }
        }
        public List<ReferenceEvidence> For(IList<SubtitleCue> targets)
        {
            var ids = new HashSet<int>(targets.Select(c => c.Id));
            return Report.Evidence.Where(e => (e.CueIds != null && e.CueIds.Any(ids.Contains))
                || (!string.IsNullOrEmpty(e.Term) && targets.Any(c => QualityPolicy.Contains(c.Text, e.Term))))
                .Take(12).ToList();
        }
        public string Snapshot { get { return TranslationCache.Hash(AtomicJson.Serialize(Report.Evidence)); } }
        private void Save() { AtomicJson.Write(file, Report); }
        internal static string Extract(string html)
        {
            string text = Regex.Replace(html, @"<(script|style|nav|footer)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 20000 ? text.Substring(0, 20000) : text;
        }
        internal static List<ReferenceEvidence> Align(IList<SubtitleCue> source, IList<SubtitleCue> reference)
        {
            var output = new List<ReferenceEvidence>();
            // Shift only when at least three unique verbatim source anchors agree; Chinese-only files stay at offset zero.
            var offsets = new List<double>();
            foreach (var cue in source.Where(c => (c.Text ?? "").Length >= 8))
            {
                var anchors = reference.Where(r => (r.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(line => line.Trim() == cue.Text.Trim())).Take(2).ToList();
                if (anchors.Count == 1 && source.Count(c => c.Text == cue.Text) == 1) offsets.Add((anchors[0].Start - cue.Start).TotalSeconds);
            }
            double offset = 0;
            if (offsets.Count >= 3)
            {
                double median = offsets.OrderBy(v => v).ElementAt(offsets.Count / 2);
                if (offsets.Count(v => Math.Abs(v - median) <= .4) >= Math.Ceiling(offsets.Count * .8)) offset = median;
            }
            reference = reference.Select(r => new SubtitleCue { Id = r.Id, Start = r.Start - TimeSpan.FromSeconds(offset), End = r.End - TimeSpan.FromSeconds(offset), Text = r.Text }).ToList();
            foreach (var cue in source)
            {
                double duration = Math.Max(.1, (cue.End - cue.Start).TotalSeconds);
                var matches = reference.Where(r => Math.Max(0, (Min(cue.End, r.End) - Max(cue.Start, r.Start)).TotalSeconds)
                    / Math.Max(duration, Math.Max(.1, (r.End - r.Start).TotalSeconds)) >= .75).Take(2).ToList();
                if (matches.Count != 1) continue;
                string text = matches[0].Text;
                if (!Regex.IsMatch(text ?? "", @"[\u4e00-\u9fff]") || text.Length > 500) continue;
                output.Add(new ReferenceEvidence { Id = "local-" + cue.Id, Kind = "local-time-hint", Term = "", Text = text,
                    CueIds = new List<int> { cue.Id }, RetrievedUtc = DateTime.UtcNow.ToString("o") });
            }
            return output;
        }
        private static TimeSpan Min(TimeSpan a, TimeSpan b) { return a < b ? a : b; }
        private static TimeSpan Max(TimeSpan a, TimeSpan b) { return a > b ? a : b; }
    }
    internal static class SafeReferenceHttp
    {
        internal static bool PublicAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) return PublicAddress(address.MapToIPv4());
            if (IPAddress.IsLoopback(address)) return false;
            var b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 && b[0] != 169
                    && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) && !(b[0] == 192 && b[1] == 168)
                    && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                    && !(b[0] == 192 && (b[1] == 0 || (b[1] == 88 && b[2] == 99)))
                    && !(b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100)))
                    && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
            // Only global unicast; exclude IPv4 translation/tunnel and documentation ranges.
            return b[0] >= 0x20 && b[0] <= 0x3f && !(b[0] == 0x20 && b[1] == 0x02)
                && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8)));
        }
        internal static void Validate(Uri uri)
        {
            if (uri == null || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)
                || !uri.IsDefaultPort || uri.HostNameType == UriHostNameType.Unknown)
                throw new InvalidDataException("参考网址不允许访问");
        }
        public static string Get(string url, string searchKey, CancellationToken cancellation)
        {
            Uri uri; if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) throw new InvalidDataException("参考网址无效");
            for (int hop = 0; hop < 4; hop++)
            {
                cancellation.ThrowIfCancellationRequested(); Validate(uri);
                if (searchKey != null && (uri.Scheme != "https" || uri.Host != "api.search.brave.com")) throw new InvalidDataException("密钥目标不匹配");
                var lookup = Dns.BeginGetHostAddresses(uri.DnsSafeHost, null, null);
                IPAddress[] addresses;
                using (var handle = lookup.AsyncWaitHandle)
                {
                    int ready = WaitHandle.WaitAny(new[] { handle, cancellation.WaitHandle }, 10000);
                    cancellation.ThrowIfCancellationRequested();
                    if (ready != 0) throw new IOException("参考域名解析超时");
                    addresses = Dns.EndGetHostAddresses(lookup);
                }
                if (addresses.Length == 0 || addresses.Any(a => !PublicAddress(a))) throw new InvalidDataException("禁止访问非公共地址");
                var allowed = new HashSet<IPAddress>(addresses);
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Proxy = null; request.AllowAutoRedirect = false; request.Timeout = 10000; request.ReadWriteTimeout = 10000;
                request.UserAgent = "Moyu/1.2 ReferenceCheck"; request.Accept = searchKey == null ? "text/html,text/plain" : "application/json";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.ConnectionGroupName = Guid.NewGuid().ToString("N"); request.KeepAlive = false;
                request.ServicePoint.BindIPEndPointDelegate = delegate(ServicePoint service, IPEndPoint remote, int retry)
                {
                    if (!PublicAddress(remote.Address) || !allowed.Contains(remote.Address)) throw new InvalidDataException("连接地址发生变化");
                    return null;
                };
                if (searchKey != null) request.Headers["X-Subscription-Token"] = searchKey;
                using (cancellation.Register(delegate { request.Abort(); }))
                {
                    try
                    {
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            int status = (int)response.StatusCode;
                            if (status >= 300 && status < 400)
                            {
                                if (searchKey != null) throw new InvalidDataException("搜索接口禁止重定向");
                                uri = new Uri(uri, response.Headers["Location"]); continue;
                            }
                            string type = response.ContentType ?? "";
                            if (!(type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) || type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
                                || (searchKey != null && type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))))
                                throw new InvalidDataException("不读取二进制参考文件");
                            using (var input = response.GetResponseStream())
                            using (var buffer = new MemoryStream())
                            {
                                byte[] block = new byte[8192]; int count;
                                while ((count = input.Read(block, 0, block.Length)) > 0)
                                { cancellation.ThrowIfCancellationRequested(); if (buffer.Length + count > 512000) throw new InvalidDataException("参考响应超限"); buffer.Write(block, 0, count); }
                                return Encoding.UTF8.GetString(buffer.ToArray());
                            }
                        }
                    }
                    catch (WebException) { cancellation.ThrowIfCancellationRequested(); throw; }
                    finally { request.ServicePoint.CloseConnectionGroup(request.ConnectionGroupName); }
                }
            }
            throw new InvalidDataException("参考重定向次数超限");
        }
    }
}
