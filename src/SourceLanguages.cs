using System;
using System.IO;

namespace PotPlayerAiSubtitle
{
    internal sealed class SourceLanguageOption
    {
        public readonly string Code;
        public readonly string Name;
        public readonly string[] SubtitleTags;

        public SourceLanguageOption(string code, string name, params string[] subtitleTags)
        {
            Code = code;
            Name = name;
            SubtitleTags = subtitleTags;
        }

        public override string ToString() { return Code == "ja" ? Name + "（默认）" : Name; }
    }

    internal static class SourceLanguages
    {
        internal static readonly SourceLanguageOption[] Options =
        {
            new SourceLanguageOption("ja", "日语", "jpn", "ja", "jp"),
            new SourceLanguageOption("en", "英语", "eng", "en"),
            new SourceLanguageOption("ko", "韩语", "kor", "ko"),
            new SourceLanguageOption("zh", "中文", "zho", "chi", "zh"),
            new SourceLanguageOption("fr", "法语", "fra", "fre", "fr"),
            new SourceLanguageOption("de", "德语", "deu", "ger", "de"),
            new SourceLanguageOption("es", "西班牙语", "spa", "es"),
            new SourceLanguageOption("it", "意大利语", "ita", "it"),
            new SourceLanguageOption("pt", "葡萄牙语", "por", "pt"),
            new SourceLanguageOption("ru", "俄语", "rus", "ru"),
            new SourceLanguageOption("th", "泰语", "tha", "th"),
            new SourceLanguageOption("vi", "越南语", "vie", "vi")
        };

        public static SourceLanguageOption Get(string code)
        {
            string value = (code ?? "").Trim();
            foreach (SourceLanguageOption option in Options)
                if (string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase)) return option;
            return Options[0];
        }

        public static string Normalize(string code) { return Get(code).Code; }

        // Keep the original Japanese cache in place; other languages never share its checkpoints.
        public static string CacheDirectory(string videoCacheDirectory, string code)
        {
            string language = Normalize(code);
            return language == "ja" ? videoCacheDirectory : Path.Combine(videoCacheDirectory, "source-" + language);
        }
    }
}
