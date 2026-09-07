using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        [STAThread]
        private static int Main(string[] args)
        {
            StoragePaths.Ensure();
            try
            {
                if (args.Length > 0 && string.Equals(args[0], "recognize-candidate", StringComparison.OrdinalIgnoreCase))
                {
                    string media = ReadOption(args, "--media");
                    if (string.IsNullOrWhiteSpace(media) || !File.Exists(media)) return 2;
                    return RecognitionPreview.Run(media);
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
