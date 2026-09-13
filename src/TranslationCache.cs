using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PotPlayerAiSubtitle
{
    // Source/ASR is deliberately shared. Translation variants never overwrite legacy artifacts.
    internal sealed class TranslationCache
    {
        public const string BasePolicy = "base-3-context-conflicts";
        public const string ReviewPolicy = "review-5-verified-candidates";
        public string BaseKey { get; private set; }
        public string VariantKey { get; private set; }
        public string BaseStatePath { get; private set; }
        public string DirectoryPath { get; private set; }
        public string StatePath { get { return Path.Combine(DirectoryPath, "translation-state.json"); } }
        private string ReceiptPath { get { return Path.Combine(DirectoryPath, "complete.json"); } }

        public TranslationCache(string sourceDirectory, AppConfig config, IList<SubtitleCue> cues)
        {
            BaseKey = Hash(AtomicJson.Serialize(new object[] { BasePolicy,
                config.ApiBaseUrl.Trim().TrimEnd('/'), config.Model, config.SourceLanguage,
                config.ContextCueCount, config.SceneMaxCues, config.SceneMaxSeconds,
                cues.Select(c => new object[] { c.Id, c.Start.Ticks, c.End.Ticks, c.Text }).ToArray() }));
            bool quality = string.Equals(config.TranslationQuality, "quality", StringComparison.OrdinalIgnoreCase);
            VariantKey = Hash(AtomicJson.Serialize(new object[] { BaseKey, ReviewPolicy, quality,
                quality && QualityPolicy.Thinking(config), quality ? config.ReviewContextCount : 0,
                quality ? (QualityPolicy.IsIntensive(config) ? "intensive" : "standard") : "",
                QualityPolicy.IsIntensive(config) ? config.IntensiveModel : "",
                quality && config.EnableWebReference && config.WebPrivacyAccepted,
                quality ? FileHash(config.ReferenceSubtitlePath) : "",
                quality ? FileHash(TermIndexStore.PathFor(sourceDirectory)) : "" }));
            string baseDirectory = Path.Combine(sourceDirectory, "translations", BaseKey.Substring(0, 24));
            BaseStatePath = Path.Combine(baseDirectory, "base-state.json");
            DirectoryPath = Path.Combine(baseDirectory, VariantKey.Substring(0, 24));
        }

        public bool TryReadComplete(string chinese, string bilingual, out string warning)
        {
            warning = null;
            TranslationCacheReceipt receipt = AtomicJson.Read<TranslationCacheReceipt>(ReceiptPath, null);
            if (receipt != null && receipt.Signature == VariantKey
                && (receipt.StateHash != FileHash(StatePath) || receipt.ReportHash != FileHash(Path.Combine(DirectoryPath, "translation-quality-report.json"))
                    || receipt.ReferenceHash != FileHash(Path.Combine(DirectoryPath, "translation-references.json"))))
                throw new InvalidDataException("质量档缓存校验失败，已保留文件且未重新付费复核；请检查该版本缓存：" + DirectoryPath);
            if (receipt == null || receipt.Signature != VariantKey || !File.Exists(chinese) || !File.Exists(bilingual)
                || !File.Exists(StatePath) || receipt.ChineseHash != FileHash(chinese)
                || receipt.BilingualHash != FileHash(bilingual) || receipt.StateHash != FileHash(StatePath)
                || receipt.ReportHash != FileHash(Path.Combine(DirectoryPath, "translation-quality-report.json"))) return false;
            warning = receipt.Warning;
            var review = AtomicJson.Read<TranslationReviewReport>(Path.Combine(DirectoryPath, "translation-quality-report.json"), null);
            // An explicit rerun can resume known failed/skipped windows, without resetting any budget.
            if (review != null && review.CanResume) return false;
            return true;
        }

        public void Begin()
        {
            if (!File.Exists(ReceiptPath)) return;
            File.Copy(ReceiptPath, Path.Combine(DirectoryPath, "previous-complete.json"), true);
            File.Delete(ReceiptPath); // Only the commit marker; subtitle files and audit remain intact.
        }

        public void Complete(string chinese, string bilingual, string warning)
        {
            AtomicJson.Write(ReceiptPath, new TranslationCacheReceipt { Signature = VariantKey,
                ChineseHash = FileHash(chinese), BilingualHash = FileHash(bilingual), StateHash = FileHash(StatePath),
                ReportHash = FileHash(Path.Combine(DirectoryPath, "translation-quality-report.json")),
                ReferenceHash = FileHash(Path.Combine(DirectoryPath, "translation-references.json")), Warning = warning });
        }

        public static TranslationState Copy(TranslationState state)
        {
            return new TranslationState { Translations = new Dictionary<string, string>(state.Translations),
                Glossary = new Dictionary<string, string>(state.Glossary), CompletedScenes = new List<int>(state.CompletedScenes),
                GlossaryConflicts = (state.GlossaryConflicts ?? new Dictionary<string, List<string>>()).ToDictionary(p => p.Key, p => new List<string>(p.Value)) };
        }

        public static bool HasCue(TranslationState state, int id)
        {
            string value;
            return state.Translations != null && state.Translations.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out value)
                && !string.IsNullOrWhiteSpace(value);
        }

        public static string Hash(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        internal static string FileHash(string file)
        {
            if (!File.Exists(file)) return "";
            using (SHA256 sha = SHA256.Create())
            using (Stream stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }

    internal sealed class TranslationCacheReceipt
    {
        public string Signature { get; set; }
        public string ChineseHash { get; set; }
        public string BilingualHash { get; set; }
        public string StateHash { get; set; }
        public string ReportHash { get; set; }
        public string ReferenceHash { get; set; }
        public string Warning { get; set; }
    }
}
