using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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
        private readonly System.Windows.Forms.Timer _animTimer;
        private AgyMetrics _metrics = new();

        // UI States
        private bool _isExpanded = false;
        private bool _isAlwaysOnTop = true;
        private double[] _opacities = new double[] { 0.95, 0.80, 0.60 };
        private int _opacityIdx = 0;
        private float _pulsePhase = 0f;

        // Visual Constants
        private const int COMPACT_HEIGHT = 158;
        private const int EXPANDED_HEIGHT = 410;
        private const int WIDGET_WIDTH = 340;

        // Custom Colors
        private readonly Color ColorBgDark = Color.FromArgb(20, 24, 33);
        private readonly Color ColorBgCard = Color.FromArgb(28, 34, 46);
        private readonly Color ColorHeader = Color.FromArgb(25, 30, 42);
        private readonly Color ColorBorder = Color.FromArgb(48, 58, 78);
        private readonly Color ColorAccentGold = Color.FromArgb(235, 168, 48);
        private readonly Color ColorGreenLive = Color.FromArgb(46, 204, 113);
        private readonly Color ColorAmberIdle = Color.FromArgb(243, 156, 18);
        private readonly Color ColorMutedGray = Color.FromArgb(145, 158, 178);
        private readonly Color ColorTextWhite = Color.FromArgb(240, 243, 248);

        // Header controls
        private Label lblTitle = null!;
        private Label lblStatusBadge = null!;
        private Button btnPin = null!;
        private Button btnOpacity = null!;
        private Button btnToggleExpand = null!;
        private Button btnClose = null!;
        private Panel pnlHeader = null!;

        // Compact stats
        private Label lblValTodayCalls = null!;
        private Label lblValTodayPrompts = null!;
        private Label lblValActiveSteps = null!;
        private Label lblValRam = null!;
        private Label lblModelBadge = null!;
        private Label lblLastActive = null!;

        // Expanded Panel
        private Panel pnlExpanded = null!;
        private ListView lvRecentConvs = null!;
        private Label lblRecentPromptTitle = null!;
        private Label lblRecentPromptText = null!;

        private NotifyIcon? _trayIcon;

        public WidgetForm()
        {
            _collector = new AgyCollector();

            InitializeWidgetStyles();
            InitializeComponent();
            SetupTrayIcon();

            // Background Polling Timer (every 1.5 seconds)
            _pollTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _pollTimer.Tick += async (s, e) => await RefreshDataAsync();

            // Pulse Animation Timer (every 60ms)
            _animTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _animTimer.Tick += (s, e) =>
            {
                _pulsePhase = (_pulsePhase + 0.12f) % (float)(Math.PI * 2);
                lblStatusBadge.Invalidate();
            };

            Load += async (s, e) =>
            {
                PositionAtBottomRight();
                _pollTimer.Start();
                _animTimer.Start();
                await RefreshDataAsync();
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
                Text = "⚡ AGY Meter",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = ColorTextWhite,
                AutoSize = true,
                Location = new Point(10, 8),
                Cursor = Cursors.SizeAll
            };
            lblTitle.MouseDown += Window_MouseDown;

            lblStatusBadge = new Label
            {
                Text = " ● OFF ",
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(60, 70, 85),
                AutoSize = true,
                Location = new Point(108, 8),
                Padding = new Padding(3, 1, 3, 1),
                Cursor = Cursors.SizeAll
            };
            lblStatusBadge.MouseDown += Window_MouseDown;
            lblStatusBadge.Paint += LblStatusBadge_Paint;

            // Buttons
            btnPin = CreateHeaderButton("📌", "항상 위에 고정 토글", 168);
            btnPin.Click += (s, e) =>
            {
                _isAlwaysOnTop = !_isAlwaysOnTop;
                TopMost = _isAlwaysOnTop;
                btnPin.ForeColor = _isAlwaysOnTop ? ColorAccentGold : ColorMutedGray;
            };
            btnPin.ForeColor = _isAlwaysOnTop ? ColorAccentGold : ColorMutedGray;

            btnOpacity = CreateHeaderButton("◐", "투명도 조절", 204);
            btnOpacity.Click += (s, e) =>
            {
                _opacityIdx = (_opacityIdx + 1) % _opacities.Length;
                Opacity = _opacities[_opacityIdx];
            };

            btnToggleExpand = CreateHeaderButton("▾", "상세 통계 펼치기/접기", 240);
            btnToggleExpand.Click += (s, e) => ToggleExpandMode();

            Button btnRefresh = CreateHeaderButton("🔄", "지금 새로고침", 276);
            btnRefresh.Click += async (s, e) => await RefreshDataAsync();

            btnClose = CreateHeaderButton("✕", "트레이로 최소화", 308);
            btnClose.Click += (s, e) =>
            {
                Hide();
                if (_trayIcon != null)
                {
                    _trayIcon.ShowBalloonTip(1500, "AntiGravity Meter", "위젯이 트레이로 최소화되었습니다. 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
                }
            };

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblStatusBadge);
            pnlHeader.Controls.Add(btnPin);
            pnlHeader.Controls.Add(btnOpacity);
            pnlHeader.Controls.Add(btnToggleExpand);
            pnlHeader.Controls.Add(btnRefresh);
            pnlHeader.Controls.Add(btnClose);

            // 2. Main Stat Cards Container
            var pnlMainCards = new Panel
            {
                Location = new Point(8, 42),
                Size = new Size(324, 82),
                BackColor = Color.Transparent
            };
            pnlMainCards.MouseDown += Window_MouseDown;

            int cardW = 76;
            int cardH = 78;
            int spacing = 6;

            var cardCalls = CreateStatCard("오늘 호출", "🔥", out lblValTodayCalls, "0회", ColorAccentGold);
            cardCalls.Location = new Point(0, 0);
            cardCalls.Size = new Size(cardW, cardH);

            var cardPrompts = CreateStatCard("오늘 프롬프트", "💬", out lblValTodayPrompts, "0회", Color.FromArgb(120, 200, 255));
            cardPrompts.Location = new Point(cardW + spacing, 0);
            cardPrompts.Size = new Size(cardW, cardH);

            var cardSteps = CreateStatCard("세션 스텝", "🎯", out lblValActiveSteps, "0 st", ColorGreenLive);
            cardSteps.Location = new Point((cardW + spacing) * 2, 0);
            cardSteps.Size = new Size(cardW, cardH);

            var cardRam = CreateStatCard("CLI RAM", "⚡", out lblValRam, "0 MB", Color.FromArgb(200, 160, 255));
            cardRam.Location = new Point((cardW + spacing) * 3, 0);
            cardRam.Size = new Size(cardW, cardH);

            pnlMainCards.Controls.Add(cardCalls);
            pnlMainCards.Controls.Add(cardPrompts);
            pnlMainCards.Controls.Add(cardSteps);
            pnlMainCards.Controls.Add(cardRam);

            // 3. Compact Footer Bar (Model & Activity)
            var pnlFooter = new Panel
            {
                Location = new Point(8, 128),
                Size = new Size(324, 24),
                BackColor = Color.Transparent
            };
            pnlFooter.MouseDown += Window_MouseDown;

            lblModelBadge = new Label
            {
                Text = "🤖 gemini-3.8-flash",
                Font = new Font("Segoe UI", 8.0f, FontStyle.Regular),
                ForeColor = Color.FromArgb(175, 185, 205),
                AutoSize = true,
                Location = new Point(2, 4),
                Cursor = Cursors.SizeAll
            };
            lblModelBadge.MouseDown += Window_MouseDown;

            lblLastActive = new Label
            {
                Text = "🕒 갱신 중...",
                Font = new Font("Segoe UI", 8.0f, FontStyle.Regular),
                ForeColor = ColorMutedGray,
                AutoSize = false,
                Size = new Size(160, 20),
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(162, 2),
                Cursor = Cursors.SizeAll
            };
            lblLastActive.MouseDown += Window_MouseDown;

            pnlFooter.Controls.Add(lblModelBadge);
            pnlFooter.Controls.Add(lblLastActive);

            // 4. Expanded Panel
            pnlExpanded = new Panel
            {
                Location = new Point(8, 156),
                Size = new Size(324, 246),
                BackColor = Color.Transparent,
                Visible = false
            };

            var lblRecentConvTitle = new Label
            {
                Text = "📋 최근 대화 세션 목록",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = ColorTextWhite,
                Location = new Point(2, 2),
                AutoSize = true
            };

            lvRecentConvs = new ListView
            {
                Location = new Point(2, 24),
                Size = new Size(320, 125),
                View = View.Details,
                FullRowSelect = true,
                BackColor = ColorBgCard,
                ForeColor = ColorTextWhite,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Segoe UI", 8.2f)
            };
            lvRecentConvs.Columns.Add("대화 세션 제목", 170);
            lvRecentConvs.Columns.Add("스텝", 50);
            lvRecentConvs.Columns.Add("최근 수정", 95);

            lblRecentPromptTitle = new Label
            {
                Text = "💬 최근 전송한 프롬프트",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = ColorTextWhite,
                Location = new Point(2, 156),
                AutoSize = true
            };

            lblRecentPromptText = new Label
            {
                Text = "(프롬프트 이력 로딩 중...)",
                Font = new Font("Segoe UI", 8.0f),
                ForeColor = Color.FromArgb(200, 210, 225),
                BackColor = ColorBgCard,
                Location = new Point(2, 178),
                Size = new Size(320, 60),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(6)
            };

            pnlExpanded.Controls.Add(lblRecentConvTitle);
            pnlExpanded.Controls.Add(lvRecentConvs);
            pnlExpanded.Controls.Add(lblRecentPromptTitle);
            pnlExpanded.Controls.Add(lblRecentPromptText);

            // Add all to Form
            Controls.Add(pnlExpanded);
            Controls.Add(pnlFooter);
            Controls.Add(pnlMainCards);
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
                BackColor = ColorBgCard,
                BorderStyle = BorderStyle.None
            };
            card.MouseDown += Window_MouseDown;

            var lblIcon = new Label
            {
                Text = icon,
                Font = new Font("Segoe UI", 12f),
                Location = new Point(4, 4),
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll
            };
            lblIcon.MouseDown += Window_MouseDown;

            var lblT = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = ColorMutedGray,
                Location = new Point(24, 6),
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll
            };
            lblT.MouseDown += Window_MouseDown;

            valLabel = new Label
            {
                Text = initialVal,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = accent,
                Location = new Point(2, 34),
                Size = new Size(72, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll
            };
            valLabel.MouseDown += Window_MouseDown;

            card.Controls.Add(lblIcon);
            card.Controls.Add(lblT);
            card.Controls.Add(valLabel);

            // Custom border paint for card
            card.Paint += (s, e) =>
            {
                using var pen = new Pen(ColorBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            return card;
        }

        private void LblStatusBadge_Paint(object? sender, PaintEventArgs e)
        {
            // Draw subtle glowing pulse when BUSY
            if (_metrics.Status == AgyStatus.Busy)
            {
                int alpha = (int)(160 + 90 * Math.Sin(_pulsePhase));
                using var brush = new SolidBrush(Color.FromArgb(alpha, 46, 204, 113));
                e.Graphics.FillRectangle(brush, lblStatusBadge.ClientRectangle);

                using var textBrush = new SolidBrush(Color.FromArgb(20, 30, 20));
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString("⚡ BUSY", lblStatusBadge.Font, textBrush, lblStatusBadge.ClientRectangle, sf);
            }
        }

        private void ToggleExpandMode()
        {
            _isExpanded = !_isExpanded;
            btnToggleExpand.Text = _isExpanded ? "▴" : "▾";
            pnlExpanded.Visible = _isExpanded;
            Height = _isExpanded ? EXPANDED_HEIGHT : COMPACT_HEIGHT;
        }

        private async Task RefreshDataAsync()
        {
            try
            {
                _metrics = await _collector.CollectMetricsAsync();
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

            // 1. Status Badge
            switch (_metrics.Status)
            {
                case AgyStatus.Busy:
                    lblStatusBadge.Text = "⚡ BUSY";
                    lblStatusBadge.BackColor = ColorGreenLive;
                    lblStatusBadge.ForeColor = Color.FromArgb(15, 25, 15);
                    break;
                case AgyStatus.Idle:
                    lblStatusBadge.Text = "● IDLE";
                    lblStatusBadge.BackColor = ColorAmberIdle;
                    lblStatusBadge.ForeColor = Color.FromArgb(20, 20, 10);
                    break;
                case AgyStatus.Offline:
                default:
                    lblStatusBadge.Text = "○ OFF";
                    lblStatusBadge.BackColor = Color.FromArgb(60, 70, 85);
                    lblStatusBadge.ForeColor = Color.White;
                    break;
            }

            // 2. Compact Stat Cards
            lblValTodayCalls.Text = $"{_metrics.TodayApiCalls}회";
            lblValTodayPrompts.Text = $"{_metrics.TodayPrompts}회";
            lblValActiveSteps.Text = $"{_metrics.ActiveConvSteps:N0}";
            lblValRam.Text = _metrics.Status != AgyStatus.Offline ? $"{_metrics.MemoryMb:F0} MB" : "0 MB";

            // 3. Footer info
            lblModelBadge.Text = $"🤖 {_metrics.ActiveModel}";
            
            if (_metrics.LastActivityTime != DateTime.MinValue)
            {
                var diff = DateTime.Now - _metrics.LastActivityTime;
                if (diff.TotalSeconds < 60)
                    lblLastActive.Text = "🕒 방금 전 활동";
                else if (diff.TotalMinutes < 60)
                    lblLastActive.Text = $"🕒 {(int)diff.TotalMinutes}분 전 활동";
                else
                    lblLastActive.Text = $"🕒 {_metrics.LastActivityTime:HH:mm:ss}";
            }
            else
            {
                lblLastActive.Text = "🕒 활동 없음";
            }

            // 4. Expanded Data
            if (_isExpanded)
            {
                // Update recent conversations
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

                // Update recent prompt
                if (_metrics.RecentPrompts.Count > 0)
                {
                    var p = _metrics.RecentPrompts[0];
                    string shortText = p.Text.Length > 90 ? p.Text.Substring(0, 90) + "..." : p.Text;
                    lblRecentPromptText.Text = $"[{p.Timestamp:HH:mm:ss}]\n{shortText}";
                }
                else
                {
                    lblRecentPromptText.Text = "(기록된 프롬프트 없음)";
                }
            }
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Text = "AntiGravity Meter",
                Icon = SystemIcons.Application,
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("위젯 열기", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; BringToFront(); });
            menu.Items.Add("지금 새로고침", null, async (s, e) => await RefreshDataAsync());
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

            // Draw clean subtle outer border
            using var borderPen = new Pen(ColorBorder, 1.5f);
            e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _animTimer.Stop();
            _animTimer.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.OnFormClosing(e);
        }
    }
}
