using System.Drawing;
namespace PotPlayerAiSubtitle
{
    internal static class MoyuTypography
    {
        internal static readonly string FamilyName = FindFamily();
        private static string FindFamily()
        {
            using (System.Drawing.Text.InstalledFontCollection fonts = new System.Drawing.Text.InstalledFontCollection())
                foreach (FontFamily family in fonts.Families) if (family.Name == "Noto Sans SC") return family.Name;
            return "Microsoft YaHei UI";
        }
        internal static float LabelSize(float size)
        {
            if (size <= 8.5f) return 9f;
            if (size == 9f) return 10.5f;
            if (size == 21f) return 19f;
            return size;
        }
    }
}
