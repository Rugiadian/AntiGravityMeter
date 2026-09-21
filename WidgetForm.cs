using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AntiGravityMeter
{
    public class WidgetForm : Form
    {
        // Win32 API for smooth window dragging
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        private readonly AgyCollector _collector;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private AgyMetrics _metrics = new();

        // UI States
        private bool _isAlwaysOnTop = true;
        private readonly double[] _opacities = new double[] { 0.95, 0.80, 0.60 };
        private int _opacityIdx = 0;

        // Visual Constants: Pure Compact Quota Widget
        private const int WIDGET_WIDTH = 360;
        private const int WIDGET_HEIGHT = 186;

        // Custom Colors
        private readonly Color ColorBgDark = Color.FromArgb(20, 24, 33);
        private readonly Color ColorHeader = Color.FromArgb(25, 30, 42);
        private readonly Color ColorBorder = Color.FromArgb(48, 58, 78);
        private readonly Color ColorAccentGold = Color.FromArgb(235, 168, 48);
        private readonly Color ColorPlanBadge = Color.FromArgb(142, 68, 173);
        private readonly Color ColorMutedGray = Color.FromArgb(145, 158, 178);
        private readonly Color ColorTextWhite = Color.FromArgb(240, 243, 248);

        // Header controls
        private Label lblTitle = null!;
        private Label lblPlanBadge = null!;
        private Button btnPin = null!;
        private Button btnOpacity = null!;
        private Button btnRefresh = null!;
        private Button btnClose = null!;
        private Panel pnlHeader = null!;

        // Core Quota Cards (Infographics from Screenshot)
        private CircularQuotaCard cardWeekly = null!;
        private CircularQuotaCard cardFiveHour = null!;

        private NotifyIcon? _trayIcon;

        public WidgetForm()
        {
            _collector = new AgyCollector();

            InitializeWidgetStyles();
            InitializeComponent();
            SetupTrayIcon();

            // Background Polling Timer (every 3.0 seconds)
            _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _pollTimer.Tick += async (s, e) => await RefreshDataAsync(false);

            Load += async (s, e) =>
            {
                PositionAtBottomRight();
                _pollTimer.Start();
                await RefreshDataAsync(true);
            };
        }

        private void InitializeWidgetStyles()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = _isAlwaysOnTop;
            Opacity = _opacities[_opacityIdx];
            DoubleBuffered = true;
            Width = WIDGET_WIDTH;
            Height = WIDGET_HEIGHT;
            BackColor = ColorBgDark;

            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        private void PositionAtBottomRight()
        {
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(this);
            int x = workingArea.Right - Width - 24;
            int y = workingArea.Bottom - Height - 24;
            Location = new Point(x, y);
        }

        private void InitializeComponent()
        {
            // 1. Header Panel
            pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = ColorHeader,
                Cursor = Cursors.SizeAll
            };
            pnlHeader.MouseDown += Window_MouseDown;

            lblTitle = new Label
            {
                Text = "⚡ Gemini Models",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = ColorTextWhite,
                AutoSize = true,
                Location = new Point(10, 8),
                Cursor = Cursors.SizeAll
            };
            lblTitle.MouseDown += Window_MouseDown;

            lblPlanBadge = new Label
            {
                Text = "Google AI Pro",
                Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = ColorPlanBadge,
                AutoSize = true,
                Location = new Point(132, 8),
                Padding = new Padding(4, 2, 4, 2),
                Cursor = Cursors.SizeAll
            };
            lblPlanBadge.MouseDown += Window_MouseDown;

            btnPin = CreateHeaderButton("📌", "항상 위에 고정 토글", 232);
            btnPin.Click += (s, e) =>
            {
                _isAlwaysOnTop = !_isAlwaysOnTop;
                TopMost = _isAlwaysOnTop;
                btnPin.ForeColor = _isAlwaysOnTop ? ColorAccentGold : ColorMutedGray;
            };
            btnPin.ForeColor = _isAlwaysOnTop ? ColorAccentGold : ColorMutedGray;

            btnOpacity = CreateHeaderButton("◐", "투명도 조절", 262);
            btnOpacity.Click += (s, e) =>
            {
                _opacityIdx = (_opacityIdx + 1) % _opacities.Length;
                Opacity = _opacities[_opacityIdx];
            };

            btnRefresh = CreateHeaderButton("🔄", "실시간 쿼터 새로고침", 292);
            btnRefresh.Click += async (s, e) => await RefreshDataAsync(true);

            btnClose = CreateHeaderButton("✕", "트레이로 최소화", 324);
            btnClose.Click += (s, e) =>
            {
                Hide();
                if (_trayIcon != null)
                {
                    _trayIcon.ShowBalloonTip(1500, "AntiGravity Meter", "위젯이 트레이로 최소화되었습니다. 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
                }
            };

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblPlanBadge);
            pnlHeader.Controls.Add(btnPin);
            pnlHeader.Controls.Add(btnOpacity);
            pnlHeader.Controls.Add(btnRefresh);
            pnlHeader.Controls.Add(btnClose);

            // 2. Circular Quota Cards (The Core Infographics)
            cardWeekly = new CircularQuotaCard
            {
                Location = new Point(8, 44),
                Size = new Size(344, 62),
                Title = "Weekly Limit Remaining",
                Subtitle = "조회 중...",
                Percent = 100
            };
            cardWeekly.MouseDown += Window_MouseDown;

            cardFiveHour = new CircularQuotaCard
            {
                Location = new Point(8, 112),
                Size = new Size(344, 62),
                Title = "Five Hour Limit Remaining",
                Subtitle = "조회 중...",
                Percent = 100
            };
            cardFiveHour.MouseDown += Window_MouseDown;

            // Add all to Form
            Controls.Add(cardFiveHour);
            Controls.Add(cardWeekly);
            Controls.Add(pnlHeader);

            MouseDown += Window_MouseDown;
        }

        private Button CreateHeaderButton(string text, string tooltip, int x)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, 5),
                Size = new Size(26, 26),
                FlatStyle = FlatStyle.Flat,
                ForeColor = ColorMutedGray,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 55, 75);

            var tip = new ToolTip();
            tip.SetToolTip(btn, tooltip);
            return btn;
        }

        private async Task RefreshDataAsync(bool force)
        {
            try
            {
                _metrics = await _collector.CollectMetricsAsync(force);
                UpdateUI();
            }
            catch
            {
            }
        }

        private void UpdateUI()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(UpdateUI));
                return;
            }

            // 1. Weekly Quota
            cardWeekly.Percent = _metrics.WeeklyLimit.RemainingPercent;
            cardWeekly.Subtitle = !string.IsNullOrEmpty(_metrics.WeeklyLimit.RefreshTimeText)
                ? _metrics.WeeklyLimit.RefreshTimeText
                : "it will fully refresh in 2 days.";

            // 2. 5-Hour Quota
            cardFiveHour.Percent = _metrics.FiveHourLimit.RemainingPercent;
            cardFiveHour.Subtitle = !string.IsNullOrEmpty(_metrics.FiveHourLimit.RefreshTimeText)
                ? _metrics.FiveHourLimit.RefreshTimeText
                : "it will fully refresh shortly.";

            // 3. User Plan
            lblPlanBadge.Text = _metrics.UserPlan;
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Text = "AntiGravity Quota Meter",
                Icon = SystemIcons.Application,
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("위젯 열기", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; BringToFront(); });
            menu.Items.Add("실시간 새로고침", null, async (s, e) => await RefreshDataAsync(true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("항상 위에 고정", null, (s, e) =>
            {
                _isAlwaysOnTop = !_isAlwaysOnTop;
                TopMost = _isAlwaysOnTop;
                btnPin.ForeColor = _isAlwaysOnTop ? ColorAccentGold : ColorMutedGray;
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (s, e) => Application.Exit());

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; BringToFront(); };
        }

        private void Window_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            using var borderPen = new Pen(ColorBorder, 1.5f);
            e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.OnFormClosing(e);
        }
    }
}
