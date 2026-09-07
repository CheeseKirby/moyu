using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class PipelineResult
    {
        public string Fingerprint { get; set; }
        public string BilingualSubtitlePath { get; set; }
        public bool CacheHit { get; set; }
        public string QualityWarning { get; set; }
        public string PublishWarning { get; set; }
    }

    internal sealed class SubtitlePipelineRunner
    {
        private readonly AppConfig config;
        private readonly IProgress<ProgressInfo> progress;
        private readonly Func<bool> ensureApiKey;

        public SubtitlePipelineRunner(AppConfig config, IProgress<ProgressInfo> progress, Func<bool> ensureApiKey)
        {
            this.config = config;
            this.progress = progress;
            this.ensureApiKey = ensureApiKey;
        }

        public PipelineResult Process(string mediaPath, CancellationToken cancellation)
        {
            if (!File.Exists(mediaPath)) throw new FileNotFoundException("视频文件已经不存在。", mediaPath);
            Report("正在识别视频", "计算内容指纹；改名或移动不会改变这个身份。", 2);
            string fingerprint = ContentFingerprint.Compute(mediaPath);
            cancellation.ThrowIfCancellationRequested();

            string language = config.SourceLanguage;
            string cacheDir = SourceLanguages.CacheDirectory(Path.Combine(StoragePaths.Cache, fingerprint), language);
            Directory.CreateDirectory(cacheDir);
            string manifestPath = Path.Combine(cacheDir, "manifest.json");
            string rawSourcePath = Path.Combine(cacheDir, language + ".raw.srt");
            string candidateSourcePath = Path.Combine(cacheDir, language + ".candidate.srt");
            string qualityReportPath = Path.Combine(cacheDir, "quality-report.json");
            string sourcePath = Path.Combine(cacheDir, language + ".srt");
            string chinesePath = Path.Combine(cacheDir, "zh-CN.srt");
            string bilingualPath = Path.Combine(cacheDir, language + "-zh-CN.srt");
            string translationStatePath = Path.Combine(cacheDir, "translation-state.json");

            JobManifest manifest = AtomicJson.Read<JobManifest>(manifestPath, new JobManifest());
            manifest.Version = "3";
            manifest.SourceLanguage = language;
            manifest.Fingerprint = fingerprint;
            manifest.FileSize = new FileInfo(mediaPath).Length;
            if (string.IsNullOrEmpty(manifest.FirstSeenPath)) manifest.FirstSeenPath = mediaPath;
            manifest.LastSeenPath = mediaPath;
            manifest.RecognitionModel = Path.GetFileName(config.WhisperModelPath);
            manifest.TranslationModel = config.Model;
            manifest.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            manifest.Error = null;

            if (File.Exists(chinesePath) && File.Exists(bilingualPath))
            {
                manifest.Status = "complete";
                AtomicJson.Write(manifestPath, manifest);
                Report("已命中字幕缓存", "找到相同视频内容的完整字幕。", 100);
                string currentPath = FindMatchingCurrentMedia(fingerprint, mediaPath);
                SubtitlePublishResult published = SubtitlePublisher.Publish(config, currentPath, sourcePath, chinesePath, bilingualPath);
                return new PipelineResult
                {
                    Fingerprint = fingerprint,
                    BilingualSubtitlePath = published.BilingualSidecarPath ?? bilingualPath,
                    CacheHit = true,
                    QualityWarning = manifest.QualityWarning,
                    PublishWarning = published.Warning
                };
            }

            try
            {
                manifest.Status = "preparing-source";
                AtomicJson.Write(manifestPath, manifest);
                PrepareSourceSubtitle(mediaPath, cacheDir, rawSourcePath, candidateSourcePath, qualityReportPath, sourcePath, manifest, cancellation);

                List<SubtitleCue> cues = SrtFile.Read(sourcePath);
                if (cues.Count == 0) throw new InvalidDataException("没有从源语言字幕中读取到有效对白。");

                TranslationState state = AtomicJson.Read<TranslationState>(translationStatePath, new TranslationState());
                if (state.Translations == null) state.Translations = new Dictionary<string, string>();
                if (state.Glossary == null) state.Glossary = new Dictionary<string, string>();
                if (state.CompletedScenes == null) state.CompletedScenes = new List<int>();

                bool completeAlready = cues.All(delegate(SubtitleCue cue) { return state.Translations.ContainsKey(cue.Id.ToString(CultureInfo.InvariantCulture)); });
                if (!completeAlready)
                {
                    if (string.IsNullOrWhiteSpace(CredentialStore.ReadApiKey()))
                    {
                        if (ensureApiKey == null || !ensureApiKey()) throw new InvalidOperationException("没有设置 DeepSeek API Key，任务已保留，可稍后继续。");
                    }
                    TranslateAllScenes(cues, state, translationStatePath, cancellation);
                }

                Report("正在生成字幕文件", "同时保存简体中文和双语字幕。", 96);
                SrtFile.Write(chinesePath, cues, state.Translations, false);
                SrtFile.Write(bilingualPath, cues, state.Translations, true);

                manifest.Status = "complete";
                manifest.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                manifest.Error = null;
                AtomicJson.Write(manifestPath, manifest);

                string matchingPath = FindMatchingCurrentMedia(fingerprint, mediaPath);
                Report("正在整理字幕文件", "视频旁保留双语字幕，并归档三种字幕版本。", 98);
                SubtitlePublishResult published = SubtitlePublisher.Publish(config, matchingPath, sourcePath, chinesePath, bilingualPath);
                Report("字幕处理完成", "双语字幕已保存到视频旁；字幕加载由 PotPlayer 负责。", 100);
                return new PipelineResult
                {
                    Fingerprint = fingerprint,
                    BilingualSubtitlePath = published.BilingualSidecarPath ?? bilingualPath,
                    CacheHit = false,
                    QualityWarning = manifest.QualityWarning,
                    PublishWarning = published.Warning
                };
            }
            catch (RecognitionQualityException ex)
            {
                manifest.Status = "recognition-review-required";
                manifest.Error = ex.Message;
                manifest.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                AtomicJson.Write(manifestPath, manifest);
                throw;
            }
            catch (Exception ex)
            {
                manifest.Status = ex is OperationCanceledException ? "cancelled" : "failed";
                manifest.Error = ex.Message;
                manifest.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                AtomicJson.Write(manifestPath, manifest);
                throw;
            }
        }

        private void PrepareSourceSubtitle(string mediaPath, string cacheDir, string rawSourcePath,
            string candidateSourcePath, string qualityReportPath, string sourcePath, JobManifest manifest, CancellationToken cancellation)
        {
            if (File.Exists(sourcePath) && SrtFile.Read(sourcePath).Count > 0)
            {
                Report("已找到源语言字幕", "继续上次未完成的任务。", 38);
                return;
            }

            if (TryRecoverExistingCandidate(cacheDir, candidateSourcePath, qualityReportPath, sourcePath, manifest)) return;

            SourceSubtitleRecognizer recognizer = new SourceSubtitleRecognizer(config, Report);
            RecognitionCandidateResult result = recognizer.Prepare(mediaPath, cacheDir, rawSourcePath,
                candidateSourcePath, qualityReportPath, cancellation);
            manifest.SourceKind = result.SourceKind;
            SubtitleQualityDecision decision = SubtitleQuality.EvaluateForTranslation(result.QualityReport);
            AtomicJson.Write(qualityReportPath, result.QualityReport);
            ApplyQualityDecision(manifest, decision);
            if (result.QualityReport.GateApplied && !decision.CanContinue)
            {
                throw new RecognitionQualityException(decision.Reason + " 已保留候选字幕和 quality-report.json，不会调用翻译 API。");
            }

            File.Copy(candidateSourcePath, sourcePath, true);
            if (decision.HasWarning) Report("识别结果已自动修复", decision.Reason, 44);
            SourceSubtitleRecognizer.DeleteAcceptedAudio(result);
        }

        private bool TryRecoverExistingCandidate(string cacheDir, string candidatePath, string reportPath, string sourcePath, JobManifest manifest)
        {
            if (!File.Exists(candidatePath) || !File.Exists(reportPath)) return false;
            List<SubtitleCue> candidate = SrtFile.Read(candidatePath);
            if (candidate.Count == 0) return false;

            SubtitleQualityReport previous = AtomicJson.Read<SubtitleQualityReport>(reportPath, null);
            if (previous == null || !previous.GateApplied) return false;
            string rejectedReportPath = Path.Combine(cacheDir, "quality-report.rejected.json");
            if (!previous.Passed && !File.Exists(rejectedReportPath)) File.Copy(reportPath, rejectedReportPath);
            SubtitleSanitizeResult sanitized = SubtitleQuality.SanitizeAfterRetry(candidate);
            SubtitleQualityReport report = SubtitleQuality.Analyze(sanitized.Cues);
            report.GateApplied = true;
            report.InitialCueCount = previous.InitialCueCount > 0 ? previous.InitialCueCount : candidate.Count;
            report.InitialSuspiciousCueCount = previous.InitialSuspiciousCueCount;
            report.RetryRegionCount = previous.RetryRegionCount;
            report.RetriedCueCount = previous.RetriedCueCount;
            report.RemovedCueCount = previous.RemovedCueCount + sanitized.RemovedCount;
            report.CompactedCueCount = previous.CompactedCueCount + sanitized.CompactedCount;
            SubtitleQualityDecision decision = SubtitleQuality.EvaluateForTranslation(report);
            AtomicJson.Write(reportPath, report);
            if (!decision.CanContinue) return false;

            SrtFile.Write(sourcePath, sanitized.Cues, null, false);
            ApplyQualityDecision(manifest, decision);
            Report("正在恢复上次的识别结果", decision.Reason, 44);
            string audioPath = Path.Combine(cacheDir, "audio-16k.wav");
            try { File.Delete(audioPath + ".ready"); } catch { }
            try { File.Delete(audioPath); } catch { }
            return true;
        }

        private static void ApplyQualityDecision(JobManifest manifest, SubtitleQualityDecision decision)
        {
            manifest.QualityDisposition = decision.HasWarning ? "accepted-with-warning" : "accepted";
            manifest.QualityWarning = decision.HasWarning ? decision.Reason : null;
        }
        private void TranslateAllScenes(List<SubtitleCue> cues, TranslationState state, string statePath, CancellationToken cancellation)
        {
            List<SubtitleScene> scenes = SrtFile.BuildScenes(cues, config.SceneMaxCues, config.SceneMaxSeconds);
            string key = CredentialStore.ReadApiKey();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("DeepSeek API Key 不可用。");
            DeepSeekClient client = new DeepSeekClient(config, key);

            for (int i = 0; i < scenes.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                SubtitleScene scene = scenes[i];
                bool sceneDone = true;
                for (int cueIndex = scene.StartCueIndex; cueIndex <= scene.EndCueIndex; cueIndex++)
                {
                    if (!state.Translations.ContainsKey(cues[cueIndex].Id.ToString(CultureInfo.InvariantCulture))) { sceneDone = false; break; }
                }
                if (sceneDone) continue;

                int percent = 45 + (int)(48.0 * i / Math.Max(1, scenes.Count));
                Report("正在进行场景翻译", string.Format(CultureInfo.InvariantCulture, "场景 {0}/{1}，字幕 {2}-{3}，模型 {4}", i + 1, scenes.Count, cues[scene.StartCueIndex].Id, cues[scene.EndCueIndex].Id, config.Model), percent);

                Exception lastError = null;
                for (int attempt = 1; attempt <= config.ApiRetryCount; attempt++)
                {
                    try
                    {
                        TranslationResult result = client.TranslateScene(cues, scene, config.ContextCueCount, state.Glossary, cancellation);
                        foreach (KeyValuePair<string, string> item in result.Translations) state.Translations[item.Key] = item.Value;
                        foreach (KeyValuePair<string, string> item in result.GlossaryUpdates) state.Glossary[item.Key] = item.Value;
                        if (!state.CompletedScenes.Contains(scene.Index)) state.CompletedScenes.Add(scene.Index);
                        AtomicJson.Write(statePath, state);
                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Logger.Write(string.Format(CultureInfo.InvariantCulture, "Scene {0} attempt {1}: {2}", scene.Index, attempt, ex.Message));
                        if (attempt < config.ApiRetryCount)
                        {
                            int delay = 1000 * attempt * attempt;
                            Report("翻译请求重试", "场景返回格式或网络异常，等待后重试。", percent);
                            if (cancellation.WaitHandle.WaitOne(delay)) cancellation.ThrowIfCancellationRequested();
                        }
                    }
                }
                if (lastError != null) throw new InvalidOperationException("场景 " + (i + 1) + " 翻译失败：" + lastError.Message, lastError);
            }
        }

        private string FindMatchingCurrentMedia(string expectedFingerprint, string fallbackPath)
        {
            CurrentMediaState current = AtomicJson.Read<CurrentMediaState>(StoragePaths.CurrentMediaFile, null);
            if (current != null && !string.IsNullOrEmpty(current.MediaPath) && File.Exists(current.MediaPath))
            {
                try
                {
                    if (string.Equals(ContentFingerprint.Compute(current.MediaPath), expectedFingerprint, StringComparison.OrdinalIgnoreCase)) return current.MediaPath;
                }
                catch { }
            }
            if (File.Exists(fallbackPath)) return fallbackPath;
            return null;
        }

        private void Report(string stage, string detail, int percent)
        {
            if (progress != null) progress.Report(new ProgressInfo(stage, detail, Math.Max(0, Math.Min(100, percent))));
        }
    }
}
