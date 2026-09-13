using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal sealed class TranslationQualityOption
    {
        public readonly string Code;
        public readonly string Name;
        public readonly string Hint;

        public TranslationQualityOption(string code, string name, string hint)
        {
            Code = code;
            Name = name;
            Hint = hint;
        }

        public override string ToString() { return Name; }
    }

    internal static class TranslationQualities
    {
        internal static readonly TranslationQualityOption[] Options =
        {
            new TranslationQualityOption("fast", "快速档", "优先出片速度，只做低成本的译文校正。"),
            new TranslationQualityOption("quality", "质量档", "精译与高风险场景复核，可选实验性深度思考。")
        };

        public static TranslationQualityOption Get(string code)
        {
            string value = (code ?? "").Trim();
            foreach (TranslationQualityOption option in Options)
                if (string.Equals(option.Code, value, StringComparison.OrdinalIgnoreCase)) return option;
            return Options[0];
        }

        public static string Normalize(string code) { return Get(code).Code; }
    }

    internal sealed class TranslationQualityComboBox : ComboBox
    {
        public TranslationQualityComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (TranslationQualityOption option in TranslationQualities.Options) Items.Add(option);
            TranslationQuality = "fast";
        }

        public string TranslationQuality
        {
            get
            {
                TranslationQualityOption option = SelectedItem as TranslationQualityOption;
                return option == null ? "fast" : option.Code;
            }
            set { SelectedItem = TranslationQualities.Get(value); }
        }
    }

    internal static class TranslationQualityCheck
    {
        // Hiragana, katakana and hangul should never survive in the Simplified Chinese line.
        private static readonly Regex SourceLeak = new Regex(@"[\u3040-\u30ff\uac00-\ud7af]+", RegexOptions.Compiled);

        public static List<int> FindDefectCueIds(IList<SubtitleCue> cues, IDictionary<string, string> translations)
        {
            List<int> defects = new List<int>();
            if (cues == null) return defects;
            if (translations == null) return cues.Select(c => c.Id).ToList();
            foreach (SubtitleCue cue in cues)
            {
                string zh;
                if (!translations.TryGetValue(cue.Id.ToString(CultureInfo.InvariantCulture), out zh) || string.IsNullOrWhiteSpace(zh))
                { defects.Add(cue.Id); continue; }
                string trimmed = zh.Trim();

                // Residual source-language text (kana / hangul) leaked into the Chinese line.
                if (SourceLeak.IsMatch(trimmed)) { defects.Add(cue.Id); continue; }

                // Absurdly long line; the translation target is <= 80 Chinese characters.
                if (trimmed.Length > 96) { defects.Add(cue.Id); continue; }

                // A single character or punctuation run dominates the line and adds no reading value.
                if (IsExcessiveRepetition(trimmed)) { defects.Add(cue.Id); continue; }
            }
            return defects;
        }

        // Layer 1: glossary adherence. A cue whose source contains a known term must use the
        // term's prescribed Chinese rendering (even when the source is a mis-transcribed variant).
        public static List<int> FindGlossaryDefects(IList<SubtitleCue> cues, IDictionary<string, string> translations, TermIndex index)
        {
            List<int> defects = new List<int>();
            if (index == null || index.IsEmpty || cues == null || translations == null) return defects;
            foreach (SubtitleCue cue in cues)
            {
                string zh;
                if (!translations.TryGetValue(cue.Id.ToString(CultureInfo.InvariantCulture), out zh)) continue;
                if (string.IsNullOrWhiteSpace(cue.Text) || string.IsNullOrWhiteSpace(zh)) continue;
                foreach (TermIndexEntry entry in index.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target)) continue;
                    bool appears = ContainsTerm(cue.Text, entry.Source)
                        || (entry.Variants != null && entry.Variants.Any(delegate(string v)
                            { return !string.IsNullOrWhiteSpace(v) && ContainsTerm(cue.Text, v); }));
                    if (!appears) continue;
                    if (zh.IndexOf(entry.Target, StringComparison.Ordinal) < 0)
                    {
                        defects.Add(cue.Id);
                        break;
                    }
                }
            }
            return defects;
        }

        // Latin names need word boundaries: Ann must not match annual or Anna.
        internal static bool ContainsTerm(string text, string term)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term)) return false;
            bool latin = term.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
            if (!latin) return text.IndexOf(term, StringComparison.Ordinal) >= 0;
            return Regex.IsMatch(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(term) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static bool AcceptRepair(SubtitleCue cue, string original, string proposed, TermIndex terms, out string reason)
        {
            reason = null;
            if (string.Equals(original, proposed, StringComparison.Ordinal)) { reason = "unchanged"; return false; }
            string id = cue.Id.ToString(CultureInfo.InvariantCulture);
            var after = new Dictionary<string, string> { { id, proposed } };
            if (FindDefectCueIds(new[] { cue }, after).Count != 0) { reason = "deterministic-defect"; return false; }
            var before = new Dictionary<string, string> { { id, original } };
            if (terms != null && terms.Entries != null)
                foreach (TermIndexEntry term in terms.Entries)
                {
                    var one = new TermIndex { Entries = new List<TermIndexEntry> { term } };
                    if (FindGlossaryDefects(new[] { cue }, after, one).Count > FindGlossaryDefects(new[] { cue }, before, one).Count)
                    { reason = "new-terminology-conflict"; return false; }
                }
            return true;
        }

        private static bool IsExcessiveRepetition(string text)
        {
            if (text.Length < 16) return false;
            List<char> meaningful = new List<char>();
            foreach (char c in text)
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)) meaningful.Add(c);

            if (meaningful.Count > 0)
            {
                char first = meaningful[0];
                int best = 0;
                int run = 0;
                for (int i = 0; i < meaningful.Count; i++)
                {
                    if (meaningful[i] == first) { run++; if (run > best) best = run; }
                    else run = 0;
                }
                if (best >= 20 && best * 100 >= meaningful.Count * 85) return true;
            }

            int punctuation = 0;
            foreach (char c in text)
                if (char.IsPunctuation(c) || char.IsSymbol(c)) punctuation++;
            return punctuation >= 24 && punctuation * 100 >= text.Length * 85;
        }
    }
}
