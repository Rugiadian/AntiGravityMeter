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
        private bool _isExpanded = false;
        private bool _isAlwaysOnTop = true;
        private double[] _opacities = new double[] { 0.95, 0.80, 0.60 };
        private int _opacityIdx = 0;

        // Visual Constants
        private const int COMPACT_HEIGHT = 208;
        private const int EXPANDED_HEIGHT = 445;
        private const int WIDGET_WIDTH = 360;

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

        // Quota Cards (The Core Circular Donut Infographics from Screenshot)
        private CircularQuotaCard cardWeekly = null!;
        private CircularQuotaCard cardFiveHour = null!;

        // Advanced Mode Toggle Button
        private Button btnToggleAdvanced = null!;

        // Advanced Panel (Hidden in Default Mode)
        private Panel pnlAdvanced = null!;
        private Label lblValTodayCalls = null!;
        private Label lblValTodayPrompts = null!;
        private Label lblValActiveSteps = null!;
        private Label lblValRam = null!;
        private ListView lvRecentConvs = null!;

        private NotifyIcon? _trayIcon;

        public WidgetForm()
        {
            _collector = new AgyCollector();

            InitializeWidgetStyles();
            InitializeComponent();
            SetupTrayIcon();

            // Background Polling Timer (every 2.0 seconds)
            _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
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
            Height = COMPACT_HEIGHT;
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

            // 2. Circular Quota Cards (From User Screenshot)
            cardWeekly = new CircularQuotaCard
            {
                Location = new Point(8, 42),
                Size = new Size(344, 58),
                Title = "Weekly Limit Remaining",
                Subtitle = "it will fully refresh in 2 days, 12 hours.",
                Percent = 85
            };
            cardWeekly.MouseDown += Window_MouseDown;

            cardFiveHour = new CircularQuotaCard
            {
                Location = new Point(8, 104),
                Size = new Size(344, 58),
                Title = "Five Hour Limit Remaining",
                Subtitle = "it will fully refresh in 4 hours, 40 minutes.",
                Percent = 92
            };
            cardFiveHour.MouseDown += Window_MouseDown;

            // 3. Advanced Mode Toggle Button (Compact footer)
            btnToggleAdvanced = new Button
            {
                Text = "▾ 고급 통계 보기 (호출/RAM/대화)",
                Location = new Point(8, 168),
                Size = new Size(344, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(28, 34, 46),
                ForeColor = ColorMutedGray,
                Font = new Font("Segoe UI", 8.2f),
                Cursor = Cursors.Hand
            };
            btnToggleAdvanced.FlatAppearance.BorderSize = 0;
            btnToggleAdvanced.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 46, 62);
            btnToggleAdvanced.Click += (s, e) => ToggleAdvancedMode();

            // 4. Advanced Panel (Hidden by default)
            pnlAdvanced = new Panel
            {
                Location = new Point(8, 202),
                Size = new Size(344, 235),
                BackColor = Color.Transparent,
                Visible = false
            };

            // Mini Stat Grid
            int cardW = 81;
            int cardH = 68;
            int spacing = 6;

            var cardCalls = CreateStatCard("오늘 호출", "🔥", out lblValTodayCalls, "0회", ColorAccentGold);
            cardCalls.Location = new Point(0, 0);
            cardCalls.Size = new Size(cardW, cardH);

            var cardPrompts = CreateStatCard("프롬프트", "💬", out lblValTodayPrompts, "0회", Color.FromArgb(120, 200, 255));
            cardPrompts.Location = new Point(cardW + spacing, 0);
            cardPrompts.Size = new Size(cardW, cardH);

            var cardSteps = CreateStatCard("세션 스텝", "🎯", out lblValActiveSteps, "0 st", Color.FromArgb(46, 204, 113));
            cardSteps.Location = new Point((cardW + spacing) * 2, 0);
            cardSteps.Size = new Size(cardW, cardH);

            var cardRam = CreateStatCard("CLI RAM", "⚡", out lblValRam, "0 MB", Color.FromArgb(200, 160, 255));
            cardRam.Location = new Point((cardW + spacing) * 3, 0);
            cardRam.Size = new Size(cardW, cardH);

            pnlAdvanced.Controls.Add(cardCalls);
            pnlAdvanced.Controls.Add(cardPrompts);
            pnlAdvanced.Controls.Add(cardSteps);
            pnlAdvanced.Controls.Add(cardRam);

            // Recent Conversations List
            var lblRecentConvTitle = new Label
            {
                Text = "📋 최근 대화 세션",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = ColorTextWhite,
                Location = new Point(0, 74),
                AutoSize = true
            };

            lvRecentConvs = new ListView
            {
                Location = new Point(0, 96),
                Size = new Size(344, 134),
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(28, 34, 46),
                ForeColor = ColorTextWhite,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Segoe UI", 8.2f)
            };
            lvRecentConvs.Columns.Add("세션 제목", 190);
            lvRecentConvs.Columns.Add("스텝", 50);
            lvRecentConvs.Columns.Add("수정 시각", 95);

            pnlAdvanced.Controls.Add(lblRecentConvTitle);
            pnlAdvanced.Controls.Add(lvRecentConvs);

            // Add all to Form
            Controls.Add(pnlAdvanced);
            Controls.Add(btnToggleAdvanced);
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

        private Panel CreateStatCard(string title, string icon, out Label valLabel, string initialVal, Color accent)
        {
            var card = new Panel
            {
                BackColor = Color.FromArgb(28, 34, 46),
                BorderStyle = BorderStyle.None
            };
            card.MouseDown += Window_MouseDown;

            var lblIcon = new Label
            {
                Text = icon,
                Font = new Font("Segoe UI", 11f),
                Location = new Point(4, 4),
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll
            };
            lblIcon.MouseDown += Window_MouseDown;

            var lblT = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 7.2f),
                ForeColor = ColorMutedGray,
                Location = new Point(22, 6),
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll
            };
            lblT.MouseDown += Window_MouseDown;

            valLabel = new Label
            {
                Text = initialVal,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = accent,
                Location = new Point(2, 30),
                Size = new Size(77, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll
            };
            valLabel.MouseDown += Window_MouseDown;

            card.Controls.Add(lblIcon);
            card.Controls.Add(lblT);
            card.Controls.Add(valLabel);

            card.Paint += (s, e) =>
            {
                using var pen = new Pen(ColorBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            return card;
        }

        private void ToggleAdvancedMode()
        {
            _isExpanded = !_isExpanded;
            btnToggleAdvanced.Text = _isExpanded ? "▴ 고급 통계 접기" : "▾ 고급 통계 보기 (호출/RAM/대화)";
            pnlAdvanced.Visible = _isExpanded;
            Height = _isExpanded ? EXPANDED_HEIGHT : COMPACT_HEIGHT;
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

            // 1. Quota Cards (Circular Donut Infographics)
            cardWeekly.Percent = _metrics.WeeklyLimit.RemainingPercent;
            cardWeekly.Subtitle = !string.IsNullOrEmpty(_metrics.WeeklyLimit.RefreshTimeText)
                ? _metrics.WeeklyLimit.RefreshTimeText
                : "it will fully refresh in 2 days, 12 hours.";

            cardFiveHour.Percent = _metrics.FiveHourLimit.RemainingPercent;
            cardFiveHour.Subtitle = !string.IsNullOrEmpty(_metrics.FiveHourLimit.RefreshTimeText)
                ? _metrics.FiveHourLimit.RefreshTimeText
                : "it will fully refresh in 4 hours, 40 minutes.";

            lblPlanBadge.Text = _metrics.UserPlan;

            // 2. Advanced View Data (Only updated if expanded or preparing)
            lblValTodayCalls.Text = $"{_metrics.TodayApiCalls}회";
            lblValTodayPrompts.Text = $"{_metrics.TodayPrompts}회";
            lblValActiveSteps.Text = $"{_metrics.ActiveConvSteps:N0}";
            lblValRam.Text = _metrics.Status != AgyStatus.Offline ? $"{_metrics.MemoryMb:F0} MB" : "0 MB";

            if (_isExpanded)
            {
                lvRecentConvs.BeginUpdate();
                lvRecentConvs.Items.Clear();
                foreach (var conv in _metrics.RecentConversations)
                {
                    var item = new ListViewItem(conv.Title);
                    item.SubItems.Add($"{conv.StepCount}");
                    item.SubItems.Add(conv.LastModified != DateTime.MinValue ? conv.LastModified.ToString("MM-dd HH:mm") : "-");
                    lvRecentConvs.Items.Add(item);
                }
                lvRecentConvs.EndUpdate();
            }
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
