using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PotPlayerAiSubtitle;

// Manual paid validation only. Not included in run-all-tests; artifacts contain private subtitle text.
internal static class LiveTranslationValidation
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 7 || args[0] != "--confirm-paid") return 2;
            var config = AtomicJson.Read<AppConfig>(args[1], null);
            if (config == null) throw new InvalidDataException("Configuration unavailable");
            string mode=args[2], source=args[3], baseline=args[4], output=args[5];
            config.SourceLanguage=SourceLanguages.Normalize(args[6]);
            Directory.CreateDirectory(output); StoragePaths.Ensure();
            var cues=SrtFile.Read(source); if(cues.Count==0) throw new InvalidDataException("Empty source");
            string key=CredentialStore.ReadApiKey(); if(string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Model credential missing");
            var timer=Stopwatch.StartNew();
            if(mode=="base")
            {
                config.TranslationQuality="fast";
                if(cues.Count>35) throw new InvalidDataException("Base probe limited to 35 cues");
                var state=AtomicJson.Read<TranslationState>(baseline,new TranslationState());
                var scene=new SubtitleScene {Index=0,StartCueIndex=Math.Min(2,cues.Count-1),EndCueIndex=cues.Count-1};
                var client=new DeepSeekClient(config,key,false);
#if LEGACY
                var result=client.TranslateScene(cues,scene,config.ContextCueCount,state.Glossary,CancellationToken.None);
#else
                var result=client.TranslateScene(cues,scene,config.ContextCueCount,state.Glossary,CancellationToken.None,state.Translations);
#endif
                AtomicJson.Write(Path.Combine(output,"translations.json"),result);
                AtomicJson.Write(Path.Combine(output,"metrics.json"),new { Mode=mode, Seconds=timer.Elapsed.TotalSeconds, Cues=result.Translations.Count, Model=config.Model });
                Console.WriteLine("PASS base protocol; cues="+result.Translations.Count+"; seconds="+timer.Elapsed.TotalSeconds.ToString("F3"));
                return 0;
            }
#if !LEGACY
            config.TranslationQuality="quality"; config.QualityMode="intensive"; config.IntensiveModel="";
            config.IntensiveThinking=mode=="thinking"; config.EnableWebReference=false; config.ReferenceSubtitlePath="";
            config.SceneMaxCues=35; config.SceneMaxSeconds=150; config.ReviewContextCount=8;
            config.IntensiveTimeLimitSeconds=600; config.IntensiveRequestLimit=100; config.IntensiveOutputTokenLimit=409600;
            var frozen=AtomicJson.Read<TranslationState>(baseline,null);
            if(frozen==null || !cues.All(c=>TranslationCache.HasCue(frozen,c.Id))) throw new InvalidDataException("Frozen base incomplete");
            var report=TranslationReview.Run(config,output,cues,frozen,Path.Combine(output,"state.json"),CancellationToken.None,key);
            SrtFile.Write(Path.Combine(output,"zh-CN.srt"),cues,frozen.Translations,false);
            Console.WriteLine("RESULT refinement; complete="+report.ProcessComplete+"; coverage="+report.CoveredCueCount+"/"+cues.Count+"; requests="+report.RequestCount+"; seconds="+report.ElapsedSeconds+"; tokens="+report.PromptTokens+"/"+report.CompletionTokens+"; sourcecheck="+report.NeedsSourceCheckCount+"; rejected="+report.RejectedCount);
            return report.ProcessComplete ? 0 : 3;
#else
            return 2;
#endif
        }
        catch(Exception ex) { Console.WriteLine("FAIL "+ex.GetType().Name); return 1; }
    }
}
