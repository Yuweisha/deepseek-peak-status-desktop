using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepSeekPeakStatus;

public sealed class MainForm : Form
{
    private static readonly Color PeakColor = Color.FromArgb(0xE0, 0x7A, 0x00);
    private static readonly Color OffPeakColor = Color.FromArgb(0x4D, 0x6B, 0xFE);
    private static readonly Color MutedColor = Color.FromArgb(0x6B, 0x70, 0x80);
    private static readonly Color PanelColor = Color.FromArgb(0xF8, 0xF9, 0xFC);

    private static readonly Icon TrayPeak = MakeTrayIcon(true);
    private static readonly Icon TrayOff = MakeTrayIcon(false);

    private readonly Store store;
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer ticker = new();

    private readonly Label lblTier = new();
    private readonly Label lblRemain = new();
    private readonly Label lblDay = new();
    private readonly Label lblSource = new();
    private readonly ListView lvSegments = new();
    private readonly ListView lvPrices = new();
    private readonly Button btnRefresh = new();
    private readonly Button btnFolder = new();
    private readonly Button btnHide = new();
    private readonly Button btnExit = new();
    private readonly CheckBox chkNotify = new();
    private readonly NumericUpDown numLead = new();
    private readonly CheckBox chkStartMin = new();

    private EffectiveConfig cfg;
    private Snapshot snap;
    private string lastPriceKey = "";
    private string lastTrayText = "";
    private long lastRefreshCheck;
    private bool refreshing;
    private bool exitRequested;
    private bool ballonShownForHiding;

    public MainForm(Store store)
    {
        this.store = store;
        Icon = LoadAppIcon();
        cfg = Peak.Resolve(store.Config, store.Cache);
        snap = Peak.Build(cfg);

        BuildUi();
        BuildTray();

        ticker.Interval = 500;
        ticker.Tick += (_, _) => OnTick();
        ticker.Start();

        Shown += (_, _) =>
        {
            if (store.Config.StartMinimized)
            {
                Hide();
            }
            _ = RefreshDataAsync(false, false);
        };
    }

    // ---------------- 界面 ----------------

    private void BuildUi()
    {
        Text = "DeepSeek 峰谷时段";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 690);
        MinimumSize = new Size(560, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));

        // 状态区
        var head = new Panel { Dock = DockStyle.Fill, BackColor = PanelColor };
        lblTier.Font = new Font("Microsoft YaHei UI", 24F, FontStyle.Bold);
        lblTier.AutoSize = true;
        lblTier.Location = new Point(20, 14);
        lblRemain.AutoSize = true;
        lblRemain.Font = new Font("Microsoft YaHei UI", 11F);
        lblRemain.Location = new Point(23, 66);
        lblDay.AutoSize = true;
        lblDay.ForeColor = MutedColor;
        lblDay.Location = new Point(23, 96);
        head.Controls.Add(lblTier);
        head.Controls.Add(lblRemain);
        head.Controls.Add(lblDay);

        // 今日时段
        var gbSeg = new GroupBox { Text = "今日时段", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        lvSegments.Dock = DockStyle.Fill;
        ConfigureList(lvSegments);
        lvSegments.Columns.Add("区间", 130);
        lvSegments.Columns.Add("档位", 110);
        // 区间列跟着窗口宽度走，右端不留空列
        lvSegments.Resize += (_, _) =>
        {
            if (lvSegments.Columns.Count == 2)
            {
                lvSegments.Columns[0].Width = Math.Max(140, lvSegments.ClientSize.Width - 120);
            }
        };
        gbSeg.Controls.Add(lvSegments);

        // 模型费率
        var gbPrice = new GroupBox { Text = "模型费率", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        lvPrices.Dock = DockStyle.Fill;
        ConfigureList(lvPrices);
        lvPrices.Columns.Add("模型", 170);
        lvPrices.Columns.Add("档位", 60);
        lvPrices.Columns.Add("缓存命中输入", 120);
        lvPrices.Columns.Add("缓存未命中输入", 130);
        lvPrices.Columns.Add("输出", 90);
        gbPrice.Controls.Add(lvPrices);

        // 底部
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        var checks = new Panel { Dock = DockStyle.Fill };
        chkNotify.AutoSize = true;
        chkNotify.Text = "切换前提醒：提前";
        chkNotify.Location = new Point(12, 6);
        chkNotify.Checked = store.Config.NotifyEnabled;
        chkNotify.CheckedChanged += (_, _) =>
        {
            store.Config.NotifyEnabled = chkNotify.Checked;
            numLead.Enabled = chkNotify.Checked;
            store.SaveConfig();
        };
        numLead.Location = new Point(150, 3);
        numLead.Size = new Size(56, 25);
        numLead.Minimum = 1;
        numLead.Maximum = 60;
        numLead.Value = Math.Clamp(store.Config.NotifyLeadMinutes, 1, 60);
        numLead.Enabled = chkNotify.Checked;
        numLead.ValueChanged += (_, _) =>
        {
            store.Config.NotifyLeadMinutes = (int)numLead.Value;
            store.SaveConfig();
        };
        var lblMin = new Label { Text = "分钟", AutoSize = true, Location = new Point(212, 7), ForeColor = MutedColor };
        chkStartMin.AutoSize = true;
        chkStartMin.Text = "启动时最小化到托盘";
        chkStartMin.Location = new Point(272, 6);
        chkStartMin.Checked = store.Config.StartMinimized;
        chkStartMin.CheckedChanged += (_, _) =>
        {
            store.Config.StartMinimized = chkStartMin.Checked;
            store.SaveConfig();
        };
        checks.Controls.Add(chkNotify);
        checks.Controls.Add(numLead);
        checks.Controls.Add(lblMin);
        checks.Controls.Add(chkStartMin);

        lblSource.Dock = DockStyle.Fill;
        lblSource.ForeColor = MutedColor;
        lblSource.Padding = new Padding(12, 2, 12, 0);
        lblSource.TextAlign = ContentAlignment.TopLeft;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 6, 8, 6),
            WrapContents = false
        };
        StyleButton(btnExit, "退出", 70, () => ExitApp());
        StyleButton(btnHide, "最小化到托盘", 110, () =>
        {
            Hide();
            ShowTrayTip("程序仍在托盘运行，双击托盘图标可重新打开。", ToolTipIcon.Info);
        });
        StyleButton(btnFolder, "打开数据文件夹", 120, () =>
        {
            Directory.CreateDirectory(Store.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", Store.DataDir) { UseShellExecute = true });
        });
        StyleButton(btnRefresh, "立即更新", 90, () => _ = RefreshDataAsync(true, true));
        buttons.Controls.Add(btnExit);
        buttons.Controls.Add(btnHide);
        buttons.Controls.Add(btnFolder);
        buttons.Controls.Add(btnRefresh);

        bottom.Controls.Add(checks, 0, 0);
        bottom.Controls.Add(lblSource, 0, 1);
        bottom.Controls.Add(buttons, 0, 2);

        root.Controls.Add(head, 0, 0);
        root.Controls.Add(gbSeg, 0, 1);
        root.Controls.Add(gbPrice, 0, 2);
        root.Controls.Add(bottom, 0, 3);
        Controls.Add(root);
    }

    private static void ConfigureList(ListView lv)
    {
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.MultiSelect = false;
        lv.HideSelection = true;
        lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        lv.BorderStyle = BorderStyle.None;
        lv.GridLines = false;
        lv.Scrollable = true;
        lv.Font = new Font("Microsoft YaHei UI", 9F);
    }

    private static void StyleButton(Button b, string text, int width, Action onClick)
    {
        b.Text = text;
        b.Width = width;
        b.Height = 32;
        b.FlatStyle = FlatStyle.System;
        b.Margin = new Padding(6, 0, 0, 0);
        b.Click += (_, _) => onClick();
    }

    private void BuildTray()
    {
        tray.Icon = TrayOff;
        tray.Text = "DeepSeek 峰谷时段";
        tray.Visible = true;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowFromTray();
            }
        };
        tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        menu.Items.Add("立即从官网更新", null, (_, _) => _ = RefreshDataAsync(true, true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = menu;
    }

    private void ShowTrayTip(string text, ToolTipIcon icon)
    {
        tray.BalloonTipTitle = "DeepSeek 峰谷时段";
        tray.BalloonTipText = text;
        tray.BalloonTipIcon = icon;
        tray.ShowBalloonTip(8000);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        exitRequested = true;
        ticker.Stop();
        tray.Visible = false;
        tray.Dispose();
        Close();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            // 点 × 只是收进托盘，程序继续盯着切换点
            e.Cancel = true;
            Hide();
            if (!ballonShownForHiding)
            {
                ballonShownForHiding = true;
                ShowTrayTip("仍在后台运行，双击托盘图标可重新打开，右键可退出。", ToolTipIcon.Info);
            }
            return;
        }
        base.OnFormClosing(e);
    }

    // ---------------- 主循环 ----------------

    private void OnTick()
    {
        cfg = Peak.Resolve(store.Config, store.Cache);
        snap = Peak.Build(cfg);
        Render();
        MaybeNotify();

        if (Environment.TickCount64 - lastRefreshCheck > 60_000)
        {
            lastRefreshCheck = Environment.TickCount64;
            _ = RefreshDataAsync(false, false);
        }
    }

    private void Render()
    {
        bool peak = snap.IsPeak;
        var color = peak ? PeakColor : OffPeakColor;
        lblTier.Text = peak ? "高峰时段" : "空闲时段";
        lblTier.ForeColor = color;

        if (snap.Remaining == null)
        {
            lblRemain.Text = "当前没有峰谷切换";
        }
        else
        {
            string target = snap.NextPeak == true ? "高峰时段" : "空闲时段";
            lblRemain.Text = $"距离进入{target}还有 {Peak.FormatRemaining(snap.Remaining.Value)}（{snap.NextLabel}）";
        }
        lblRemain.ForeColor = Color.FromArgb(0x33, 0x36, 0x40);

        string dayNote = snap.AllDayOff ? "，全天按空闲价计费" : "";
        string tzLabel = cfg.TimeZone.Id == Peak.ResolveTimeZone("Asia/Shanghai").Id ? "北京时间" : cfg.Raw.TimeZone;
        lblDay.Text = $"{snap.DateKey} {Peak.WeekdayName(snap.Weekday)} · {snap.DayKind}{dayNote} · 时区 {tzLabel}";

        lblSource.Text =
            $"{SourceSummary()}{Environment.NewLine}" +
            $"峰时区间：{(cfg.RawWindows.Count > 0 ? string.Join("、", cfg.RawWindows) : "未配置")}；周末与法定节假日全天空闲（已加载 {cfg.Holidays.Count} 个节假日日期）";

        RenderSegments();
        RenderPrices();

        string trayText = $"{snap.TierName} {snap.DateKey.Substring(5)}";
        if (snap.Remaining != null)
        {
            trayText = $"DeepSeek {snap.TierName} 剩 {Peak.FormatRemaining(snap.Remaining.Value)}";
        }
        trayText = trayText.Length > 62 ? trayText[..62] : trayText;
        if (trayText != lastTrayText)
        {
            tray.Text = trayText;
            lastTrayText = trayText;
        }
        var icon = peak ? TrayPeak : TrayOff;
        if (!ReferenceEquals(tray.Icon, icon))
        {
            tray.Icon = icon;
        }
    }

    private string SourceSummary()
    {
        string pricing;
        if (!cfg.AutoUpdateEnabled)
        {
            pricing = "价格/时段：本地设置（自动更新已关闭）";
        }
        else if (cfg.ScheduleFromWeb)
        {
            pricing = $"价格/时段：官网（{Peak.AgeLabel(cfg.PricingCache?.FetchedAt ?? 0)}）";
        }
        else if (cfg.PricingCache != null && cfg.TimeZone.Id != Peak.ResolveTimeZone("Asia/Shanghai").Id)
        {
            pricing = "价格/时段：本地设置（官网时段为北京时间，当前时区不适用）";
        }
        else
        {
            pricing = "价格/时段：本地设置";
        }
        string holidays = cfg.HolidaysFromWeb
            ? $"节假日：在线数据 {cfg.HolidayCache!.Dates.Count} 天（{Peak.AgeLabel(cfg.HolidayCache.FetchedAt)}）"
            : $"节假日：本地设置 {cfg.Holidays.Count} 天";
        return $"{pricing} ｜ {holidays}";
    }

    private void RenderSegments()
    {
        string key = string.Join(",", snap.Segments.Select(s => $"{s.Start}-{s.End}-{s.Peak}")) + "|" + snap.Current.Start;
        if (lvSegments.Tag as string == key)
        {
            return;
        }
        lvSegments.Tag = key;
        lvSegments.BeginUpdate();
        lvSegments.Items.Clear();
        foreach (var seg in snap.Segments)
        {
            bool current = seg.Start == snap.Current.Start && seg.Peak == snap.Current.Peak;
            var item = new ListViewItem(Peak.FormatMinute(seg.Start) + " - " + Peak.FormatMinute(seg.End));
            item.SubItems.Add(seg.Peak ? "峰时" : "空闲");
            item.ForeColor = seg.Peak ? PeakColor : OffPeakColor;
            if (current)
            {
                item.Text = "▶ " + item.Text;
                item.Font = new Font(lvSegments.Font, FontStyle.Bold);
            }
            lvSegments.Items.Add(item);
        }
        lvSegments.EndUpdate();
    }

    private void RenderPrices()
    {
        var prices = cfg.Prices;
        string key = AppJson.SerializeForKey(prices) + "|" + (cfg.ScheduleFromWeb ? "web" : "local");
        if (key == lastPriceKey)
        {
            return;
        }
        lastPriceKey = key;

        lvPrices.BeginUpdate();
        lvPrices.Items.Clear();
        foreach (var (id, model) in prices.Models)
        {
            AddPriceRow(model.Label ?? id, "峰时", model.Peak, PeakColor);
            AddPriceRow(model.Label ?? id, "空闲", model.OffPeak, OffPeakColor);
        }
        lvPrices.EndUpdate();
    }

    private void AddPriceRow(string model, string tier, PriceTier price, Color color)
    {
        string Show(string? v) => string.IsNullOrWhiteSpace(v) ? "--" : v.Trim();
        var item = new ListViewItem(model);
        item.SubItems.Add(tier);
        item.SubItems.Add(Show(price.CacheHit));
        item.SubItems.Add(Show(price.CacheMiss));
        item.SubItems.Add(Show(price.Output));
        item.ForeColor = color;
        lvPrices.Items.Add(item);
    }

    private void MaybeNotify()
    {
        if (!cfg.NotifyEnabled || snap.ChangeKey == null || snap.Remaining == null)
        {
            return;
        }
        if (snap.Remaining > cfg.NotifyLeadMinutes * 60.0)
        {
            return;
        }
        if (snap.ChangeKey == store.Cache.LastNotified)
        {
            return;
        }
        // 先记账再弹窗：等用户看提示的这几秒里，下一秒的 tick 不能重复触发
        store.Cache.LastNotified = snap.ChangeKey;
        store.SaveCache();

        int minutes = Math.Max(1, (int)Math.Ceiling(snap.Remaining.Value / 60));
        bool enteringPeak = snap.NextPeak == true;
        ShowTrayTip(
            enteringPeak
                ? $"约 {minutes} 分钟后进入高峰时段（{snap.NextLabel}），单价为空闲时段的 2 倍。"
                : $"约 {minutes} 分钟后进入空闲时段（{snap.NextLabel}），单价降为高峰时段的一半。",
            enteringPeak ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private async Task RefreshDataAsync(bool force, bool interactive)
    {
        if (refreshing)
        {
            return;
        }
        if (!store.Config.AutoUpdateEnabled && !force)
        {
            return;
        }
        refreshing = true;
        if (interactive)
        {
            btnRefresh.Enabled = false;
            btnRefresh.Text = "更新中…";
        }
        try
        {
            var result = await Updater.RefreshAsync(store, force);
            lastRefreshCheck = Environment.TickCount64;
            cfg = Peak.Resolve(store.Config, store.Cache);
            snap = Peak.Build(cfg);
            lastPriceKey = "";
            Render();
            if (interactive)
            {
                ShowTrayTip(result.Ok
                    ? "价格、时段与节假日已从官网更新。"
                    : "部分数据更新失败，沿用上次结果：" + string.Join("；", result.Problems),
                    result.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            if (interactive)
            {
                ShowTrayTip("更新失败：" + ex.Message, ToolTipIcon.Warning);
            }
        }
        finally
        {
            refreshing = false;
            btnRefresh.Enabled = true;
            btnRefresh.Text = "立即更新";
        }
    }

    // ---------------- 托盘图标 ----------------

    private static readonly Color OffPeakAccent = Color.FromArgb(0x4D, 0x6B, 0xFE); // DeepSeek 官方蓝
    private static readonly Color PeakAccent = Color.FromArgb(0xFF, 0x8C, 0x00);

    /// <summary>把鲸鱼掩码整体染成当前档位的颜色，形状仍是 DeepSeek logo。</summary>
    private static Icon MakeTrayIcon(bool peak)
    {
        int size = SystemInformation.SmallIconSize.Width <= 16 ? 16 : 32;
        using var mask = LoadMask(size);
        var color = peak ? PeakAccent : OffPeakAccent;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, size, size);
        var src = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dst = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(src.Stride) * size;
            var buffer = new byte[bytes];
            Marshal.Copy(src.Scan0, buffer, 0, bytes);
            for (int i = 0; i + 3 < buffer.Length; i += 4)
            {
                buffer[i] = color.B;
                buffer[i + 1] = color.G;
                buffer[i + 2] = color.R;
                // alpha 保持掩码原样
            }
            Marshal.Copy(buffer, 0, dst.Scan0, bytes);
        }
        finally
        {
            mask.UnlockBits(src);
            bmp.UnlockBits(dst);
        }
        // 句柄故意不释放：Icon 在程序生命周期内一直被托盘引用
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Bitmap LoadMask(int size)
    {
        var bytes = Convert.FromBase64String(size <= 16 ? TrayMask.M16 : TrayMask.M32);
        using var ms = new MemoryStream(bytes);
        using var img = new Bitmap(ms);
        return new Bitmap(img);
    }

    /// <summary>
    /// 窗口与任务栏按钮用的图标。WinForms 默认只给一个占位图标，
    /// 必须从嵌入的 app.ico 取，才是 DeepSeek 鲸鱼。
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly
                .GetManifestResourceStream("DeepSeekPeakStatus.app.ico");
            if (stream != null)
            {
                return new Icon(stream);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
        return SystemIcons.Application;
    }
}
