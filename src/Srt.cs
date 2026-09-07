using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PotPlayerAiSubtitle
{
    internal sealed class SubtitleCue
    {
        public int Id { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Text { get; set; }
    }

    internal sealed class SubtitleScene
    {
        public int Index { get; set; }
        public int StartCueIndex { get; set; }
        public int EndCueIndex { get; set; }
    }

    internal static class SrtFile
    {
        private static readonly Regex TimeLine = new Regex(@"^\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{3})", RegexOptions.Compiled);

        public static List<SubtitleCue> Read(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] blocks = Regex.Split(text.Trim(), "\n{2,}");
            List<SubtitleCue> cues = new List<SubtitleCue>();
            int generatedId = 1;

            foreach (string rawBlock in blocks)
            {
                string[] lines = rawBlock.Split(new[] { '\n' }, StringSplitOptions.None);
                if (lines.Length < 2) continue;
                int timeIndex = 0;
                int parsedId;
                if (int.TryParse(lines[0].Trim(), out parsedId)) timeIndex = 1;
                else parsedId = generatedId;
                if (timeIndex >= lines.Length) continue;

                Match match = TimeLine.Match(lines[timeIndex]);
                if (!match.Success) continue;
                StringBuilder body = new StringBuilder();
                for (int i = timeIndex + 1; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd();
                    if (body.Length > 0) body.Append('\n');
                    body.Append(line);
                }
                string cueText = body.ToString().Trim();
                if (cueText.Length == 0) continue;

                cues.Add(new SubtitleCue
                {
                    Id = generatedId,
                    Start = ParseTime(match, 1),
                    End = ParseTime(match, 5),
                    Text = cueText
                });
                generatedId++;
            }
            return cues;
        }

        public static void Write(string path, IList<SubtitleCue> cues, IDictionary<string, string> translations, bool bilingual)
        {
            StringBuilder output = new StringBuilder();
            for (int i = 0; i < cues.Count; i++)
            {
                SubtitleCue cue = cues[i];
                string translated;
                if (translations == null || !translations.TryGetValue(cue.Id.ToString(CultureInfo.InvariantCulture), out translated)) translated = "";
                output.Append(cue.Id).Append("\r\n");
                output.Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(cue.End)).Append("\r\n");
                if (bilingual)
                {
                    output.Append(cue.Text.Trim()).Append("\r\n");
                    output.Append(translated.Trim()).Append("\r\n\r\n");
                }
                else
                {
                    output.Append((translations == null ? cue.Text : translated).Trim()).Append("\r\n\r\n");
                }
            }
            WriteAtomic(path, output.ToString());
        }

        public static List<SubtitleScene> BuildScenes(IList<SubtitleCue> cues, int maxCues, int maxSeconds)
        {
            List<SubtitleScene> scenes = new List<SubtitleScene>();
            if (cues.Count == 0) return scenes;
            int start = 0;
            int sceneIndex = 0;
            for (int i = 0; i < cues.Count; i++)
            {
                int count = i - start + 1;
                double duration = (cues[i].End - cues[start].Start).TotalSeconds;
                double gap = i > start ? (cues[i].Start - cues[i - 1].End).TotalSeconds : 0;
                bool breakAtPause = count >= 12 && gap >= 4.0;
                bool atLimit = count >= maxCues || duration >= maxSeconds;
                if ((breakAtPause || atLimit) && i > start)
                {
                    int end = breakAtPause ? i - 1 : i;
                    scenes.Add(new SubtitleScene { Index = sceneIndex++, StartCueIndex = start, EndCueIndex = end });
                    start = end + 1;
                }
            }
            if (start < cues.Count) scenes.Add(new SubtitleScene { Index = sceneIndex, StartCueIndex = start, EndCueIndex = cues.Count - 1 });
            return scenes;
        }

        public static bool LooksJapanese(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int japanese = 0;
            int letters = 0;
            foreach (char c in text)
            {
                if ((c >= '\u3040' && c <= '\u30ff') || (c >= '\u4e00' && c <= '\u9fff')) japanese++;
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsDigit(c)) letters++;
            }
            return letters > 0 && japanese >= 5 && japanese * 4 >= letters;
        }

        public static string FindJapaneseSrtBesideMedia(string mediaPath)
        {
            string directory = Path.GetDirectoryName(mediaPath);
            string name = Path.GetFileNameWithoutExtension(mediaPath);
            string[] preferred =
            {
                Path.Combine(directory, name + ".ja.srt"),
                Path.Combine(directory, name + ".jpn.srt"),
                Path.Combine(directory, name + ".jp.srt")
            };
            foreach (string path in preferred) if (File.Exists(path)) return path;

            string[] candidates = Directory.GetFiles(directory, name + "*.srt");
            foreach (string path in candidates)
            {
                string lower = Path.GetFileName(path).ToLowerInvariant();
                if (lower.EndsWith(".zh-cn.srt", StringComparison.Ordinal)
                    || lower.EndsWith(".ja-zh-cn.srt", StringComparison.Ordinal)
                    || lower.EndsWith(".candidate.srt", StringComparison.Ordinal)) continue;
                try
                {
                    string sample = File.ReadAllText(path, Encoding.UTF8);
                    if (sample.Length > 16000) sample = sample.Substring(0, 16000);
                    if (LooksJapanese(sample)) return path;
                }
                catch { }
            }
            return null;
        }

        private static TimeSpan ParseTime(Match match, int group)
        {
            int hours = int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
            int minutes = int.Parse(match.Groups[group + 1].Value, CultureInfo.InvariantCulture);
            int seconds = int.Parse(match.Groups[group + 2].Value, CultureInfo.InvariantCulture);
            int millis = int.Parse(match.Groups[group + 3].Value, CultureInfo.InvariantCulture);
            return new TimeSpan(0, hours, minutes, seconds, millis);
        }

        private static string FormatTime(TimeSpan time)
        {
            int hours = (int)time.TotalHours;
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", hours, time.Minutes, time.Seconds, time.Milliseconds);
        }

        private static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch
            {
                File.Copy(temporary, path, true);
                File.Delete(temporary);
            }
        }
    }
}
