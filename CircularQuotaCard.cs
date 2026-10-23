using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AntiGravityMeter
{
    /// <summary>
    /// Compact 1/2 height quota card:
    /// [주간] 6일 23시간 (10/23)       50% ◔
    /// [5시간] 4시간 49분 (20:50)        0% ◔
    /// </summary>
    public class CircularQuotaCard : Control
    {
        private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

        private string _title = "주간";
        private string _subtitle = "";
        private int _percent = 100;
        private DateTime _resetTime = DateTime.MinValue;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; Invalidate(); } }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Subtitle
        {
            get => _subtitle;
            set { if (_subtitle != value) { _subtitle = value; Invalidate(); } }
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
            set { if (_resetTime != value) { _resetTime = value; Invalidate(); } }
        }

        public CircularQuotaCard()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            Height = 28;
            BackColor = Color.FromArgb(30, 36, 48);
        }

        public static string FormatShortCountdown(DateTime resetLocal)
        {
            if (resetLocal == DateTime.MinValue) return "";
            var span = resetLocal - DateTime.Now;
            if (span.TotalMinutes < 1) return "곧 리셋";
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}일 {span.Hours}시간";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}시간 {span.Minutes}분";
            return $"{span.Minutes}분";
        }

        public static string FormatShortDate(DateTime resetLocal)
        {
            if (resetLocal == DateTime.MinValue) return "";
            var span = resetLocal - DateTime.Now;
            // 24시간 이상 남았으면 날짜(M/d), 24시간 이내면 시각(HH:mm)
            if (span.TotalDays >= 1)
                return resetLocal.ToString("M/d", Ko);
            return resetLocal.ToString("HH:mm", Ko);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // 1. Background & Border
            using (var bgBrush = new SolidBrush(BackColor))
            {
                g.FillRectangle(bgBrush, rect);
            }
            using (var borderPen = new Pen(Color.FromArgb(48, 58, 78), 1))
            {
                g.DrawRectangle(borderPen, 0, 0, rect.Width - 1, rect.Height - 1);
            }

            int midY = rect.Height / 2;

            // Progress color
            Color progressColor = _percent >= 70 ? Color.FromArgb(46, 204, 113)
                                : _percent >= 30 ? Color.FromArgb(243, 156, 18)
                                : Color.FromArgb(231, 76, 60);

            // 2. Right: Mini Donut Ring (16px)
            const int gaugeSize = 16;
            int gaugeX = rect.Width - gaugeSize - 7;
            int gaugeY = (rect.Height - gaugeSize) / 2;
            var gaugeRect = new Rectangle(gaugeX, gaugeY, gaugeSize, gaugeSize);

            const float strokeWidth = 2.4f;
            using (var trackPen = new Pen(Color.FromArgb(44, 52, 68), strokeWidth))
            {
                g.DrawEllipse(trackPen, gaugeRect);
            }
            if (_percent > 0)
            {
                using var progPen = new Pen(progressColor, strokeWidth)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawArc(progPen, gaugeRect, -90f, (_percent / 100f) * 360f);
            }

            // 3. Percentage Text (Left of the mini donut ring)
            string pctText = $"{_percent}%";
            float pctX;
            using (var pctFont = new Font("Segoe UI", 8.8f, FontStyle.Bold))
            using (var pctBrush = new SolidBrush(Color.White))
            {
                var sz = g.MeasureString(pctText, pctFont);
                pctX = gaugeX - sz.Width - 3;
                g.DrawString(pctText, pctFont, pctBrush, pctX, (rect.Height - sz.Height) / 2f);
            }

            // 4. Left: Short Title Label (주간 / 5시간 / 남은량)
            const int labelX = 8;
            const int labelW = 42;
            using (var titleFont = new Font("Segoe UI", 8.2f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(Color.FromArgb(160, 175, 195)))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(_title, titleFont, titleBrush, new RectangleF(labelX, 0, labelW, rect.Height), sf);
            }

            // 5. Middle: Countdown (enlarged & highlighted) + Reset date/time
            float infoX = labelX + labelW + 2;
            float infoW = Math.Max(20, pctX - infoX - 4);

            string countdown = FormatShortCountdown(_resetTime);
            if (countdown.Length > 0)
            {
                string dateStr = FormatShortDate(_resetTime);
                using var cdFont = new Font("Segoe UI", 8.8f, FontStyle.Bold);
                using var dateFont = new Font("Segoe UI", 7.6f, FontStyle.Regular);
                using var cdBrush = new SolidBrush(Color.FromArgb(125, 195, 255)); // Sky Blue highlight
                using var dateBrush = new SolidBrush(Color.FromArgb(145, 160, 180));

                var cdSize = g.MeasureString(countdown, cdFont);
                float drawY = (rect.Height - cdSize.Height) / 2f;

                // Draw countdown
                g.DrawString(countdown, cdFont, cdBrush, infoX, drawY);

                // Draw date right next to countdown (e.g. " (10/23)" or " (20:50)")
                if (dateStr.Length > 0)
                {
                    string suffix = $" ({dateStr})";
                    var dateSize = g.MeasureString(suffix, dateFont);
                    float dateY = (rect.Height - dateSize.Height) / 2f;
                    g.DrawString(suffix, dateFont, dateBrush, infoX + cdSize.Width - 4, dateY);
                }
            }
            else if (!string.IsNullOrWhiteSpace(_subtitle))
            {
                using var subFont = new Font("Segoe UI", 7.6f, FontStyle.Regular);
                using var subBrush = new SolidBrush(Color.FromArgb(145, 160, 180));
                using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(_subtitle, subFont, subBrush, new RectangleF(infoX, 0, infoW, rect.Height), sf);
            }
        }
    }
}
