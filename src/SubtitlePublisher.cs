using System;
using System.Collections.Generic;
using System.IO;

namespace PotPlayerAiSubtitle
{
    internal sealed class SubtitlePublishResult
    {
        public string BilingualSidecarPath { get; set; }
        public bool BilingualSidecarWasAlreadyAvailable { get; set; }
        public bool BilingualSidecarChanged { get; set; }
        public string HubDirectory { get; set; }
        public string Warning { get; set; }
    }

    internal static class SubtitlePublisher
    {
        public static SubtitlePublishResult Publish(AppConfig config, string mediaPath, string sourcePath, string chinesePath, string bilingualPath)
        {
            SubtitlePublishResult result = new SubtitlePublishResult();
            List<string> warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            {
                result.Warning = "视频路径已经变化，字幕暂时只保存在内容指纹缓存中";
                return result;
            }

            string mediaName = Path.GetFileNameWithoutExtension(mediaPath);
            string safeName = MakeSafeFileName(mediaName);
            string cacheDirectory = SourceCacheDirectory(bilingualPath);
            string videoCacheDirectory = config.SourceLanguage == "ja" ? cacheDirectory : Directory.GetParent(cacheDirectory).FullName;

            if (config.CopyFinishedSubtitlesBesideMedia)
            {
                try
                {
                    string mediaDirectory = Path.GetDirectoryName(mediaPath);
                    string sidecar = Path.Combine(mediaDirectory, mediaName + ".srt");
                    bool sidecarMatchesBilingual = File.Exists(sidecar) && FilesEqual(sidecar, bilingualPath);
                    if (File.Exists(sidecar)
                        && !sidecarMatchesBilingual
                        && !FilesEqual(sidecar, sourcePath)
                        && !FilesEqual(sidecar, chinesePath)
                        && !IsGeneratedBilingual(sidecar, videoCacheDirectory))
                    {
                        warnings.Add("视频旁已有非本工具生成的同名字幕，为避免覆盖，双语字幕只归档到字幕库");
                    }
                    else
                    {
                        // An identical sidecar must not be replaced: PotPlayer watches subtitle file changes,
                        // and rewriting it can trigger another subtitle reload while a video is playing.
                        if (!sidecarMatchesBilingual)
                        {
                            CopyAtomic(bilingualPath, sidecar);
                            result.BilingualSidecarChanged = true;
                        }
                        result.BilingualSidecarWasAlreadyAvailable = sidecarMatchesBilingual;
                        result.BilingualSidecarPath = sidecar;
                    }
                    RemoveLegacyCopyIfOwned(Path.Combine(mediaDirectory, mediaName + ".zh-CN.srt"), chinesePath);
                    RemoveLegacyCopyIfOwned(Path.Combine(mediaDirectory, mediaName + ".ja-zh-CN.srt"), bilingualPath);
                }
                catch (Exception ex)
                {
                    Logger.Write("Cannot publish bilingual subtitle beside media: " + ex.Message);
                    warnings.Add("视频旁双语字幕保存失败，完整结果仍保留在缓存和字幕库中");
                }
            }

            try
            {
                string hubRoot = string.IsNullOrWhiteSpace(config.SubtitleHubPath) ? StoragePaths.Hub : Path.GetFullPath(config.SubtitleHubPath);
                string hubDirectory = Path.Combine(hubRoot, safeName + "字幕");
                Directory.CreateDirectory(hubDirectory);
                CopyAtomic(bilingualPath, Path.Combine(hubDirectory, safeName + "-双语.srt"));
                CopyAtomic(sourcePath, Path.Combine(hubDirectory, safeName + "-源语言.srt"));
                CopyAtomic(chinesePath, Path.Combine(hubDirectory, safeName + "-中文.srt"));
                result.HubDirectory = hubDirectory;
            }
            catch (Exception ex)
            {
                Logger.Write("Cannot publish subtitle hub: " + ex.Message);
                warnings.Add("字幕库归档失败，字幕仍保留在内容指纹缓存中");
            }

            result.Warning = warnings.Count == 0 ? null : string.Join("；", warnings.ToArray());
            return result;
        }

        private static string SourceCacheDirectory(string bilingualPath)
        {
            DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(bilingualPath));
            DirectoryInfo parent = directory.Parent;
            // Current variants: source/translations/base-hash/variant-hash. Legacy outputs stay flat.
            if (parent != null && parent.Parent != null && parent.Parent.Parent != null
                && parent.Parent.Name == "translations" && IsCacheKey(directory.Name) && IsCacheKey(parent.Name))
                return parent.Parent.Parent.FullName;
            return directory.FullName;
        }

        private static bool IsCacheKey(string value)
        {
            if (value.Length != 24) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        internal static bool IsGeneratedBilingual(string path, string videoCacheDirectory)
        {
            if (string.IsNullOrWhiteSpace(videoCacheDirectory) || !File.Exists(path)) return false;
            foreach (SourceLanguageOption language in SourceLanguages.Options)
            {
                string generated = Path.Combine(SourceLanguages.CacheDirectory(videoCacheDirectory, language.Code),
                    language.Code + "-zh-CN.srt");
                if (FilesEqual(path, generated)) return true;
                string variants = Path.Combine(SourceLanguages.CacheDirectory(videoCacheDirectory, language.Code), "translations");
                if (Directory.Exists(variants))
                    foreach (string variant in Directory.GetFiles(variants, language.Code + "-zh-CN.srt", SearchOption.AllDirectories))
                        if (FilesEqual(path, variant)) return true;
            }
            return false;
        }

        private static string MakeSafeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = (value ?? "").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            string safe = new string(chars).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(safe) ? "视频" : safe;
        }

        private static void RemoveLegacyCopyIfOwned(string legacyPath, string generatedPath)
        {
            try
            {
                if (File.Exists(legacyPath) && FilesEqual(legacyPath, generatedPath)) File.Delete(legacyPath);
            }
            catch (Exception ex)
            {
                Logger.Write("Cannot remove legacy subtitle copy: " + ex.Message);
            }
        }

        private static bool FilesEqual(string first, string second)
        {
            FileInfo a = new FileInfo(first);
            FileInfo b = new FileInfo(second);
            if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
            byte[] left = new byte[81920];
            byte[] right = new byte[81920];
            using (FileStream x = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (FileStream y = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                while (true)
                {
                    int xr = x.Read(left, 0, left.Length);
                    int yr = y.Read(right, 0, right.Length);
                    if (xr != yr) return false;
                    if (xr == 0) return true;
                    for (int i = 0; i < xr; i++) if (left[i] != right[i]) return false;
                }
            }
        }

        private static void CopyAtomic(string source, string destination)
        {
            string directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            File.Copy(source, temporary, true);
            try
            {
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            catch
            {
                File.Copy(temporary, destination, true);
                File.Delete(temporary);
            }
        }
    }
}
