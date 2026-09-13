using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PotPlayerAiSubtitle
{
    internal sealed class TermIndexEntry
    {
        public string Source { get; set; }          // canonical source form, e.g. ヨシマン
        public string Target { get; set; }          // Simplified Chinese, e.g. 吉满
        public List<string> Variants { get; set; }  // common mis-transcriptions / alternate spellings
    }

    internal sealed class TermIndex
    {
        public string Version { get; set; }
        public string SourceLanguage { get; set; }
        public List<TermIndexEntry> Entries { get; set; }

        public TermIndex()
        {
            Version = "1";
            Entries = new List<TermIndexEntry>();
        }

        public bool IsEmpty { get { return Entries == null || Entries.Count == 0; } }

        // Names / terms for the Whisper initial prompt. Bounded to stay within n_text_ctx/2.
        public string Prompt()
        {
            if (IsEmpty) return "";
            StringBuilder builder = new StringBuilder();
            foreach (TermIndexEntry entry in Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Source)) continue;
                if (builder.Length > 0) builder.Append('、');
                builder.Append(entry.Source);
            }
            string text = builder.ToString();
            return text.Length <= 120 ? text : text.Substring(0, 120);
        }

        // Deterministic spell correction: unify mis-transcribed katakana runs back to canonical form.
        public IList<SubtitleCue> ApplySpellCorrection(IList<SubtitleCue> cues)
        {
            if (IsEmpty || cues == null || cues.Count == 0) return cues;
            Dictionary<string, string> variantMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (TermIndexEntry entry in Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Source)) continue;
                if (entry.Variants == null) continue;
                foreach (string variant in entry.Variants)
                {
                    if (string.IsNullOrWhiteSpace(variant)) continue;
                    if (string.Equals(variant, entry.Source, StringComparison.Ordinal)) continue;
                    if (!variantMap.ContainsKey(variant)) variantMap[variant] = entry.Source;
                }
            }
            if (variantMap.Count == 0) return cues;

            Regex katakanaRun = new Regex(@"[\u30a1-\u30f6]+", RegexOptions.Compiled);
            List<SubtitleCue> result = new List<SubtitleCue>();
            foreach (SubtitleCue cue in cues)
            {
                string text = cue.Text;
                if (!string.IsNullOrEmpty(text))
                    text = katakanaRun.Replace(text, delegate(Match match)
                    {
                        string canonical;
                        return variantMap.TryGetValue(match.Value, out canonical) ? canonical : match.Value;
                    });
                result.Add(new SubtitleCue { Id = cue.Id, Start = cue.Start, End = cue.End, Text = text });
            }
            return result;
        }
    }

    internal static class TermIndexStore
    {
        public static string PathFor(string cacheDir) { return Path.Combine(cacheDir, "term-index.json"); }

        public static TermIndex Load(string cacheDir)
        {
            if (string.IsNullOrWhiteSpace(cacheDir)) return null;
            try { return AtomicJson.Read<TermIndex>(PathFor(cacheDir), null); }
            catch { return null; }
        }

        public static void Save(string cacheDir, TermIndex index)
        {
            if (index == null || string.IsNullOrWhiteSpace(cacheDir)) return;
            AtomicJson.Write(PathFor(cacheDir), index);
        }
    }

    internal static class TermBuilder
    {
        private static readonly Regex Katakana = new Regex(@"[\u30a1-\u30f6]{2,}", RegexOptions.Compiled);
        private static readonly Regex Honorific = new Regex(@"[\u3040-\u30ff\u4e00-\u9fff]{1,8}(さん|ちゃん|くん|さま|様|先生|先輩|お姉ちゃん)", RegexOptions.Compiled);

        // Deterministic candidate scan: repeating katakana names + honorific combinations, ranked by frequency.
        public static List<string> ExtractCandidates(IList<SubtitleCue> cues, int maximum = 30)
        {
            Dictionary<string, int> frequency = new Dictionary<string, int>(StringComparer.Ordinal);
            if (cues != null)
            {
                foreach (SubtitleCue cue in cues)
                {
                    if (string.IsNullOrEmpty(cue.Text)) continue;
                    foreach (Match match in Katakana.Matches(cue.Text)) Add(frequency, match.Value);
                    foreach (Match match in Honorific.Matches(cue.Text)) Add(frequency, match.Value);
                }
            }
            return frequency.OrderByDescending(delegate(KeyValuePair<string, int> pair) { return pair.Value; })
                .ThenBy(delegate(KeyValuePair<string, int> pair) { return pair.Key; })
                .Take(maximum).Select(delegate(KeyValuePair<string, int> pair) { return pair.Key; }).ToList();
        }

        // Representative source lines that mention any candidate, kept small enough for one call.
        public static List<string> CollectContext(IList<SubtitleCue> cues, IEnumerable<string> candidates, int perCandidate = 3, int maximumLines = 80)
        {
            HashSet<string> wants = new HashSet<string>(candidates ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> lines = new List<string>();
            if (cues == null || wants.Count == 0) return lines;
            foreach (SubtitleCue cue in cues)
            {
                if (string.IsNullOrEmpty(cue.Text)) continue;
                bool matched = false;
                foreach (string candidate in wants)
                {
                    if (cue.Text.IndexOf(candidate, StringComparison.Ordinal) < 0) continue;
                    int count;
                    seen.TryGetValue(candidate, out count);
                    if (count >= perCandidate) continue;
                    seen[candidate] = count + 1;
                    matched = true;
                }
                if (!matched) continue;
                lines.Add(cue.Text.Length <= 120 ? cue.Text : cue.Text.Substring(0, 120));
                if (lines.Count >= maximumLines) break;
            }
            return lines;
        }

        // Evenly sampled source lines, used when the deterministic candidate scan is thin.
        public static List<string> SampleContext(IList<SubtitleCue> cues, int maximumLines = 80)
        {
            List<string> lines = new List<string>();
            if (cues == null || cues.Count == 0) return lines;
            int step = Math.Max(1, cues.Count / Math.Max(1, maximumLines));
            for (int i = 0; i < cues.Count && lines.Count < maximumLines; i += step)
            {
                string text = cues[i].Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                lines.Add(text.Length <= 120 ? text : text.Substring(0, 120));
            }
            return lines;
        }

        public static List<string> BuildContext(IList<SubtitleCue> cues, IList<string> candidates, int maximumLines = 80)
        {
            if (candidates == null || candidates.Count == 0) return SampleContext(cues, maximumLines);
            return CollectContext(cues, candidates, 3, maximumLines);
        }

        private static void Add(Dictionary<string, int> frequency, string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return;
            int count;
            frequency.TryGetValue(term, out count);
            frequency[term] = count + 1;
        }
    }
}
