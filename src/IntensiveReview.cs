using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class RefinementWindow
    {
        public string Phase { get; set; }
        public int SceneIndex { get; set; }
        public int Attempts { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public string ReferenceHash { get; set; }
        public List<int> TargetIds { get; set; }
        public List<TranslationReviewIssue> Issues { get; set; }
    }
    internal static class IntensiveReview
    {
        internal static TranslationReviewReport Run(AppConfig config, string directory, IList<SubtitleCue> cues,
            TranslationState state, string statePath, CancellationToken cancellation, string key, Action<string> progress, IntensiveTaskLedger ledger)
        {
            if (ledger == null)
            {
                using (var owned = new IntensiveTaskLedger(config, directory, new TranslationCache(directory, config, cues).VariantKey, cancellation))
                    return Run(config, directory, cues, state, statePath, owned.Token, key, progress, owned);
            }
            string file = Path.Combine(directory, "translation-quality-report.json");
            string signature = new TranslationCache(directory, config, cues).VariantKey;
            var report = AtomicJson.Read<TranslationReviewReport>(file, null);
            if (File.Exists(file) && (report == null || report.Version != "5" || report.Signature != signature || report.RefinementWindows == null
                || report.FrozenTranslations == null || report.Changes == null)) throw new InvalidDataException("精修记录损坏或策略不一致，未重新付费。");
            var scenes = SrtFile.BuildScenes(cues, Math.Max(1, Math.Min(35, config.SceneMaxCues)), Math.Min(150, config.SceneMaxSeconds));
            if (report == null)
            {
                report = new TranslationReviewReport { Version = "5", Signature = signature, Intensive = true, Phase = "refine",
                    GeneratedUtc = DateTime.UtcNow.ToString("o"), TotalCueCount = cues.Count, TotalSceneCount = scenes.Count, SelectedSceneCount = scenes.Count,
                    FrozenTranslations = new Dictionary<string, string>(state.Translations), Changes = new List<TranslationChange>(),
                    RefinementWindows = new List<RefinementWindow>(), Windows = new List<ReviewWindowRecord>(), Repairs = new List<RepairRecord>() };
                report.BaselineHash = Hash(state.Translations);
                foreach (var scene in scenes) report.RefinementWindows.Add(Window("refine", scene, cues));
                AtomicJson.Write(file, report);
            }
            if (Hash(report.FrozenTranslations) != report.BaselineHash) throw new InvalidDataException("精修冻结底稿校验失败");
            var expected = new Dictionary<string, string>(report.FrozenTranslations);
            foreach (var change in report.Changes.Where(c => c.Accepted)) expected[change.CueId.ToString(CultureInfo.InvariantCulture)] = change.After;
            // State may lag the journal, but cannot contain unrecorded edits.
            foreach (var pair in state.Translations)
            {
                string current;
                if (!expected.TryGetValue(pair.Key, out current) || (pair.Value != current && pair.Value != report.FrozenTranslations[pair.Key]
                    && !report.Changes.Any(c => c.CueId.ToString(CultureInfo.InvariantCulture) == pair.Key && (c.Before == pair.Value || c.After == pair.Value))))
                    throw new InvalidDataException("精修译文含未记录修改，停止恢复");
            }
            state.Translations = expected; AtomicJson.Write(statePath, state);
            if (report.ProcessComplete) return report;
            var references = new TranslationReferences(config, directory, cues);
            key = key ?? CredentialStore.ReadApiKey();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("精修模型密钥不可用；未发送请求，底稿已保留。");
            var client = new DeepSeekClient(QualityPolicy.ReviewConfig(config), key, config.IntensiveThinking);
            client.References = references; client.TaskLedger = ledger; client.GlossaryConflicts = state.GlossaryConflicts;
            try
            {
                references.Prepare(state.Glossary, cancellation);
                // User acknowledgement is consumed by the cumulative ledger; only then can an ambiguous window retry.
                foreach (var w in report.RefinementWindows.Where(w => w.Status == "interrupted")) w.Status = "pending";
                foreach (var w in report.RefinementWindows.Where(w => w.Status == "failed")) w.Status = "pending";
                report.Finished = false; report.BudgetExhausted = false;
                while (report.Phase != "done")
                {
                    cancellation.ThrowIfCancellationRequested(); int failures = 0;
                    foreach (var window in report.RefinementWindows.Where(w => w.Phase == report.Phase))
                    {
                        if (window.Status == "succeeded") continue;
                        ledger.BeginOperation(window.Phase + "-" + window.SceneIndex);
                        if (!ledger.CanAttempt) { window.Status = "exhausted"; failures++; continue; }
                        if (failures >= 2) break;
                        cancellation.ThrowIfCancellationRequested();
                        var scene = scenes.First(s => s.Index == window.SceneIndex);
                        var prior = report.RefinementWindows.Where(w => w.Phase == "refine" && w.SceneIndex == scene.Index).SelectMany(w => w.Issues).ToList();
                        if (report.Phase == "adjudicate") foreach (var issue in prior.Where(i => !string.IsNullOrWhiteSpace(i.SearchTerm))) references.Search(issue.SearchTerm, cancellation);
                        var input = report.Phase == "refine" ? report.FrozenTranslations : report.PhaseTranslations;
                        window.Status = "interrupted"; window.Attempts++; window.ReferenceHash = references.Snapshot;
                        AtomicJson.Write(file, report);
                        if (progress != null) progress((report.Phase == "refine" ? "全片精修" : report.Phase == "adjudicate" ? "疑难裁决" : "一致性收尾")
                            + "：场景 " + (scene.Index + 1) + "/" + scenes.Count);
                        int beforeRequests = ledger.State.Usage.RequestCount;
                        try
                        {
                            window.Issues = client.RefineWindow(cues, scene, input, state.Glossary, config.ReviewContextCount, report.Phase, prior, cancellation);
                            window.Status = "succeeded";
                            Apply(window, cues, state, report, directory);
                            AtomicJson.Write(file, report); // Commit candidates and edits atomically before state.
                            AtomicJson.Write(statePath, state); ledger.CommitResponse(); failures = 0;
                        }
                        catch (ReviewBudgetException) { window.Status = "pending"; if (ledger.State.Usage.RequestCount == beforeRequests) window.Attempts--; report.BudgetExhausted = true; throw; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            window.Status = ledger.State.UnknownRequest ? "interrupted" : "failed";
                            window.Error = ex.GetType().Name; failures++;
                            if (!ledger.State.UnknownRequest) ledger.CommitResponse();
                            if (ledger.State.UnknownRequest) break;
                        }
                        finally
                        {
                            // Client truncation retry consumes the window's second attempt too.
                            window.Attempts += Math.Max(0, ledger.State.Usage.RequestCount - beforeRequests - 1);
                            AtomicJson.Write(file, report);
                        }
                    }
                    if (report.RefinementWindows.Any(w => w.Phase == report.Phase && w.Status != "succeeded")) break;
                    if (report.Phase == "refine")
                    {
                        report.Phase = "adjudicate";
                        var risky = new HashSet<int>(report.RefinementWindows.Where(w => w.Phase == "refine" && w.Issues.Any(i => !i.NeedsSourceCheck
                            && i.Severity != "style" && !string.IsNullOrWhiteSpace(i.Candidate))) .Select(w => w.SceneIndex));
                        foreach (var scene in scenes.Where(s => risky.Contains(s.Index))) report.RefinementWindows.Add(Window("adjudicate", scene, cues));
                    }
                    else if (report.Phase == "adjudicate")
                    {
                        report.Phase = "consistency";
                        var ids = new HashSet<int>(report.Changes.Where(c => c.Accepted).Select(c => c.CueId));
                        foreach (int id in TranslationQualityCheck.FindDefectCueIds(cues, state.Translations)) ids.Add(id);
                        var index = TermIndexStore.Load(directory);
                        foreach (int id in TranslationQualityCheck.FindGlossaryDefects(cues, state.Translations, index)) ids.Add(id);
                        if (state.GlossaryConflicts != null) foreach (var cue in cues.Where(c => state.GlossaryConflicts.Keys.Any(t => QualityPolicy.Contains(c.Text,t)))) ids.Add(cue.Id);
                        foreach (var scene in scenes.Where(s => cues.Skip(s.StartCueIndex).Take(s.EndCueIndex-s.StartCueIndex+1).Any(c => ids.Contains(c.Id))))
                            report.RefinementWindows.Add(Window("consistency", scene, cues));
                    }
                    else report.Phase = "done";
                    report.PhaseTranslations = new Dictionary<string, string>(state.Translations);
                    AtomicJson.Write(file, report);
                }
            }
            catch (ReviewBudgetException) { report.BudgetExhausted = true; }
            catch (OperationCanceledException)
            {
                if (ledger.UserCancellation.IsCancellationRequested) throw;
                report.BudgetExhausted = true;
            }
            finally
            {
                ledger.Checkpoint(); var usage = ledger.State.Usage;
                report.RequestCount = usage.RequestCount; report.ResponseCount = usage.ResponseCount; report.UnknownUsageResponses = usage.UnknownUsageResponses;
                report.PromptTokens = usage.PromptTokens; report.CompletionTokens = usage.CompletionTokens; report.ReasoningTokens = usage.ReasoningTokens;
                report.ReservedOutputTokens = usage.ReservedOutputTokens; report.InputCharacters = usage.InputCharacters; report.ElapsedSeconds = usage.ElapsedSeconds;
                report.CoveredCueCount = report.RefinementWindows.Where(w => w.Phase == "refine" && w.Status == "succeeded").SelectMany(w => w.TargetIds).Distinct().Count();
                report.ReviewedSceneCount = report.RefinementWindows.Count(w => w.Phase == "refine" && w.Status == "succeeded");
                report.FixedCount = state.Translations.Count(p => report.FrozenTranslations[p.Key] != p.Value); report.RejectedCount = report.Changes.GroupBy(c => c.CueId).Count(g => !g.Last().Accepted && g.Last().Reason != "unchanged");
                report.NeedsSourceCheckCount = report.RefinementWindows.SelectMany(w => w.Issues).GroupBy(i => i.CueId).Count(g => g.Last().NeedsSourceCheck);
                report.RemainingLayer1CueCount = TranslationQualityCheck.FindDefectCueIds(cues, state.Translations).Count;
                report.SearchAttempts = references.Report.SearchAttempts; report.ReferenceWarning = references.Report.Warning;
                report.ProcessComplete = report.Phase == "done" && report.CoveredCueCount == cues.Count && report.RefinementWindows.All(w => w.Status == "succeeded");
                report.Status = report.ProcessComplete ? "full-review-complete" : "partial"; report.Finished = true;
                report.Warning = report.ProcessComplete ? null : "精修未完成，已有字幕可用；重新开始可续做未达重试上限的窗口";
                if (report.RefinementWindows.Any(w => w.Status == "exhausted")) report.Warning += "；部分窗口两次请求均失败，已停止自动重试";
                if (report.NeedsSourceCheckCount > 0 || report.RejectedCount > 0 || report.RemainingLayer1CueCount > 0)
                    report.Warning = (report.Warning == null ? "精修流程完成不代表语义全对" : report.Warning) + "；仍有需核对或未采纳的条目";
                if (!string.IsNullOrWhiteSpace(report.ReferenceWarning)) report.Warning = (report.Warning ?? "") + "；" + report.ReferenceWarning;
                AtomicJson.Write(file, report);
            }
            return report;
        }
        private static RefinementWindow Window(string phase, SubtitleScene scene, IList<SubtitleCue> cues)
        { return new RefinementWindow { Phase = phase, SceneIndex = scene.Index, Status = "pending",
            TargetIds = cues.Skip(scene.StartCueIndex).Take(scene.EndCueIndex-scene.StartCueIndex+1).Select(c => c.Id).ToList(), Issues = new List<TranslationReviewIssue>() }; }
        private static string Hash(IDictionary<string, string> translations)
        { return TranslationCache.Hash(AtomicJson.Serialize(translations.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new object[] {p.Key,p.Value}).ToArray())); }
        private static void Apply(RefinementWindow window, IList<SubtitleCue> cues, TranslationState state, TranslationReviewReport report, string directory)
        {
            var proposed = report.RefinementWindows.Where(w => w.Phase == "refine" && w.SceneIndex == window.SceneIndex)
                .SelectMany(w => w.Issues).ToList();
            foreach (var issue in window.Issues)
            {
                string id = issue.CueId.ToString(CultureInfo.InvariantCulture), before, reason = null;
                state.Translations.TryGetValue(id, out before);
                string after = issue.Candidate;
                bool actionable = !issue.NeedsSourceCheck && issue.Severity != "style" && !string.IsNullOrWhiteSpace(after);
                bool accepted = false;
                if (window.Phase == "refine") reason = actionable ? "awaiting-verification" : "not-actionable";
                else if (window.Phase == "adjudicate")
                {
                    // A second opinion may confirm the exact proposal, never invent an unverified third wording.
                    accepted = actionable && proposed.Any(p => p.CueId == issue.CueId && !p.NeedsSourceCheck
                        && p.Severity != "style" && p.Candidate == after);
                    if (!accepted) reason = "not-confirmed";
                    else accepted = TranslationQualityCheck.AcceptRepair(cues.First(c => c.Id == issue.CueId), before, after, TermIndexStore.Load(directory), out reason);
                }
                else
                {
                    // The last pass is a veto, not another rewrite pass. Even its new wording is never adopted.
                    after = report.FrozenTranslations[id];
                    accepted = !issue.NeedsSourceCheck && issue.Severity != "style" && before != after;
                    reason = accepted ? "consistency-rollback" : "consistency-report-only";
                }
                report.Changes.Add(new TranslationChange { CueId = issue.CueId, Before = before, After = after, Accepted = accepted,
                    Reason = reason ?? (accepted ? issue.Suggestion : "needs-source-check") });
                if (accepted) state.Translations[id] = after;
            }
        }
    }
}
