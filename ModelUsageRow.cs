using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AntiGravityMeter
{
    /// <summary>
    /// Compact single-line row (18px): provider dot, model name, thin progress bar, % and reset countdown.
    /// </summary>
    public class ModelUsageRow : Control
    {
        private string _modelName = "";
        private string _provider = "";
        private int _percent = 100;
        private DateTime _resetTime = DateTime.MinValue;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string ModelName
        {
            get => _modelName;
            set { if (_modelName != value) { _modelName = value; Invalidate(); } }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Provider
        {
            get => _provider;
            set { if (_provider != value) { _provider = value; Invalidate(); } }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Percent
        {
            get => _percent;
            set { int v = Math.Clamp(value, 0, 100); if (_percent != v) { _percent = v; Invalidate(); } }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DateTime ResetTime
        {
            get => _resetTime;
            set { _resetTime = value; Invalidate(); }
        }

        public ModelUsageRow()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            Height = 18;
            BackColor = Color.FromArgb(30, 36, 48);
        }

        public static Color ProviderColor(string provider) => provider switch
        {
            "Google" => Color.FromArgb(66, 133, 244),
            "Anthropic" => Color.FromArgb(217, 119, 87),
            "OpenAI" => Color.FromArgb(16, 163, 127),
            _ => Color.FromArgb(145, 158, 178)
        };

        public static Color PercentColor(int percent) =>
            percent >= 70 ? Color.FromArgb(46, 204, 113)
            : percent >= 30 ? Color.FromArgb(243, 156, 18)
            : Color.FromArgb(231, 76, 60);

        public static string FormatShortSpan(DateTime resetLocal)
        {
            if (resetLocal == DateTime.MinValue) return "";
            var span = resetLocal - DateTime.Now;
            if (span.TotalMinutes < 1) return "soon";
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{span.Minutes}m";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, rect);

            int midY = rect.Height / 2;

            // Layout (right to left): reset text | % | bar | name
            const int resetW = 46;
            const int pctW = 32;
            const int barW = 34;
            int resetX = rect.Width - resetW - 4;
            int pctX = resetX - pctW;
            int barX = pctX - barW - 4;

            // 1. Provider dot (5px)
            using (var dot = new SolidBrush(ProviderColor(_provider)))
            {
                g.FillEllipse(dot, 6, midY - 2, 5, 5);
            }

            using var nameFont = new Font("Segoe UI", 7.6f, FontStyle.Regular);
            using var smallFont = new Font("Segoe UI", 7.0f, FontStyle.Regular);
            using var pctFont = new Font("Segoe UI", 7.8f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.FromArgb(232, 237, 245));
            using var mutedBrush = new SolidBrush(Color.FromArgb(145, 160, 180));

            // 2. Model name (ellipsis when too long)
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(_modelName, nameFont, textBrush, new RectangleF(16, 0, barX - 18, rect.Height), sf);
            }

            // 3. Thin progress bar (4px)
            var barRect = new Rectangle(barX, midY - 2, barW, 4);
            using (var track = new SolidBrush(Color.FromArgb(44, 52, 68)))
            {
                g.FillRectangle(track, barRect);
            }
            int fillW = (int)Math.Round(barW * (_percent / 100.0));
            if (fillW > 0)
            {
                using var fill = new SolidBrush(PercentColor(_percent));
                g.FillRectangle(fill, new Rectangle(barX, midY - 2, fillW, 4));
            }

            // 4. Percentage & reset countdown (right aligned)
            using (var sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString($"{_percent}%", pctFont, textBrush, new RectangleF(pctX, 0, pctW, rect.Height), sfRight);
                string reset = _percent < 100 ? FormatShortSpan(_resetTime) : "";
                if (reset.Length > 0)
                {
                    g.DrawString("⟳" + reset, smallFont, mutedBrush, new RectangleF(resetX, 0, resetW, rect.Height), sfRight);
                }
            }
        }
    }
}
