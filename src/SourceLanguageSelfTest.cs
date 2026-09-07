using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PotPlayerAiSubtitle
{
    internal static class SourceLanguageSelfTest
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Run(string tempRoot, List<string> results)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            AppConfig config = AppConfig.CreateDefault();
            Check(config.SourceLanguage == "ja", "源语言默认值不是日语。");
            foreach (string json in new[] { "{}", "{\"SourceLanguage\":null}", "{\"SourceLanguage\":\"\"}", "{\"SourceLanguage\":\"invalid\"}" })
                Check(serializer.Deserialize<AppConfig>(json).SourceLanguage == "ja", "旧配置或空语言未回退日语。");
            config.SourceLanguage = " EN ";
            string settings = Path.Combine(tempRoot, "language-settings.json");
            AtomicJson.Write(settings, config);
            Check(AtomicJson.Read<AppConfig>(settings, null).SourceLanguage == "en", "源语言未持久保存。");
            Check(serializer.Deserialize<JobRequest>("{}").SourceLanguage == "ja", "旧队列未默认日语。");
            JobRequest queued = new JobRequest { SourceLanguage = "ko" };
            Check(serializer.Deserialize<JobRequest>(AtomicJson.Serialize(queued)).SourceLanguage == "ko", "队列语言未保存。");
            results.Add("PASS source language defaults, config persistence and legacy requests");

            string videoCache = Path.Combine(tempRoot, "language-cache");
            Check(SourceLanguages.CacheDirectory(videoCache, "ja") == videoCache, "旧日语缓存路径发生变化。");
            HashSet<string> cacheDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<SubtitleCue> cues = new List<SubtitleCue>
            {
                new SubtitleCue { Id = 1, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = "A test subtitle." }
            };
            SubtitleScene scene = new SubtitleScene { Index = 0, StartCueIndex = 0, EndCueIndex = 0 };
            foreach (SourceLanguageOption language in SourceLanguages.Options)
            {
                config.SourceLanguage = language.Code;
                Check(cacheDirectories.Add(SourceLanguages.CacheDirectory(videoCache, language.Code)), "不同语言共用了缓存路径。");
                SourceSubtitleRecognizer recognizer = new SourceSubtitleRecognizer(config, null);
                foreach (bool retry in new[] { false, true })
                    Check(recognizer.BuildWhisperArguments(Path.Combine(tempRoot, "clip.wav"), Path.Combine(tempRoot, "out"), retry)
                        .Contains(" -l " + language.Code + " -osrt"), "Whisper 未使用所选语言。");
                DeepSeekClient client = new DeepSeekClient(config, "not-a-real-key");
                Check(client.BuildSystemPrompt().Contains(language.Name + "影视字幕翻译器"), "翻译系统提示未使用所选语言。");
                Dictionary<string, object> payload = serializer.Deserialize<Dictionary<string, object>>(
                    client.BuildUserPrompt(cues, scene, 0, new Dictionary<string, string>()));
                Check((string)payload["source_language"] == language.Code, "翻译请求未声明源语言。");
            }
            results.Add("PASS all source languages reach Whisper and translation prompts with isolated caches");

            string media = Path.Combine(tempRoot, "language-video.mkv");
            File.WriteAllBytes(media, new byte[] { 3, 2, 1 });
            string japanese = Path.Combine(tempRoot, "language-video.ja.srt");
            string english = Path.Combine(tempRoot, "language-video.eng.srt");
            SrtFile.Write(japanese, new List<SubtitleCue> { new SubtitleCue { Id = 1, Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = "こんにちは、元気ですか。" } }, null, false);
            SrtFile.Write(english, cues, null, false);
            Check(SrtFile.FindSourceSrtBesideMedia(media, null, videoCache) == japanese, "默认语言未选择日语字幕。");
            Check(SrtFile.FindSourceSrtBesideMedia(media, "en", videoCache) == english, "未选择英语别名字幕。");
            Check(SrtFile.FindSourceSrtBesideMedia(media, "ko", videoCache) == null, "误用了其他语言的外置字幕。");
            config.SourceLanguage = "en";
            string englishCache = SourceLanguages.CacheDirectory(videoCache, "en");
            Directory.CreateDirectory(englishCache);
            SourceSubtitleRecognizer externalRecognizer = new SourceSubtitleRecognizer(config, null);
            RecognitionCandidateResult candidate = externalRecognizer.Prepare(media, englishCache,
                Path.Combine(englishCache, "en.raw.srt"), Path.Combine(englishCache, "en.candidate.srt"),
                Path.Combine(englishCache, "quality-report.json"), CancellationToken.None);
            Check(candidate.SourceKind == "external-en-srt" && SrtFile.Read(candidate.CandidatePath)[0].Text == cues[0].Text,
                "外置源字幕未传递到识别候选结果。");
            results.Add("PASS source subtitle selection and recognition preparation honor language");

            string oldBilingual = Path.Combine(videoCache, "ja-zh-CN.srt");
            File.Copy(japanese, oldBilingual);
            string sidecar = Path.Combine(tempRoot, "language-video.srt");
            File.Copy(oldBilingual, sidecar);
            File.Delete(japanese);
            Check(SrtFile.FindSourceSrtBesideMedia(media, "ja", videoCache) == null, "生成字幕被当成了源字幕。");
            string newBilingual = Path.Combine(englishCache, "en-zh-CN.srt");
            string chinese = Path.Combine(englishCache, "zh-CN.srt");
            SrtFile.Write(chinese, cues, new Dictionary<string, string> { { "1", "测试字幕。" } }, false);
            SrtFile.Write(newBilingual, cues, new Dictionary<string, string> { { "1", "测试字幕。" } }, true);
            config.SubtitleHubPath = Path.Combine(tempRoot, "language-hub");
            SubtitlePublishResult published = SubtitlePublisher.Publish(config, media, english, chinese, newBilingual);
            Check(published.BilingualSidecarChanged && File.ReadAllText(sidecar) == File.ReadAllText(newBilingual), "切换语言后未替换本工具的旧字幕。");
            File.WriteAllText(sidecar, "user-owned subtitle", Encoding.UTF8);
            SubtitlePublishResult protectedResult = SubtitlePublisher.Publish(config, media, english, chinese, newBilingual);
            Check(!protectedResult.BilingualSidecarChanged && File.ReadAllText(sidecar) == "user-owned subtitle", "覆盖了用户自己的字幕。");
            results.Add("PASS language switching updates owned subtitles without reusing output or overwriting user files");
        }
    }
}
