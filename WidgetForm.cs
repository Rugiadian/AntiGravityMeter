using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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

        // Visual Constants: 1/2 Compact Quota Widget
        private const int WIDGET_WIDTH = 256;
        private const int WIDGET_HEIGHT = 208;
        private const int CARD_HEIGHT = 28;
        private const int ROW_HEIGHT = 18;

        // Custom Colors
        private readonly Color ColorBgDark = Color.FromArgb(20, 24, 33);
        private readonly Color ColorHeader = Color.FromArgb(25, 30, 42);
        private readonly Color ColorBorder = Color.FromArgb(48, 58, 78);
        private readonly Color ColorAccentGold = Color.FromArgb(235, 168, 48);
        private readonly Color ColorPlanBadge = Color.FromArgb(142, 68, 173);
        private readonly Color ColorMutedGray = Color.FromArgb(145, 158, 178);
        private readonly Color ColorTextWhite = Color.FromArgb(240, 243, 248);
        private readonly Color ColorCardBg = Color.FromArgb(30, 36, 48);

        // Header controls
        private Label lblTitle = null!;
        private Label lblPlanBadge = null!;
        private Button btnModels = null!;
        private Button btnPin = null!;
        private Button btnOpacity = null!;
        private Button btnRefresh = null!;
        private Button btnClose = null!;
        private Panel pnlHeader = null!;

        // Dynamic content: one section (Weekly + 5h cards) per quota group, plus per-model rows
        private readonly List<Control> _dynamicControls = new();
        private readonly List<(CircularQuotaCard Card, int GroupIdx, int BucketIdx)> _cards = new();
        private readonly List<(ModelUsageRow Row, int ModelIdx)> _modelRows = new();
        private string _layoutSignature = "\0";
        private bool _showModelList = false;
        private readonly ToolTip _toolTip = new();

        private NotifyIcon? _trayIcon;
        private ContextMenuStrip _contextMenu = null!;

        public WidgetForm()
        {
            _collector = new AgyCollector();

            LoadSettings();
            InitializeWidgetStyles();
            InitializeComponent();
            SetupContextMenu();
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

        private void LoadSettings()
        {
            try
            {
                string path = GetSettingsPath();
                if (System.IO.File.Exists(path))
                {
                    string json = System.IO.File.ReadAllText(path);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("showModelList", out var sml)) _showModelList = sml.GetBoolean();
                    if (root.TryGetProperty("alwaysOnTop", out var aot)) _isAlwaysOnTop = aot.GetBoolean();
                    if (root.TryGetProperty("opacityIdx", out var op)) _opacityIdx = Math.Clamp(op.GetInt32(), 0, _opacities.Length - 1);
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                string path = GetSettingsPath();
                var data = new
                {
                    showModelList = _showModelList,
                    alwaysOnTop = _isAlwaysOnTop,
                    opacityIdx = _opacityIdx
                };
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data));
            }
            catch { }
        }

        private static string GetSettingsPath() =>
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "widget_settings.json");

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
            // 1. Header Panel (26px compact)
            pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 26,
                BackColor = ColorHeader,
                Cursor = Cursors.SizeAll
            };
            pnlHeader.MouseDown += Window_MouseDown;

            lblTitle = new Label
            {
                Text = "⚡ AntiGravity",
                Font = new Font("Segoe UI", 8.2f, FontStyle.Bold),
                ForeColor = ColorTextWhite,
                AutoSize = true,
                Location = new Point(6, 5),
                Cursor = Cursors.SizeAll
            };
            lblTitle.MouseDown += Window_MouseDown;

            lblPlanBadge = new Label
            {
                Text = "Pro",
                Font = new Font("Segoe UI", 6.8f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = ColorPlanBadge,
                AutoSize = true,
                Location = new Point(lblTitle.Left + lblTitle.PreferredWidth + 3, 5),
                Padding = new Padding(3, 1, 3, 1),
                Cursor = Cursors.SizeAll
            };
            lblPlanBadge.MouseDown += Window_MouseDown;

            // Header Buttons (18x18px, right-aligned)
            int btnY = 4;
            btnModels = CreateHeaderButton("☰", "모델별 사용량 목록 온/오프 (☰)", 154, btnY);
            btnModels.Click += (s, e) => ToggleModelList();
            btnModels.ForeColor = _showModelList ? ColorAccentGold : ColorMutedGray;

            btnPin = CreateHeaderButton("📌", "항상 위에 고정 토글", 174, btnY);
            btnPin.Click += (s, e) => ToggleAlwaysOnTop();
            btnPin.ForeColor = _isAlwaysOnTop ? ColorAccentGold : ColorMutedGray;

            btnOpacity = CreateHeaderButton("◐", "투명도 조절", 194, btnY);
            btnOpacity.Click += (s, e) => CycleOpacity();

            btnRefresh = CreateHeaderButton("🔄", "실시간 쿼터 새로고침", 214, btnY);
            btnRefresh.Click += async (s, e) => await RefreshDataAsync(true);

            btnClose = CreateHeaderButton("✕", "트레이로 최소화", 234, btnY);
            btnClose.Click += (s, e) => HideWidget();

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblPlanBadge);
            pnlHeader.Controls.Add(btnModels);
            pnlHeader.Controls.Add(btnPin);
            pnlHeader.Controls.Add(btnOpacity);
            pnlHeader.Controls.Add(btnRefresh);
            pnlHeader.Controls.Add(btnClose);

            // 2. Quota group cards & per-model rows are created dynamically (see RebuildLayout)
            Controls.Add(pnlHeader);

            MouseDown += Window_MouseDown;
        }

        private Button CreateHeaderButton(string text, string tooltip, int x, int y = 4)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(18, 18),
                FlatStyle = FlatStyle.Flat,
                ForeColor = ColorMutedGray,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 7.5f),
                Cursor = Cursors.Hand,
                Padding = new Padding(0)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 55, 75);

            _toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        private async Task RefreshDataAsync(bool force)
        {
            try
            {
                _metrics = await _collector.CollectMetricsAsync(force);
                UpdateUI();
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), "[RefreshDataAsync] " + ex + "\n"); } catch { }
            }
        }

        private void UpdateUI()
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(UpdateUI));
                    return;
                }

                // 1. Rebuild the card/row layout only when groups, buckets or models change
                string signature = BuildLayoutSignature();
                if (signature != _layoutSignature)
                {
                    _layoutSignature = signature;
                    RebuildLayout();
                }

                // 2. Quota group cards (Weekly / 5-Hour per group)
                foreach (var (card, gi, bi) in _cards)
                {
                    if (gi >= _metrics.Groups.Count || bi >= _metrics.Groups[gi].Buckets.Count) continue;
                    var bucket = _metrics.Groups[gi].Buckets[bi];
                    card.Percent = bucket.RemainingPercent;
                    card.ResetTime = bucket.ResetTime;
                    card.Subtitle = !string.IsNullOrEmpty(bucket.RefreshTimeText)
                        ? bucket.RefreshTimeText
                        : (bucket.RemainingPercent >= 100 ? "한도 여유" : "곧 리셋");
                    if (card.ResetTime != DateTime.MinValue)
                    {
                        card.Invalidate();
                    }
                }

                // 3. Per-model usage rows
                foreach (var (row, mi) in _modelRows)
                {
                    if (mi >= _metrics.Models.Count) continue;
                    var m = _metrics.Models[mi];
                    row.Percent = m.RemainingPercent;
                    row.ResetTime = m.ResetTime;
                }

                // 4. User Plan
                lblPlanBadge.Text = _metrics.UserPlan;
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), "[UpdateUI] " + ex + "\n"); } catch { }
            }
        }

        private string BuildLayoutSignature()
        {
            var groups = string.Join("|", _metrics.Groups.Select(g =>
                g.Name + ":" + string.Join(",", g.Buckets.Select(b => b.Title))));
            var models = _showModelList
                ? string.Join("|", _metrics.Models.Select(m => m.DisplayName))
                : "-";
            return groups + "#" + models;
        }

        /// <summary>
        /// Recreates the content area: a titled section per quota group (one card per bucket),
        /// followed by an optional compact list of every available model. Resizes the widget
        /// while keeping its bottom edge fixed (it lives at the bottom-right by default).
        /// </summary>
        private void RebuildLayout()
        {
            SuspendLayout();

            foreach (var c in _dynamicControls)
            {
                Controls.Remove(c);
                c.Dispose();
            }
            _dynamicControls.Clear();
            _cards.Clear();
            _modelRows.Clear();

            int contentWidth = WIDGET_WIDTH - 12;
            int y = pnlHeader.Height + 4;

            if (_metrics.Groups.Count == 0)
            {
                // Placeholder while the first request is in flight
                var placeholder = new CircularQuotaCard
                {
                    Location = new Point(6, y),
                    Size = new Size(contentWidth, CARD_HEIGHT),
                    Title = "조회 중",
                    Subtitle = "데이터 가져오는 중...",
                    Percent = 100
                };
                AddDynamic(placeholder);
                y += CARD_HEIGHT + 4;
            }

            for (int gi = 0; gi < _metrics.Groups.Count; gi++)
            {
                var group = _metrics.Groups[gi];

                var lblGroup = CreateSectionLabel(group.Name, y);
                string tip = group.Description;
                if (group.IsSynthesized) tip += "\n(모델별 quotaInfo 기준으로 계산된 값입니다)";
                if (!string.IsNullOrWhiteSpace(tip)) _toolTip.SetToolTip(lblGroup, tip.Trim());
                AddDynamic(lblGroup);
                y += 16;

                for (int bi = 0; bi < group.Buckets.Count; bi++)
                {
                    var card = new CircularQuotaCard
                    {
                        Location = new Point(6, y),
                        Size = new Size(contentWidth, CARD_HEIGHT),
                        Title = ShortBucketTitle(group.Buckets[bi]),
                        Subtitle = "조회 중...",
                        Percent = 100
                    };
                    AddDynamic(card);
                    _cards.Add((card, gi, bi));
                    y += CARD_HEIGHT + 3;
                }
                y += 2;
            }

            if (_metrics.Models.Count > 0)
            {
                string toggleText = _showModelList
                    ? "▼ 모델별 사용량 (Per-Model) [클릭하여 접기]"
                    : "▶ 모델별 사용량 (Per-Model) [클릭하여 펼치기]";
                var lblToggle = CreateToggleSectionLabel(toggleText, y);
                lblToggle.Click += (s, e) => ToggleModelList();
                _toolTip.SetToolTip(lblToggle, _showModelList
                    ? "클릭하면 모델별 목록을 접습니다 (☰ 버튼 또는 우클릭 메뉴로도 가능)"
                    : "클릭하면 모델별 상세 사용량 목록을 펼칩니다 (☰ 버튼 또는 우클릭 메뉴로도 가능)");
                AddDynamic(lblToggle);
                y += 18;

                if (_showModelList)
                {
                    var pnlModels = new Panel
                    {
                        Location = new Point(6, y),
                        Size = new Size(contentWidth, _metrics.Models.Count * ROW_HEIGHT + 4),
                        BackColor = ColorCardBg,
                        ContextMenuStrip = _contextMenu
                    };
                    pnlModels.Paint += (s, e) =>
                    {
                        using var pen = new Pen(ColorBorder, 1);
                        e.Graphics.DrawRectangle(pen, 0, 0, pnlModels.Width - 1, pnlModels.Height - 1);
                    };

                    for (int mi = 0; mi < _metrics.Models.Count; mi++)
                    {
                        var m = _metrics.Models[mi];
                        var row = new ModelUsageRow
                        {
                            Location = new Point(1, 2 + mi * ROW_HEIGHT),
                            Size = new Size(contentWidth - 2, ROW_HEIGHT),
                            BackColor = ColorCardBg,
                            ModelName = m.DisplayName,
                            Provider = m.Provider,
                            ContextMenuStrip = _contextMenu
                        };
                        row.MouseDown += Window_MouseDown;
                        string ids = string.Join(", ", m.ModelIds);
                        _toolTip.SetToolTip(row, string.IsNullOrEmpty(m.Provider) ? ids : $"{m.Provider} · {ids}");
                        pnlModels.Controls.Add(row);
                        _modelRows.Add((row, mi));
                    }

                    AddDynamic(pnlModels);
                    y += pnlModels.Height + 4;
                }
            }

            int newHeight = Math.Max(y + 3, WIDGET_HEIGHT);
            if (newHeight != Height)
            {
                int bottom = Bottom;
                Height = newHeight;
                if (IsHandleCreated)
                {
                    var area = Screen.FromControl(this).WorkingArea;
                    Top = Math.Max(area.Top, bottom - newHeight);
                }
            }

            ResumeLayout();
            Invalidate();
        }

        private static string ShortBucketTitle(ModelQuotaItem bucket) => bucket.Window switch
        {
            "weekly" => "위클리",
            "5h" => "5시간",
            _ => bucket.Title.Contains("Weekly", StringComparison.OrdinalIgnoreCase) ? "위클리"
               : bucket.Title.Contains("Five Hour", StringComparison.OrdinalIgnoreCase) ? "5시간"
               : "위클리"
        };

        private Label CreateSectionLabel(string text, int y)
        {
            var lbl = new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 7.4f, FontStyle.Bold),
                ForeColor = ColorMutedGray,
                AutoSize = false,
                Location = new Point(8, y),
                Size = new Size(WIDGET_WIDTH - 16, 14),
                Cursor = Cursors.SizeAll,
                ContextMenuStrip = _contextMenu
            };
            return lbl;
        }

        private Label CreateToggleSectionLabel(string text, int y)
        {
            var lbl = new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 7.2f, FontStyle.Bold),
                ForeColor = _showModelList ? ColorAccentGold : ColorMutedGray,
                AutoSize = false,
                Location = new Point(8, y),
                Size = new Size(WIDGET_WIDTH - 16, 16),
                Cursor = Cursors.Hand,
                ContextMenuStrip = _contextMenu
            };
            lbl.MouseEnter += (s, e) => lbl.ForeColor = Color.White;
            lbl.MouseLeave += (s, e) => lbl.ForeColor = _showModelList ? ColorAccentGold : ColorMutedGray;
            return lbl;
        }

        private void AddDynamic(Control c)
        {
            c.MouseDown += Window_MouseDown;
            c.ContextMenuStrip = _contextMenu;
            Controls.Add(c);
            _dynamicControls.Add(c);
        }

        private void ToggleModelList()
        {
            _showModelList = !_showModelList;
            btnModels.ForeColor = _showModelList ? ColorAccentGold : ColorMutedGray;
            SaveSettings();
            _layoutSignature = "\0";
            UpdateUI();
        }

        private void ToggleAlwaysOnTop()
        {
            _isAlwaysOnTop = !_isAlwaysOnTop;
            TopMost = _isAlwaysOnTop;
            btnPin.ForeColor = _isAlwaysOnTop ? ColorAccentGold : ColorMutedGray;
            SaveSettings();
        }

        private void CycleOpacity()
        {
            _opacityIdx = (_opacityIdx + 1) % _opacities.Length;
            Opacity = _opacities[_opacityIdx];
            SaveSettings();
        }

        private void HideWidget()
        {
            Hide();
            if (_trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(1500, "AntiGravity Meter", "위젯이 트레이로 최소화되었습니다. 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
            }
        }

        private void SetupContextMenu()
        {
            _contextMenu = new ContextMenuStrip();

            var itemModelList = new ToolStripMenuItem("모델별 상세 목록 표시 (Per-Model Usage)")
            {
                Checked = _showModelList,
                CheckOnClick = true
            };
            itemModelList.Click += (s, e) => ToggleModelList();

            var itemAlwaysOnTop = new ToolStripMenuItem("항상 위에 고정")
            {
                Checked = _isAlwaysOnTop,
                CheckOnClick = true
            };
            itemAlwaysOnTop.Click += (s, e) => ToggleAlwaysOnTop();

            _contextMenu.Opening += (s, e) =>
            {
                itemModelList.Checked = _showModelList;
                itemAlwaysOnTop.Checked = _isAlwaysOnTop;
            };

            _contextMenu.Items.Add(itemModelList);
            _contextMenu.Items.Add(itemAlwaysOnTop);
            _contextMenu.Items.Add("투명도 조절 (95% / 80% / 60%)", null, (s, e) => CycleOpacity());
            _contextMenu.Items.Add("실시간 쿼터 새로고침", null, async (s, e) => await RefreshDataAsync(true));
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add("트레이로 최소화", null, (s, e) => HideWidget());
            _contextMenu.Items.Add("종료", null, (s, e) => Application.Exit());

            ContextMenuStrip = _contextMenu;
            pnlHeader.ContextMenuStrip = _contextMenu;
        }

        private void SetupTrayIcon()
        {
            Icon appIcon = SystemIcons.Application;
            try
            {
                var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null)
                {
                    appIcon = extracted;
                    this.Icon = extracted;
                }
            }
            catch { }

            _trayIcon = new NotifyIcon
            {
                Text = "AntiGravity Quota Meter",
                Icon = appIcon,
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("위젯 열기", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; BringToFront(); });

            var itemModelList = new ToolStripMenuItem("모델별 상세 목록 표시 (Per-Model Usage)")
            {
                Checked = _showModelList,
                CheckOnClick = true
            };
            itemModelList.Click += (s, e) => ToggleModelList();

            var itemAlwaysOnTop = new ToolStripMenuItem("항상 위에 고정")
            {
                Checked = _isAlwaysOnTop,
                CheckOnClick = true
            };
            itemAlwaysOnTop.Click += (s, e) => ToggleAlwaysOnTop();

            menu.Opening += (s, e) =>
            {
                itemModelList.Checked = _showModelList;
                itemAlwaysOnTop.Checked = _isAlwaysOnTop;
            };

            menu.Items.Add(itemModelList);
            menu.Items.Add(itemAlwaysOnTop);
            menu.Items.Add("실시간 새로고침", null, async (s, e) => await RefreshDataAsync(true));
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
