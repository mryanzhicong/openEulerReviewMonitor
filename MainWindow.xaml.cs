using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ForumReviewMonitor;

public partial class MainWindow : Window
{
    private List<ReviewItem> reviewItems = new();
    private ReviewStats stats = new();
    private int reviewPage;
    private Settings settings;
    private readonly AppLog log = new();
    private readonly ForumClient forum = new();
    private readonly RobotClient robots = new();
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? operation;
    private bool running, busy, exiting, authAlerted;
    private DateTimeOffset? next, lastAttempt, lastSuccess;
    private string resultStatus = "";
    private string activity = "";
    private readonly System.Drawing.Icon trayIcon;
    private readonly Dictionary<int, (string Title, string Detail, bool? Success)> pushResults = new();

    public MainWindow() : this(false) { }
    internal MainWindow(bool headless)
    {
        InitializeComponent();
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height * 0.85));
        settings = Storage.LoadSettings();
        stats = Storage.LoadStats(settings);
        LoadControls();
        UpdateEnabledChannels();
        RefreshReviewPage();
        RefreshStatsPage();
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/openEulerReviewMonitor;component/Assets/openeuler.ico")).Stream)
        using (var icon = new System.Drawing.Icon(stream)) trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Text = "openEuler 论坛审核助手 · 已停止", Icon = trayIcon, Visible = !headless };
        log.Added += AppendLog;
        CookieBox.PasswordChanged += (_, _) => ResetAuthResult();
        ApiKeyBox.PasswordChanged += (_, _) => ResetAuthResult();
        ForumBox.TextChanged += (_, _) => ResetAuthResult();
        UsernameBox.TextChanged += (_, _) => ResetAuthResult();
        WeComHook.PasswordChanged += (_, _) => ResetPushResult(1);
        DingHook.PasswordChanged += (_, _) => ResetPushResult(2);
        DingSecret.PasswordChanged += (_, _) => ResetPushResult(2);
        FeishuHook.PasswordChanged += (_, _) => ResetPushResult(3);
        FeishuSecret.PasswordChanged += (_, _) => ResetPushResult(3);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => RestoreWindow());
        menu.Items.Add("启动监控", null, async (_, _) => await StartAsync());
        menu.Items.Add("停止监控", null, (_, _) => Stop());
        menu.Items.Add("立即检查", null, async (_, _) => await CheckAsync());
        menu.Items.Add("打开审核页面", null, (_, _) => OpenReview());
        menu.Items.Add("查看日志", null, (_, _) => ShowLogs());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => RestoreWindow();
        tray.BalloonTipClicked += (_, _) => OpenReview();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            UpdateStatus();
            if (running && !busy && next <= DateTimeOffset.UtcNow) await CheckAsync();
        };
        timer.Start();
        SystemEvents.PowerModeChanged += PowerChanged;
        Closing += OnClosing;
        CloseTrayBox.Checked += CloseTrayChanged;
        CloseTrayBox.Unchecked += CloseTrayChanged;
        IntervalBox.TextChanged += MonitorTimeChanged;
        RepeatBox.TextChanged += MonitorTimeChanged;
        IntervalBox.LostKeyboardFocus += RestoreInvalidTime;
        RepeatBox.LostKeyboardFocus += RestoreInvalidTime;
        log.Write("INFO", "程序启动。Windows 通知始终启用；监控尚未启动。");
        if (Storage.LoadWarning is string warning) Report("WARN", warning);
    }

    private void LoadControls()
    {
        ForumBox.Text = settings.ForumUrl;
        AuthBox.SelectedIndex = settings.AuthMode == "Cookie" ? 0 : 1;
        CookieBox.Password = settings.Cookie;
        ApiKeyBox.Password = settings.ApiKey;
        UsernameBox.Text = settings.ApiUsername;
        IntervalBox.Text = settings.IntervalMinutes.ToString();
        RepeatBox.Text = settings.RepeatMinutes.ToString();
        CloseTrayBox.IsChecked = settings.CloseToTray;
        WeComEnabled.IsChecked = settings.WeCom.Enabled; WeComHook.Password = settings.WeCom.Webhook;
        DingEnabled.IsChecked = settings.DingTalk.Enabled; DingHook.Password = settings.DingTalk.Webhook; DingSecret.Password = settings.DingTalk.Secret;
        FeishuEnabled.IsChecked = settings.Feishu.Enabled; FeishuHook.Password = settings.Feishu.Webhook; FeishuSecret.Password = settings.Feishu.Secret;
    }
    private Settings ReadControls(bool auth = true)
    {
        if (!int.TryParse(IntervalBox.Text, out int interval) || !int.TryParse(RepeatBox.Text, out int repeat)) throw new InvalidOperationException("检查间隔和重复提醒必须填写整数分钟。");
        var value = new Settings
        {
            ForumUrl = ForumBox.Text.Trim(), AuthMode = AuthBox.SelectedIndex == 0 ? "Cookie" : "API Key",
            Cookie = CookieBox.Password.Trim(), ApiKey = ApiKeyBox.Password.Trim(), ApiUsername = UsernameBox.Text.Trim(),
            IntervalMinutes = interval, RepeatMinutes = repeat, CloseToTray = CloseTrayBox.IsChecked == true,
            WeCom = new() { Enabled = WeComEnabled.IsChecked == true, Webhook = WeComHook.Password.Trim() },
            DingTalk = new() { Enabled = DingEnabled.IsChecked == true, Webhook = DingHook.Password.Trim(), Secret = DingSecret.Password.Trim() },
            Feishu = new() { Enabled = FeishuEnabled.IsChecked == true, Webhook = FeishuHook.Password.Trim(), Secret = FeishuSecret.Password.Trim() }
        };
        value.Validate(auth);
        return value;
    }
    private Settings ReadAuthentication(bool requireAuth = true)
    {
        var candidate = new Settings
        {
            ForumUrl = ForumBox.Text.Trim(), AuthMode = AuthBox.SelectedIndex == 0 ? "Cookie" : "API Key",
            Cookie = CookieBox.Password.Trim(), ApiKey = ApiKeyBox.Password.Trim(), ApiUsername = UsernameBox.Text.Trim(),
            IntervalMinutes = settings.IntervalMinutes, RepeatMinutes = settings.RepeatMinutes, CloseToTray = settings.CloseToTray,
            WeCom = settings.WeCom, DingTalk = settings.DingTalk, Feishu = settings.Feishu
        };
        candidate.ValidateAuthentication(requireAuth);
        return candidate;
    }
    private void SaveAuthClick(object sender, RoutedEventArgs e)
    {
        try { Apply(ReadAuthentication(false)); Report("INFO", "认证信息已加密保存。"); }
        catch (Exception ex) { CompleteAuth(false, "保存失败：" + SafeError(ex)); }
    }
    private void ResetAuthResult()
    {
        if (AuthResultTitle == null || busy) return;
        SetAuthResult("尚未验证", "认证信息已修改，请重新验证。", "#EDF2F8", "#334155");
    }
    private void SetAuthResult(string title, string detail, string background, string foreground)
    {
        AuthResultTitle.Text = title; AuthResultText.Text = detail;
        AuthResultPanel.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        AuthResultTitle.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
    }
    private void CompleteAuth(bool success, string detail)
    {
        SetAuthResult(success ? "验证成功" : "验证失败", detail, success ? "#E8F5EC" : "#FFF0EE", success ? "#166534" : "#B42318");
        Report(success ? "INFO" : "ERROR", detail);
    }
    private void Apply(Settings value)
    {
        Storage.SaveSettings(value);
        if (settings.ForumUrl != value.ForumUrl || settings.AuthMode != value.AuthMode || settings.Cookie != value.Cookie || settings.ApiKey != value.ApiKey || settings.ApiUsername != value.ApiUsername)
        {
            reviewItems.Clear(); reviewPage = 0; RefreshReviewPage(); CountText.Text = "待审核：—"; lastSuccess = null; lastAttempt = null; resultStatus = "";
            stats = Storage.LoadStats(value); RefreshStatsPage();
        }
        settings = value;
        UpdateEnabledChannels();
    }
    private void UpdateEnabledChannels()
    {
        var names = new List<string> { "Windows 通知（始终启用）" };
        if (settings.WeCom.Enabled) names.Add("企业微信");
        if (settings.DingTalk.Enabled) names.Add("钉钉");
        if (settings.Feishu.Enabled) names.Add("飞书");
        EnabledChannelsText.Text = "当前已启用：" + string.Join("、", names);
    }
    private void CloseTrayChanged(object sender, RoutedEventArgs e)
    {
        bool value = CloseTrayBox.IsChecked == true;
        bool previous = settings.CloseToTray;
        if (value == previous) return;
        settings.CloseToTray = value;
        try
        {
            Storage.SaveSettings(settings);
            Report("INFO", value ? "关闭窗口将隐藏到托盘，已自动保存。" : "关闭窗口将直接退出，已自动保存。");
        }
        catch (Exception ex)
        {
            settings.CloseToTray = previous;
            CloseTrayBox.IsChecked = previous;
            Error(ex);
        }
    }
    private void MonitorTimeChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        bool interval = box == IntervalBox;
        int previous = interval ? settings.IntervalMinutes : settings.RepeatMinutes;
        if (!int.TryParse(box.Text, out int value) || value < (interval ? 1 : 0) || value > (interval ? 1440 : 10080))
        {
            box.ToolTip = interval ? "请输入 1–1440 分钟；当前输入尚未保存。" : "请输入 0–10080 分钟；当前输入尚未保存。";
            return;
        }
        box.ToolTip = null;
        if (value == previous) return;
        if (interval) settings.IntervalMinutes = value; else settings.RepeatMinutes = value;
        try
        {
            Storage.SaveSettings(settings);
            if (interval && running && next.HasValue) next = DateTimeOffset.UtcNow.AddMinutes(value);
            Report("INFO", (interval ? "检查间隔" : "重复提醒") + $"已设为 {value} 分钟，已自动保存。");
            UpdateStatus();
        }
        catch (Exception ex)
        {
            if (interval) settings.IntervalMinutes = previous; else settings.RepeatMinutes = previous;
            box.Text = previous.ToString();
            Error(ex);
        }
    }
    private void RestoreInvalidTime(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        box.Text = (box == IntervalBox ? settings.IntervalMinutes : settings.RepeatMinutes).ToString();
        box.ToolTip = null;
    }
    private void AuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CookiePanel == null || ApiPanel == null) return;
        CookiePanel.Visibility = AuthBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApiPanel.Visibility = AuthBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ResetAuthResult();
    }
    private async void ToggleMonitorClick(object sender, RoutedEventArgs e)
    {
        if (running || busy) Stop(); else await StartAsync();
    }
    private void NavigationSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 1040;
        Grid.SetRow(ActionBar, narrow ? 0 : 1);
        ActionBar.Margin = narrow ? new Thickness(0, 0, 0, 8) : new Thickness(0);
    }
    private async Task StartAsync()
    {
        if (running || busy) return;
        try { Apply(ReadControls()); }
        catch (Exception ex) { Error(ex); return; }
        running = true; authAlerted = false;
        Report("INFO", $"监控已启动，检查间隔 {settings.IntervalMinutes} 分钟。");
        await CheckAsync();
    }
    private void Stop()
    {
        running = false; next = null; operation?.Cancel();
        Report("INFO", "监控已停止。"); UpdateStatus();
    }
    private async void CheckClick(object sender, RoutedEventArgs e) => await CheckAsync();
    private async Task CheckAsync()
    {
        if (busy || exiting) return;
        try { if (!running) Apply(ReadControls()); }
        catch (Exception ex) { Error(ex); return; }
        busy = true; activity = "检查中"; operation = new(); next = null;
        lastAttempt = DateTimeOffset.Now; UpdateStatus();
        log.Write("INFO", "开始读取全部待处理审核项。");
        try
        {
            var state = Storage.LoadState(settings);
            var items = await forum.FetchAsync(settings, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            lastSuccess = DateTimeOffset.Now; authAlerted = false; resultStatus = "成功";
            reviewItems = items; RefreshReviewPage();
            CountText.Text = $"待审核：{items.Count}";
            var newlyCompleted = stats.Apply(items, DateTimeOffset.Now);
            if (newlyCompleted.Count > 0)
            {
                try
                {
                    var completionTimes = await forum.FetchCompletionTimesAsync(settings, newlyCompleted.Select(x => x.Id), operation.Token);
                    foreach (var record in newlyCompleted)
                        if (completionTimes.TryGetValue(record.Id, out var completedAt)) record.ServerCompletedAt = completedAt;
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception ex) { log.Write("WARN", "未读取到服务端审核完成时间，已保留本地观察时间。" + SafeError(ex)); }
            }
            Storage.SaveStats(settings, stats);
            RefreshStatsPage();
            state.Prune(items);
            Storage.SaveState(settings, state);
            log.Write("INFO", $"检查成功，当前待处理 {items.Count} 项。");
            int failures = 0;
            foreach (var (name, robot) in Channels())
            {
                operation.Token.ThrowIfCancellationRequested();
                var key = ChannelKey(name, robot);
                var due = state.Due(key, items, settings.RepeatMinutes, DateTimeOffset.UtcNow);
                if (due.Count == 0) continue;
                try
                {
                    string text = NotificationText.Create(due, items.Count, settings.BaseUri);
                    if (robot == null) NotifyWindows("openEuler 审核提醒", text);
                    else await robots.SendAsync(name, robot, text, operation.Token);
                    state.Mark(key, due, DateTimeOffset.UtcNow);
                    Storage.SaveState(settings, state);
                    log.Write("INFO", $"{name}：已{(robot == null ? "提交系统通知" : "发送")}，涵盖 {due.Count} 项。");
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception ex) { failures++; log.Write("ERROR", $"{name}推送失败，下次检查补发。{SafeError(ex)}"); }
            }
            Report(failures == 0 ? "INFO" : "WARN", failures == 0 ? $"检查完成，待处理 {items.Count} 项；没有新增时不会重复推送。" : $"检查完成，{failures} 个通知渠道失败，请查看日志。");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { log.Write("INFO", "当前操作已取消。"); }
        catch (Exception ex)
        {
            resultStatus = "失败"; Error(ex);
            if (ex is AuthenticationException && !authAlerted)
            {
                authAlerted = true;
                NotifyWindows("openEuler 认证需要更新", "认证失效或权限不足，请打开论坛审核助手更新凭据。监控会按间隔重试。");
            }
        }
        finally
        {
            operation.Dispose(); operation = null; busy = false; activity = "";
            if (running) next = DateTimeOffset.UtcNow.AddMinutes(settings.IntervalMinutes);
            UpdateStatus();
        }
    }
    private IEnumerable<(string Name, RobotSettings? Robot)> Channels()
    {
        yield return ("Windows", null);
        if (settings.WeCom.Enabled) yield return ("企业微信", settings.WeCom);
        if (settings.DingTalk.Enabled) yield return ("钉钉", settings.DingTalk);
        if (settings.Feishu.Enabled) yield return ("飞书", settings.Feishu);
    }
    private static string ChannelKey(string name, RobotSettings? robot) => robot == null ? name : name + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(robot.Webhook)))[..16];
    private async void ValidateClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Settings candidate;
        try { candidate = ReadAuthentication(); }
        catch (Exception ex) { CompleteAuth(false, SafeError(ex)); return; }
        busy = true; activity = "验证中"; operation = new(TimeSpan.FromSeconds(60));
        ValidateButton.Content = "正在验证…";
        SetAuthResult("正在验证…", "正在连接论坛并检查审核权限，请稍候（最多 60 秒）。可点击“停止监控”取消。", "#EAF2FF", "#1D4ED8");
        log.Write("INFO", $"开始验证 {candidate.AuthMode} 认证。"); UpdateStatus();
        bool success = false;
        string detail;
        try
        {
            var items = await forum.FetchAsync(candidate, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            success = true;
            detail = $"{candidate.AuthMode} 验证成功，可读取 {items.Count} 个待处理项。\n验证时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n此次验证未推送通知；点击“保存认证”可保存当前凭据。";
        }
        catch (Exception ex) { detail = $"{candidate.AuthMode} 验证失败：{SafeError(ex)}"; }
        finally { busy = false; activity = ""; operation.Dispose(); operation = null; ValidateButton.Content = "验证认证"; UpdateStatus(); }
        CompleteAuth(success, detail);
    }
    private static string ChannelName(int index) => index switch { 0 => "Windows", 1 => "企业微信", 2 => "钉钉", 3 => "飞书", _ => throw new InvalidOperationException("请选择推送渠道。") };
    private void ChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowsConfig == null || PushResultPanel == null) return;
        int index = ChannelBox.SelectedIndex;
        WindowsConfig.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        WeComConfig.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        DingConfig.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        FeishuConfig.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        TestButton.Content = "测试 " + ChannelName(index) + " 通知";
        SaveChannelButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        DisplayPushResult(index);
    }
    private void ResetPushResult(int index)
    {
        if (busy) return;
        pushResults.Remove(index);
        if (ChannelBox.SelectedIndex == index) DisplayPushResult(index);
    }
    private void DisplayPushResult(int index)
    {
        var result = pushResults.TryGetValue(index, out var saved) ? saved : (Title: "尚未测试", Detail: "点击当前渠道的测试按钮，结果会显示在这里。", Success: (bool?)null);
        PushResultTitle.Text = result.Title;
        PushResultText.Text = result.Detail;
        PushResultPanel.Background = (Brush)new BrushConverter().ConvertFromString(result.Success == true ? "#E8F5EC" : result.Success == false ? "#FFF0EE" : "#EDF2F8")!;
        PushResultTitle.Foreground = result.Success == true ? Brushes.DarkGreen : result.Success == false ? Brushes.Firebrick : Brushes.SlateGray;
    }
    private void SetPushResult(int index, string title, string detail, bool? success)
    {
        pushResults[index] = (title, detail, success);
        if (ChannelBox.SelectedIndex == index) DisplayPushResult(index);
    }
    private RobotSettings? ReadChannel(int index, bool testing)
    {
        RobotSettings? robot = index switch
        {
            0 => null,
            1 => new() { Enabled = testing || WeComEnabled.IsChecked == true, Webhook = WeComHook.Password.Trim() },
            2 => new() { Enabled = testing || DingEnabled.IsChecked == true, Webhook = DingHook.Password.Trim(), Secret = DingSecret.Password.Trim() },
            3 => new() { Enabled = testing || FeishuEnabled.IsChecked == true, Webhook = FeishuHook.Password.Trim(), Secret = FeishuSecret.Password.Trim() },
            _ => throw new InvalidOperationException("请选择推送渠道。")
        };
        if (robot != null) Settings.ValidatePushChannel(ChannelName(index), robot);
        return robot;
    }
    private void SaveChannelClick(object sender, RoutedEventArgs e)
    {
        int index = ChannelBox.SelectedIndex;
        try
        {
            var robot = ReadChannel(index, false);
            if (robot == null) return;
            var candidate = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
            if (index == 1) candidate.WeCom = robot;
            else if (index == 2) candidate.DingTalk = robot;
            else candidate.Feishu = robot;
            Apply(candidate);
            SetPushResult(index, "已保存", ChannelName(index) + "配置已加密保存；" + (robot.Enabled ? "已启用同步推送。" : "当前未启用。"), true);
            Report("INFO", ChannelName(index) + "配置已保存。");
        }
        catch (Exception ex) { SetPushResult(index, "保存失败", SafeError(ex), false); Error(ex); }
    }
    private async void TestClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        int index = ChannelBox.SelectedIndex;
        string name = ChannelName(index);
        RobotSettings? robot;
        try { robot = ReadChannel(index, true); }
        catch (Exception ex) { SetPushResult(index, "测试失败", SafeError(ex), false); Error(ex); return; }
        busy = true; activity = "测试推送中"; operation = new(); UpdateStatus();
        SetPushResult(index, "正在测试…", "正在向 " + name + " 发送测试通知，请稍候。", null);
        try
        {
            const string text = "openEuler 论坛审核助手：这是一条测试通知。";
            if (robot == null) NotifyWindows("openEuler 测试推送", text);
            else await robots.SendAsync(name, robot, text, operation.Token);
            string detail = name + (robot == null ? "：已提交系统通知，实际显示受 Windows 通知和勿扰设置影响。" : "：测试消息发送成功。");
            SetPushResult(index, "测试成功", detail, true);
            Report("INFO", detail);
        }
        catch (Exception ex) { SetPushResult(index, "测试失败", SafeError(ex), false); Error(ex); }
        finally { busy = false; activity = ""; operation.Dispose(); operation = null; UpdateStatus(); }
    }
    private void AdjustTime(TextBox box, int delta)
    {
        int minimum = box == IntervalBox ? 1 : 0;
        int maximum = box == IntervalBox ? 1440 : 10080;
        int fallback = box == IntervalBox ? 15 : 0;
        int value = int.TryParse(box.Text, out int parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
        box.Text = Math.Clamp(value + delta, minimum, maximum).ToString();
        box.CaretIndex = box.Text.Length;
    }
    private void AdjustTimeClick(object sender, RoutedEventArgs e)
    {
        string tag = (string)((FrameworkElement)sender).Tag;
        AdjustTime(tag.StartsWith("Interval") ? IntervalBox : RepeatBox, tag.EndsWith("+") ? 1 : -1);
    }
    private void TimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) { AdjustTime((TextBox)sender, e.Key == Key.Up ? 1 : -1); e.Handled = true; }
    }
    private void NotifyWindows(string title, string text) => tray.ShowBalloonTip(8000, title, NotificationText.Clip(text, 240), Forms.ToolTipIcon.Info);
    private void UpdateStatus()
    {
        StatusText.Text = running ? "监控：运行中" : "监控：已停止";
        CheckStateText.Text = "检查：" + (busy ? activity : resultStatus == "" ? "尚未检查" : resultStatus);
        CheckStateText.Foreground = (!busy ? resultStatus : "") switch
        {
            "成功" => Brushes.ForestGreen,
            "失败" => Brushes.Firebrick,
            _ => Brushes.SlateGray
        };
        NextText.Text = running && next.HasValue ? $"下次检查：{Math.Max(0, (int)(next.Value - DateTimeOffset.UtcNow).TotalSeconds) / 60:00}:{Math.Max(0, (int)(next.Value - DateTimeOffset.UtcNow).TotalSeconds) % 60:00}" : "下次检查：—";
        LastText.Text = $"上次检查：{lastAttempt?.ToString("MM-dd HH:mm:ss") ?? "—"}";
        SuccessText.Text = $"上次成功：{lastSuccess?.ToString("MM-dd HH:mm:ss") ?? "—"}";
        StartButton.IsEnabled = true;
        StartButton.Content = running ? "停止监控" : busy ? "取消操作" : "启动监控";
        StartButton.Background = running || busy ? Brushes.White : new SolidColorBrush(Color.FromRgb(37, 99, 235));
        StartButton.Foreground = running || busy ? new SolidColorBrush(Color.FromRgb(36, 54, 75)) : Brushes.White;
        CheckButton.IsEnabled = TestButton.IsEnabled = !busy;
        AuthSettingsPanel.IsEnabled = PushSettingsPanel.IsEnabled = !running && !busy;
        tray.Text = running ? "openEuler 论坛审核助手 · 监控中" : "openEuler 论坛审核助手 · 已停止";
    }
    private void Report(string level, string text) { FeedbackText.Text = text.Replace('\r', ' ').Replace('\n', ' '); log.Write(level, text.Replace('\r', ' ').Replace('\n', ' ')); }
    private void Error(Exception ex) => Report("ERROR", SafeError(ex));
    private static string SafeError(Exception ex) => ex switch
    {
        AuthenticationException => ex.Message,
        InvalidOperationException => ex.Message,
        HttpRequestException => "网络连接失败，请检查网络或系统代理。",
        OperationCanceledException => "请求超时或操作已取消。",
        System.Text.Json.JsonException => "接口返回格式异常，无法读取有效 JSON。",
        IOException or UnauthorizedAccessException => "数据文件读写失败，请检查程序目录权限或磁盘空间。",
        CryptographicException => "Windows 凭据加密／解密失败。",
        _ => "操作失败（" + ex.GetType().Name + "），请检查配置后重试。"
    };
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(new Action(() => { if (running) { next = DateTimeOffset.UtcNow; log.Write("INFO", "电脑已恢复，准备补查。"); } }));
    }
    private void ReviewClick(object sender, RoutedEventArgs e) => OpenReview();
    private void RowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReviewGrid.SelectedItem is ReviewItem item) OpenReview(item.Id);
    }
    private void OpenReview(long? id = null)
    {
        try { Process.Start(new ProcessStartInfo(new Uri(settings.BaseUri, id.HasValue ? $"review/{id}" : "review?status=pending").AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Error(ex); }
    }
    private void ShowLogs()
    {
        RestoreWindow(); LogTab.IsSelected = true;
        if (AutoScrollLogs.IsChecked == true) LogTextBox.ScrollToEnd();
    }
    private void AppendLog(string line)
    {
        if (LogTextBox.LineCount >= 2000)
            LogTextBox.Text = string.Join(Environment.NewLine, log.Lines) + Environment.NewLine;
        else
            LogTextBox.AppendText(line + Environment.NewLine);
        if (AutoScrollLogs.IsChecked == true) LogTextBox.ScrollToEnd();
    }
    private void CopyLogsClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogTextBox.Text); }
        catch { Report("ERROR", "剪贴板暂时不可用，请重试。"); }
    }
    private const int ReviewPageSize = 10;
    private void RefreshReviewPage()
    {
        if (ReviewGrid == null || ReviewPageText == null) return;
        int size = ReviewPageSize;
        int pages = Math.Max(1, (reviewItems.Count + size - 1) / size);
        reviewPage = Math.Clamp(reviewPage, 0, pages - 1);
        ReviewGrid.ItemsSource = reviewItems.Skip(reviewPage * size).Take(size).ToList();
        ReviewPageText.Text = $"第 {reviewPage + 1} / {pages} 页 · 共 {reviewItems.Count} 项 · 每页 {size} 项";
        ReviewFirst.IsEnabled = ReviewPrev.IsEnabled = reviewPage > 0; ReviewLast.IsEnabled = ReviewNext.IsEnabled = reviewPage < pages - 1;
    }
    private void ReviewPageClick(object sender, RoutedEventArgs e)
    {
        string action = (string)((Button)sender).Tag;
        reviewPage = action switch
        {
            "first" => 0,
            "last" => Math.Max(0, (reviewItems.Count - 1) / ReviewPageSize),
            _ => reviewPage + int.Parse(action)
        };
        RefreshReviewPage();
    }
    private void RefreshStatsPage()
    {
        if (StatsGrid == null || DistributionPanel == null) return;
        var pending = stats.Records.Where(r => r.CompletedAt == null).OrderByDescending(r => r.FirstSeen).ToList();
        var completed = stats.Records.Where(r => r.CompletedAt != null).OrderByDescending(r => r.CompletedAt).ToList();
        StatsTotalText.Text = stats.Records.Count.ToString();
        StatsCompletedText.Text = completed.Count.ToString();
        StatsPendingText.Text = pending.Count.ToString();
        var durations = completed.Select(r => r.Duration).Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
        StatsAvgText.Text = durations.Count > 0 ? ReviewStatsRecord.FormatDuration(TimeSpan.FromTicks((long)durations.Average(d => d.Ticks))) : "—";
        StatsMedianText.Text = durations.Count > 0 ? ReviewStatsRecord.FormatDuration(durations.Count % 2 == 0 ? TimeSpan.FromTicks((durations[durations.Count / 2 - 1].Ticks + durations[durations.Count / 2].Ticks) / 2) : durations[durations.Count / 2]) : "—";
        DateTimeOffset now = DateTimeOffset.Now;
        StatsLongestText.Text = pending.Count > 0 ? ReviewStatsRecord.FormatDuration(now - pending.Min(r => r.PostCreatedAt ?? r.ReviewQueuedAt ?? r.FirstSeen)) : "—";
        RenderDistribution(durations);
        StatsGrid.ItemsSource = pending.Concat(completed).ToList();
    }
    private void RenderDistribution(List<TimeSpan> durations)
    {
        DistributionPanel.Children.Clear();
        if (durations.Count == 0)
        {
            DistributionPanel.Children.Add(new TextBlock { Text = "暂无已完成记录，审核完成后将在此显示分布。", Foreground = Brushes.SlateGray });
            return;
        }
        int[] counts =
        {
            durations.Count(d => d.TotalHours < 1),
            durations.Count(d => d.TotalHours >= 1 && d.TotalHours < 4),
            durations.Count(d => d.TotalHours >= 4 && d.TotalHours < 24),
            durations.Count(d => d.TotalHours >= 24 && d.TotalHours < 72),
            durations.Count(d => d.TotalHours >= 72)
        };
        string[] labels = { "< 1 小时", "1–4 小时", "4–24 小时", "1–3 天", "≥ 3 天" };
        int max = counts.Max();
        for (int i = 0; i < labels.Length; i++)
        {
            var row = new Grid { Margin = new Thickness(0, i > 0 ? 4 : 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.SlateGray });
            var track = new Grid { Margin = new Thickness(8,0,12,0) }; Grid.SetColumn(track, 1);
            track.Children.Add(new Border { Background = (Brush)new BrushConverter().ConvertFromString("#E8EDF5")!, Height = 14, CornerRadius = new CornerRadius(2) });
            if (counts[i] > 0)
                track.Children.Add(new Border
                {
                    Background = (Brush)new BrushConverter().ConvertFromString("#2563EB")!,
                    Height = 14, Width = Math.Max(3, 360.0 * counts[i] / max),
                    CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left
                });
            row.Children.Add(track);
            row.Children.Add(new TextBlock
            {
                Text = $"{counts[i]}（{counts[i] * 100.0 / durations.Count:0.#}%）",
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(row.Children[^1], 2);
            DistributionPanel.Children.Add(row);
        }
    }
    private void ContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReviewGrid != null && ReviewGrid.Columns.Count > 2 && ReviewGrid.ActualWidth > 0)
            ReviewGrid.Columns[2].Width = Math.Max(100, ReviewGrid.ActualWidth - 434);
        if (ReviewGrid != null && ReviewGrid.ActualHeight > 30)
            ReviewGrid.RowHeight = Math.Clamp(Math.Floor((ReviewGrid.ActualHeight - 30) / ReviewPageSize), 18, 28);
        RefreshReviewPage();
    }
    private void LogFollowChanged(object sender, RoutedEventArgs e)
    {
        if (LogTextBox != null && AutoScrollLogs.IsChecked == true) LogTextBox.ScrollToEnd();
    }
    private void ReviewRowLoaded(object sender, DataGridRowEventArgs e)
    {
        var copy = new MenuItem { Header = "复制标题", Tag = e.Row.Item };
        copy.Click += CopyReviewTitleClick;
        e.Row.ContextMenu = new ContextMenu();
        e.Row.ContextMenu.Items.Add(copy);
    }
    private void CopyReviewTitleClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ReviewItem item })
        {
            try { Clipboard.SetText(item.Title); }
            catch { Report("ERROR", "剪贴板暂时不可用，请重试。"); }
        }
    }
    private void ExportLogsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"openEuler-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip", Filter = "日志压缩包|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
            try { System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(Storage.DirectoryPath, "logs"), temporary); File.Copy(temporary, dialog.FileName, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            MessageBox.Show(this, "日志已导出（保留最近 30 天）。");
        }
        catch { MessageBox.Show(this, "导出失败，请检查目标目录权限。"); }
    }
    private void ExportStatsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"openEuler-审核统计-{DateTime.Now:yyyyMMdd-HHmmss}.csv", Filter = "CSV 文件|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
            var rows = stats.Records.OrderBy(r => r.CompletedAt != null).ThenByDescending(r => r.EffectiveCompletedAt ?? r.FirstSeen);
            var csv = new StringBuilder("ID,类型,标题,作者,发帖时间,进入审核列表时间,检测到时间,处理完成时间,完成时间来源,耗时\r\n");
            foreach (var r in rows)
                csv.AppendJoin(',', Csv(r.Id.ToString()), Csv(r.Type), Csv(r.Title), Csv(r.Author), Csv(r.PostCreatedText), Csv(r.QueuedText), Csv(r.DetectedText), Csv(r.CompletedText), Csv(r.CompletionSourceText), Csv(r.DurationText)).Append("\r\n");
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            MessageBox.Show(this, $"已导出 {stats.Records.Count} 条统计记录。", "导出统计");
        }
        catch { MessageBox.Show(this, "导出失败，请检查目标目录权限。", "导出统计"); }
    }
    public void RestoreWindow() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting && CloseTrayBox.IsChecked == true) { e.Cancel = true; Hide(); return; }
        if (!exiting) { e.Cancel = true; ExitApp(); }
    }
    private void ExitApp()
    {
        if (exiting) return;
        exiting = true; running = false; operation?.Cancel(); timer.Stop();
        SystemEvents.PowerModeChanged -= PowerChanged;
        log.Write("INFO", "程序退出。");
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); forum.Dispose(); robots.Dispose();
        Application.Current.Shutdown();
    }
}

