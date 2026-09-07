using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PotPlayerAiSubtitle
{
    internal sealed class SubtitleQualityIssue
    {
        public string Kind { get; set; }
        public string Severity { get; set; }
        public List<int> CueIds { get; set; }
        public long StartMilliseconds { get; set; }
        public long EndMilliseconds { get; set; }
        public string Text { get; set; }
    }

    internal sealed class SubtitleQualityReport
    {
        public string Version { get; set; }
        public string GeneratedUtc { get; set; }
        public bool GateApplied { get; set; }
        public bool Passed { get; set; }
        public int InitialCueCount { get; set; }
        public int InitialSuspiciousCueCount { get; set; }
        public int RetryRegionCount { get; set; }
        public int RetriedCueCount { get; set; }
        public int RemovedCueCount { get; set; }
        public int CompactedCueCount { get; set; }
        public int CueCount { get; set; }
        public int SuspiciousCueCount { get; set; }
        public int Over10Seconds { get; set; }
        public int Over20Seconds { get; set; }
        public int Over30Seconds { get; set; }
        public int Over60Seconds { get; set; }
        public int InvalidDurationCount { get; set; }
        public int OverlapCount { get; set; }
        public int LowInformationLongCount { get; set; }
        public int LowDiversityCount { get; set; }
        public int RepeatedClusterCount { get; set; }
        public int RepeatedVocalizationClusterCount { get; set; }
        public int LongestCueId { get; set; }
        public double LongestSeconds { get; set; }
        public string Disposition { get; set; }
        public string DecisionReason { get; set; }
        public bool ContinuedWithWarnings { get; set; }
        public List<SubtitleQualityIssue> Issues { get; set; }

        public SubtitleQualityReport()
        {
            Issues = new List<SubtitleQualityIssue>();
        }
    }

    internal sealed class SubtitleTimeRange
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
    }

    internal sealed class SubtitleSanitizeResult
    {
        public List<SubtitleCue> Cues { get; set; }
        public int RemovedCount { get; set; }
        public int CompactedCount { get; set; }
    }

    internal sealed class SubtitleQualityDecision
    {
        public bool CanContinue { get; set; }
        public bool HasWarning { get; set; }
        public string Reason { get; set; }
    }

    internal static class SubtitleQuality
    {
        private static readonly Regex SpaceRun = new Regex(@"[ \t]+", RegexOptions.Compiled);
        private static readonly HashSet<string> Vocalizations = new HashSet<string>(StringComparer.Ordinal)
        {
            "あ", "い", "う", "え", "お", "ん", "っ", "あっ", "いっ", "うっ", "えっ", "おっ", "んっ",
            "ああ", "いい", "うう", "ええ", "おお", "んん", "うん", "あん", "はあ", "ふう", "へえ",
            "あー", "いー", "うー", "えー", "おー", "んー", "はぁ", "ふぅ", "えぇ", "うぅ"
        };

        public static SubtitleQualityReport Analyze(IList<SubtitleCue> source)
        {
            List<SubtitleCue> cues = source == null
                ? new List<SubtitleCue>()
                : source.OrderBy(delegate(SubtitleCue cue) { return cue.Start; }).ThenBy(delegate(SubtitleCue cue) { return cue.End; }).ToList();
            SubtitleQualityReport report = new SubtitleQualityReport
            {
                Version = "2",
                GeneratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                CueCount = cues.Count
            };
            HashSet<int> suspicious = new HashSet<int>();

            SubtitleCue previous = null;
            foreach (SubtitleCue cue in cues)
            {
                double duration = (cue.End - cue.Start).TotalSeconds;
                if (duration > report.LongestSeconds)
                {
                    report.LongestSeconds = Math.Round(duration, 3);
                    report.LongestCueId = cue.Id;
                }
                if (duration > 10) report.Over10Seconds++;
                if (duration > 20) report.Over20Seconds++;
                if (duration > 30) report.Over30Seconds++;
                if (duration > 60) report.Over60Seconds++;

                string meaningful = Meaningful(cue.Text);
                if (duration <= 0)
                {
                    report.InvalidDurationCount++;
                    AddIssue(report, suspicious, "invalid-duration", "error", cue.Start, cue.End, new[] { cue.Id }, cue.Text);
                }
                if (duration > 30)
                    AddIssue(report, suspicious, "over-30-seconds", "error", cue.Start, cue.End, new[] { cue.Id }, cue.Text);

                if (meaningful.Length <= 3 && duration > 8)
                {
                    report.LowInformationLongCount++;
                    AddIssue(report, suspicious, "low-information-long", "error", cue.Start, cue.End, new[] { cue.Id }, cue.Text);
                }
                else if (meaningful.Length <= 15 && duration > 15)
                {
                    AddIssue(report, suspicious, "short-text-long", "warning", cue.Start, cue.End, new[] { cue.Id }, cue.Text);
                }

                if (IsLowDiversity(meaningful))
                {
                    report.LowDiversityCount++;
                    AddIssue(report, suspicious, "low-character-diversity", "error", cue.Start, cue.End, new[] { cue.Id }, cue.Text);
                }

                if (previous != null && previous.End - cue.Start > TimeSpan.FromMilliseconds(200))
                {
                    report.OverlapCount++;
                    AddIssue(report, suspicious, "timeline-overlap", "error", cue.Start, previous.End > cue.End ? previous.End : cue.End, new[] { previous.Id, cue.Id }, previous.Text + " | " + cue.Text);
                }
                previous = cue;
            }

            int index = 0;
            while (index < cues.Count)
            {
                string normalized = NormalizeForComparison(cues[index].Text);
                int end = index + 1;
                while (end < cues.Count
                    && normalized.Length > 0
                    && string.Equals(normalized, NormalizeForComparison(cues[end].Text), StringComparison.Ordinal)
                    && cues[end].Start - cues[end - 1].End <= TimeSpan.FromSeconds(4))
                {
                    end++;
                }
                int count = end - index;
                if (count >= 3)
                {
                    bool vocalization = IsVocalization(normalized);
                    report.RepeatedClusterCount++;
                    if (vocalization) report.RepeatedVocalizationClusterCount++;
                    TimeSpan repeatedSpan = cues[end - 1].End - cues[index].Start;
                    bool severeRepeat = vocalization
                        ? count >= 5 || repeatedSpan > TimeSpan.FromSeconds(6)
                        : count >= 5 || repeatedSpan > TimeSpan.FromSeconds(15);
                    AddIssue(report, suspicious, vocalization ? "repeated-vocalization-cluster" : "repeated-text-cluster",
                        severeRepeat ? "error" : "warning",
                        cues[index].Start, cues[end - 1].End,
                        cues.Skip(index).Take(count).Select(delegate(SubtitleCue cue) { return cue.Id; }), cues[index].Text);
                }
                index = end;
            }

            report.SuspiciousCueCount = suspicious.Count;
            report.Passed = cues.Count > 0
                && report.Issues.All(delegate(SubtitleQualityIssue issue) { return issue.Severity != "error"; });
            return report;
        }

        public static SubtitleQualityDecision EvaluateForTranslation(SubtitleQualityReport report)
        {
            SubtitleQualityDecision decision = new SubtitleQualityDecision();
            if (report == null)
            {
                decision.CanContinue = false;
                decision.Reason = "缺少字幕质量报告。";
                return decision;
            }

            int cueCount = Math.Max(0, report.CueCount);
            int baseline = report.InitialCueCount > 0 ? report.InitialCueCount : cueCount + Math.Max(0, report.RemovedCueCount);
            List<string> blocking = new List<string>();
            if (cueCount == 0) blocking.Add("没有有效字幕");
            if (report.InvalidDurationCount > 0) blocking.Add("仍有无效时间轴");
            if (report.Over60Seconds > 0) blocking.Add("仍有超过60秒的异常字幕");

            int longLimit = Math.Max(2, (int)Math.Ceiling(cueCount * 0.01));
            if (report.Over30Seconds > longLimit) blocking.Add("超过30秒的字幕过多");
            int overlapLimit = Math.Max(5, (int)Math.Ceiling(cueCount * 0.02));
            if (report.OverlapCount > overlapLimit) blocking.Add("时间轴重叠过多");
            if (baseline >= 20 && cueCount * 100 < baseline * 50) blocking.Add("自动清理后保留的字幕不足一半");
            if (baseline >= 20 && report.RemovedCueCount * 100 > baseline * 40) blocking.Add("自动清理删除比例过高");
            if (cueCount > 0 && report.SuspiciousCueCount * 100 > cueCount * 30) blocking.Add("残留可疑字幕比例过高");

            if (blocking.Count > 0)
            {
                decision.CanContinue = false;
                decision.Reason = string.Join("；", blocking.ToArray()) + "。为避免生成大面积错误翻译，任务已停止。";
                report.Disposition = "blocked";
                report.DecisionReason = decision.Reason;
                report.ContinuedWithWarnings = false;
                return decision;
            }

            int suspiciousWarning = Math.Max(5, (int)Math.Ceiling(cueCount * 0.03));
            int removedWarning = Math.Max(5, (int)Math.Ceiling(Math.Max(1, baseline) * 0.03));
            int compactedWarning = Math.Max(5, (int)Math.Ceiling(Math.Max(1, cueCount) * 0.01));
            decision.CanContinue = true;
            decision.HasWarning = !report.Passed
                || report.SuspiciousCueCount >= suspiciousWarning
                || report.RemovedCueCount >= removedWarning
                || report.CompactedCueCount >= compactedWarning;
            decision.Reason = decision.HasWarning
                ? string.Format(CultureInfo.InvariantCulture,
                    "发现 {0} 条可疑识别结果，已自动清理 {1} 条并压缩 {2} 条重复内容；时间轴结构可用，将继续翻译。最终字幕可能仍有少量识别误差。",
                    report.SuspiciousCueCount, report.RemovedCueCount, report.CompactedCueCount)
                : "字幕质量检查通过。";
            report.Disposition = decision.HasWarning ? "accepted-with-warning" : "accepted";
            report.DecisionReason = decision.Reason;
            report.ContinuedWithWarnings = decision.HasWarning;
            return decision;
        }
        public static List<SubtitleTimeRange> BuildRetryRanges(IList<SubtitleCue> cues, SubtitleQualityReport report, TimeSpan audioDuration, int maximumRegions)
        {
            List<SubtitleTimeRange> ranges = new List<SubtitleTimeRange>();
            if (cues == null || report == null) return ranges;
            foreach (SubtitleQualityIssue issue in report.Issues)
            {
                if (issue.Severity != "error") continue;
                TimeSpan start = TimeSpan.FromMilliseconds(Math.Max(0, issue.StartMilliseconds - 1500));
                TimeSpan end = TimeSpan.FromMilliseconds(Math.Min(audioDuration.TotalMilliseconds, issue.EndMilliseconds + 1500));
                if (end > start) ranges.Add(new SubtitleTimeRange { Start = start, End = end });
            }
            if (ranges.Count == 0) return ranges;

            ranges = ranges.OrderBy(delegate(SubtitleTimeRange range) { return range.Start; }).ToList();
            List<SubtitleTimeRange> merged = new List<SubtitleTimeRange>();
            foreach (SubtitleTimeRange range in ranges)
            {
                if (merged.Count == 0 || range.Start > merged[merged.Count - 1].End + TimeSpan.FromSeconds(1))
                    merged.Add(new SubtitleTimeRange { Start = range.Start, End = range.End });
                else if (range.End > merged[merged.Count - 1].End)
                    merged[merged.Count - 1].End = range.End;
            }

            List<SubtitleTimeRange> split = new List<SubtitleTimeRange>();
            TimeSpan maximum = TimeSpan.FromSeconds(30);
            foreach (SubtitleTimeRange range in merged)
            {
                TimeSpan cursor = range.Start;
                while (cursor < range.End && split.Count < maximumRegions)
                {
                    TimeSpan end = cursor + maximum;
                    if (end > range.End) end = range.End;
                    split.Add(new SubtitleTimeRange { Start = cursor, End = end });
                    cursor = end;
                }
                if (split.Count >= maximumRegions) break;
            }
            return split;
        }

        public static SubtitleSanitizeResult SanitizeAfterRetry(IList<SubtitleCue> source)
        {
            int removed = 0;
            int compacted = 0;
            List<SubtitleCue> normalized = new List<SubtitleCue>();
            foreach (SubtitleCue original in source.OrderBy(delegate(SubtitleCue cue) { return cue.Start; }).ThenBy(delegate(SubtitleCue cue) { return cue.End; }))
            {
                string text = NormalizeWhitespace(original.Text);
                double duration = (original.End - original.Start).TotalSeconds;
                if (string.IsNullOrWhiteSpace(text) || duration <= 0.10 || duration > 30)
                {
                    removed++;
                    continue;
                }
                string meaningful = Meaningful(text);
                if (meaningful.Length <= 3 && duration > 8)
                {
                    removed++;
                    continue;
                }
                string compact = CompactRepetition(text);
                TimeSpan end = original.End;
                if (!string.Equals(compact, text, StringComparison.Ordinal))
                {
                    compacted++;
                    if (end - original.Start > TimeSpan.FromSeconds(4)) end = original.Start + TimeSpan.FromSeconds(4);
                }
                if (Meaningful(compact).Length <= 3 && end - original.Start > TimeSpan.FromSeconds(8))
                {
                    removed++;
                    continue;
                }
                normalized.Add(new SubtitleCue { Id = original.Id, Start = original.Start, End = end, Text = compact });
            }

            List<SubtitleCue> collapsed = CollapseRepeatedClusters(normalized, ref removed);
            List<SubtitleCue> repaired = RepairOverlaps(collapsed, ref removed);
            for (int i = 0; i < repaired.Count; i++) repaired[i].Id = i + 1;
            return new SubtitleSanitizeResult { Cues = repaired, RemovedCount = removed, CompactedCount = compacted };
        }

        public static string NormalizeForComparison(string text)
        {
            return Meaningful(text).ToLowerInvariant();
        }

        private static List<SubtitleCue> CollapseRepeatedClusters(List<SubtitleCue> cues, ref int removed)
        {
            List<SubtitleCue> result = new List<SubtitleCue>();
            int index = 0;
            while (index < cues.Count)
            {
                string normalized = NormalizeForComparison(cues[index].Text);
                int end = index + 1;
                while (end < cues.Count
                    && normalized.Length > 0
                    && string.Equals(normalized, NormalizeForComparison(cues[end].Text), StringComparison.Ordinal)
                    && cues[end].Start - cues[end - 1].End <= TimeSpan.FromSeconds(4)) end++;

                int count = end - index;
                TimeSpan span = cues[end - 1].End - cues[index].Start;
                bool vocalization = IsVocalization(normalized);
                bool severe = count >= 3 && (vocalization
                    ? count >= 5 || span > TimeSpan.FromSeconds(6)
                    : count >= 5 || span > TimeSpan.FromSeconds(15));
                if (severe)
                {
                    int keepCount = count >= 5 ? 3 : 2;
                    int previousKeep = -1;
                    int keptCount = 0;
                    for (int slot = 0; slot < keepCount; slot++)
                    {
                        int keepIndex = index + (int)Math.Round(slot * (count - 1.0) / Math.Max(1, keepCount - 1));
                        if (keepIndex == previousKeep) continue;
                        SubtitleCue cue = Clone(cues[keepIndex]);
                        TimeSpan maximumDuration = vocalization ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(8);
                        if (cue.End - cue.Start > maximumDuration) cue.End = cue.Start + maximumDuration;
                        result.Add(cue);
                        previousKeep = keepIndex;
                        keptCount++;
                    }
                    removed += count - keptCount;
                }
                else
                {
                    for (int i = index; i < end; i++) result.Add(Clone(cues[i]));
                }
                index = end;
            }
            return result;
        }
        private static List<SubtitleCue> RepairOverlaps(List<SubtitleCue> cues, ref int removed)
        {
            List<SubtitleCue> result = new List<SubtitleCue>();
            foreach (SubtitleCue source in cues.OrderBy(delegate(SubtitleCue cue) { return cue.Start; }).ThenBy(delegate(SubtitleCue cue) { return cue.End; }))
            {
                SubtitleCue cue = Clone(source);
                if (result.Count > 0 && cue.Start < result[result.Count - 1].End)
                {
                    SubtitleCue previous = result[result.Count - 1];
                    previous.End = cue.Start;
                    if (previous.End - previous.Start < TimeSpan.FromMilliseconds(200))
                    {
                        result.RemoveAt(result.Count - 1);
                        removed++;
                    }
                }
                if (cue.End - cue.Start < TimeSpan.FromMilliseconds(200))
                {
                    removed++;
                    continue;
                }
                result.Add(cue);
            }
            return result;
        }

        private static string CompactRepetition(string text)
        {
            string meaningful = Meaningful(text);
            if (!IsLowDiversity(meaningful)) return text;
            for (int unitLength = 1; unitLength <= Math.Min(6, meaningful.Length / 4); unitLength++)
            {
                int matches = 0;
                for (int i = 0; i < meaningful.Length; i++)
                    if (meaningful[i] == meaningful[i % unitLength]) matches++;
                if (matches * 100 >= meaningful.Length * 85)
                {
                    string unit = meaningful.Substring(0, unitLength);
                    return unit + "、" + unit + "、" + unit + "……";
                }
            }
            string sample = new string(meaningful.Take(Math.Min(3, meaningful.Length)).ToArray());
            return sample + "……";
        }

        private static bool IsLowDiversity(string meaningful)
        {
            if (meaningful.Length < 16) return false;
            int unique = meaningful.Distinct().Count();
            return unique <= 3 || unique * 100 <= meaningful.Length * 18;
        }

        private static bool IsVocalization(string normalized)
        {
            if (Vocalizations.Contains(normalized)) return true;
            return normalized.Length > 0 && normalized.Length <= 12 && normalized.Distinct().Count() == 1;
        }

        private static string Meaningful(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            StringBuilder builder = new StringBuilder(text.Length);
            foreach (char c in text)
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)) builder.Append(c);
            return builder.ToString();
        }

        private static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return SpaceRun.Replace(text.Replace('\r', ' ').Replace('\n', ' '), " ").Trim();
        }

        private static SubtitleCue Clone(SubtitleCue cue)
        {
            return new SubtitleCue { Id = cue.Id, Start = cue.Start, End = cue.End, Text = cue.Text };
        }

        private static void AddIssue(SubtitleQualityReport report, HashSet<int> suspicious, string kind, string severity,
            TimeSpan start, TimeSpan end, IEnumerable<int> cueIds, string text)
        {
            List<int> ids = cueIds.Distinct().ToList();
            foreach (int id in ids) suspicious.Add(id);
            string preview = NormalizeWhitespace(text);
            if (preview.Length > 160) preview = preview.Substring(0, 160) + "…";
            report.Issues.Add(new SubtitleQualityIssue
            {
                Kind = kind,
                Severity = severity,
                CueIds = ids,
                StartMilliseconds = (long)Math.Max(0, start.TotalMilliseconds),
                EndMilliseconds = (long)Math.Max(0, end.TotalMilliseconds),
                Text = preview
            });
        }
    }
}
