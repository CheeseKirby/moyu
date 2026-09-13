using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PotPlayerAiSubtitle
{
    internal sealed partial class MainForm
    {
        private ComboBox qualityModeBox;
        private TextBox intensiveModelBox, referenceSubtitleBox, searchKeyBox;
        private CheckBox webReferenceCheck;
        private NumericUpDown intensiveMinutesBox, intensiveRequestsBox, intensiveTokensBox, searchLimitBox;
        private bool standardThinkingPreference, intensiveThinkingPreference = true, loadingQualityOptions;
        private int previousQualityMode;

        private void BuildQualityOptions(FlowLayoutPanel flow)
        {
            CardPanel card = NewCard(0, 0, 860, 468, Color.White);
            card.Controls.Add(CreateLabel("质量方案与参考", 24, 18, 700, 27, 12F, FontStyle.Bold, TextColor));
            qualityModeBox = new ComboBox { Left = 24, Top = 60, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "质量方案" };
            qualityModeBox.Items.AddRange(new object[] { "标准 · 有限重点复核", "精修（候选）· 全片审校" }); qualityModeBox.SelectedIndex = 0;
            card.Controls.Add(qualityModeBox);
            var note = CreateLabel("精修仍待质量验收：候选经复核才采纳，仍可能误改；默认思考。", 302, 60, 530, 40, 8.5F, FontStyle.Regular, MutedColor); Stretch(note); card.Controls.Add(note);
            qualityModeBox.SelectedIndexChanged += delegate
            {
                if (loadingQualityOptions) return;
                if (previousQualityMode == 1) intensiveThinkingPreference = thinkingCheck.Checked; else standardThinkingPreference = thinkingCheck.Checked;
                previousQualityMode = qualityModeBox.SelectedIndex;
                thinkingCheck.Checked = previousQualityMode == 1 ? intensiveThinkingPreference : standardThinkingPreference;
                UpdateThinkingAvailability();
            };
            webReferenceCheck = new CheckBox { Left = 24, Top = 103, Width = 808, Height = 26, Text = "联网参考（Brave Search；无密钥或无可靠正文时仅提示）", ForeColor = TextColor }; Stretch(webReferenceCheck); card.Controls.Add(webReferenceCheck);
            AddFieldLabel(card, "搜索密钥（独立安全保存；不修改留空）", 24, 140);
            searchKeyBox = new TextBox { UseSystemPasswordChar = true, AccessibleName = "Brave Search 密钥" }; FrameInput(card, searchKeyBox, 24, 164, 808, true);
            AddFieldLabel(card, "参考 SRT（换作品请更换/清空）", 24, 215);
            referenceSubtitleBox = new TextBox { AccessibleName = "本地参考字幕" }; FrameInput(card, referenceSubtitleBox, 24, 239, 650, true);
            Button choose = CreateButton("选择 / 清空", AccentSoft, AccentColor, 688, 239, 146, 42); AnchorRight(choose);
            choose.Click += delegate
            {
                if (!string.IsNullOrWhiteSpace(referenceSubtitleBox.Text)) { referenceSubtitleBox.Clear(); return; }
                using (var dialog = new OpenFileDialog { Filter = "SRT 字幕|*.srt", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) referenceSubtitleBox.Text = dialog.FileName;
            }; card.Controls.Add(choose);
            AddFieldLabel(card, "精修模型（留空沿用主模型；同服务与凭据）", 24, 290);
            intensiveModelBox = new TextBox { AccessibleName = "精修模型" }; FrameInput(card, intensiveModelBox, 24, 314, 808, true);
            intensiveMinutesBox = Limit(card, "精修分钟 · 0不限", 24, 370, 10080);
            intensiveRequestsBox = Limit(card, "模型请求 · 0不限", 232, 370, 100000);
            intensiveTokensBox = Limit(card, "输出预留 · 0不限", 440, 370, 100000000);
            searchLimitBox = Limit(card, "搜索次数 · 0默认", 648, 370, 300);
            card.SizeChanged += delegate
            {
                if (card.ClientSize.Width < 700) return;
                int cell = (card.ClientSize.Width - 48) / 4;
                var inputs = new[] { intensiveMinutesBox, intensiveRequestsBox, intensiveTokensBox, searchLimitBox };
                for (int i = 0; i < inputs.Length; i++)
                { inputs[i].Left = 24 + i * cell; inputs[i].Width = cell - 16; ((Control)inputs[i].Tag).Left = inputs[i].Left; ((Control)inputs[i].Tag).Width = cell - 10; }
            };
            flow.Controls.Add(card); UpdateQualityOptionsAvailability();
        }
        private NumericUpDown Limit(Control card, string title, int left, int top, int maximum)
        {
            var label = CreateLabel(title, left, top, 194, 22, 8.5F, FontStyle.Regular, MutedColor); card.Controls.Add(label);
            var input = new NumericUpDown { Left = left, Top = top + 28, Width = 182, Height = 30, Maximum = maximum, Minimum = 0,
                Font = new Font(Font.FontFamily, 10F), ThousandsSeparator = true, AccessibleName = title };
            input.Tag = label; card.Controls.Add(input); return input;
        }
        private void LoadQualityOptions(AppConfig config)
        {
            loadingQualityOptions = true;
            standardThinkingPreference = config.EnableThinking; intensiveThinkingPreference = config.IntensiveThinking;
            previousQualityMode = config.QualityMode == "intensive" ? 1 : 0; qualityModeBox.SelectedIndex = previousQualityMode;
            thinkingCheck.Checked = previousQualityMode == 1 ? intensiveThinkingPreference : standardThinkingPreference;
            webReferenceCheck.Checked = config.EnableWebReference;
            referenceSubtitleBox.Text = config.ReferenceSubtitlePath ?? ""; intensiveModelBox.Text = config.IntensiveModel ?? "";
            intensiveMinutesBox.Value = Math.Min(intensiveMinutesBox.Maximum, Math.Ceiling(config.IntensiveTimeLimitSeconds / 60m));
            intensiveRequestsBox.Value = Math.Min(intensiveRequestsBox.Maximum, config.IntensiveRequestLimit);
            intensiveTokensBox.Value = Math.Min(intensiveTokensBox.Maximum, config.IntensiveOutputTokenLimit);
            searchLimitBox.Value = Math.Min(searchLimitBox.Maximum, config.SearchRequestLimit);
            loadingQualityOptions = false; UpdateThinkingAvailability();
        }
        private void ReadQualityOptions(AppConfig config)
        {
            if (previousQualityMode == 1) intensiveThinkingPreference = thinkingCheck.Checked; else standardThinkingPreference = thinkingCheck.Checked;
            config.EnableThinking = standardThinkingPreference; config.IntensiveThinking = intensiveThinkingPreference;
            config.QualityMode = qualityModeBox.SelectedIndex == 1 ? "intensive" : "standard";
            config.EnableWebReference = webReferenceCheck.Checked; config.ReferenceSubtitlePath = referenceSubtitleBox.Text.Trim();
            config.IntensiveModel = intensiveModelBox.Text.Trim(); config.IntensiveTimeLimitSeconds = (int)intensiveMinutesBox.Value * 60;
            config.IntensiveRequestLimit = (int)intensiveRequestsBox.Value; config.IntensiveOutputTokenLimit = (int)intensiveTokensBox.Value;
            config.SearchRequestLimit = (int)searchLimitBox.Value; QualityPolicy.Validate(config, true);
        }
        private void UpdateQualityOptionsAvailability()
        {
            if (qualityModeBox == null) return;
            bool quality = translationQualityBox.TranslationQuality == "quality";
            qualityModeBox.Enabled = quality;
            bool intensive = quality && qualityModeBox.SelectedIndex == 1;
            if (webReferenceCheck == null) return;
            webReferenceCheck.Enabled = quality; referenceSubtitleBox.Enabled = quality;
            intensiveModelBox.Enabled = intensive; intensiveMinutesBox.Enabled = intensive; intensiveRequestsBox.Enabled = intensive; intensiveTokensBox.Enabled = intensive;
            searchLimitBox.Enabled = quality;
            thinkingCheck.Text = qualityModeBox.SelectedIndex == 1 ? "精修启用深度思考（全片精修与疑难裁决，可能较慢）" : "重点复核启用深度思考（质量档，耗时更长）";
        }
        private bool ConfirmQualityAction(string message)
        {
            if (InvokeRequired) return (bool)Invoke(new Func<string, bool>(ConfirmQualityAction), message);
            return MessageBox.Show(this, message, "质量任务确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
    }
}
