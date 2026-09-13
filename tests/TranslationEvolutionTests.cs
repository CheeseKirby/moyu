using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using PotPlayerAiSubtitle;
using Server = TranslationClientTests.Server;

internal static class TranslationEvolutionTests
{
    private static int checks;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch(T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private static string Dir() { string d = Path.Combine(StoragePaths.Root, "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static List<SubtitleCue> Cues(int n) { return Enumerable.Range(1,n).Select(i => new SubtitleCue { Id=i, Start=TimeSpan.FromSeconds(i*5), End=TimeSpan.FromSeconds(i*5+2), Text="I did not call Alice." }).ToList(); }
    private static TranslationState State(IList<SubtitleCue> cues) { return new TranslationState { Translations=cues.ToDictionary(c=>c.Id.ToString(), c=>"我给爱丽丝打了电话。") }; }
    private static AppConfig Config(string url) { var c=AppConfig.CreateDefault(); c.ApiBaseUrl=url; c.Model="test"; c.SourceLanguage="en"; c.TranslationQuality="quality"; c.QualityMode="intensive"; c.SceneMaxCues=1; return c; }
    private static string Reply(string content) { return AtomicJson.Serialize(new Dictionary<string,object> { {"choices",new object[]{new Dictionary<string,object>{{"finish_reason","stop"},{"message",new Dictionary<string,object>{{"content",content}}}}}}, {"usage",new Dictionary<string,object>{{"prompt_tokens",100},{"completion_tokens",20}}} }); }
    private const string Clean="{\"issues\":[]}";
    private const string Edit="{\"issues\":[{\"id\":1,\"kind\":\"meaning\",\"severity\":\"meaning\",\"evidence\":\"not call\",\"suggestion\":\"保留否定\",\"candidate\":\"我没有给爱丽丝打电话。\",\"needs_source_check\":false,\"reference_ids\":[]}]}";
    private static TranslationReviewReport Run(AppConfig cfg,string dir,List<SubtitleCue> cues,TranslationState state)
    { return TranslationReview.Run(cfg,dir,cues,state,Path.Combine(dir,"state.json"),CancellationToken.None,"fake-key"); }
    public static int Main()
    {
        try
        {
            StoragePaths.Ensure();
            var cues=Cues(8);var state=State(cues);var cfg=Config("http://127.0.0.1:1");
            var scene=new SubtitleScene {Index=1,StartCueIndex=4,EndCueIndex=7};
            var client=new DeepSeekClient(cfg,"fake");
            state.Glossary["Alice"]="爱丽丝"; state.Glossary["Bob"]="鲍勃";
            string prompt=client.BuildUserPrompt(cues,scene,2,state.Glossary,state.Translations);
            Check(prompt.Contains("爱丽丝")&&!prompt.Contains("鲍勃"),"Irrelevant glossary not filtered");
            var request=(Dictionary<string,object>)AtomicJson.DeserializeObject(prompt);
            Check(((object[])request["preceding_translation_reference"]).Length==2,"Context count expanded");
            prompt=client.BuildUserPrompt(cues,scene,0,state.Glossary,state.Translations);
            request=(Dictionary<string,object>)AtomicJson.DeserializeObject(prompt);
            Check(((object[])request["preceding_translation_reference"]).Length==0,"Zero context ignored");
            Check(!QualityPolicy.Contains("malice","Alice")&&QualityPolicy.Contains("Alice!","alice"),"Term boundary/case broken");
            QualityPolicy.MergeGlossary(state,new Dictionary<string,string>{{"Alice","艾丽斯"}});
            Check(!state.Glossary.ContainsKey("Alice")&&state.GlossaryConflicts["Alice"].Count==2,"Conflicting term forced");
            QualityPolicy.MergeGlossary(state,new Dictionary<string,string>{{"Alice","艾莉丝"}});
            Check(state.GlossaryConflicts["Alice"].Count==3&&!state.Glossary.ContainsKey("Alice"),"Conflict resurrected");
            var copy=TranslationCache.Copy(state);copy.GlossaryConflicts["Alice"].Add("other");
            Check(state.GlossaryConflicts["Alice"].Count==3,"Copy shares mutable conflicts");
            cfg.TranslationQuality="fast";cfg.QualityMode="intensive";AppConfig.Save(cfg);
            Check(AppConfig.Load().QualityMode=="intensive","Fast switch clears intensive preference");
            cfg.UiSettingsVersion=3;cfg.TranslationQuality="quality";cfg.EnableThinking=true;cfg.EnableWebReference=true;AppConfig.Save(cfg);
            var migrated=AppConfig.Load();Check(migrated.QualityMode=="standard"&&migrated.EnableThinking&&migrated.IntensiveThinking&&!migrated.EnableWebReference,"Unsafe migration");
            Console.WriteLine("PASS base context, glossary conflict isolation, and migration");

            using(var server=new Server(Enumerable.Repeat(Reply(Clean),8).ToArray()))
            {
                cfg=Config(server.Url);var dir=Dir();state=State(cues);
                var report=Run(cfg,dir,cues,state);server.Complete();
                Check(report.ProcessComplete&&report.CoveredCueCount==8&&report.RefinementWindows.Count==8,"Not full coverage or unnecessary extra rounds");
                Check(report.RequestCount==8&&report.PromptTokens==800,"Actual usage missing");
                Check(server.Requests.All(r=>(string)((Dictionary<string,object>)r["thinking"])["type"]=="enabled"),"Intensive thinking not enabled");
                var replay=Run(cfg,dir,cues,state);Check(replay.RequestCount==8&&replay.ProcessComplete,"Completed refinement repaid");
            }
            using(var server=new Server(Reply(Edit),Reply(Edit),Reply(Clean)))
            {
                cfg=Config(server.Url);var dir=Dir();var one=Cues(1);state=State(one);
                var report=Run(cfg,dir,one,state);server.Complete();
                Check(report.ProcessComplete&&state.Translations["1"]=="我没有给爱丽丝打电话。","Supported edit missing");
                Check(report.RefinementWindows.Select(w=>w.Phase).SequenceEqual(new[]{"refine","adjudicate","consistency"}),"Missing bounded consistency phase");
                Check(report.FrozenTranslations["1"]=="我给爱丽丝打了电话。","Base mutated");
                var restored=State(one);Run(cfg,dir,one,restored);Check(restored.Translations["1"]==state.Translations["1"],"Journal not replayed");
            }
            foreach(var unsafeEdit in new[]{Edit.Replace("not call","fabricated evidence"),Edit.Replace("\"needs_source_check\":false","\"needs_source_check\":true"),Edit.Replace("\"severity\":\"meaning\"","\"severity\":\"style\"")})
            using(var server=new Server(Reply(unsafeEdit)))
            {
                var one=Cues(1);state=State(one);var report=Run(Config(server.Url),Dir(),one,state);server.Complete();
                Check(state.Translations["1"]=="我给爱丽丝打了电话。"&&report.FixedCount==0,"Unsupported/style edit accepted");
            }
            using(var server=new Server(Reply(Edit.Replace("\"candidate\":\"我没有给爱丽丝打电话。\"","\"candidate\":\"\""))))
            {
                var one=Cues(1);state=State(one);var report=Run(Config(server.Url),Dir(),one,state);server.Complete();
                Check(report.ProcessComplete&&report.RefinementWindows.Count==1&&report.FixedCount==0,"Empty candidate led to unverified rewrite");
            }
            using(var server=new Server(Reply(Edit.Replace("保留否定","方向正确但可更自然"))))
            {
                // Assert against the real parser rather than trusting a model severity label.
                var preferenceClient=new DeepSeekClient(Config(server.Url),"fake",true);
                var one=Cues(1); var result=preferenceClient.RefineWindow(one,new SubtitleScene{Index=0,StartCueIndex=0,EndCueIndex=0},State(one).Translations,new Dictionary<string,string>(),0,"refine",new List<TranslationReviewIssue>(),CancellationToken.None); server.Complete();
                Check(result.Count==1 && result[0].Severity=="style","Style preference mislabeled as meaning was not blocked");
            }
            foreach(var verdict in new[]{Clean, Edit.Replace("我没有给爱丽丝打电话。","我没打过电话。")})
            using(var server=new Server(Reply(Edit),Reply(verdict)))
            {
                var one=Cues(1);state=State(one);var report=Run(Config(server.Url),Dir(),one,state);server.Complete();
                Check(report.ProcessComplete&&report.FixedCount==0&&state.Translations["1"]==report.FrozenTranslations["1"],"Rejected or different candidate accepted");
                Check(!AtomicJson.Serialize(server.Requests[1]).Contains("保留否定"),"Proposer rationale leaked into independent verification");
            }
            using(var server=new Server(Reply(Edit),Reply(Edit),Reply(Edit.Replace("我没有给爱丽丝打电话。","新造的译文。"))))
            {
                var one=Cues(1);state=State(one);var dir=Dir();var report=Run(Config(server.Url),dir,one,state);server.Complete();
                Check(report.ProcessComplete&&report.FixedCount==0&&state.Translations["1"]==report.FrozenTranslations["1"],"Consistency invented a rewrite or failed rollback");
                Check(report.Changes.Last().Reason=="consistency-rollback","Rollback missing from journal");
                var restored=State(one);Run(Config(server.Url),dir,one,restored);Check(restored.Translations["1"]==state.Translations["1"],"Rollback not replayed");
            }
            using(var server=new Server(Reply(Edit)))
            {
                cfg=Config(server.Url);cfg.IntensiveRequestLimit=1;var one=Cues(1);state=State(one);var report=Run(cfg,Dir(),one,state);server.Complete();
                Check(!report.ProcessComplete&&report.FixedCount==0&&state.Translations["1"]==report.FrozenTranslations["1"],"Unverified candidate exposed on budget stop");
            }
            using(var server=new Server(Reply(Edit),Reply(Edit),Reply(Edit.Replace("\"id\":1","\"id\":2"))))
            {
                cfg=Config(server.Url);cfg.SceneMaxCues=2;var two=Cues(2);state=State(two);var report=Run(cfg,Dir(),two,state);server.Complete();
                Check(report.FixedCount==1&&state.Translations["2"]==report.FrozenTranslations["2"],"Closing pass modified a previously unchanged cue");
                Check(report.Changes.Last().Reason=="consistency-report-only","New closing-pass concern was not retained");
            }
            using(var server=new Server(Reply(Edit),Reply(Edit),Reply(Clean)))
            {
                cfg=Config(server.Url);cfg.IntensiveRequestLimit=1;var dir=Dir();var one=Cues(1);state=State(one);Run(cfg,dir,one,state);
                cfg.IntensiveRequestLimit=3;var report=Run(cfg,dir,one,state);server.Complete();
                Check(report.ProcessComplete&&report.RequestCount==3&&report.FixedCount==1,"Candidate verification could not resume within cumulative budget");
            }
            using(var server=new Server(Reply(Edit.Replace("\"reference_ids\":[]","\"search_term\":\"not call\",\"reference_ids\":[]")),Reply(Edit),Reply(Clean)))
            {
                var one=Cues(1);state=State(one);var report=Run(Config(server.Url),Dir(),one,state);server.Complete();
                Check(report.FixedCount==1,"Optional search request prevented source-only confirmation without a search key");
            }
            using(var server=new Server(Reply(Edit),Reply(Edit.Replace("\"reference_ids\":[]","\"search_term\":\"not call\",\"reference_ids\":[]"))))
            {
                var one=Cues(1);state=State(one);var report=Run(Config(server.Url),Dir(),one,state);server.Complete();
                Check(report.FixedCount==0&&report.NeedsSourceCheckCount==1,"Unresolved external question accepted without evidence");
            }
            Console.WriteLine("PASS whole-film coverage, evidence guards, three phases, immutable base and free replay");

            using(var server=new Server(Reply(Clean),Reply(Clean),Reply(Clean)))
            {
                cfg=Config(server.Url);cfg.IntensiveRequestLimit=1;var dir=Dir();var three=Cues(3);state=State(three);
                var partial=Run(cfg,dir,three,state);
                Check(!partial.ProcessComplete&&partial.CoveredCueCount==1&&partial.BudgetExhausted,"Budget overspent or falsely complete");
                cfg.IntensiveRequestLimit=3;var complete=Run(cfg,dir,three,state);server.Complete();
                Check(complete.ProcessComplete&&complete.RequestCount==3,"Increasing budget reset counters or failed resume");
            }
            var ledgerDir=Dir();cfg=Config("http://127.0.0.1:1");
            using(var ledger=new IntensiveTaskLedger(cfg,ledgerDir,"fixed",CancellationToken.None)) {ledger.Reserve(4096,500);}
            Throws<InvalidDataException>(()=>{using(var l=new IntensiveTaskLedger(cfg,ledgerDir,"fixed",CancellationToken.None)) {}});
            using(var ledger=new IntensiveTaskLedger(cfg,ledgerDir,"fixed",CancellationToken.None,m=>true))
            {Check(ledger.State.UnknownRetryConsents==1&&ledger.State.Usage.RequestCount==1,"Unknown retry clears spend");ledger.Reserve(4096,500);ledger.Observe(Reply(Clean),"request");}
            using(var ledger=new IntensiveTaskLedger(cfg,ledgerDir,"fixed",CancellationToken.None))
            {string response;Check(ledger.TryReplay("request",out response)&&ledger.State.Usage.RequestCount==2,"Durable response lost");ledger.CommitResponse();}
            var cappedDir=Dir();
            using(var ledger=new IntensiveTaskLedger(cfg,cappedDir,"cap",CancellationToken.None))
            { ledger.BeginOperation("window"); ledger.Reserve(4096,100); ledger.KnownFailure(); ledger.Reserve(8192,100); ledger.Observe(Reply(Clean),"cached"); }
            using(var ledger=new IntensiveTaskLedger(cfg,cappedDir,"cap",CancellationToken.None))
            {
                ledger.BeginOperation("window"); Check(ledger.CanAttempt,"Durable response blocked by retry limit");
                string response; Check(ledger.TryReplay("cached",out response),"Paid response not replayable");
                ledger.CommitResponse(); Check(!ledger.CanAttempt,"Restart reset per-window attempts");
                Throws<InvalidOperationException>(()=>ledger.Reserve(4096,100));
                Check(ledger.State.Usage.RequestCount==2,"Rejected retry charged budget");
                ledger.BeginOperation("other"); Check(ledger.CanAttempt,"Independent window blocked");
            }
            Console.WriteLine("PASS resource caps, cumulative resume, durable operation limits and explicit ambiguous-response consent");

            foreach(string ip in new[]{"127.0.0.1","10.1.2.3","172.16.0.1","192.168.1.2","169.254.169.254","100.64.1.1","::1","fc00::1","fe80::1","::ffff:127.0.0.1"})
                Check(!SafeReferenceHttp.PublicAddress(IPAddress.Parse(ip)),"Private destination allowed: "+ip);
            Check(SafeReferenceHttp.PublicAddress(IPAddress.Parse("8.8.8.8")),"Public IP rejected");
            Throws<InvalidDataException>(()=>SafeReferenceHttp.Validate(new Uri("file:///C:/test")));
            Throws<InvalidDataException>(()=>SafeReferenceHttp.Validate(new Uri("https://user:pass@example.com/")));
            Check(!TranslationReferences.Extract("<script>steal secret</script><p>visible</p>").Contains("steal"),"Script extraction unsafe");
            var reference=Cues(1);reference[0].Text="已有中文译法";
            Check(TranslationReferences.Align(Cues(1),reference).Count==1,"Aligned subtitle not used as hint");
            reference[0].Start+=TimeSpan.FromSeconds(40);reference[0].End+=TimeSpan.FromSeconds(40);
            Check(TranslationReferences.Align(Cues(1),reference).Count==0,"Wrong timeline accepted");
            cfg=Config("http://127.0.0.1:1");cfg.EnableWebReference=true;cfg.WebPrivacyAccepted=true;
            var refs=new TranslationReferences(cfg,Dir(),Cues(1));refs.ReadKey=()=>"secret";int calls=0;
            refs.Fetch=(url,key,ct)=>{calls++;if(key!=null)return "{\"web\":{\"results\":[{\"url\":\"https://example.com/dictionary\"}]}}";return "<p>Alice is a proper name. This is a sufficiently long dictionary entry for testing.</p>";};
            refs.Search("Alice",CancellationToken.None);refs.Search("Alice",CancellationToken.None);
            Check(refs.Report.SearchAttempts==1&&calls==2&&refs.Report.Evidence.Count==1,"Search not bounded/cached or evidence absent");
            refs.Search("not-in-source",CancellationToken.None);Check(calls==2,"Unrelated model query allowed");
            cfg.WebPrivacyAccepted=false;refs=new TranslationReferences(cfg,Dir(),Cues(1));refs.Fetch=(u,k,c)=>{throw new Exception("Must not access network");};
            refs.Search("Alice",CancellationToken.None);Check(refs.Report.SearchAttempts==0,"No-consent query sent");
            Console.WriteLine("PASS controlled references, consent, duplicate suppression, subtitle alignment and network safety");
            Console.WriteLine("PASS "+checks+" evolution assertions");return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}
    }
}
