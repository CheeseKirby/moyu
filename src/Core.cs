using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PotPlayerAiSubtitle
{
    internal sealed class AppConfig
    {
        public string ApiBaseUrl { get; set; }
        public string Model { get; set; }
        public string FfmpegPath { get; set; }
        public string WhisperPath { get; set; }
        public string WhisperModelPath { get; set; }
        public string WhisperVadModelPath { get; set; }
        public string PotPlayerPath { get; set; }
        public string SubtitleHubPath { get; set; }
        public bool CopyFinishedSubtitlesBesideMedia { get; set; }
        public int SceneMaxCues { get; set; }
        public int SceneMaxSeconds { get; set; }
        public int ContextCueCount { get; set; }
        public int ApiRetryCount { get; set; }
        public bool MonitorPotPlayer { get; set; }
        public bool StartWithWindows { get; set; }
        public int UiSettingsVersion { get; set; }

        public static AppConfig CreateDefault()
        {
            string root = StoragePaths.Root;
            string playerRoot = Directory.GetParent(root).FullName;
            return new AppConfig
            {
                ApiBaseUrl = "https://api.deepseek.com",
                Model = "deepseek-v4-flash-vision-exp",
                FfmpegPath = Path.Combine(root, "Tools", "ffmpeg.exe"),
                WhisperPath = Path.Combine(root, "Tools", "Whisper", "Vulkan", "whisper-cli.exe"),
                WhisperModelPath = Path.Combine(playerRoot, "Model", "ggml-large-v3-turbo.bin"),
                WhisperVadModelPath = Path.Combine(root, "Tools", "Whisper", "Models", "ggml-silero-v6.2.0.bin"),
                PotPlayerPath = Path.Combine(playerRoot, "PotPlayerMini64.exe"),
                SubtitleHubPath = StoragePaths.Hub,
                CopyFinishedSubtitlesBesideMedia = true,
                SceneMaxCues = 35,
                SceneMaxSeconds = 150,
                ContextCueCount = 4,
                ApiRetryCount = 3,
                MonitorPotPlayer = true,
                StartWithWindows = true,
                UiSettingsVersion = 2
            };
        }

        public static AppConfig Load()
        {
            AppConfig defaults = CreateDefault();
            if (!File.Exists(StoragePaths.SettingsFile))
            {
                AtomicJson.Write(StoragePaths.SettingsFile, defaults);
                return defaults;
            }

            AppConfig loaded = AtomicJson.Read<AppConfig>(StoragePaths.SettingsFile, null);
            if (loaded == null) return defaults;
            if (string.IsNullOrWhiteSpace(loaded.ApiBaseUrl)) loaded.ApiBaseUrl = defaults.ApiBaseUrl;
            if (string.IsNullOrWhiteSpace(loaded.Model)) loaded.Model = defaults.Model;
            if (string.IsNullOrWhiteSpace(loaded.FfmpegPath)) loaded.FfmpegPath = defaults.FfmpegPath;
            if (string.IsNullOrWhiteSpace(loaded.WhisperPath)) loaded.WhisperPath = defaults.WhisperPath;
            if (string.IsNullOrWhiteSpace(loaded.WhisperModelPath)) loaded.WhisperModelPath = defaults.WhisperModelPath;
            if (string.IsNullOrWhiteSpace(loaded.WhisperVadModelPath)) loaded.WhisperVadModelPath = defaults.WhisperVadModelPath;
            if (string.IsNullOrWhiteSpace(loaded.PotPlayerPath)) loaded.PotPlayerPath = defaults.PotPlayerPath;
            if (string.IsNullOrWhiteSpace(loaded.SubtitleHubPath)) loaded.SubtitleHubPath = defaults.SubtitleHubPath;
            if (loaded.SceneMaxCues < 10) loaded.SceneMaxCues = defaults.SceneMaxCues;
            if (loaded.SceneMaxSeconds < 30) loaded.SceneMaxSeconds = defaults.SceneMaxSeconds;
            if (loaded.ContextCueCount < 0) loaded.ContextCueCount = defaults.ContextCueCount;
            if (loaded.ApiRetryCount < 1) loaded.ApiRetryCount = defaults.ApiRetryCount;
            if (loaded.UiSettingsVersion < 2)
            {
                if (loaded.UiSettingsVersion < 1)
                {
                    loaded.MonitorPotPlayer = true;
                    loaded.StartWithWindows = true;
                }
                loaded.UiSettingsVersion = 2;
                Save(loaded);
            }
            return loaded;
        }

        public static void Save(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            AtomicJson.Write(StoragePaths.SettingsFile, config);
        }
    }

    internal static class StoragePaths
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        public static readonly string Cache = Path.Combine(Root, "Cache");
        public static readonly string Config = Path.Combine(Root, "Config");
        public static readonly string Logs = Path.Combine(Root, "Logs");
        public static readonly string Queue = Path.Combine(Root, "Queue");
        public static readonly string Hub = Path.Combine(Root, "hub");
        public static readonly string SettingsFile = Path.Combine(Config, "settings.json");
        public static readonly string CurrentMediaFile = Path.Combine(Config, "current-media.json");
        public static readonly string DetectedMediaFile = Path.Combine(Config, "detected-media.json");

        public static void Ensure()
        {
            Directory.CreateDirectory(Cache);
            Directory.CreateDirectory(Config);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Queue);
            Directory.CreateDirectory(Hub);
        }
    }

    internal sealed class JobRequest
    {
        public string MediaPath { get; set; }
        public string RequestedUtc { get; set; }
    }

    internal sealed class CurrentMediaState
    {
        public string MediaPath { get; set; }
        public string UpdatedUtc { get; set; }
    }

    internal sealed class JobManifest
    {
        public string Version { get; set; }
        public string Fingerprint { get; set; }
        public long FileSize { get; set; }
        public string FirstSeenPath { get; set; }
        public string LastSeenPath { get; set; }
        public string SourceKind { get; set; }
        public string RecognitionModel { get; set; }
        public string TranslationModel { get; set; }
        public string Status { get; set; }
        public string UpdatedUtc { get; set; }
        public string Error { get; set; }
        public string QualityDisposition { get; set; }
        public string QualityWarning { get; set; }
    }

    internal sealed class TranslationState
    {
        public Dictionary<string, string> Translations { get; set; }
        public Dictionary<string, string> Glossary { get; set; }
        public List<int> CompletedScenes { get; set; }

        public TranslationState()
        {
            Translations = new Dictionary<string, string>();
            Glossary = new Dictionary<string, string>();
            CompletedScenes = new List<int>();
        }
    }

    internal sealed class ProgressInfo
    {
        public string Stage { get; set; }
        public string Detail { get; set; }
        public int Percent { get; set; }

        public ProgressInfo(string stage, string detail, int percent)
        {
            Stage = stage;
            Detail = detail;
            Percent = percent;
        }
    }

    internal static class AtomicJson
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };

        public static T Read<T>(string path, T fallback)
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                string json = File.ReadAllText(path, Encoding.UTF8);
                return Serializer.Deserialize<T>(json);
            }
            catch (Exception ex)
            {
                Logger.Write("Cannot read JSON " + path + ": " + ex.Message);
                return fallback;
            }
        }

        public static void Write(string path, object value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            string json = Serializer.Serialize(value);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
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

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        public static object DeserializeObject(string json)
        {
            return Serializer.DeserializeObject(json);
        }
    }

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static readonly string LogPath = Path.Combine(StoragePaths.Logs, "worker.log");

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(StoragePaths.Logs);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal static class ContentFingerprint
    {
        private const int ChunkSize = 256 * 1024;
        private const int SegmentCount = 9;

        public static string Compute(string path)
        {
            FileInfo info = new FileInfo(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 sha = SHA256.Create())
            {
                Add(sha, Encoding.ASCII.GetBytes("PotPlayerAiSubtitleFingerprint-v1"));
                Add(sha, BitConverter.GetBytes(info.Length));

                if (info.Length <= 16L * 1024L * 1024L)
                {
                    byte[] all = new byte[81920];
                    int read;
                    while ((read = stream.Read(all, 0, all.Length)) > 0) Add(sha, all, read);
                }
                else
                {
                    byte[] buffer = new byte[ChunkSize];
                    long maxStart = Math.Max(0, info.Length - ChunkSize);
                    for (int i = 0; i < SegmentCount; i++)
                    {
                        long offset = i == SegmentCount - 1 ? maxStart : (maxStart * i / (SegmentCount - 1));
                        Add(sha, BitConverter.GetBytes(offset));
                        stream.Position = offset;
                        int total = 0;
                        while (total < buffer.Length)
                        {
                            int read = stream.Read(buffer, total, buffer.Length - total);
                            if (read <= 0) break;
                            total += read;
                        }
                        Add(sha, buffer, total);
                    }
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static void Add(HashAlgorithm sha, byte[] bytes)
        {
            Add(sha, bytes, bytes.Length);
        }

        private static void Add(HashAlgorithm sha, byte[] bytes, int count)
        {
            if (count > 0) sha.TransformBlock(bytes, 0, count, bytes, 0);
        }

        private static string ToHex(byte[] value)
        {
            StringBuilder builder = new StringBuilder(value.Length * 2);
            for (int i = 0; i < value.Length; i++) builder.Append(value[i].ToString("x2"));
            return builder.ToString();
        }
    }
}
