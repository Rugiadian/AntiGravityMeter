using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AntiGravityMeter
{
    public class CircularQuotaCard : Control
    {
        private string _title = "Limit Remaining";
        private string _subtitle = "Refreshing...";
        private int _percent = 100;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Title
        {
            get => _title;
            set { _title = value; Invalidate(); }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Subtitle
        {
            get => _subtitle;
            set { _subtitle = value; Invalidate(); }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Percent
        {
            get => _percent;
            set { _percent = Math.Clamp(value, 0, 100); Invalidate(); }
        }

        public CircularQuotaCard()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            Height = 62;
            BackColor = Color.FromArgb(30, 36, 48); // Dark Slate
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // 1. Card Background & Border
            using (var bgBrush = new SolidBrush(BackColor))
            {
                g.FillRectangle(bgBrush, rect);
            }

            using (var borderPen = new Pen(Color.FromArgb(48, 58, 78), 1))
            {
                g.DrawRectangle(borderPen, 0, 0, rect.Width - 1, rect.Height - 1);
            }

            // 2. Left Texts (Title & Subtitle)
            using (var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (var subFont = new Font("Segoe UI", 8.0f, FontStyle.Regular))
            using (var titleBrush = new SolidBrush(Color.FromArgb(240, 244, 250)))
            using (var subBrush = new SolidBrush(Color.FromArgb(140, 155, 175)))
            {
                g.DrawString(_title, titleFont, titleBrush, 12, 10);
                g.DrawString(_subtitle, subFont, subBrush, 12, 34);
            }

            // 3. Right: Circular Ring Infographic (Donut Gauge)
            int gaugeDiameter = 36;
            int gaugeX = rect.Width - gaugeDiameter - 12;
            int gaugeY = (rect.Height - gaugeDiameter) / 2;
            var gaugeRect = new Rectangle(gaugeX, gaugeY, gaugeDiameter, gaugeDiameter);

            // Ring track background
            float strokeWidth = 4.5f;
            using (var trackPen = new Pen(Color.FromArgb(44, 52, 68), strokeWidth))
            {
                g.DrawEllipse(trackPen, gaugeRect);
            }

            // Progress Arc color based on percentage
            Color progressColor;
            if (_percent >= 70)
                progressColor = Color.FromArgb(46, 204, 113); // Fresh Emerald Green
            else if (_percent >= 30)
                progressColor = Color.FromArgb(243, 156, 18);  // Warm Amber
            else
                progressColor = Color.FromArgb(231, 76, 60);   // Warning Coral Red

            if (_percent > 0)
            {
                using (var progPen = new Pen(progressColor, strokeWidth))
                {
                    progPen.StartCap = LineCap.Round;
                    progPen.EndCap = LineCap.Round;
                    float sweepAngle = (_percent / 100f) * 360f;
                    g.DrawArc(progPen, gaugeRect, -90f, sweepAngle);
                }
            }

            // 4. Percentage Text (Left to the circular gauge)
            string pctText = $"{_percent}%";
            using (var pctFont = new Font("Segoe UI", 13.5f, FontStyle.Bold))
            using (var pctBrush = new SolidBrush(Color.White))
            {
                var size = g.MeasureString(pctText, pctFont);
                float pctX = gaugeX - size.Width - 10;
                float pctY = (rect.Height - size.Height) / 2f;
                g.DrawString(pctText, pctFont, pctBrush, pctX, pctY);
            }
        }
    }
}
