using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PotPlayerAiSubtitle;
using Server = TranslationClientTests.Server;

internal static class QualityTierTests
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private static string DirectoryFor(string name)
    { string dir = Path.Combine(StoragePaths.Root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(dir); return dir; }
    private static List<SubtitleCue> Cues(int count)
    { return Enumerable.Range(1, count).Select(i => new SubtitleCue { Id = i, Start = TimeSpan.FromSeconds(i * 5), End = TimeSpan.FromSeconds(i * 5 + 2), Text = "こんにちは" }).ToList(); }
    private static TranslationState State(IList<SubtitleCue> cues)
    { return new TranslationState { Translations = cues.ToDictionary(c => c.Id.ToString(), c => "你好") }; }
    private static AppConfig Config(string endpoint = "http://127.0.0.1:1")
    { var cfg = AppConfig.CreateDefault(); cfg.ApiBaseUrl = endpoint; cfg.Model = "loopback"; cfg.TranslationQuality = "quality"; cfg.EnableThinking = false; cfg.SceneMaxCues = 1; cfg.SourceLanguage = "ja"; return cfg; }
    private static string Reply(string content, string finish = "stop")
    {
        return AtomicJson.Serialize(new Dictionary<string, object> {
            { "choices", new object[] { new Dictionary<string, object> { { "finish_reason", finish },
                { "message", new Dictionary<string, object> { { "content", content } } } } } },
            { "usage", new Dictionary<string, object> { { "prompt_tokens", 100 }, { "completion_tokens", 20 } } } });
    }
    private const string Clean = "{\"issues\":[]}";
    private const string Issue = "{\"issues\":[{\"id\":1,\"kind\":\"meaning\",\"evidence\":\"こんにちは\",\"suggestion\":\"应为再见\",\"needs_source_check\":false}]}";
    private const string Translation = "{\"translations\":[{\"id\":1,\"zh\":\"你好\"}]}";
    private const string Repair = "{\"translations\":[{\"id\":1,\"zh\":\"再见\"}]}";

    private static void RulesAndSelection()
    {
        var cues = Cues(30); var state = State(cues);
        state.Translations.Remove("2"); state.Translations["3"] = " ";
        Check(TranslationQualityCheck.FindDefectCueIds(cues, state.Translations).SequenceEqual(new[] { 2, 3 }), "Missing/blank cues escaped checking");
        Check(!TranslationQualityCheck.ContainsTerm("annual Anna", "Ann"), "Latin substring false positive");
        Check(TranslationQualityCheck.ContainsTerm("ANN's home", "Ann"), "Latin boundary/case false negative");
        string reason;
        Check(!TranslationQualityCheck.AcceptRepair(cues[0], "你好", "こんにちは", null, out reason), "Source leakage accepted");
        Check(!TranslationQualityCheck.AcceptRepair(cues[0], "你好", "", null, out reason), "Empty repair accepted");
        Check(!TranslationQualityCheck.AcceptRepair(cues[0], "你好", "你好", null, out reason) && reason == "unchanged", "Unchanged counted as fixed");
        Check(TranslationQualityCheck.AcceptRepair(cues[0], "こんにちは", "你好", null, out reason), "Valid repair rejected");
        var selected = TranslationReview.SelectRiskyScenes(cues, Config(), null, new List<int> { 29, 30 });
        Check(selected.Count == 6 && selected.Any(s => s.Index >= 28), "Late-film risk missed");
        Check(selected.Any(s => s.Index < 10) && selected.Any(s => s.Index >= 10 && s.Index < 20), "Temporal coverage missing");
        Console.WriteLine("PASS deterministic defects, repair guard, term boundaries, late-film risk and temporal coverage");
    }

    private static void CacheIdentity()
    {
        var dir = DirectoryFor("identity"); var cues = Cues(1); var cfg = Config(); cfg.TranslationQuality = "fast";
        var fast = new TranslationCache(dir, cfg, cues); cfg.EnableThinking = true;
        Check(fast.VariantKey == new TranslationCache(dir, cfg, cues).VariantKey, "Disabled thinking invalidated fast cache");
        cfg.TranslationQuality = "quality"; var quality = new TranslationCache(dir, cfg, cues);
        Check(fast.BaseKey == quality.BaseKey && fast.VariantKey != quality.VariantKey, "Tier cache identity wrong");
        cfg.EnableThinking = false; var off = new TranslationCache(dir, cfg, cues);
        Check(off.BaseKey == quality.BaseKey && off.VariantKey != quality.VariantKey, "Review thinking does not isolate variants");
        cfg.ReviewContextCount++; var context = new TranslationCache(dir, cfg, cues);
        Check(context.BaseKey == off.BaseKey && context.VariantKey != off.VariantKey, "Review context invalidation wrong");
        TermIndexStore.Save(dir, new TermIndex { Entries = new List<TermIndexEntry> { new TermIndexEntry { Source = "Ann", Target = "安" } } });
        var terms = new TranslationCache(dir, cfg, cues);
        Check(terms.BaseKey == context.BaseKey && terms.VariantKey != context.VariantKey, "Terms invalidation wrong");
        cfg.Model = "new-model"; Check(terms.BaseKey != new TranslationCache(dir, cfg, cues).BaseKey, "Model cache reused");
        cfg.Model = "loopback"; cfg.ContextCueCount++; Check(terms.BaseKey != new TranslationCache(dir, cfg, cues).BaseKey, "Base context cache reused");
        cfg.ContextCueCount--; cfg.ApiBaseUrl += "/other"; Check(terms.BaseKey != new TranslationCache(dir, cfg, cues).BaseKey, "Endpoint cache reused");
        cfg.ApiBaseUrl = "http://127.0.0.1:1"; cues[0].Text = "changed";
        Check(terms.BaseKey != new TranslationCache(dir, cfg, cues).BaseKey, "Source cache reused");
        Console.WriteLine("PASS stage identities isolate source/model/endpoint/context/terms/effective-thinking without repaying base translation");
    }

    private static void ReviewSuccessAndJournal()
    {
        using (var server = new Server(Reply(Issue), Reply(Repair)))
        {
            var cfg = Config(server.Url); cfg.EnableThinking = true; var dir = DirectoryFor("journal"); var cues = Cues(1); var state = State(cues); string statePath = Path.Combine(dir, "state.json");
            var report = TranslationReview.Run(cfg, dir, cues, state, statePath, CancellationToken.None, "fake-key");
            server.Complete();
            Check(report.ReviewedSceneCount == 1 && report.FixedCount == 1 && state.Translations["1"] == "再见", "Review/repair failed");
            Check(report.Changes[0].Before == "你好" && report.Changes[0].After == "再见" && report.Changes[0].Accepted, "Audit lost original");
            Check(report.RequestCount == 2 && report.PromptTokens == 200 && report.CompletionTokens == 40, "Usage accounting wrong");
            Check((string)((Dictionary<string, object>)server.Requests[0]["thinking"])["type"] == "enabled", "Review thinking off");
            Check((string)((Dictionary<string, object>)server.Requests[1]["thinking"])["type"] == "disabled", "Repair thinking wasted cost");
            // Simulate crash before the accepted state write; replay the durable journal without any HTTP.
            var restored = State(cues);
            var resumed = TranslationReview.Run(cfg, dir, cues, restored, statePath, CancellationToken.None, "fake-key");
            Check(restored.Translations["1"] == "再见" && resumed.RequestCount == 2, "Journal replay repaid or lost edit");
        }
        using (var server = new Server(Reply(Issue), Reply("{\"translations\":[{\"id\":1,\"zh\":\"こんにちは\"}]}")))
        {
            var dir = DirectoryFor("reject"); var cues = Cues(1); var state = State(cues);
            var report = TranslationReview.Run(Config(server.Url), dir, cues, state, Path.Combine(dir, "state.json"), CancellationToken.None, "fake-key"); server.Complete();
            Check(report.FixedCount == 0 && report.RejectedCount == 1 && report.UnresolvedSuggestionCount == 1 && state.Translations["1"] == "你好", "Unsafe edit applied or hidden");
        }
        Console.WriteLine("PASS thinking scoped to review, actual usage accounting, accepted/rejected audit and free journal replay");
    }

    private static void FailuresAndBudgets()
    {
        using (var server = new Server(Reply("not-json"), Reply("not-json")))
        {
            var cues = Cues(10); var dir = DirectoryFor("failure"); var state = State(cues);
            var report = TranslationReview.Run(Config(server.Url), dir, cues, state, Path.Combine(dir, "state.json"), CancellationToken.None, "fake-key"); server.Complete();
            Check(report.ReviewedSceneCount == 0 && report.FailedSceneCount == 2 && report.SkippedSceneCount == 4 && report.RequestCount == 2, "Failed reviews counted as success or circuit breaker absent");
            Check(report.Status == "partial" && !string.IsNullOrWhiteSpace(report.Warning), "Degradation invisible");
            Check(state.Translations.All(p => p.Value == "你好"), "Failure damaged baseline");
        }
        var ledger = new TranslationReviewReport { RequestLimit = 1, OutputTokenLimit = 4096, TimeLimitSeconds = 2 };
        var budget = new ReviewBudget(ledger, delegate { }); budget.Reserve(4096, 100);
        Throws<ReviewBudgetException>(() => budget.Reserve(1, 1)); Check(ledger.RequestCount == 1, "Reservation exceeded limit");
        using (var server = new Server(Reply("", "length")))
        {
            var cfg = Config(server.Url); var cues = Cues(1);
            var report = new TranslationReviewReport { RequestLimit = 10, OutputTokenLimit = 4096, TimeLimitSeconds = 10 };
            var client = new DeepSeekClient(cfg, "fake-key", false, new ReviewBudget(report, delegate { }));
            Throws<ReviewBudgetException>(() => client.ReviewWindow(cues, new SubtitleScene { StartCueIndex = 0, EndCueIndex = 0 }, State(cues).Translations, new Dictionary<string, string>(), 0, CancellationToken.None));
            server.Complete(); Check(report.RequestCount == 1, "Length retry escaped total budget");
        }
        using (var server = new Server((string)null))
        {
            var cfg = Config(server.Url); var cues = Cues(1);
            var report = new TranslationReviewReport { RequestLimit = 10, OutputTokenLimit = 49152, TimeLimitSeconds = 1 };
            var client = new DeepSeekClient(cfg, "fake-key", false, new ReviewBudget(report, delegate { }));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Throws<ReviewBudgetException>(() => client.ReviewWindow(cues, new SubtitleScene { StartCueIndex = 0, EndCueIndex = 0 }, State(cues).Translations, new Dictionary<string, string>(), 0, CancellationToken.None));
            Check(watch.Elapsed.TotalSeconds < 3, "Deadline failed to abort active HTTP");
        }
        var missingDir = DirectoryFor("missing-key"); var missingCues = Cues(1);
        var missing = TranslationReview.Run(Config(), missingDir, missingCues, State(missingCues), Path.Combine(missingDir, "state.json"), CancellationToken.None, "");
        Check(missing.RequestCount == 0 && missing.SkippedSceneCount == 1 && missing.Status == "partial", "Missing key misreported");
        Console.WriteLine("PASS honest failures, outage circuit breaker, missing-key degradation, retry reservation and in-flight deadline");
    }

    private static void SourceGuardAndRecovery()
    {
        foreach (string uncertain in new[] {
            "{\"issues\":[{\"id\":1,\"suggestion\":\"疑为误听，建议改成再见\",\"needs_source_check\":false}]}",
            "{\"issues\":[{\"id\":1,\"suggestion\":\"改成再见\"}]}" })
        using (var server = new Server(Reply(uncertain)))
        {
            var dir = DirectoryFor("source-guard"); var cues = Cues(1); var state = State(cues);
            var report = TranslationReview.Run(Config(server.Url), dir, cues, state, Path.Combine(dir, "state.json"), CancellationToken.None, "fake-key"); server.Complete();
            Check(report.NeedsSourceCheckCount == 1 && report.RequestCount == 1 && report.FixedCount == 0 && state.Translations["1"] == "你好", "Uncertain source triggered speculative repair");
        }
        using (var server = new Server(Reply("{\"issues\":[{}]}"), Reply(Clean)))
        {
            var dir = DirectoryFor("recover"); var cues = Cues(1); var state = State(cues); var cfg = Config(server.Url); string file = Path.Combine(dir, "state.json");
            var first = TranslationReview.Run(cfg, dir, cues, state, file, CancellationToken.None, "fake-key");
            Check(first.ReviewedSceneCount == 0 && first.CanResume && first.RequestCount == 1, "Malformed row silently passed");
            var second = TranslationReview.Run(cfg, dir, cues, state, file, CancellationToken.None, "fake-key"); server.Complete();
            Check(second.ReviewedSceneCount == 1 && second.RequestCount == 2 && !second.CanResume && second.Windows[0].AttemptCount == 2, "Explicit resume resets budget or misses recovery");
        }
        using (var server = new Server(Reply(Translation), Reply("bad"), Reply(Clean)))
        {
            var cfg = Config(server.Url); cfg.CopyFinishedSubtitlesBesideMedia = false; cfg.SubtitleHubPath = DirectoryFor("recovery-hub");
            string media = Path.Combine(DirectoryFor("recovery-media"), "sample.mp4"); File.WriteAllText(media, Guid.NewGuid().ToString());
            string cacheDir = Path.Combine(StoragePaths.Cache, ContentFingerprint.Compute(media)); Directory.CreateDirectory(cacheDir);
            var cues = Cues(1); SrtFile.Write(Path.Combine(cacheDir, "ja.srt"), cues, null, false);
            var runner = new SubtitlePipelineRunner(cfg, null, () => true, () => "fake-key", message => true);
            Check(!runner.Process(media, CancellationToken.None).CacheHit, "Initial run unexpectedly cached");
            Check(!runner.Process(media, CancellationToken.None).CacheHit && server.Requests.Count == 3, "Failed-window rerun repaid base or blocked by receipt");
            Check(runner.Process(media, CancellationToken.None).CacheHit, "Recovered result did not commit");
            server.Complete();
        }
        Console.WriteLine("PASS source uncertainty blocks guesses, malformed review fails honestly and explicit retry retains cumulative budgets");
    }

    private static void CancelResume()
    {
        using (var server = new Server((string)null))
        using (var cancel = new CancellationTokenSource())
        {
            var cfg = Config(server.Url); var cues = Cues(1); var state = State(cues); var dir = DirectoryFor("cancel"); string statePath = Path.Combine(dir, "state.json");
            var thread = new Thread(delegate() { server.Received.WaitOne(5000); cancel.Cancel(); }); thread.Start();
            Throws<OperationCanceledException>(() => TranslationReview.Run(cfg, dir, cues, state, statePath, cancel.Token, "fake-key")); thread.Join();
            var report = AtomicJson.Read<TranslationReviewReport>(Path.Combine(dir, "translation-quality-report.json"), null);
            Check(report != null && !report.Finished && report.RequestCount == 1 && report.Windows[0].Status == "interrupted", "Cancellation did not checkpoint");
            var resumed = TranslationReview.Run(cfg, dir, cues, state, statePath, CancellationToken.None, "fake-key");
            Check(resumed.RequestCount == 1 && resumed.ReviewedSceneCount == 0 && resumed.Status == "partial", "Ambiguous paid request repeated");
        }
        Console.WriteLine("PASS cancellation preserves caller semantics and does not silently repay interrupted requests");
    }

    private static void PipelineCache()
    {
        using (var server = new Server(Reply(Translation), Reply(Clean), Reply(Clean), Reply(Translation), Reply(Clean)))
        {
            var cfg = Config(server.Url); cfg.TranslationQuality = "fast"; cfg.CopyFinishedSubtitlesBesideMedia = true;
            string dir = DirectoryFor("pipeline"); cfg.SubtitleHubPath = Path.Combine(dir, "hub");
            string media = Path.Combine(dir, "sample.mp4"); File.WriteAllText(media, "fake-media-" + Guid.NewGuid());
            string cacheDir = Path.Combine(StoragePaths.Cache, ContentFingerprint.Compute(media)); Directory.CreateDirectory(cacheDir);
            var cues = Cues(1); SrtFile.Write(Path.Combine(cacheDir, "ja.srt"), cues, null, false);
            File.WriteAllText(Path.Combine(cacheDir, "zh-CN.srt"), "legacy-do-not-touch"); File.WriteAllText(Path.Combine(cacheDir, "ja-zh-CN.srt"), "legacy-do-not-touch");
            AtomicJson.Write(Path.Combine(cacheDir, "manifest.json"), new JobManifest { RecognitionModel = "original-asr" });
            var runner = new SubtitlePipelineRunner(cfg, null, () => true, () => "fake-key", message => true);
            var fast = runner.Process(media, CancellationToken.None); Check(!fast.CacheHit && server.Requests.Count == 1, "Legacy translation wrongly reused");
            cfg.TranslationQuality = "quality";
            var quality = runner.Process(media, CancellationToken.None); Check(!quality.CacheHit && server.Requests.Count == 2, "Fast-to-quality no-op or repaid base");
            Check(!string.IsNullOrWhiteSpace(quality.QualitySummary), "Quality summary missing");
            Check(runner.Process(media, CancellationToken.None).CacheHit && server.Requests.Count == 2, "Valid quality cache missed");
            cfg.EnableThinking = true; Check(!runner.Process(media, CancellationToken.None).CacheHit && server.Requests.Count == 3, "Thinking variant wrongly reused");
            Check((string)((Dictionary<string, object>)server.Requests[0]["thinking"])["type"] == "disabled", "Base used thinking");
            cfg.Model = "changed-model"; Check(!runner.Process(media, CancellationToken.None).CacheHit && server.Requests.Count == 5, "Model change reused base");
            server.Complete();
            Check(File.ReadAllText(Path.Combine(cacheDir, "zh-CN.srt")) == "legacy-do-not-touch", "Legacy cache overwritten");
            Check(AtomicJson.Read<JobManifest>(Path.Combine(cacheDir, "manifest.json"), null).RecognitionModel == "original-asr", "Recognition provenance falsified");
            File.WriteAllText(Path.Combine(dir, "sample.srt"), "user-subtitle-do-not-touch");
            var hit = runner.Process(media, CancellationToken.None);
            Check(hit.CacheHit && !string.IsNullOrWhiteSpace(hit.PublishWarning) && File.ReadAllText(Path.Combine(dir, "sample.srt")) == "user-subtitle-do-not-touch", "User sidecar overwritten");
            var cache = new TranslationCache(cacheDir, cfg, cues);
            File.WriteAllText(Path.Combine(cache.DirectoryPath, "zh-CN.srt"), "corrupt-output");
            Check(!runner.Process(media, CancellationToken.None).CacheHit && server.Requests.Count == 5, "Torn output not rebuilt locally");
            File.WriteAllText(cache.StatePath, "{}");
            Throws<InvalidDataException>(() => runner.Process(media, CancellationToken.None));
        }
        Console.WriteLine("PASS real pipeline cache switching/resume, base sharing, legacy preservation, source provenance, owned/user sidecars and corruption checks");
    }

    private static void LanguageVariants()
    {
        foreach (string language in new[] { "en", "ko" })
        using (var server = new Server(Reply(Translation), Reply(Clean)))
        {
            var cfg = Config(server.Url); cfg.SourceLanguage = language; cfg.EnableThinking = true;
            cfg.SubtitleHubPath = DirectoryFor("language-hub"); cfg.CopyFinishedSubtitlesBesideMedia = true;
            string media = Path.Combine(DirectoryFor("language-media"), "sample.mp4"); File.WriteAllText(media, Guid.NewGuid().ToString());
            string videoCache = Path.Combine(StoragePaths.Cache, ContentFingerprint.Compute(media));
            string cacheDir = SourceLanguages.CacheDirectory(videoCache, language); Directory.CreateDirectory(cacheDir);
            var cues = Cues(1); cues[0].Text = language == "en" ? "Hello" : "안녕하세요";
            SrtFile.Write(Path.Combine(cacheDir, language + ".srt"), cues, null, false);
            var runner = new SubtitlePipelineRunner(cfg, null, () => true, () => "fake-key", message => true);
            var result = runner.Process(media, CancellationToken.None); server.Complete();
            Check(!result.CacheHit && File.Exists(result.BilingualSubtitlePath), "Non-Japanese variant failed");
            Check(SubtitlePublisher.IsGeneratedBilingual(result.BilingualSubtitlePath, videoCache), "Nested non-Japanese ownership not recognized");
            Check(runner.Process(media, CancellationToken.None).CacheHit, "Non-Japanese cache not reusable");
            Check((string)((Dictionary<string, object>)server.Requests[0]["thinking"])["type"] == "disabled", "Quality thinking leaked into base translation");
        }
        Console.WriteLine("PASS English/Korean nested cache, publication ownership and nonthinking base under quality thinking preference");
    }

    public static int Main()
    {
        try { StoragePaths.Ensure(); RulesAndSelection(); CacheIdentity(); ReviewSuccessAndJournal(); FailuresAndBudgets(); SourceGuardAndRecovery(); CancelResume(); PipelineCache(); LanguageVariants(); Console.WriteLine("PASS " + checks + " quality-tier assertions"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
