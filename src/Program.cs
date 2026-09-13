using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal static class Program
    {
        internal const string AppMutexName = WatcherContract.AppMutexName;
        private const string WakeEventName = "Local\\PotPlayerAiSubtitleAppWake-v2";
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            StoragePaths.Ensure();
            try
            {
                if (args.Length > 0 && string.Equals(args[0], "recognize-candidate", StringComparison.OrdinalIgnoreCase))
                {
                    string media = ReadOption(args, "--media");
                    if (string.IsNullOrWhiteSpace(media) || !File.Exists(media)) return 2;
                    return RecognitionPreview.Run(media);
                }
                if (args.Length > 0 && string.Equals(args[0], "compare-recognition", StringComparison.OrdinalIgnoreCase))
                {
                    string media = ReadOption(args, "--media");
                    if (string.IsNullOrWhiteSpace(media) || !File.Exists(media)) return 2;
                    return RecognitionComparison.Run(media, ReadOption(args, "--out"), ReadOption(args, "--lang"));
                }
                if (args.Length > 0 && string.Equals(args[0], "test-thinking", StringComparison.OrdinalIgnoreCase))
                {
                    string srt = ReadOption(args, "--srt");
                    if (string.IsNullOrWhiteSpace(srt) || !File.Exists(srt)) return 2;
                    return ThinkingTest.Run(srt);
                }
                if (args.Length > 0 && string.Equals(args[0], "translate-srt", StringComparison.OrdinalIgnoreCase))
                {
                    string srt = ReadOption(args, "--srt");
                    if (string.IsNullOrWhiteSpace(srt) || !File.Exists(srt)) return 2;
                    return TranslateSrtTest.Run(srt, ReadOption(args, "--mode") ?? "fast", ReadOption(args, "--out"), ReadOption(args, "--ctx"), ReadOption(args, "--keep"), ReadOption(args, "--lang"));
                }
                if (args.Length > 0 && string.Equals(args[0], "review-srt", StringComparison.OrdinalIgnoreCase))
                {
                    string srt = ReadOption(args, "--srt");
                    string baseDir = ReadOption(args, "--base");
                    if (string.IsNullOrWhiteSpace(srt) || !File.Exists(srt) || string.IsNullOrWhiteSpace(baseDir)) return 2;
                    return ReviewSrtTest.Run(srt, baseDir, ReadOption(args, "--ctx") ?? "8", ReadOption(args, "--out"), ReadOption(args, "--lang"));
                }
                if (args.Length > 0 && string.Equals(args[0], "process-media", StringComparison.OrdinalIgnoreCase))
                {
                    string media = ReadOption(args, "--media");
                    if (string.IsNullOrWhiteSpace(media) || !File.Exists(media)) return 2;
                    return ProcessMedia.Run(media, ReadOption(args, "--tier"), ReadOption(args, "--lang"));
                }
                if (args.Length > 0 && string.Equals(args[0], "self-test", StringComparison.OrdinalIgnoreCase))
                    return SelfTest.Run();
                if (args.Length > 0 && string.Equals(args[0], "ui-self-test", StringComparison.OrdinalIgnoreCase))
                    return UiSelfTest.Run();
                if (args.Length > 0 &&
                    (string.Equals(args[0], "--wait-for-potplayer", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[0], "wait-for-potplayer", StringComparison.OrdinalIgnoreCase)))
                    { StartupManager.EnsureRunning(); return 0; }
                if (args.Length == 1 && args[0] == "--sync-watcher")
                { StartupManager.SetEnabled(AppConfig.Load().StartWithWindows); return 0; }

                bool startHidden = false;
                bool openSettings = false;
                if (args.Length > 0 && string.Equals(args[0], "notify", StringComparison.OrdinalIgnoreCase))
                {
                    string media = ReadOption(args, "--media");
                    if (string.IsNullOrWhiteSpace(media)) return 2;
                    DetectionInbox.Publish(media);
                }
                else if (args.Length > 0 && string.Equals(args[0], "clear-current", StringComparison.OrdinalIgnoreCase))
                {
                    AtomicJson.Write(StoragePaths.CurrentMediaFile, new CurrentMediaState { MediaPath = "", UpdatedUtc = DateTime.UtcNow.ToString("o") });
                    return 0;
                }
                else if (args.Length > 0 &&
                    (string.Equals(args[0], "start-monitor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[0], "monitor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[0], "--tray", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[0], "tray", StringComparison.OrdinalIgnoreCase)))
                {
                    startHidden = true;
                }
                else if (args.Length > 0 && string.Equals(args[0], "configure", StringComparison.OrdinalIgnoreCase))
                {
                    openSettings = true;
                }

                return RunApplication(startHidden, openSettings);
            }
            catch (Exception ex)
            {
                Logger.Write("Program error: " + ex);
                return 1;
            }
        }

        private static int RunApplication(bool startHidden, bool openSettings)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, AppMutexName, out created))
            {
                if (!created)
                {
                    SignalExistingInstance();
                    return 0;
                }

                using (EventWaitHandle wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm(startHidden, openSettings, wakeEvent));
                }
                return 0;
            }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (EventWaitHandle wake = EventWaitHandle.OpenExisting(WakeEventName)) wake.Set();
            }
            catch { }
        }

        private static string ReadOption(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    internal static class DetectionInbox
    {
        public static void Publish(string mediaPath)
        {
            string fullPath = Path.GetFullPath(mediaPath);
            if (!File.Exists(fullPath) || !PotPlayerMonitor.IsMediaFile(fullPath)) throw new FileNotFoundException("找不到可处理的视频文件。", fullPath);
            AtomicJson.Write(StoragePaths.DetectedMediaFile, new CurrentMediaState
            {
                MediaPath = fullPath,
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            });
        }

        public static string Take()
        {
            CurrentMediaState state = AtomicJson.Read<CurrentMediaState>(StoragePaths.DetectedMediaFile, null);
            AtomicJson.Write(StoragePaths.DetectedMediaFile, new CurrentMediaState { MediaPath = "", UpdatedUtc = DateTime.UtcNow.ToString("o") });
            if (state == null || string.IsNullOrWhiteSpace(state.MediaPath) || !File.Exists(state.MediaPath)) return null;
            DateTime updated;
            if (DateTime.TryParse(state.UpdatedUtc, out updated) && (DateTime.UtcNow - updated.ToUniversalTime()).TotalMinutes > 2) return null;
            return Path.GetFullPath(state.MediaPath);
        }
    }
    internal static class QueueManager
    {
        public static void Notify(string mediaPath)
        {
            Notify(mediaPath, AppConfig.Load().SourceLanguage);
        }

        public static void Notify(string mediaPath, string sourceLanguage)
        {
            string fullPath = Path.GetFullPath(mediaPath);
            CurrentMediaState current = new CurrentMediaState
            {
                MediaPath = fullPath,
                UpdatedUtc = DateTime.UtcNow.ToString("o")
            };
            AtomicJson.Write(StoragePaths.CurrentMediaFile, current);
            if (!File.Exists(fullPath)) return;

            JobRequest request = new JobRequest
            {
                MediaPath = fullPath,
                RequestedUtc = DateTime.UtcNow.ToString("o"),
                SourceLanguage = sourceLanguage
            };
            string requestPath = Path.Combine(StoragePaths.Queue, "request-" + HashPath(fullPath + "|" + request.SourceLanguage) + ".json");
            AtomicJson.Write(requestPath, request);
        }

        public static KeyValuePair<string, JobRequest>? PeekNext()
        {
            string[] files = Directory.GetFiles(StoragePaths.Queue, "request-*.json")
                .OrderBy(delegate(string path) { return File.GetLastWriteTimeUtc(path); })
                .ToArray();
            foreach (string path in files)
            {
                JobRequest request = AtomicJson.Read<JobRequest>(path, null);
                if (request != null && !string.IsNullOrWhiteSpace(request.MediaPath))
                    return new KeyValuePair<string, JobRequest>(path, request);
                try { File.Delete(path); } catch { }
            }
            return null;
        }

        public static void Complete(string requestPath)
        {
            try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch { }
        }

        private static string HashPath(string path)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
                return builder.ToString();
            }
        }
    }

    internal static class RecognitionPreview
    {
        public static int Run(string mediaPath)
        {
            AppConfig config = AppConfig.Load();
            string fingerprint = ContentFingerprint.Compute(mediaPath);
            string language = config.SourceLanguage;
            string cacheDir = SourceLanguages.CacheDirectory(Path.Combine(StoragePaths.Cache, fingerprint), language);
            Directory.CreateDirectory(cacheDir);
            string rawPath = Path.Combine(cacheDir, language + ".candidate.raw.srt");
            string candidatePath = Path.Combine(cacheDir, language + ".candidate.srt");
            string reportPath = Path.Combine(cacheDir, "quality-report.json");
            SourceSubtitleRecognizer recognizer = new SourceSubtitleRecognizer(config,
                delegate(string stage, string detail, int percent)
                {
                    Logger.Write(string.Format("Candidate {0}% {1}: {2}", percent, stage, detail));
                });
            RecognitionCandidateResult result = recognizer.RecognizeLocalCandidate(mediaPath, cacheDir, rawPath, candidatePath, reportPath, CancellationToken.None);
            SubtitleQualityDecision decision = SubtitleQuality.EvaluateForTranslation(result.QualityReport);
            AtomicJson.Write(reportPath, result.QualityReport);
            Logger.Write(string.Format("Candidate complete: accepted={0}, cues={1}, suspicious={2}, path={3}",
                decision.CanContinue, result.QualityReport.CueCount, result.QualityReport.SuspiciousCueCount, candidatePath));
            return decision.CanContinue ? 0 : 3;
        }
    }

    internal static class RecognitionComparison
    {
        public static int Run(string mediaPath, string outDirectory, string langOverride)
        {
            AppConfig baseConfig = AppConfig.Load();
            if (!string.IsNullOrWhiteSpace(langOverride)) baseConfig.SourceLanguage = langOverride;
            if (string.IsNullOrWhiteSpace(outDirectory)) outDirectory = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrWhiteSpace(outDirectory)) outDirectory = StoragePaths.Root;
            Directory.CreateDirectory(outDirectory);

            string name = Path.GetFileNameWithoutExtension(mediaPath);
            string lang = baseConfig.SourceLanguage;
            string fingerprint = ContentFingerprint.Compute(mediaPath);
            string fastCache = Path.Combine(Path.GetTempPath(), "MoyuCompareFast-" + Guid.NewGuid().ToString("N"));
            string qualityCache = Path.Combine(Path.GetTempPath(), "MoyuCompareQuality-" + Guid.NewGuid().ToString("N"));
            try
            {
                RecognitionCandidateResult fast = RunMode(mediaPath, baseConfig, fastCache, fingerprint, lang, outDirectory, name + "-fast.srt", "fast");
                BuildTermIndexFromCandidate(baseConfig, fastCache, qualityCache, fingerprint, lang);
                RecognitionCandidateResult quality = RunMode(mediaPath, baseConfig, qualityCache, fingerprint, lang, outDirectory, name + "-quality.srt", "quality");
                WriteSummary(outDirectory, name, fast, quality);
                Logger.Write("Recognition comparison complete: " + Path.Combine(outDirectory, name + "-compare.txt"));
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Write("Recognition comparison failed: " + ex);
                return 1;
            }
            finally
            {
                TryDeleteDirectory(fastCache);
                TryDeleteDirectory(qualityCache);
            }
        }

        private static RecognitionCandidateResult RunMode(string mediaPath, AppConfig baseConfig, string cacheRoot, string fingerprint, string lang,
            string outDirectory, string outFile, string mode)
        {
            Directory.CreateDirectory(cacheRoot);
            string cacheDir = SourceLanguages.CacheDirectory(Path.Combine(cacheRoot, fingerprint), lang);
            Directory.CreateDirectory(cacheDir);
            string rawPath = Path.Combine(cacheDir, lang + ".candidate.raw.srt");
            string candidatePath = Path.Combine(cacheDir, lang + ".candidate.srt");
            string reportPath = Path.Combine(cacheDir, "quality-report.json");

            AppConfig cfg = AppConfig.Load();
            cfg.TranslationQuality = mode;
            cfg.SourceLanguage = lang;
            SourceSubtitleRecognizer recognizer = new SourceSubtitleRecognizer(cfg,
                delegate(string stage, string detail, int percent)
                {
                    Logger.Write(string.Format("Compare[{0}] {1}% {2}: {3}", mode, percent, stage, detail));
                });
            RecognitionCandidateResult result = recognizer.RecognizeLocalCandidate(mediaPath, cacheDir, rawPath, candidatePath, reportPath, CancellationToken.None);
            File.Copy(candidatePath, Path.Combine(outDirectory, outFile), true);
            AtomicJson.Write(Path.Combine(outDirectory, nameWithoutExtension(outFile) + "-report.json"), result.QualityReport);
            return result;
        }

        private static string nameWithoutExtension(string path) { return Path.GetFileNameWithoutExtension(path); }

        private static void BuildTermIndexFromCandidate(AppConfig baseConfig, string sourceCacheRoot, string targetCacheRoot, string fingerprint, string lang)
        {
            try
            {
                string sourceCacheDir = SourceLanguages.CacheDirectory(Path.Combine(sourceCacheRoot, fingerprint), lang);
                string candidatePath = Path.Combine(sourceCacheDir, lang + ".candidate.srt");
                if (!File.Exists(candidatePath)) return;
                List<SubtitleCue> cues = SrtFile.Read(candidatePath);
                List<string> candidates = TermBuilder.ExtractCandidates(cues);
                List<string> context = TermBuilder.BuildContext(cues, candidates);
                if (context.Count == 0) return;
                string key = CredentialStore.ReadApiKey();
                if (string.IsNullOrWhiteSpace(key))
                {
                    Logger.Write("Comparison: no API key for term index, quality prompt disabled.");
                    return;
                }
                AppConfig cfg = AppConfig.Load();
                cfg.TranslationQuality = "quality";
                cfg.SourceLanguage = lang;
                DeepSeekClient client = new DeepSeekClient(cfg, key);
                TermIndex index = new TermIndex { SourceLanguage = lang, Entries = client.BuildTermIndex(candidates, context, new Dictionary<string, string>(), CancellationToken.None) };
                TermIndexStore.Save(SourceLanguages.CacheDirectory(Path.Combine(targetCacheRoot, fingerprint), lang), index);
                Logger.Write("Comparison: term index saved with " + index.Entries.Count + " entries.");
            }
            catch (Exception ex)
            {
                Logger.Write("Comparison: term index build skipped (" + ex.Message + ").");
            }
        }

        private static void WriteSummary(string outDirectory, string name, RecognitionCandidateResult fast, RecognitionCandidateResult quality)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("魔芋 识别档位对比");
            AppendRow(builder, "指标", "快速档", "质量档");
            AppendRow(builder, "字幕条数", fast.QualityReport.CueCount.ToString(), quality.QualityReport.CueCount.ToString());
            AppendRow(builder, "可疑条数", fast.QualityReport.SuspiciousCueCount.ToString(), quality.QualityReport.SuspiciousCueCount.ToString());
            AppendRow(builder, "自动清理", fast.QualityReport.RemovedCueCount.ToString(), quality.QualityReport.RemovedCueCount.ToString());
            AppendRow(builder, ">10秒", fast.QualityReport.Over10Seconds.ToString(), quality.QualityReport.Over10Seconds.ToString());
            AppendRow(builder, ">20秒", fast.QualityReport.Over20Seconds.ToString(), quality.QualityReport.Over20Seconds.ToString());
            AppendRow(builder, ">30秒", fast.QualityReport.Over30Seconds.ToString(), quality.QualityReport.Over30Seconds.ToString());
            AppendRow(builder, "最长(秒)", fast.QualityReport.LongestSeconds.ToString("0.0"), quality.QualityReport.LongestSeconds.ToString("0.0"));
            builder.AppendLine();
            builder.AppendLine("阅读提示: 质量档含响度/高通处理、术语索引提示词与拼写校正。");
            builder.AppendLine("输出: " + name + "-fast.srt / " + name + "-quality.srt 及各自 -report.json");
            File.WriteAllText(Path.Combine(outDirectory, name + "-compare.txt"), builder.ToString(), new UTF8Encoding(false));
        }

        private static void AppendRow(StringBuilder builder, string a, string b, string c)
        {
            builder.AppendLine(a + "\t" + b + "\t" + c);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(Path.GetTempPath());
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
            }
            catch { }
        }
    }

    internal static class ThinkingTest
    {
        public static int Run(string srtPath)
        {
            AppConfig config = AppConfig.Load();
            config.TranslationQuality = "quality"; // A/B probe only; never persists the tier.
            List<SubtitleCue> cues = SrtFile.Read(srtPath);
            if (cues.Count == 0) { Logger.Write("Thinking test: no cues in " + srtPath); return 2; }
            SubtitleScene scene = new SubtitleScene { Index = 0, StartCueIndex = 0, EndCueIndex = Math.Min(11, cues.Count - 1) };
            Dictionary<string, string> glossary = new Dictionary<string, string>();

            string key = CredentialStore.ReadApiKey();
            if (string.IsNullOrWhiteSpace(key)) { Logger.Write("Thinking test: no API key."); return 3; }

            string sampleName = Path.GetFileNameWithoutExtension(srtPath);
            TimeSpan offMs = TimeSpan.Zero;
            TimeSpan onMs = TimeSpan.Zero;
            List<string> offText = new List<string>();
            List<string> onText = new List<string>();
            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                TranslationResult off = new DeepSeekClient(config, key, false).TranslateScene(cues, scene, config.ContextCueCount, glossary, CancellationToken.None);
                watch.Stop();
                offMs = watch.Elapsed;
                for (int i = scene.StartCueIndex; i <= scene.EndCueIndex; i++)
                {
                    string id = cues[i].Id.ToString(CultureInfo.InvariantCulture);
                    string text;
                    offText.Add(off.Translations.TryGetValue(id, out text) ? text : "");
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Thinking test off failed: " + ex.Message);
                offText.Add("[off failed] " + ex.Message);
            }

            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                TranslationResult on = new DeepSeekClient(config, key, true).TranslateScene(cues, scene, config.ContextCueCount, glossary, CancellationToken.None);
                watch.Stop();
                onMs = watch.Elapsed;
                for (int i = scene.StartCueIndex; i <= scene.EndCueIndex; i++)
                {
                    string id = cues[i].Id.ToString(CultureInfo.InvariantCulture);
                    string text;
                    onText.Add(on.Translations.TryGetValue(id, out text) ? text : "");
                }
            }
            catch (Exception ex)
            {
                string raw = "";
                try { raw = new DeepSeekClient(config, key, true).ProbeTranslation(cues, scene, config.ContextCueCount, glossary, CancellationToken.None); }
                catch (Exception probe) { raw = "[probe failed] " + probe.Message; }
                Logger.Write("Thinking test on failed: " + ex.Message + " raw_head=" + (raw.Length > 200 ? raw.Substring(0, 200) : raw));
                onText.Add("[on failed] " + ex.Message + " | raw: " + (raw.Length > 300 ? raw.Substring(0, 300) : raw));
            }

            Dictionary<string, object> report = new Dictionary<string, object>();
            report["mode"] = new Dictionary<string, object> { { "off_ms", offMs.TotalMilliseconds }, { "on_ms", onMs.TotalMilliseconds } };
            report["off"] = offText;
            report["on"] = onText;
            report["source"] = cues.Take(scene.EndCueIndex + 1).Select(delegate(SubtitleCue cue) { return cue.Text; }).ToList();
            string resultPath = Path.Combine(StoragePaths.Logs, "thinking-test.json");
            AtomicJson.Write(resultPath, report);
            Logger.Write(string.Format("Thinking test: off={0:0}ms on={1:0}ms samples={2} result={3}",
                offMs.TotalMilliseconds, onMs.TotalMilliseconds, scene.EndCueIndex - scene.StartCueIndex + 1, resultPath));
            return 0;
        }
    }

    internal static class TranslateSrtTest
    {
        public static int Run(string srtPath, string mode, string outDirectory, string ctxOverride, string keepDir, string langOverride)
        {
            AppConfig config = AppConfig.Load();
            config.TranslationQuality = mode;
            if (!string.IsNullOrWhiteSpace(langOverride)) config.SourceLanguage = langOverride;
            int ctx;
            if (!string.IsNullOrWhiteSpace(ctxOverride) && int.TryParse(ctxOverride, out ctx) && ctx >= 0)
                config.ReviewContextCount = ctx;
            if (string.IsNullOrWhiteSpace(outDirectory)) outDirectory = Path.GetDirectoryName(srtPath);
            if (string.IsNullOrWhiteSpace(outDirectory)) outDirectory = StoragePaths.Root;
            Directory.CreateDirectory(outDirectory);

            List<SubtitleCue> cues = SrtFile.Read(srtPath);
            if (cues.Count == 0) { Logger.Write("translate-srt: no cues."); return 2; }
            string cacheDir = Path.Combine(Path.GetTempPath(), "MoyuTr-" + mode + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cacheDir);
            TranslationState state = new TranslationState();
            try
            {
                bool quality = string.Equals(mode, "quality", StringComparison.OrdinalIgnoreCase);
                if (quality) BuildTermIndex(config, cues, cacheDir);
                if (quality) TranslationReview.SeedGlossary(config, cacheDir, state);

                string key = CredentialStore.ReadApiKey();
                if (string.IsNullOrWhiteSpace(key)) { Logger.Write("translate-srt: no API key."); return 3; }
                DeepSeekClient client = new DeepSeekClient(config, key);
                List<SubtitleScene> scenes = SrtFile.BuildScenes(cues, config.SceneMaxCues, config.SceneMaxSeconds);
                for (int i = 0; i < scenes.Count; i++)
                {
                    if (i % 10 == 0) Logger.Write("translate-srt[" + mode + "] scene " + (i + 1) + "/" + scenes.Count);
                    TranslationResult result = client.TranslateScene(cues, scenes[i], config.ContextCueCount, state.Glossary, CancellationToken.None);
                    foreach (KeyValuePair<string, string> item in result.Translations) state.Translations[item.Key] = item.Value;
                    foreach (KeyValuePair<string, string> item in result.GlossaryUpdates) state.Glossary[item.Key] = item.Value;
                }

                if (!string.IsNullOrWhiteSpace(keepDir))
                {
                    Directory.CreateDirectory(keepDir);
                    string indexSrc = Path.Combine(cacheDir, "term-index.json");
                    if (File.Exists(indexSrc)) File.Copy(indexSrc, Path.Combine(keepDir, "term-index.json"), true);
                    AtomicJson.Write(Path.Combine(keepDir, "translation-state.json"), state);
                    Logger.Write("translate-srt[" + mode + "] base frozen to " + keepDir);
                    return 0;
                }

                string statePath = Path.Combine(cacheDir, "translation-state.json");
                if (quality)
                {
                    TranslationReview.Run(config, cacheDir, cues, state, statePath, CancellationToken.None);
                    string report = Path.Combine(cacheDir, "translation-quality-report.json");
                    if (File.Exists(report)) File.Copy(report, Path.Combine(outDirectory, Path.GetFileNameWithoutExtension(srtPath) + "-" + mode + "-quality-report.json"), true);
                }
                else
                {
                    AtomicJson.Write(statePath, state);
                }

                string safeName = Path.GetFileNameWithoutExtension(srtPath) + "-" + mode;
                SrtFile.Write(Path.Combine(outDirectory, safeName + "-zh.srt"), cues, state.Translations, false);
                SrtFile.Write(Path.Combine(outDirectory, safeName + "-bi.srt"), cues, state.Translations, true);
                Logger.Write("translate-srt[" + mode + "] complete: " + safeName + "-zh.srt, cues=" + cues.Count);
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Write("translate-srt[" + mode + "] failed: " + ex.Message);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }
            }
        }

        private static void BuildTermIndex(AppConfig config, IList<SubtitleCue> cues, string cacheDir)
        {
            try
            {
                List<string> candidates = TermBuilder.ExtractCandidates(cues);
                List<string> context = TermBuilder.BuildContext(cues, candidates);
                if (context.Count == 0) return;
                string key = CredentialStore.ReadApiKey();
                if (string.IsNullOrWhiteSpace(key)) return;
                TermIndex index = new TermIndex
                {
                    SourceLanguage = config.SourceLanguage,
                    Entries = new DeepSeekClient(config, key).BuildTermIndex(candidates, context, new Dictionary<string, string>(), CancellationToken.None)
                };
                TermIndexStore.Save(cacheDir, index);
                Logger.Write("translate-srt term index entries=" + index.Entries.Count);
            }
            catch (Exception ex)
            {
                Logger.Write("translate-srt term index build failed: " + ex.Message);
            }
        }
    }

    internal static class ReviewSrtTest
    {
        public static int Run(string srtPath, string baseDir, string ctx, string outDirectory, string language = null)
        {
            AppConfig config = AppConfig.Load();
            config.TranslationQuality = "quality";
            if (!string.IsNullOrWhiteSpace(language)) config.SourceLanguage = SourceLanguages.Normalize(language);
            int context;
            config.ReviewContextCount = int.TryParse(ctx, out context) && context >= 0 ? context : 0;
            if (string.IsNullOrWhiteSpace(outDirectory)) outDirectory = Path.Combine(StoragePaths.Cache, "review-evaluation-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            if (string.Equals(Path.GetFullPath(outDirectory).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("评测输出不能与冻结基线使用同一目录。");
            Directory.CreateDirectory(outDirectory);

            List<SubtitleCue> cues = SrtFile.Read(srtPath);
            if (cues.Count == 0) return 2;
            TranslationState state = AtomicJson.Read<TranslationState>(Path.Combine(baseDir, "translation-state.json"), null);
            if (state == null || state.Translations == null || state.Translations.Count == 0)
            {
                Logger.Write("review-srt: no frozen translation state in " + baseDir);
                return 2;
            }

            string tmpState = Path.Combine(outDirectory, "state-tmp-" + context + ".json");
            // Evaluation must never write into the frozen baseline directory.
            string reviewDir = Path.Combine(outDirectory, "review-ctx" + context + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(reviewDir);
            TermIndex frozenTerms = TermIndexStore.Load(baseDir);
            if (frozenTerms != null) TermIndexStore.Save(reviewDir, frozenTerms);
            TranslationReview.Run(config, reviewDir, cues, state, tmpState, CancellationToken.None);
            string name = Path.GetFileNameWithoutExtension(srtPath);
            SrtFile.Write(Path.Combine(outDirectory, name + "-ctx" + context + "-zh.srt"), cues, state.Translations, false);
            string report = Path.Combine(reviewDir, "translation-quality-report.json");
            if (File.Exists(report))
                File.Copy(report, Path.Combine(outDirectory, name + "-ctx" + context + "-report.json"), true);
            Logger.Write("review-srt ctx=" + context + " complete");
            return 0;
        }
    }

    internal static class ProcessMedia
    {
        public static int Run(string mediaPath, string tierOverride, string langOverride)
        {
            AppConfig config = AppConfig.Load();
            if (!string.IsNullOrWhiteSpace(tierOverride))
                config.TranslationQuality = tierOverride;
            if (!string.IsNullOrWhiteSpace(langOverride))
                config.SourceLanguage = langOverride;
            config.CopyFinishedSubtitlesBesideMedia = false;
            string tempHub = Path.Combine(Path.GetTempPath(), "MoyuHub-" + Guid.NewGuid().ToString("N"));
            config.SubtitleHubPath = tempHub;
            try
            {
                IProgress<ProgressInfo> progress = new Progress<ProgressInfo>(delegate(ProgressInfo info)
                {
                    Logger.Write(string.Format("Pipeline {0}% {1}: {2}", info.Percent, info.Stage, info.Detail));
                });
                SubtitlePipelineRunner runner = new SubtitlePipelineRunner(config, progress, delegate { return true; });
                PipelineResult result = runner.Process(mediaPath, CancellationToken.None);
                Logger.Write("Pipeline result: cacheHit=" + result.CacheHit
                    + " bilingual=" + (result.BilingualSubtitlePath ?? "")
                    + " qualityWarning=" + (result.QualityWarning ?? ""));
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Write("Pipeline failed: " + ex);
                return 1;
            }
        }
    }

    internal static class SelfTest
    {
        public static int Run()
        {
            string resultPath = Path.Combine(StoragePaths.Logs, "self-test-result.txt");
            string tempRoot = Path.Combine(Path.GetTempPath(), "PotPlayerAiSubtitleSelfTest-" + Guid.NewGuid().ToString("N"));
            List<string> results = new List<string>();
            bool success = false;
            try
            {
                Directory.CreateDirectory(tempRoot);
                string original = Path.Combine(tempRoot, "original-video.bin");
                byte[] block = new byte[400000];
                new Random(7319).NextBytes(block);
                using (FileStream stream = new FileStream(original, FileMode.CreateNew, FileAccess.Write))
                {
                    for (int i = 0; i < 50; i++) stream.Write(block, 0, block.Length);
                }
                string movedDir = Path.Combine(tempRoot, "renamed-folder");
                Directory.CreateDirectory(movedDir);
                string renamed = Path.Combine(movedDir, "completely-different-name.bin");
                File.Copy(original, renamed);
                string fp1 = ContentFingerprint.Compute(original);
                string fp2 = ContentFingerprint.Compute(renamed);
                if (!string.Equals(fp1, fp2, StringComparison.Ordinal)) throw new Exception("改名和移动后的内容指纹不一致。");
                results.Add("PASS content fingerprint survives rename and path change");

                string srt = Path.Combine(tempRoot, "sample.srt");
                List<SubtitleCue> cues = new List<SubtitleCue>();
                cues.Add(new SubtitleCue { Id = 1, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = "こんにちは" });
                cues.Add(new SubtitleCue { Id = 2, Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(5), Text = "元気ですか。" });
                SrtFile.Write(srt, cues, null, false);
                List<SubtitleCue> read = SrtFile.Read(srt);
                if (read.Count != 2 || read[1].Text != "元気ですか。") throw new Exception("SRT 读写测试失败。");
                results.Add("PASS SRT read/write");

                string publishMedia = Path.Combine(tempRoot, "publish-test.mp4");
                File.WriteAllBytes(publishMedia, new byte[] { 9, 8, 7, 6 });
                string sourceSubtitle = Path.Combine(tempRoot, "source.srt");
                string chineseSubtitle = Path.Combine(tempRoot, "chinese.srt");
                string bilingualSubtitle = Path.Combine(tempRoot, "bilingual.srt");
                SrtFile.Write(sourceSubtitle, cues, null, false);
                Dictionary<string, string> translations = new Dictionary<string, string>();
                translations["1"] = "你好";
                translations["2"] = "你好吗。";
                SrtFile.Write(chineseSubtitle, cues, translations, false);
                SrtFile.Write(bilingualSubtitle, cues, translations, true);
                File.Copy(chineseSubtitle, Path.Combine(tempRoot, "publish-test.zh-CN.srt"));
                File.Copy(bilingualSubtitle, Path.Combine(tempRoot, "publish-test.ja-zh-CN.srt"));
                AppConfig publishConfig = AppConfig.CreateDefault();
                publishConfig.SubtitleHubPath = Path.Combine(tempRoot, "custom-hub");
                SubtitlePublishResult published = SubtitlePublisher.Publish(publishConfig, publishMedia, sourceSubtitle, chineseSubtitle, bilingualSubtitle);
                string expectedHub = Path.Combine(publishConfig.SubtitleHubPath, "publish-test字幕");
                if (!File.Exists(Path.Combine(tempRoot, "publish-test.srt"))
                    || !File.Exists(Path.Combine(expectedHub, "publish-test-双语.srt"))
                    || !File.Exists(Path.Combine(expectedHub, "publish-test-源语言.srt"))
                    || !File.Exists(Path.Combine(expectedHub, "publish-test-中文.srt"))
                    || File.Exists(Path.Combine(tempRoot, "publish-test.zh-CN.srt"))
                    || File.Exists(Path.Combine(tempRoot, "publish-test.ja-zh-CN.srt")))
                    throw new Exception("字幕发布目录结构或旧旁挂文件清理测试失败。");
                SubtitlePublishResult republished = SubtitlePublisher.Publish(publishConfig, publishMedia, sourceSubtitle, chineseSubtitle, bilingualSubtitle);
                if (!republished.BilingualSidecarWasAlreadyAvailable || republished.BilingualSidecarChanged)
                    throw new Exception("相同旁挂字幕被不必要地重写。 ");
                results.Add("PASS bilingual sidecar and persistent hub layout");

                List<SubtitleScene> scenes = SrtFile.BuildScenes(read, 35, 150);
                if (scenes.Count != 1) throw new Exception("场景分段测试失败。");
                results.Add("PASS scene grouping");

                List<SubtitleCue> bad = new List<SubtitleCue>();
                bad.Add(new SubtitleCue { Id = 1, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(45), Text = "う?" });
                bad.Add(new SubtitleCue { Id = 2, Start = TimeSpan.FromSeconds(44), End = TimeSpan.FromSeconds(46), Text = "う?" });
                bad.Add(new SubtitleCue { Id = 3, Start = TimeSpan.FromSeconds(46), End = TimeSpan.FromSeconds(48), Text = "う?" });
                bad.Add(new SubtitleCue { Id = 4, Start = TimeSpan.FromSeconds(48), End = TimeSpan.FromSeconds(50), Text = "う?" });
                SubtitleQualityReport badReport = SubtitleQuality.Analyze(bad);
                if (badReport.Passed || badReport.Over30Seconds != 1 || badReport.RepeatedVocalizationClusterCount != 1)
                    throw new Exception("字幕质量异常检测测试失败。");
                SubtitleSanitizeResult cleaned = SubtitleQuality.SanitizeAfterRetry(bad);
                SubtitleQualityReport cleanReport = SubtitleQuality.Analyze(cleaned.Cues);
                if (!cleanReport.Passed || cleaned.Cues.Count > 3) throw new Exception("字幕质量清理测试失败。");
                results.Add("PASS subtitle quality detection and cleanup");
                List<SubtitleCue> repeated = new List<SubtitleCue>();
                for (int i = 0; i < 10; i++)
                    repeated.Add(new SubtitleCue { Id = i + 1, Start = TimeSpan.FromSeconds(i * 2), End = TimeSpan.FromSeconds(i * 2 + 1.5), Text = "同じ台詞" });
                SubtitleSanitizeResult repeatedCleaned = SubtitleQuality.SanitizeAfterRetry(repeated);
                SubtitleQualityReport repeatedReport = SubtitleQuality.Analyze(repeatedCleaned.Cues);
                repeatedReport.GateApplied = true;
                repeatedReport.InitialCueCount = repeated.Count;
                repeatedReport.RemovedCueCount = repeatedCleaned.RemovedCount;
                SubtitleQualityDecision repeatedDecision = SubtitleQuality.EvaluateForTranslation(repeatedReport);
                if (!repeatedDecision.CanContinue || repeatedCleaned.Cues.Count > 3)
                    throw new Exception("重复识别自动修复测试失败。");
                results.Add("PASS repeated speech repair and warning continuation");

                SubtitleQualityReport fatalReport = new SubtitleQualityReport
                {
                    GateApplied = true,
                    Passed = false,
                    InitialCueCount = 100,
                    CueCount = 90,
                    InvalidDurationCount = 1
                };
                if (SubtitleQuality.EvaluateForTranslation(fatalReport).CanContinue)
                    throw new Exception("严重时间轴异常阻断测试失败。");
                results.Add("PASS severe timeline errors remain blocked");
                SourceLanguageSelfTest.Run(tempRoot, results);
                success = true;
            }
            catch (Exception ex)
            {
                results.Add("FAIL " + ex);
                Logger.Write("Self-test failed: " + ex);
            }
            finally
            {
                try
                {
                    string resolved = Path.GetFullPath(tempRoot);
                    string expectedBase = Path.GetFullPath(Path.GetTempPath());
                    if (resolved.StartsWith(expectedBase, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
                }
                catch { }
                File.WriteAllLines(resultPath, results.ToArray(), new UTF8Encoding(false));
            }
            return success ? 0 : 1;
        }
    }
}
