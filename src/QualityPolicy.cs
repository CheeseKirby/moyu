using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PotPlayerAiSubtitle
{
    internal static class QualityPolicy
    {
        public static bool IsIntensive(AppConfig config)
        { return config.TranslationQuality == "quality" && string.Equals(config.QualityMode, "intensive", StringComparison.OrdinalIgnoreCase); }
        public static bool Thinking(AppConfig config) { return IsIntensive(config) ? config.IntensiveThinking : config.EnableThinking; }
        public static int SearchLimit(AppConfig config)
        { return config.SearchRequestLimit > 0 ? Math.Min(300, config.SearchRequestLimit) : IsIntensive(config) ? 30 : 6; }
        public static void Validate(AppConfig config, bool requireReferenceFile = false)
        {
            if (config.IntensiveTimeLimitSeconds < 0 || config.IntensiveRequestLimit < 0 || config.IntensiveOutputTokenLimit < 0 || config.SearchRequestLimit < 0)
                throw new InvalidDataException("资源上限不能为负数；精修三项0表示不限，搜索0表示默认值。");
            if (requireReferenceFile && config.TranslationQuality == "quality" && !string.IsNullOrWhiteSpace(config.ReferenceSubtitlePath)
                && (!File.Exists(config.ReferenceSubtitlePath) || !string.Equals(Path.GetExtension(config.ReferenceSubtitlePath), ".srt", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("参考字幕必须是存在的 SRT 文件；不使用时请清空参考路径。");
            if (requireReferenceFile && config.TranslationQuality == "quality" && !string.IsNullOrWhiteSpace(config.ReferenceSubtitlePath)
                && new FileInfo(config.ReferenceSubtitlePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("参考字幕超过4MB上限。");
        }
        public static AppConfig ReviewConfig(AppConfig config)
        {
            // Keep configuration cloning strongly typed through the same serializer as persisted settings.
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var result = serializer.Deserialize<AppConfig>(AtomicJson.Serialize(config));
            if (IsIntensive(config) && !string.IsNullOrWhiteSpace(config.IntensiveModel)) result.Model = config.IntensiveModel.Trim();
            return result;
        }
        public static Dictionary<string, string> RelevantGlossary(IDictionary<string, string> glossary, IEnumerable<SubtitleCue> cues)
        {
            string text = string.Join("\n", cues.Select(c => c.Text).ToArray());
            return (glossary ?? new Dictionary<string, string>()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && Contains(text, p.Key))
                .OrderBy(p => p.Key, StringComparer.Ordinal).Take(80).ToDictionary(p => p.Key, p => p.Value);
        }
        internal static bool Contains(string text, string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return false;
            if (Regex.IsMatch(term, @"^[a-zA-Z0-9 '-]+$"))
                return Regex.IsMatch(text ?? "", @"(?<![A-Za-z0-9])" + Regex.Escape(term) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
            return (text ?? "").IndexOf(term, StringComparison.Ordinal) >= 0;
        }
        public static void MergeGlossary(TranslationState state, IDictionary<string, string> updates)
        {
            if (state.GlossaryConflicts == null) state.GlossaryConflicts = new Dictionary<string, List<string>>();
            foreach (var item in updates)
            {
                if (item.Key.Length > 100 || item.Value.Length > 150) continue;
                List<string> alternatives;
                if (state.GlossaryConflicts.TryGetValue(item.Key, out alternatives))
                { if (!alternatives.Contains(item.Value) && alternatives.Count < 8) alternatives.Add(item.Value); continue; }
                string existing;
                if (state.Glossary.TryGetValue(item.Key, out existing) && existing != item.Value)
                { state.GlossaryConflicts[item.Key] = new List<string> { existing, item.Value }; state.Glossary.Remove(item.Key); }
                else state.Glossary[item.Key] = item.Value;
            }
        }
    }
}
