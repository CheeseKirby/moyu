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
        public string QualitySummary { get; set; }
    }

    internal sealed class SubtitlePipelineRunner
    {
        private readonly AppConfig config;
        private readonly IProgress<ProgressInfo> progress;
        private readonly Func<bool> ensureApiKey;
        private readonly Func<string> readApiKey;
        private readonly Func<string, bool> confirmUnknown;
        private IntensiveTaskLedger intensiveLedger;

        public SubtitlePipelineRunner(AppConfig config, IProgress<ProgressInfo> progress, Func<bool> ensureApiKey, Func<string> readApiKey = null, Func<string, bool> confirmUnknown = null)
        {
            this.config = config;
            this.progress = progress;
            this.ensureApiKey = ensureApiKey;
            this.readApiKey = readApiKey ?? CredentialStore.ReadApiKey;
            this.confirmUnknown = confirmUnknown;
        }

        public PipelineResult Process(string mediaPath, CancellationToken cancellation)
        {
            QualityPolicy.Validate(config, true);
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
            // Keep source provenance when reusing an accepted source subtitle.
            if (!File.Exists(sourcePath)) manifest.RecognitionModel = Path.GetFileName(config.WhisperModelPath);
            manifest.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            manifest.Error = null;

            try
            {
                manifest.Status = "preparing-source";
                AtomicJson.Write(manifestPath, manifest);
                PrepareSourceSubtitle(mediaPath, cacheDir, rawSourcePath, candidateSourcePath, qualityReportPath, sourcePath, manifest, cancellation);

                List<SubtitleCue> cues = SrtFile.Read(sourcePath);
                if (cues.Count == 0) throw new InvalidDataException("没有从源语言字幕中读取到有效对白。");

                TranslationCache cache = new TranslationCache(cacheDir, config, cues);
                Directory.CreateDirectory(cache.DirectoryPath);
                chinesePath = Path.Combine(cache.DirectoryPath, "zh-CN.srt");
                bilingualPath = Path.Combine(cache.DirectoryPath, language + "-zh-CN.srt");
                translationStatePath = cache.BaseStatePath;
                string reviewWarning;
                if (cache.TryReadComplete(chinesePath, bilingualPath, out reviewWarning))
                {
                    manifest.Status = "complete";
                    manifest.TranslationModel = config.Model;
                    AtomicJson.Write(manifestPath, manifest);
                    Report("已命中字幕缓存", "字幕与当前模型、源字幕及处理策略一致。", 100);
                    SubtitlePublishResult cached = SubtitlePublisher.Publish(config, FindMatchingCurrentMedia(fingerprint, mediaPath), sourcePath, chinesePath, bilingualPath);
                    return new PipelineResult { Fingerprint = fingerprint, CacheHit = true,
                        BilingualSubtitlePath = cached.BilingualSidecarPath ?? bilingualPath,
                        QualityWarning = JoinWarnings(manifest.QualityWarning, reviewWarning), PublishWarning = cached.Warning,
                        QualitySummary = ReadReviewSummary(cache.DirectoryPath) };
                }
                cache.Begin();
                if (QualityPolicy.IsIntensive(config))
                {
                    intensiveLedger = new IntensiveTaskLedger(config, cache.DirectoryPath, cache.VariantKey, cancellation, confirmUnknown);
                    cancellation = intensiveLedger.Token;
                }
                if (!File.Exists(translationStatePath) && (File.Exists(Path.Combine(cacheDir, "translation-state.json"))
                    || Directory.GetFiles(Path.Combine(cacheDir, "translations"), "base-state.json", SearchOption.AllDirectories).Length > 0))
                {
                    if (confirmUnknown == null || !confirmUnknown("当前底稿策略或模型已变化，需要重新翻译并产生接口费用。旧字幕和缓存不会删除。是否开始新底稿？"))
                        throw new InvalidOperationException("未确认新底稿的付费翻译；旧缓存已保留。");
                }
                TranslationState state = AtomicJson.Read<TranslationState>(translationStatePath, null);
                if (File.Exists(translationStatePath) && (state == null || state.Translations == null || state.Glossary == null || state.CompletedScenes == null))
                    throw new InvalidDataException("基础译文断点损坏，已停止以避免重复付费。");
                state = state ?? new TranslationState();
                if (state.Translations == null) state.Translations = new Dictionary<string, string>();
                if (state.Glossary == null) state.Glossary = new Dictionary<string, string>();
                if (state.CompletedScenes == null) state.CompletedScenes = new List<int>();

                bool isQuality = string.Equals(config.TranslationQuality, "quality", StringComparison.OrdinalIgnoreCase);

                bool completeAlready = cues.All(delegate(SubtitleCue cue) { return TranslationCache.HasCue(state, cue.Id); });
                if (!completeAlready)
                {
                    if (string.IsNullOrWhiteSpace(readApiKey()))
                    {
                        if (ensureApiKey == null || !ensureApiKey()) throw new InvalidOperationException("没有设置 DeepSeek API Key，任务已保留，可稍后继续。");
                    }
                    TranslateAllScenes(cues, state, translationStatePath, cancellation);
                }

                // The base remains immutable during polish/review; cancelling can resume the variant.
                AtomicJson.Write(cache.BaseStatePath, state);
                TranslationState variantState = AtomicJson.Read<TranslationState>(cache.StatePath, null);
                if (File.Exists(cache.StatePath) && (variantState == null || variantState.Translations == null || variantState.Glossary == null || variantState.CompletedScenes == null))
                    throw new InvalidDataException("译文版本断点损坏，已保留文件且未重新付费：" + cache.StatePath);
                state = variantState ?? TranslationCache.Copy(state);
                translationStatePath = cache.StatePath;
                AtomicJson.Write(translationStatePath, state);
                reviewWarning = null;
                if (isQuality)
                {
                    if (QualityPolicy.IsIntensive(config) && string.IsNullOrWhiteSpace(readApiKey()) && (ensureApiKey == null || !ensureApiKey()))
                        throw new InvalidOperationException("精修需要模型密钥，底稿已保留。");
                    TermIndex terms = TermIndexStore.Load(cacheDir);
                    if (terms != null) TermIndexStore.Save(cache.DirectoryPath, terms);
                    TranslationReview.SeedGlossary(config, cache.DirectoryPath, state);
                    string reviewStage = QualityPolicy.IsIntensive(config) ? "正在全片精修" : "正在重点复核";
                    Report(reviewStage, QualityPolicy.IsIntensive(config) ? "全片覆盖、疑难裁决与一致性收尾；可以取消并保留断点。" : "全片规则检查；最多复核 6 个场景，修复有预算上限。", 94);
                    TranslationReviewReport review = QualityPolicy.IsIntensive(config)
                        ? IntensiveReview.Run(config, cache.DirectoryPath, cues, state, translationStatePath, cancellation, readApiKey(), detail => Report(reviewStage, detail, 94), intensiveLedger)
                        : TranslationReview.Run(config, cache.DirectoryPath, cues, state, translationStatePath, cancellation, readApiKey(), detail => Report(reviewStage, detail, 94));
                    reviewWarning = review.Warning;
                }
                else
                    ApplyFastTierPolish(cues, state, translationStatePath, cancellation);
                AtomicJson.Write(translationStatePath, state);

                Report("正在生成字幕文件", "同时保存简体中文和双语字幕。", 96);
                SrtFile.Write(chinesePath, cues, state.Translations, false);
                SrtFile.Write(bilingualPath, cues, state.Translations, true);

                cache.Complete(chinesePath, bilingualPath, reviewWarning);
                manifest.TranslationModel = config.Model;
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
                    QualityWarning = JoinWarnings(manifest.QualityWarning, reviewWarning),
                    QualitySummary = ReadReviewSummary(cache.DirectoryPath),
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
            finally { if (intensiveLedger != null) { intensiveLedger.Dispose(); intensiveLedger = null; } }
        }

        private static string ReadReviewSummary(string directory)
        {
            var review = AtomicJson.Read<TranslationReviewReport>(Path.Combine(directory, "translation-quality-report.json"), null);
            return review == null ? null : review.Summary;
        }

        private static string JoinWarnings(string a, string b)
        {
            return string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a + "；" + b;
        }

        private void ApplyFastTierPolish(List<SubtitleCue> cues, TranslationState state, string statePath, CancellationToken cancellation)
        {
            // 快速档的低成本译文校正：只对“残留源文 / 超长 / 极端重复”的少量字幕做一次定向修复。
            List<int> defectIds = TranslationQualityCheck.FindDefectCueIds(cues, state.Translations);
            if (defectIds.Count == 0) return;

            Report("正在校正译文", string.Format(CultureInfo.InvariantCulture, "发现 {0} 条待修正，正在定向重译。", defectIds.Count), 94);
            string key = readApiKey();
            if (string.IsNullOrWhiteSpace(key)) return;

            DeepSeekClient client = new DeepSeekClient(config, key);
            int applied = 0;
            int batchSize = Math.Max(1, config.SceneMaxCues);
            for (int offset = 0; offset < defectIds.Count; offset += batchSize)
            {
                cancellation.ThrowIfCancellationRequested();
                List<int> batch = defectIds.GetRange(offset, Math.Min(batchSize, defectIds.Count - offset));
                try
                {
                    Dictionary<string, string> repaired = client.RepairCues(cues, batch, config.ContextCueCount, state.Glossary, state.Translations, cancellation);
                    foreach (KeyValuePair<string, string> item in repaired)
                    {
                        string existing;
                        if (state.Translations.TryGetValue(item.Key, out existing) && string.Equals(existing, item.Value, StringComparison.Ordinal)) continue;
                        SubtitleCue cue = cues.FirstOrDefault(c => c.Id.ToString(CultureInfo.InvariantCulture) == item.Key);
                        string reason;
                        if (cue == null || !TranslationQualityCheck.AcceptRepair(cue, existing, item.Value, null, out reason)) continue;
                        state.Translations[item.Key] = item.Value;
                        applied++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 快速档不做质量兜底：修复失败也直接沿用原译文继续出片，仅记录日志。
                    Logger.Write("Fast-tier translation polish failed: " + ex.Message);
                }
            }
            if (applied > 0)
            {
                AtomicJson.Write(statePath, state);
                Report("正在生成字幕文件", string.Format(CultureInfo.InvariantCulture, "已校正 {0} 条译文，其余沿用原译文。", applied), 95);
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
            string key = readApiKey();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("DeepSeek API Key 不可用。");
            DeepSeekClient client = new DeepSeekClient(config, key);

            client.TaskLedger = intensiveLedger;
            for (int i = 0; i < scenes.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                SubtitleScene scene = scenes[i];
                bool sceneDone = true;
                for (int cueIndex = scene.StartCueIndex; cueIndex <= scene.EndCueIndex; cueIndex++)
                {
                    if (!TranslationCache.HasCue(state, cues[cueIndex].Id)) { sceneDone = false; break; }
                }
                if (sceneDone) continue;
                if (intensiveLedger != null)
                {
                    intensiveLedger.BeginOperation("base-" + scene.Index);
                    if (!intensiveLedger.CanAttempt) throw new InvalidOperationException("场景翻译已达到两次实际请求上限，未重复付费。");
                }

                int percent = 45 + (int)(48.0 * i / Math.Max(1, scenes.Count));
                Report("正在进行场景翻译", string.Format(CultureInfo.InvariantCulture, "场景 {0}/{1}，字幕 {2}-{3}，模型 {4}", i + 1, scenes.Count, cues[scene.StartCueIndex].Id, cues[scene.EndCueIndex].Id, config.Model), percent);

                Exception lastError = null;
                int retryLimit = intensiveLedger == null ? config.ApiRetryCount : 2;
                for (int attempt = 1; attempt <= retryLimit; attempt++)
                {
                    try
                    {
                        TranslationResult result = client.TranslateScene(cues, scene, config.ContextCueCount, state.Glossary, cancellation, state.Translations);
                        foreach (KeyValuePair<string, string> item in result.Translations) state.Translations[item.Key] = item.Value;
                        QualityPolicy.MergeGlossary(state, result.GlossaryUpdates);
                        if (!state.CompletedScenes.Contains(scene.Index)) state.CompletedScenes.Add(scene.Index);
                        AtomicJson.Write(statePath, state);
                        if (intensiveLedger != null) intensiveLedger.CommitResponse();
                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    // The client already retried with a larger budget; do not repeat it unchanged.
                    catch (TranslationOutputLimitException) { throw; }
                    catch (ReviewBudgetException) { throw; }
                    catch (Exception ex)
                    {
                        if (intensiveLedger != null && intensiveLedger.State.UnknownRequest) throw;
                        if (intensiveLedger != null) intensiveLedger.CommitResponse();
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
