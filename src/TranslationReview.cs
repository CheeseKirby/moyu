using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class ReviewWindowRecord
    {
        public int SceneIndex { get; set; }
        public int AttemptCount { get; set; }
        public int FirstCueId { get; set; }
        public int LastCueId { get; set; }
        public double StartSeconds { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public List<TranslationReviewIssue> Issues { get; set; }
    }
    internal sealed class TranslationChange
    {
        public int CueId { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public bool Accepted { get; set; }
        public string Reason { get; set; }
    }
    internal sealed class RepairRecord
    {
        public string Name { get; set; }
        public List<int> CueIds { get; set; }
        public string Status { get; set; }
    }
    internal sealed class TranslationReviewReport
    {
        public string Version { get; set; }
        public bool Intensive { get; set; }
        public int CoveredCueCount { get; set; }
        public string Phase { get; set; }
        public bool ProcessComplete { get; set; }
        public List<RefinementWindow> RefinementWindows { get; set; }
        public Dictionary<string, string> FrozenTranslations { get; set; }
        public Dictionary<string, string> PhaseTranslations { get; set; }
        public int SearchAttempts { get; set; }
        public string ReferenceWarning { get; set; }
        public string Signature { get; set; }
        public string BaselineHash { get; set; }
        public string GeneratedUtc { get; set; }
        public bool Finished { get; set; }
        public string Status { get; set; }
        public string Warning { get; set; }
        public int Layer1CueCount { get; set; }
        public int GlossaryDefectCount { get; set; }
        public int BaseDefectCount { get; set; }
        public int TotalCueCount { get; set; }
        public int TotalSceneCount { get; set; }
        public int SelectedSceneCount { get; set; }
        public int ReviewedSceneCount { get; set; }
        public int FailedSceneCount { get; set; }
        public int SkippedSceneCount { get; set; }
        public int ReviewIssueCount { get; set; }
        public int NeedsSourceCheckCount { get; set; }
        public int FixedCount { get; set; }
        public int RejectedCount { get; set; }
        public int UnchangedCount { get; set; }
        public int UnresolvedSuggestionCount { get; set; }
        public int RemainingLayer1CueCount { get; set; }
        public int RemainingGlossaryCueCount { get; set; }
        // Compatibility field: rule-clean only, never a semantic quality verdict.
        public bool Readable { get; set; }
        public int RequestLimit { get; set; }
        public int OutputTokenLimit { get; set; }
        public int TimeLimitSeconds { get; set; }
        public int RequestCount { get; set; }
        public int ResponseCount { get; set; }
        public int UnknownUsageResponses { get; set; }
        public int ReservedOutputTokens { get; set; }
        public int InputCharacters { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public int ResponsesWithReasoningUsage { get; set; }
        public bool UsageComplete { get { return ResponseCount == RequestCount && UnknownUsageResponses == 0; } }
        public double ElapsedSeconds { get; set; }
        public bool BudgetExhausted { get; set; }
        public List<int> InitialHardDefects { get; set; }
        public List<ReviewWindowRecord> Windows { get; set; }
        public List<RepairRecord> Repairs { get; set; }
        public List<TranslationChange> Changes { get; set; }
        public bool CanResume
        {
            get { if (Intensive) return !ProcessComplete && (RefinementWindows == null
                    || !RefinementWindows.Any(w => w.Phase == Phase && w.Status != "succeeded")
                    || RefinementWindows.Any(w => w.Phase == Phase && w.Status != "succeeded" && w.Status != "exhausted"));
                return !BudgetExhausted && RequestCount < RequestLimit && ReservedOutputTokens < OutputTokenLimit
                && ElapsedSeconds < TimeLimitSeconds && Windows != null && Windows.Any(w =>
                    (w.Status == "failed" && w.AttemptCount < 2)
                    || (w.Status == "skipped" && (w.Error == "missing-api-key" || w.Error == "circuit-open"))); }
        }
        public string Summary
        {
            get { if (Intensive) return (ProcessComplete ? "全片精修流程完成" : "精修未完成") + "；覆盖 " + CoveredCueCount + "/" + TotalCueCount
                    + " 条；采纳 " + FixedCount + " 项；待核对 " + NeedsSourceCheckCount + " 条；模型请求 " + RequestCount + " 次；搜索 " + SearchAttempts + " 次。";
                return "重点复核 " + ReviewedSceneCount + "/" + SelectedSceneCount + " 个场景（全片 " + TotalSceneCount
                + " 个）；采纳 " + FixedCount + " 条修改；请求 " + RequestCount + " 次。"; }
        }
    }

    internal static class TranslationReview
    {
        private const int MaxReviewScenes = 6;
        private const int RepairBatchSize = 24;

        public static void SeedGlossary(AppConfig config, string cacheDir, TranslationState state)
        {
            TermIndex index = TermIndexStore.Load(cacheDir);
            if (index == null || index.IsEmpty) return;
            if (state.Glossary == null) state.Glossary = new Dictionary<string, string>();
            foreach (TermIndexEntry entry in index.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Source) && !string.IsNullOrWhiteSpace(entry.Target))
                    QualityPolicy.MergeGlossary(state, new Dictionary<string, string> { { entry.Source, entry.Target } });
                // Map recognition variants to the canonical Chinese rendering so a same object
                // stays consistent even when Whisper spelled the name differently.
                if (entry.Variants != null)
                {
                    foreach (string variant in entry.Variants)
                        if (!string.IsNullOrWhiteSpace(variant) && !string.IsNullOrWhiteSpace(entry.Target) )
                            QualityPolicy.MergeGlossary(state, new Dictionary<string, string> { { variant, entry.Target } });
                }
            }
        }

        public static TranslationReviewReport Run(AppConfig config, string cacheDir, IList<SubtitleCue> cues,
            TranslationState state, string statePath, CancellationToken cancellation, string apiKey = null, Action<string> progress = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (QualityPolicy.IsIntensive(config))
                return IntensiveReview.Run(config, cacheDir, cues, state, statePath, cancellation, apiKey, progress, null);
            TermIndex index = TermIndexStore.Load(cacheDir);
            if (index == null && state.Glossary != null)
                index = new TermIndex { SourceLanguage = config.SourceLanguage,
                    Entries = state.Glossary.Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                        .Select(p => new TermIndexEntry { Source = p.Key, Target = p.Value }).ToList() };
            string reportPath = Path.Combine(cacheDir, "translation-quality-report.json");
            string signature = new TranslationCache(cacheDir, config, cues).VariantKey;
            TranslationReviewReport report = AtomicJson.Read<TranslationReviewReport>(reportPath, null);
            if (File.Exists(reportPath) && (report == null || report.Version != "3" || report.Signature != signature))
                throw new InvalidDataException("复核记录与输入策略不一致或已损坏；请使用独立的评测目录，避免重复付费。");
            if (report != null)
            {
                var original = new Dictionary<string, string>(state.Translations);
                foreach (var change in report.Changes.Where(c => c.Accepted).Reverse())
                {
                    string id = change.CueId.ToString(CultureInfo.InvariantCulture);
                    if (change.Before == null) original.Remove(id); else original[id] = change.Before;
                }
                if (report.BaselineHash != StateHash(original))
                    throw new InvalidDataException("复核基线已变化，不能沿用旧记录；已停止以避免重复付费。");
            }
            if (report == null)
            {
                var hard = TranslationQualityCheck.FindDefectCueIds(cues, state.Translations);
                var terms = TranslationQualityCheck.FindGlossaryDefects(cues, state.Translations, index);
                var scenes = BuildReviewScenes(cues, config);
                var selected = SelectRiskyScenes(cues, config, index, hard.Concat(terms).Distinct().ToList());
                report = new TranslationReviewReport { Version = "3", Signature = signature, BaselineHash = StateHash(state.Translations),
                    GeneratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    BaseDefectCount = hard.Count, GlossaryDefectCount = terms.Count,
                    Layer1CueCount = hard.Concat(terms).Distinct().Count(), TotalCueCount = cues.Count,
                    TotalSceneCount = scenes.Count, SelectedSceneCount = selected.Count,
                    InitialHardDefects = hard, Windows = selected.Select(s => new ReviewWindowRecord {
                        SceneIndex = s.Index, FirstCueId = cues[s.StartCueIndex].Id, LastCueId = cues[s.EndCueIndex].Id,
                        StartSeconds = cues[s.StartCueIndex].Start.TotalSeconds, Status = "pending", Issues = new List<TranslationReviewIssue>() }).ToList(),
                    Repairs = new List<RepairRecord>(), Changes = new List<TranslationChange>(),
                    RequestLimit = 10, OutputTokenLimit = config.EnableThinking ? 131072 : 49152,
                    TimeLimitSeconds = config.EnableThinking ? 480 : 180 };
            }
            // Accepted edits are a write-ahead journal. A crash between report/state writes is recoverable.
            foreach (TranslationChange change in report.Changes.Where(c => c.Accepted))
                state.Translations[change.CueId.ToString(CultureInfo.InvariantCulture)] = change.After;
            AtomicJson.Write(statePath, state);
            string key = apiKey ?? CredentialStore.ReadApiKey();
            if (report.Finished && report.CanResume && !string.IsNullOrWhiteSpace(key))
            {
                foreach (var w in report.Windows.Where(w => (w.Status == "failed" && w.AttemptCount < 2)
                    || (w.Status == "skipped" && (w.Error == "missing-api-key" || w.Error == "circuit-open"))))
                { w.Status = "pending"; w.Error = null; }
                report.Finished = false;
            }
            if (report.Finished) return report;

            var references = new TranslationReferences(config, cacheDir, cues);
            references.Prepare(state.Glossary, cancellation);
            Action save = delegate { AtomicJson.Write(reportPath, report); };
            ReviewBudget budget = new ReviewBudget(report, save);
            try
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    foreach (var w in report.Windows.Where(w => w.Status == "pending")) { w.Status = "skipped"; w.Error = "missing-api-key"; }
                }
                else
                {
                    // Only review uses optional thinking. Mechanical repairs stay inexpensive.
                    DeepSeekClient reviewer = new DeepSeekClient(config, key, config.EnableThinking, budget);
                    DeepSeekClient repairer = new DeepSeekClient(config, key, false, budget);
                    reviewer.References = references; repairer.References = references; reviewer.GlossaryConflicts = state.GlossaryConflicts;
                    Repair("rules", report.InitialHardDefects.Take(RepairBatchSize).ToList(), null, repairer, cues, state,
                        statePath, config, index, report, budget, cancellation);
                    var allScenes = BuildReviewScenes(cues, config);
                    int failures = 0;
                    foreach (ReviewWindowRecord window in report.Windows)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (window.Status != "pending") continue;
                        if (report.BudgetExhausted || failures >= 2)
                        { window.Status = "skipped"; window.Error = report.BudgetExhausted ? "budget" : "circuit-open"; budget.Checkpoint(); continue; }
                        window.AttemptCount++;
                        window.Status = "interrupted";
                        budget.Checkpoint(); // Never silently pay again for an ambiguous interrupted request.
                        if (progress != null) progress("重点复核场景 " + (window.SceneIndex + 1) + "/" + report.TotalSceneCount + "；最多 6 个场景。");
                        try
                        {
                            SubtitleScene scene = allScenes.First(s => s.Index == window.SceneIndex);
                            window.Issues = reviewer.ReviewWindow(cues, scene, state.Translations, state.Glossary, config.ReviewContextCount, cancellation);
                            window.Status = "succeeded";
                            failures = 0;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (ReviewBudgetException) { report.BudgetExhausted = true; window.Status = "skipped"; window.Error = "budget"; }
                        catch (Exception ex) { window.Status = "failed"; window.Error = ex is InvalidDataException ? ex.Message : ex.GetType().Name; failures++; Logger.Write("Quality review failed: " + window.Error); }
                        budget.Checkpoint();
                    }
                    foreach (var issue in report.Windows.SelectMany(w => w.Issues).Where(i => !i.NeedsSourceCheck && !string.IsNullOrWhiteSpace(i.SearchTerm)))
                    {
                        references.Search(issue.SearchTerm, cancellation);
                        if (!references.Report.Evidence.Any(e => e.Term == issue.SearchTerm)) issue.NeedsSourceCheck = true;
                    }
                    var suggestions = Suggestions(report);
                    var uncertainIds = new HashSet<int>(report.Windows.SelectMany(w => w.Issues).Where(i => i.NeedsSourceCheck).Select(i => i.CueId));
                    foreach (int id in uncertainIds) suggestions.Remove(id);
                    Repair("review", suggestions.Keys.OrderBy(id => id).Take(RepairBatchSize).ToList(), suggestions,
                        repairer, cues, state, statePath, config, index, report, budget, cancellation);
                }
                cancellation.ThrowIfCancellationRequested();
                Finish(report, cues, state, index);
                report.SearchAttempts = references.Report.SearchAttempts; report.ReferenceWarning = references.Report.Warning;
                if (!string.IsNullOrWhiteSpace(report.ReferenceWarning)) report.Warning = (report.Warning ?? "") + "；" + report.ReferenceWarning;
                report.Finished = true;
                return report;
            }
            finally { budget.Checkpoint(); }
        }

        private static string StateHash(IDictionary<string, string> translations)
        {
            return TranslationCache.Hash(AtomicJson.Serialize(translations.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new object[] { p.Key, p.Value }).ToArray()));
        }

        private static Dictionary<int, string> Suggestions(TranslationReviewReport report)
        {
            return report.Windows.Where(w => w.Status == "succeeded").SelectMany(w => w.Issues)
                .GroupBy(i => i.CueId).ToDictionary(g => g.Key, g => g.First().Suggestion);
        }

        private static void Repair(string name, List<int> ids, IDictionary<int, string> suggestions, DeepSeekClient client,
            IList<SubtitleCue> cues, TranslationState state, string statePath, AppConfig config, TermIndex index,
            TranslationReviewReport report, ReviewBudget budget, CancellationToken cancellation)
        {
            if (ids.Count == 0 || report.Repairs.Any(r => r.Name == name)) return;
            cancellation.ThrowIfCancellationRequested();
            RepairRecord attempt = new RepairRecord { Name = name, CueIds = ids, Status = "interrupted" };
            report.Repairs.Add(attempt);
            budget.Checkpoint();
            if (report.BudgetExhausted) { attempt.Status = "skipped"; budget.Checkpoint(); return; }
            try
            {
                var proposed = client.RepairCues(cues, ids, config.ReviewContextCount, state.Glossary, state.Translations, cancellation, suggestions);
                foreach (int id in ids)
                {
                    string key = id.ToString(CultureInfo.InvariantCulture), before, after, reason;
                    state.Translations.TryGetValue(key, out before);
                    if (!proposed.TryGetValue(key, out after))
                    {
                        report.Changes.Add(new TranslationChange { CueId = id, Before = before, After = null, Reason = "missing-candidate" });
                        continue;
                    }
                    bool accepted = TranslationQualityCheck.AcceptRepair(cues.First(c => c.Id == id), before, after, index, out reason);
                    report.Changes.Add(new TranslationChange { CueId = id, Before = before, After = after, Accepted = accepted,
                        Reason = reason ?? (suggestions != null && suggestions.ContainsKey(id) ? suggestions[id] : "rule-repair") });
                    if (accepted) state.Translations[key] = after;
                }
                attempt.Status = "succeeded";
                budget.Checkpoint();
                AtomicJson.Write(statePath, state);
            }
            catch (OperationCanceledException) { throw; }
            catch (ReviewBudgetException) { report.BudgetExhausted = true; attempt.Status = "skipped"; }
            catch (Exception ex) { attempt.Status = "failed"; Logger.Write("Quality repair failed: " + ex.GetType().Name); }
            budget.Checkpoint();
        }

        private static void Finish(TranslationReviewReport report, IList<SubtitleCue> cues, TranslationState state, TermIndex index)
        {
            report.ReviewedSceneCount = report.Windows.Count(w => w.Status == "succeeded");
            report.FailedSceneCount = report.Windows.Count(w => w.Status == "failed" || w.Status == "interrupted");
            report.SkippedSceneCount = report.SelectedSceneCount - report.ReviewedSceneCount - report.FailedSceneCount;
            report.ReviewIssueCount = Suggestions(report).Count;
            report.NeedsSourceCheckCount = report.Windows.SelectMany(w => w.Issues).Where(i => i.NeedsSourceCheck).Select(i => i.CueId).Distinct().Count();
            report.FixedCount = report.Changes.Count(c => c.Accepted);
            report.UnchangedCount = report.Changes.Count(c => c.Reason == "unchanged");
            report.RejectedCount = report.Changes.Count(c => !c.Accepted && c.Reason != "unchanged");
            var repaired = new HashSet<int>(report.Changes.Where(c => c.Accepted).Select(c => c.CueId));
            report.UnresolvedSuggestionCount = Suggestions(report).Keys.Count(id => !repaired.Contains(id));
            report.RemainingLayer1CueCount = TranslationQualityCheck.FindDefectCueIds(cues, state.Translations).Count;
            report.RemainingGlossaryCueCount = TranslationQualityCheck.FindGlossaryDefects(cues, state.Translations, index).Count;
            report.Readable = report.RemainingLayer1CueCount == 0;
            bool incomplete = report.ReviewedSceneCount != report.SelectedSceneCount || report.Repairs.Any(r => r.Status != "succeeded")
                || report.RemainingLayer1CueCount > 0 || report.UnresolvedSuggestionCount > 0;
            report.Status = incomplete ? "partial" : "limited-review-complete";
            var warnings = new List<string>();
            if (report.ReviewedSceneCount != report.SelectedSceneCount) warnings.Add("重点复核仅完成 " + report.ReviewedSceneCount + "/" + report.SelectedSceneCount + " 个场景");
            if (report.BudgetExhausted) warnings.Add("复核已达预算上限，保留已有译文");
            if (report.Repairs.Any(r => r.Status != "succeeded")) warnings.Add("部分定向修复未完成");
            if (report.RemainingLayer1CueCount > 0) warnings.Add("仍有 " + report.RemainingLayer1CueCount + " 条规则异常");
            if (report.NeedsSourceCheckCount > 0) warnings.Add("有 " + report.NeedsSourceCheckCount + " 条需核对源文，未猜测重译");
            if (report.UnresolvedSuggestionCount > 0) warnings.Add("有 " + report.UnresolvedSuggestionCount + " 条建议未自动采纳");
            if (report.RemainingGlossaryCueCount > 0) warnings.Add("有 " + report.RemainingGlossaryCueCount + " 条术语候选冲突（不强制替换）");
            report.Warning = warnings.Count == 0 ? null : string.Join("；", warnings.ToArray());
        }

        private static List<SubtitleScene> BuildReviewScenes(IList<SubtitleCue> cues, AppConfig config)
        { return SrtFile.BuildScenes(cues, Math.Max(1, Math.Min(35, config.SceneMaxCues)), Math.Min(150, config.SceneMaxSeconds)); }

        internal static List<SubtitleScene> SelectRiskyScenes(IList<SubtitleCue> cues, AppConfig config, TermIndex index, List<int> layer1)
        {
            var scenes = BuildReviewScenes(cues, config);
            var defects = new HashSet<int>(layer1);
            var score = scenes.ToDictionary(s => s.Index, s => 1.0 + Enumerable.Range(s.StartCueIndex, s.EndCueIndex - s.StartCueIndex + 1)
                .Sum(i => (defects.Contains(cues[i].Id) ? 12 : 0) + (ContainsAnyTerm(cues[i].Text, index) ? 3 : 0)
                    + ((cues[i].End - cues[i].Start).TotalSeconds > 15 ? 2 : 0)));
            var selected = new List<SubtitleScene>();
            // Reserve one slot per temporal third before spending the rest on risk.
            double start = cues.Count == 0 ? 0 : cues[0].Start.TotalSeconds;
            double span = cues.Count == 0 ? 1 : Math.Max(1, cues[cues.Count - 1].End.TotalSeconds - start);
            for (int part = 0; part < 3; part++)
            {
                int bucket = part;
                var best = scenes.Where(s => Math.Min(2, (int)((cues[s.StartCueIndex].Start.TotalSeconds - start) * 3 / span)) == bucket)
                    .OrderByDescending(s => score[s.Index]).ThenBy(s => s.Index).FirstOrDefault();
                if (best != null) selected.Add(best);
            }
            while (selected.Count < Math.Min(MaxReviewScenes, scenes.Count))
            {
                var best = scenes.Where(s => !selected.Contains(s)).OrderByDescending(s => score[s.Index] /
                    (selected.Any(p => Math.Abs(p.Index - s.Index) <= 1) ? 2.0 : 1.0)).ThenBy(s => s.Index).First();
                selected.Add(best);
            }
            return selected.OrderBy(s => s.Index).ToList();
        }

        private static bool ContainsAnyTerm(string text, TermIndex index)
        {
            return index != null && !index.IsEmpty && index.Entries.Any(e => TranslationQualityCheck.ContainsTerm(text, e.Source)
                || (e.Variants != null && e.Variants.Any(v => TranslationQualityCheck.ContainsTerm(text, v))));
        }
    }
}
