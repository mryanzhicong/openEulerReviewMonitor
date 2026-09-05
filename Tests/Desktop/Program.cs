using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ForumReviewMonitor;

internal static class Program
{
    private static int passed;
    [STAThread]
    public static void Main()
    {
        var app = new App(); app.InitializeComponent();
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { true }, null)!;
        try
        {
            T Get<T>(string name) where T : class => (T)window.FindName(name);
            void Call(string method, object sender) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { sender, new RoutedEventArgs() });
            var interval = Get<TextBox>("IntervalBox");
            var repeat = Get<TextBox>("RepeatBox");
            void Step(string tag) => Call("AdjustTimeClick", new RepeatButton { Tag = tag });
            interval.Text = "15"; Step("Interval,+"); Check(interval.Text == "16", "mouse increment");
            Step("Interval,-"); Check(interval.Text == "15", "mouse decrement");
            interval.Text = "1"; Step("Interval,-"); Check(interval.Text == "1", "interval minimum");
            interval.Text = "1440"; Step("Interval,+"); Check(interval.Text == "1440", "interval maximum");
            repeat.Text = "0"; Step("Repeat,-"); Check(repeat.Text == "0", "repeat minimum");
            repeat.Text = "10080"; Step("Repeat,+"); Check(repeat.Text == "10080", "repeat maximum");
            interval.Text = "invalid"; Step("Interval,+"); Check(interval.Text == "16", "invalid typed value repaired");
            interval.Text = "15"; repeat.Text = "0";
            var channels = Get<ComboBox>("ChannelBox");
            channels.SelectedIndex = 1;
            Check(Get<Border>("WeComConfig").Visibility == Visibility.Visible && Get<Border>("DingConfig").Visibility == Visibility.Collapsed && Get<Border>("WindowsConfig").Visibility == Visibility.Collapsed, "only selected channel displayed");
            Get<PasswordBox>("WeComHook").Password = "draft-value";
            Get<CheckBox>("WeComEnabled").IsChecked = true;
            channels.SelectedIndex = 2; channels.SelectedIndex = 1;
            Check(Get<PasswordBox>("WeComHook").Password == "draft-value" && Get<CheckBox>("WeComEnabled").IsChecked == true, "channel switching preserves draft and enabled state");
            Get<PasswordBox>("WeComHook").Password = "";
            Call("TestClick", Get<Button>("TestButton"));
            Check(Get<TextBlock>("PushResultTitle").Text == "测试失败", "current channel failure is inline without dialog");
            channels.SelectedIndex = 2;
            Check(Get<TextBlock>("PushResultTitle").Text == "尚未测试", "channel results are independent");
            channels.SelectedIndex = 1;
            Check(Get<TextBlock>("PushResultTitle").Text == "测试失败", "channel result retained");
            channels.SelectedIndex = 0;
            Check(Get<Button>("SaveChannelButton").Visibility == Visibility.Collapsed && Get<Button>("TestButton").Content.ToString()!.Contains("Windows"), "Windows always-on configuration");
            Get<PasswordBox>("CookieBox").Password = "";
            Call("ValidateClick", Get<Button>("ValidateButton"));
            Check(Get<TextBlock>("AuthResultTitle").Text == "验证失败", "authentication failure is inline without dialog");
            Check(app.Windows.Count == 1, "no result dialog or extra window");
            string output = Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(output);
            // Render the actual WPF visual tree without opening a desktop window or sending notifications.
            var content = (FrameworkElement)window.Content;
            var tabs = Find<TabControl>(content)!;
            Get<Button>("StartButton").IsEnabled = false;
            content.Measure(new Size(1100, 620)); content.Arrange(new Rect(0, 0, 1100, 620)); content.UpdateLayout();
            var startSurface = (Border)Get<Button>("StartButton").Template.FindName("Surface", Get<Button>("StartButton"));
            Check(System.Windows.Documents.TextElement.GetForeground(startSurface).ToString() == "#FF58677A", "disabled primary button retains dark readable text");
            Check(Get<TextBlock>("EnabledChannelsText").Text.Contains("Windows") && !Get<TextBlock>("EnabledChannelsText").Text.Contains("企业微信"), "enabled summary reflects saved configuration rather than unsaved switches");
            Get<Button>("StartButton").IsEnabled = true;
            Check(Grid.GetRow(Get<StackPanel>("ActionBar")) == 1, "wide layout shares navigation row");
            var runningState = typeof(MainWindow).GetField("running", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var resultState = typeof(MainWindow).GetField("resultStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var updateState = typeof(MainWindow).GetMethod("UpdateStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
            resultState.SetValue(window, "成功"); updateState.Invoke(window, null);
            Check(Get<TextBlock>("CheckStateText").Foreground.ToString() == "#FF228B22", "successful check status is fully green");
            resultState.SetValue(window, "失败"); updateState.Invoke(window, null);
            Check(Get<TextBlock>("CheckStateText").Foreground.ToString() == "#FFB22222", "failed check status remains fully red");
            resultState.SetValue(window, "");
            runningState.SetValue(window, true); updateState.Invoke(window, null);
            Check(Get<Button>("StartButton").Content.ToString() == "停止监控", "combined button shows stop while running");
            Call("ToggleMonitorClick", Get<Button>("StartButton"));
            Check(!(bool)runningState.GetValue(window)! && Get<Button>("StartButton").Content.ToString() == "启动监控", "combined button stops monitoring and returns to start");
            var actionButtons = Get<StackPanel>("ActionBar").Children.Cast<Button>().ToArray();
            Check(actionButtons.All(button => Math.Abs(button.ActualWidth - 140) < 0.1), "main action buttons have equal widths");
            double ActionGap(int i) => actionButtons[i + 1].TranslatePoint(new Point(), content).X - actionButtons[i].TranslatePoint(new Point(actionButtons[i].ActualWidth, 0), content).X;
            Check(Math.Abs(ActionGap(0) - 8) < 0.1 && Math.Abs(ActionGap(1) - 8) < 0.1, "main action buttons have equal gaps");
            var firstTab = (TabItem)tabs.Items[0];
            tabs.SelectedIndex = 0; content.UpdateLayout();
            double firstX = firstTab.TranslatePoint(new Point(), content).X;
            tabs.SelectedIndex = 1; content.UpdateLayout();
            Check(Math.Abs(firstX - firstTab.TranslatePoint(new Point(), content).X) < 0.1, "tab strip left edge remains fixed after selection change");
            Check(!Get<Button>("ReviewFirst").IsEnabled && !Get<Button>("ReviewLast").IsEnabled, "empty list disables first and last buttons");
            var fixtureItems = Enumerable.Range(1, 25).Select(i => new ReviewItem(i, "待审主题", "分页测试标题 " + i, "测试作者", DateTimeOffset.Now)).ToList();
            typeof(MainWindow).GetField("reviewItems", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, fixtureItems);
            tabs.SelectedIndex = 0; content.UpdateLayout();
            typeof(MainWindow).GetMethod("RefreshReviewPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var ids = new HashSet<long>();
            for (int i = 0; i < 30; i++)
            {
                Check(Get<DataGrid>("ReviewGrid").Items.Count == (i < 2 ? 10 : 5), "fixed page size: 10, 10, 5 for 25 items");
                foreach (ReviewItem row in Get<DataGrid>("ReviewGrid").Items) Check(ids.Add(row.Id), "review item appears once across pages");
                if (!Get<Button>("ReviewNext").IsEnabled) break;
                Call("ReviewPageClick", new Button { Tag = "1" });
            }
            Check(ids.Count == 25, "pagination covers full snapshot");
            Call("ReviewPageClick", new Button { Tag = "-2" });
            content.Measure(new Size(1040, 620)); content.Arrange(new Rect(0, 0, 1040, 620)); content.UpdateLayout();
            var reviewGrid = Get<DataGrid>("ReviewGrid");
            Check(reviewGrid.Items.Count == 10, "resizing keeps ten items per page");
            Check(reviewGrid.RowHeight * 10 + reviewGrid.ColumnHeaderHeight <= reviewGrid.ActualHeight, "ten rows fit minimum window without scrolling");
            Call("ReviewPageClick", Get<Button>("ReviewLast"));
            Check(((ReviewItem)reviewGrid.Items[0]).Id == 21 && reviewGrid.Items.Count == 5 && !Get<Button>("ReviewLast").IsEnabled, "last button jumps to final partial page");
            Call("ReviewPageClick", Get<Button>("ReviewFirst"));
            Check(((ReviewItem)reviewGrid.Items[0]).Id == 1 && reviewGrid.Items.Count == 10 && !Get<Button>("ReviewFirst").IsEnabled, "first button returns to first page");
            var testLog = (AppLog)typeof(MainWindow).GetField("log", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            testLog.Write("INFO", "fixture-search info"); testLog.Write("ERROR", "fixture-search error");
            var logBox = Get<TextBox>("LogTextBox");
            Check(logBox.Text.Contains("fixture-search info") && logBox.Text.Contains("fixture-search error"), "scrollable log retains all levels");
            Get<CheckBox>("AutoScrollLogs").IsChecked = false;
            testLog.Write("INFO", "fixture-follow-disabled");
            Check(logBox.Text.Contains("fixture-follow-disabled"), "log continues recording with auto scroll disabled");
            Get<CheckBox>("AutoScrollLogs").IsChecked = true;
            Get<ComboBox>("AuthBox").SelectedIndex = 1;
            channels.SelectedIndex = 2;
            foreach (int index in new[] { 0, 1, 2 })
            {
                tabs.SelectedIndex = index;
                content.Measure(new Size(1100, 620)); content.Arrange(new Rect(0, 0, 1100, 620)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1100, 620, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"tab-{index}.png")); encoder.Save(stream);
            }
            tabs.SelectedIndex = 1;
            Check(tabs.Items.Count == 3, "three main tabs");
            double[]? stablePositions = null;
            double[] Positions() => new[] {
                Get<Button>("TestButton").TranslatePoint(new Point(), content).Y,
                Get<Border>("PushResultPanel").TranslatePoint(new Point(), content).Y,
                Get<Button>("ValidateButton").TranslatePoint(new Point(), content).Y,
                Get<Grid>("MonitorSettingsPanel").TranslatePoint(new Point(), content).Y,
                Get<Grid>("SettingsLayout").ActualHeight
            };
            foreach (int authMode in new[] { 0, 1 })
            foreach (int channel in new[] { 0, 1, 2, 3 })
            {
                Get<ComboBox>("AuthBox").SelectedIndex = authMode;
                channels.SelectedIndex = channel;
                content.Measure(new Size(1040, 620)); content.Arrange(new Rect(0, 0, 1040, 620)); content.UpdateLayout();
                Check(Grid.GetRow(Get<StackPanel>("ActionBar")) == 0, "narrow layout moves actions to separate row");
                var layout = Get<Grid>("SettingsLayout");
                double bottom = layout.TranslatePoint(new Point(0, layout.ActualHeight), content).Y;
                Check(bottom <= Get<TextBlock>("StatusText").TranslatePoint(new Point(), content).Y - 8, "all settings fit above status bar");
                var card = Get<Border>("PushChannelCard");
                Check(Math.Abs(card.ActualHeight - Get<Grid>("LeftSettingsColumn").ActualHeight) < 0.1 && Math.Abs(card.TranslatePoint(new Point(), layout).Y) < 0.1, "push card fills settings height from the top");
                var monitorCard = Get<Border>("MonitorCard");
                var resultCard = Get<Border>("PushResultPanel");
                Check(Math.Abs(monitorCard.TranslatePoint(new Point(0, monitorCard.ActualHeight), content).Y - 8 - resultCard.TranslatePoint(new Point(0, resultCard.ActualHeight), content).Y) < 1.1, $"test result keeps bottom inset: monitor={monitorCard.TranslatePoint(new Point(0, monitorCard.ActualHeight), content).Y}, result={resultCard.TranslatePoint(new Point(0, resultCard.ActualHeight), content).Y}");
                var testButton = Get<Button>("TestButton");
                var label = new TextBlock { Text = testButton.Content.ToString(), FontFamily = testButton.FontFamily, FontSize = testButton.FontSize };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Check(label.DesiredSize.Width + testButton.Padding.Left + testButton.Padding.Right + 2 <= testButton.ActualWidth, "full test button label fits for every channel");
                var positions = Positions();
                stablePositions ??= positions;
                Check(positions.Zip(stablePositions).All(pair => Math.Abs(pair.First - pair.Second) < 0.1), "channel and authentication switching preserves button and card positions");
                var selectedConfig = Get<Border>(new[] { "WindowsConfig", "WeComConfig", "DingConfig", "FeishuConfig" }[channel]);
                Check(selectedConfig.DesiredSize.Height <= Get<Grid>("ChannelConfigSlot").ActualHeight, "channel fields fit reserved area");
            }
            Get<TextBlock>("PushResultText").Text = string.Concat(Enumerable.Repeat("这是一条很长的失败提示，用于检查结果区布局。", 20));
            Get<TextBlock>("AuthResultText").Text = Get<TextBlock>("PushResultText").Text;
            content.UpdateLayout();
            Check(Positions().Zip(stablePositions!).All(pair => Math.Abs(pair.First - pair.Second) < 0.1), "long results do not move buttons or card edges");
            var footer = Get<Border>("CloseBehaviorPanel");
            var pushCard = Get<Border>("PushChannelCard");
            Check(Math.Abs(footer.ActualWidth - Get<Border>("AboutPanel").ActualWidth) < 0.1 && Math.Abs(footer.ActualHeight - Get<Border>("AboutPanel").ActualHeight) < 0.1 && footer.TranslatePoint(new Point(), content).Y >= pushCard.TranslatePoint(new Point(0, pushCard.ActualHeight), content).Y + 9, "other configuration and about share equal columns below cards");
            var configFile = Path.Combine(Storage.DirectoryPath, "settings.dat");
            byte[]? savedBytes = File.Exists(configFile) ? File.ReadAllBytes(configFile) : null;
            var originalSettings = (Settings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            bool originalClose = originalSettings.CloseToTray;
            try
            {
                interval.Text = "23"; repeat.Text = "7";
                Check(Storage.LoadSettings().IntervalMinutes == 23 && Storage.LoadSettings().RepeatMinutes == 7, "monitor times persist without a save button");
                interval.Text = "0";
                Check(originalSettings.IntervalMinutes == 23 && Storage.LoadSettings().IntervalMinutes == 23, "invalid interval does not replace active value");
                var runningField = typeof(MainWindow).GetField("running", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var nextField = typeof(MainWindow).GetField("next", BindingFlags.NonPublic | BindingFlags.Instance)!;
                runningField.SetValue(window, true); nextField.SetValue(window, DateTimeOffset.UtcNow.AddMinutes(1));
                interval.Text = "24";
                Check(((DateTimeOffset)nextField.GetValue(window)! - DateTimeOffset.UtcNow).TotalMinutes > 23.9, "interval change updates running countdown");
                Check(Get<Grid>("MonitorSettingsPanel").IsEnabled, "monitor times editable while running");
                runningField.SetValue(window, false);
                interval.Text = "invalid-unsaved-value";
                var closeBox = Get<CheckBox>("CloseTrayBox");
                foreach (bool enabled in new[] { false, true })
                {
                    closeBox.IsChecked = enabled;
                    Check(originalSettings.CloseToTray == enabled && Storage.LoadSettings().CloseToTray == enabled, "close behavior applies and persists immediately");
                    Check(Storage.LoadSettings().IntervalMinutes == originalSettings.IntervalMinutes, "auto save ignores unrelated unsaved inputs");
                }
                Get<Grid>("MonitorSettingsPanel").IsEnabled = false;
                Check(closeBox.IsEnabled, "close behavior remains editable while monitor settings are disabled");
            }
            finally
            {
                originalSettings.CloseToTray = originalClose;
                Get<CheckBox>("CloseTrayBox").IsChecked = originalClose;
                if (savedBytes != null) File.WriteAllBytes(configFile, savedBytes);
                else if (File.Exists(configFile)) File.Delete(configFile);
            }
            Console.WriteLine($"All {passed} desktop assertions passed. Renders: {output}");
        }
        finally { typeof(MainWindow).GetMethod("ExitApp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); }
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is T child) return child;
        return null;
    }
}

