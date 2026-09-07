using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal sealed class RecognitionQualityException : Exception
    {
        public RecognitionQualityException(string message) : base(message) { }
    }

    internal sealed class RecognitionCandidateResult
    {
        public string SourceKind { get; set; }
        public string CandidatePath { get; set; }
        public string AudioPath { get; set; }
        public SubtitleQualityReport QualityReport { get; set; }
    }

    internal sealed class SourceSubtitleRecognizer
    {
        private const double SegmentSeconds = 300;
        private const double SegmentOverlapSeconds = 1.25;
        private const int MaximumRetryRegions = 48;
        private const int MaximumFinalRetryRegions = 12;
        private const string SegmentedRecognitionVersion = "local-whisper-segmented-v2";
        private readonly AppConfig config;
        private readonly Action<string, string, int> progress;

        public SourceSubtitleRecognizer(AppConfig config, Action<string, string, int> progress)
        {
            this.config = config;
            this.progress = progress;
        }

        public RecognitionCandidateResult Prepare(string mediaPath, string cacheDir, string rawPath, string candidatePath,
            string reportPath, CancellationToken cancellation)
        {
            string external = SrtFile.FindSourceSrtBesideMedia(mediaPath, config.SourceLanguage,
                config.SourceLanguage == "ja" ? cacheDir : Directory.GetParent(cacheDir).FullName);
            if (!string.IsNullOrEmpty(external))
            {
                Report("已找到源语言字幕", Path.GetFileName(external), 25);
                InvalidateSegmentedRawCache(rawPath);
                File.Copy(external, rawPath, true);
                return WriteTrustedCandidate(SrtFile.Read(rawPath), "external-" + config.SourceLanguage + "-srt", candidatePath, reportPath);
            }

            string ffmpeg = ToolProcess.ResolveExecutable(config.FfmpegPath, "ffmpeg.exe");
            if (!string.IsNullOrEmpty(ffmpeg))
            {
                Report("正在检查内嵌字幕", "尝试提取视频中的源语言字幕轨道。", 10);
                string embedded = Path.Combine(cacheDir, "embedded-" + config.SourceLanguage + ".srt");
                foreach (string tag in SourceLanguages.Get(config.SourceLanguage).SubtitleTags)
                {
                    string args = "-nostdin -hide_banner -loglevel error -y -i " + ToolProcess.Quote(mediaPath)
                        + " -map 0:s:m:language:" + tag + " -c:s srt " + ToolProcess.Quote(embedded);
                    int code = ToolProcess.Run(ffmpeg, args, cacheDir, cancellation, true);
                    if (code == 0 && File.Exists(embedded) && new FileInfo(embedded).Length > 20)
                    {
                        List<SubtitleCue> cues = SrtFile.Read(embedded);
                        if (cues.Count > 0)
                        {
                            InvalidateSegmentedRawCache(rawPath);
                            File.Copy(embedded, rawPath, true);
                            return WriteTrustedCandidate(cues, "embedded-" + config.SourceLanguage + "-subtitle", candidatePath, reportPath);
                        }
                    }
                }
            }

            return RecognizeLocalCandidate(mediaPath, cacheDir, rawPath, candidatePath, reportPath, cancellation);
        }

        public RecognitionCandidateResult RecognizeLocalCandidate(string mediaPath, string cacheDir, string rawPath,
            string candidatePath, string reportPath, CancellationToken cancellation)
        {
            string ffmpeg = ToolProcess.ResolveExecutable(config.FfmpegPath, "ffmpeg.exe");
            if (string.IsNullOrEmpty(ffmpeg)) throw new FileNotFoundException("没有找到 ffmpeg.exe。", config.FfmpegPath);
            if (!File.Exists(config.WhisperPath)) throw new FileNotFoundException("没有找到 PotPlayer 的 Whisper 命令行程序。", config.WhisperPath);
            if (!File.Exists(config.WhisperModelPath)) throw new FileNotFoundException("没有找到 Whisper 模型。", config.WhisperModelPath);
            if (!File.Exists(config.WhisperVadModelPath)) throw new FileNotFoundException("没有找到 Whisper VAD 模型。", config.WhisperVadModelPath);

            string audio = EnsureAudio(mediaPath, cacheDir, ffmpeg, cancellation);
            TimeSpan duration = WaveAudio.GetDuration(audio);
            if (duration <= TimeSpan.Zero) throw new InvalidDataException("无法读取临时音频时长。");

            string temporary = Path.Combine(cacheDir, "recognition-temp");
            ResetTemporaryDirectory(temporary, cacheDir);
            try
            {
                List<SubtitleCue> initial;
                if (TryReadSegmentedRawCache(rawPath, audio, out initial))
                {
                    Report("正在复用分段识别结果", "识别版本和输入未变化，只重新执行质量复核。", 38);
                }
                else
                {
                    Report("正在分段识别源语言字幕", "按原始时间轴分段处理，避免错误上下文扩散。", 24);
                    InvalidateSegmentedRawCache(rawPath);
                    initial = RecognizeSegmented(audio, duration, temporary, cancellation);
                    if (initial.Count == 0) throw new InvalidDataException("Whisper 没有识别到有效对白。");
                    SrtFile.Write(rawPath, initial, null, false);
                    WriteSegmentedRawCacheMarker(rawPath, audio);
                }

                SubtitleQualityReport initialReport = SubtitleQuality.Analyze(initial);
                List<SubtitleTimeRange> retryRanges = SubtitleQuality.BuildRetryRanges(initial, initialReport, duration, MaximumRetryRegions);
                List<SubtitleCue> retried = new List<SubtitleCue>(initial);
                int retriedCueCount = RetryRanges(retried, audio, retryRanges, temporary, 0,
                    "正在复核可疑片段", 38, 5, cancellation);

                SubtitleSanitizeResult sanitized = SubtitleQuality.SanitizeAfterRetry(retried);
                List<SubtitleCue> candidateCues = sanitized.Cues;
                int removedCueCount = sanitized.RemovedCount;
                int compactedCueCount = sanitized.CompactedCount;
                SubtitleQualityReport finalReport = SubtitleQuality.Analyze(candidateCues);

                List<SubtitleTimeRange> finalRetryRanges = SubtitleQuality.BuildRetryRanges(
                    candidateCues, finalReport, duration, MaximumFinalRetryRegions);
                if (finalRetryRanges.Count > 0)
                {
                    List<SubtitleCue> finalRetried = new List<SubtitleCue>(candidateCues);
                    retriedCueCount += RetryRanges(finalRetried, audio, finalRetryRanges, temporary, 1000,
                        "正在复核残留异常", 43, 1, cancellation);
                    SubtitleSanitizeResult finalSanitized = SubtitleQuality.SanitizeAfterRetry(finalRetried);
                    candidateCues = finalSanitized.Cues;
                    removedCueCount += finalSanitized.RemovedCount;
                    compactedCueCount += finalSanitized.CompactedCount;
                    finalReport = SubtitleQuality.Analyze(candidateCues);
                }

                finalReport.GateApplied = true;
                finalReport.InitialCueCount = initial.Count;
                finalReport.InitialSuspiciousCueCount = initialReport.SuspiciousCueCount;
                finalReport.RetryRegionCount = retryRanges.Count + finalRetryRanges.Count;
                finalReport.RetriedCueCount = retriedCueCount;
                finalReport.RemovedCueCount = removedCueCount;
                finalReport.CompactedCueCount = compactedCueCount;
                finalReport.Passed = finalReport.Passed && candidateCues.Count > 0;

                SrtFile.Write(candidatePath, candidateCues, null, false);
                AtomicJson.Write(reportPath, finalReport);
                Report(finalReport.Passed ? "源语言字幕质量检查通过" : "源语言字幕需要检查",
                    string.Format(CultureInfo.InvariantCulture, "候选字幕 {0} 条，可疑 {1} 条，最长 {2:0.0} 秒。",
                    finalReport.CueCount, finalReport.SuspiciousCueCount, finalReport.LongestSeconds), 44);

                return new RecognitionCandidateResult
                {
                    SourceKind = SegmentedRecognitionVersion,
                    CandidatePath = candidatePath,
                    AudioPath = audio,
                    QualityReport = finalReport
                };
            }
            finally
            {
                TryDeleteTemporaryDirectory(temporary, cacheDir);
            }
        }

        public static void DeleteAcceptedAudio(RecognitionCandidateResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.AudioPath)) return;
            try { File.Delete(result.AudioPath + ".ready"); } catch { }
            try { File.Delete(result.AudioPath); } catch { }
        }

        private RecognitionCandidateResult WriteTrustedCandidate(List<SubtitleCue> cues, string sourceKind, string candidatePath, string reportPath)
        {
            SubtitleQualityReport report = SubtitleQuality.Analyze(cues);
            report.GateApplied = false;
            report.Passed = cues.Count > 0;
            report.InitialCueCount = cues.Count;
            report.InitialSuspiciousCueCount = report.SuspiciousCueCount;
            SrtFile.Write(candidatePath, cues, null, false);
            AtomicJson.Write(reportPath, report);
            return new RecognitionCandidateResult { SourceKind = sourceKind, CandidatePath = candidatePath, QualityReport = report };
        }

        private bool TryReadSegmentedRawCache(string rawPath, string audioPath, out List<SubtitleCue> cues)
        {
            cues = new List<SubtitleCue>();
            string markerPath = GetSegmentedRawCacheMarkerPath(rawPath);
            if (!File.Exists(rawPath) || !File.Exists(markerPath)) return false;
            try
            {
                string expected = BuildSegmentedRawCacheSignature(rawPath, audioPath);
                string actual = File.ReadAllText(markerPath, Encoding.UTF8).Trim();
                if (!string.Equals(actual, expected, StringComparison.Ordinal)) return false;
                cues = SrtFile.Read(rawPath);
                return cues.Count > 0;
            }
            catch (Exception ex)
            {
                Logger.Write("Cannot reuse segmented recognition cache: " + ex.Message);
                cues = new List<SubtitleCue>();
                return false;
            }
        }

        private void WriteSegmentedRawCacheMarker(string rawPath, string audioPath)
        {
            string markerPath = GetSegmentedRawCacheMarkerPath(rawPath);
            File.WriteAllText(markerPath, BuildSegmentedRawCacheSignature(rawPath, audioPath), new UTF8Encoding(false));
        }

        private static void InvalidateSegmentedRawCache(string rawPath)
        {
            try { File.Delete(GetSegmentedRawCacheMarkerPath(rawPath)); } catch { }
        }

        private static string GetSegmentedRawCacheMarkerPath(string rawPath)
        {
            return rawPath + ".segmented-v2.ready";
        }

        private string BuildSegmentedRawCacheSignature(string rawPath, string audioPath)
        {
            return string.Join("|", new[]
            {
                SegmentedRecognitionVersion,
                SegmentSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                SegmentOverlapSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                config.SourceLanguage,
                DescribeCacheInput(rawPath),
                DescribeCacheInput(audioPath),
                DescribeCacheInput(config.WhisperPath),
                DescribeCacheInput(config.WhisperModelPath),
                DescribeCacheInput(config.WhisperVadModelPath)
            });
        }

        private static string DescribeCacheInput(string path)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists) return "missing:" + Path.GetFileName(path);
            return Path.GetFileName(path) + ":" + file.Length.ToString(CultureInfo.InvariantCulture)
                + ":" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        private string EnsureAudio(string mediaPath, string cacheDir, string ffmpeg, CancellationToken cancellation)
        {
            string audio = Path.Combine(cacheDir, "audio-16k.wav");
            string ready = audio + ".ready";
            if (File.Exists(audio) && File.Exists(ready) && new FileInfo(audio).Length > 1024)
            {
                Report("正在复用已提取音频", "继续识别，无需再次读取整部视频。", 18);
                return audio;
            }

            try { File.Delete(ready); } catch { }
            try { File.Delete(audio); } catch { }
            Report("正在提取音频", "生成供本地 Whisper 使用的单声道音频。", 15);
            string args = "-nostdin -hide_banner -loglevel error -y -i " + ToolProcess.Quote(mediaPath)
                + " -vn -ac 1 -ar 16000 -c:a pcm_s16le " + ToolProcess.Quote(audio);
            int code = ToolProcess.Run(ffmpeg, args, cacheDir, cancellation, false);
            if (code != 0 || !File.Exists(audio) || new FileInfo(audio).Length <= 1024)
                throw new InvalidOperationException("音频提取失败。请查看 Logs\\worker.log。");
            File.WriteAllText(ready, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            return audio;
        }

        private List<SubtitleCue> RecognizeSegmented(string audio, TimeSpan duration, string temporary, CancellationToken cancellation)
        {
            List<SubtitleCue> merged = new List<SubtitleCue>();
            int segmentCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds / SegmentSeconds));
            for (int i = 0; i < segmentCount; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                double coreStart = i * SegmentSeconds;
                double coreEnd = Math.Min(duration.TotalSeconds, (i + 1) * SegmentSeconds);
                double extractionStart = Math.Max(0, coreStart - SegmentOverlapSeconds);
                double extractionEnd = Math.Min(duration.TotalSeconds, coreEnd + SegmentOverlapSeconds);
                string clip = Path.Combine(temporary, "segment-" + i.ToString("000", CultureInfo.InvariantCulture) + ".wav");
                ExtractClip(audio, clip, extractionStart, extractionEnd - extractionStart, cancellation);
                int percent = 24 + (int)(14.0 * (i + 1) / segmentCount);
                Report("正在分段识别源语言字幕", string.Format(CultureInfo.InvariantCulture,
                    "片段 {0}/{1}，{2}-{3}", i + 1, segmentCount,
                    FormatClock(TimeSpan.FromSeconds(coreStart)), FormatClock(TimeSpan.FromSeconds(coreEnd))), percent);
                List<SubtitleCue> local = RunWhisper(clip, Path.Combine(temporary, "segment-" + i.ToString("000", CultureInfo.InvariantCulture)), false, cancellation);
                foreach (SubtitleCue cue in local)
                {
                    SubtitleCue adjusted = Offset(cue, TimeSpan.FromSeconds(extractionStart));
                    TimeSpan midpoint = adjusted.Start + TimeSpan.FromTicks((adjusted.End - adjusted.Start).Ticks / 2);
                    if (midpoint >= TimeSpan.FromSeconds(coreStart) && (i == segmentCount - 1 || midpoint < TimeSpan.FromSeconds(coreEnd)))
                        merged.Add(adjusted);
                }
            }
            return Renumber(merged);
        }

        private List<SubtitleCue> RecognizeRange(string audio, SubtitleTimeRange range, string temporary, int index, CancellationToken cancellation)
        {
            string name = "retry-" + index.ToString("000", CultureInfo.InvariantCulture);
            string clip = Path.Combine(temporary, name + ".wav");
            ExtractClip(audio, clip, range.Start.TotalSeconds, (range.End - range.Start).TotalSeconds, cancellation);
            List<SubtitleCue> local = RunWhisper(clip, Path.Combine(temporary, name), true, cancellation);
            List<SubtitleCue> adjusted = new List<SubtitleCue>();
            foreach (SubtitleCue cue in local)
            {
                SubtitleCue global = Offset(cue, range.Start);
                TimeSpan midpoint = global.Start + TimeSpan.FromTicks((global.End - global.Start).Ticks / 2);
                if (midpoint >= range.Start && midpoint < range.End) adjusted.Add(global);
            }
            return adjusted;
        }

        private int RetryRanges(List<SubtitleCue> cues, string audio, IList<SubtitleTimeRange> ranges,
            string temporary, int fileIndexOffset, string stage, int startPercent, int percentSpan,
            CancellationToken cancellation)
        {
            int retriedCueCount = 0;
            for (int i = 0; i < ranges.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                SubtitleTimeRange range = ranges[i];
                int percent = startPercent + (int)(percentSpan * (i + 1.0) / Math.Max(1, ranges.Count));
                Report(stage, string.Format(CultureInfo.InvariantCulture,
                    "局部重识别 {0}/{1}，{2}-{3}", i + 1, ranges.Count,
                    FormatClock(range.Start), FormatClock(range.End)), percent);
                List<SubtitleCue> replacement = RecognizeRange(audio, range, temporary, fileIndexOffset + i, cancellation);
                retriedCueCount += replacement.Count;
                ReplaceRange(cues, range, replacement);
            }
            return retriedCueCount;
        }

        private void ReplaceRange(List<SubtitleCue> cues, SubtitleTimeRange range, List<SubtitleCue> replacement)
        {
            cues.RemoveAll(delegate(SubtitleCue cue)
            {
                TimeSpan midpoint = cue.Start + TimeSpan.FromTicks((cue.End - cue.Start).Ticks / 2);
                return midpoint >= range.Start && midpoint < range.End;
            });
            cues.AddRange(replacement);
            List<SubtitleCue> ordered = Renumber(cues);
            cues.Clear();
            cues.AddRange(ordered);
        }

        private void ExtractClip(string audio, string output, double startSeconds, double durationSeconds, CancellationToken cancellation)
        {
            string ffmpeg = ToolProcess.ResolveExecutable(config.FfmpegPath, "ffmpeg.exe");
            string args = "-nostdin -hide_banner -loglevel error -y -ss "
                + startSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " -t "
                + Math.Max(0.1, durationSeconds).ToString("0.000", CultureInfo.InvariantCulture)
                + " -i " + ToolProcess.Quote(audio) + " -ac 1 -ar 16000 -c:a pcm_s16le " + ToolProcess.Quote(output);
            int code = ToolProcess.Run(ffmpeg, args, Path.GetDirectoryName(output), cancellation, false);
            if (code != 0 || !File.Exists(output) || new FileInfo(output).Length <= 44)
                throw new InvalidOperationException("识别音频分段失败。请查看 Logs\\worker.log。");
        }

        internal string BuildWhisperArguments(string clip, string outputBase, bool retry)
        {
            string playerRoot = Directory.GetParent(StoragePaths.Root).FullName;
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            string args = "-m " + ToolProcess.Quote(ToolProcess.RelativePathUnder(config.WhisperModelPath, playerRoot))
                + " -f " + ToolProcess.Quote(ToolProcess.RelativePathUnder(clip, playerRoot))
                + " -l " + config.SourceLanguage + " -osrt -t " + threads.ToString(CultureInfo.InvariantCulture)
                + " -p 1 --vad -vm " + ToolProcess.Quote(ToolProcess.RelativePathUnder(config.WhisperVadModelPath, playerRoot))
                + (retry ? " -vt 0.45 -vspd 180 -vsd 400 -vmsd 20 -vp 120 -vo 0.08" : " -vt 0.35 -vspd 180 -vsd 300 -vmsd 25 -vp 150 -vo 0.10")
                + " -mc 0 -ml 42 -nf -sns -of " + ToolProcess.Quote(ToolProcess.RelativePathUnder(outputBase, playerRoot));
            return args;
        }

        private List<SubtitleCue> RunWhisper(string clip, string outputBase, bool retry, CancellationToken cancellation)
        {
            string playerRoot = Directory.GetParent(StoragePaths.Root).FullName;
            int code = ToolProcess.Run(config.WhisperPath, BuildWhisperArguments(clip, outputBase, retry), playerRoot, cancellation, false);
            string output = outputBase + ".srt";
            if (code != 0) throw new InvalidOperationException("Whisper 分段识别失败。请查看 Logs\\worker.log。");
            if (!File.Exists(output) || new FileInfo(output).Length <= 10) return new List<SubtitleCue>();
            return SrtFile.Read(output);
        }

        private static SubtitleCue Offset(SubtitleCue cue, TimeSpan offset)
        {
            return new SubtitleCue { Id = cue.Id, Start = cue.Start + offset, End = cue.End + offset, Text = cue.Text };
        }

        private static List<SubtitleCue> Renumber(IEnumerable<SubtitleCue> cues)
        {
            List<SubtitleCue> ordered = cues.OrderBy(delegate(SubtitleCue cue) { return cue.Start; }).ThenBy(delegate(SubtitleCue cue) { return cue.End; }).ToList();
            for (int i = 0; i < ordered.Count; i++) ordered[i].Id = i + 1;
            return ordered;
        }

        private static string FormatClock(TimeSpan time)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds);
        }

        private static void ResetTemporaryDirectory(string path, string cacheDir)
        {
            TryDeleteTemporaryDirectory(path, cacheDir);
            Directory.CreateDirectory(path);
        }

        private static void TryDeleteTemporaryDirectory(string path, string cacheDir)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(cacheDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
            }
            catch (Exception ex) { Logger.Write("Cannot clean recognition temporary directory: " + ex.Message); }
        }

        private void Report(string stage, string detail, int percent)
        {
            if (progress != null) progress(stage, detail, Math.Max(0, Math.Min(100, percent)));
        }
    }

    internal static class WaveAudio
    {
        public static TimeSpan GetDuration(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII))
            {
                if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("临时音频不是 RIFF WAV。");
                reader.ReadUInt32();
                if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("临时音频不是 WAVE。");
                uint byteRate = 0;
                long dataSize = 0;
                while (stream.Position + 8 <= stream.Length)
                {
                    string id = new string(reader.ReadChars(4));
                    uint size = reader.ReadUInt32();
                    long next = stream.Position + size + (size % 2);
                    if (id == "fmt " && size >= 12)
                    {
                        reader.ReadUInt16();
                        reader.ReadUInt16();
                        reader.ReadUInt32();
                        byteRate = reader.ReadUInt32();
                    }
                    else if (id == "data") dataSize += size;
                    stream.Position = Math.Min(next, stream.Length);
                }
                if (byteRate == 0 || dataSize == 0) throw new InvalidDataException("临时音频缺少有效 WAV 数据。");
                return TimeSpan.FromSeconds(dataSize / (double)byteRate);
            }
        }
    }

    internal static class ToolProcess
    {
        public static string ResolveExecutable(string configured, string name)
        {
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        public static int Run(string executable, string arguments, string workingDirectory, CancellationToken cancellation, bool ignoreErrors)
        {
            Logger.Write(Path.GetFileName(executable) + " " + arguments);
            ProcessStartInfo start = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = new Process())
            {
                StringBuilder output = new StringBuilder();
                object sync = new object();
                DataReceivedEventHandler append = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sync)
                    {
                        if (output.Length < 65536) output.AppendLine(e.Data);
                    }
                };
                process.StartInfo = start;
                process.OutputDataReceived += append;
                process.ErrorDataReceived += append;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                while (!process.WaitForExit(250))
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        cancellation.ThrowIfCancellationRequested();
                    }
                }
                process.WaitForExit();
                if (process.ExitCode != 0 && output.Length > 0) Logger.Write(output.ToString().Trim());
                if (!ignoreErrors && process.ExitCode != 0) Logger.Write("Process exit code: " + process.ExitCode);
                return process.ExitCode;
            }
        }

        public static string RelativePathUnder(string path, string root)
        {
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return fullPath.Substring(fullRoot.Length);
            return fullPath;
        }

        public static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
