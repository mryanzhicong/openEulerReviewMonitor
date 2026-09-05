using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ForumReviewMonitor;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activation;
    private DispatcherTimer? activationTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            mutex = new Mutex(true, @"Global\openEulerReviewMonitor.v1", out bool first);
            if (!first)
            {
                try { using var signal = EventWaitHandle.OpenExisting(@"Global\openEulerReviewMonitor.Activate.v1"); signal.Set(); } catch { }
                Shutdown();
                return;
            }
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\openEulerReviewMonitor.Activate.v1");
            var window = new MainWindow();
            MainWindow = window;
            activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            activationTimer.Tick += (_, _) => { if (activation.WaitOne(0)) window.RestoreWindow(); };
            activationTimer.Start();
            window.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("无法启动。请确认程序目录可写，且其他 Windows 会话未运行本程序。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        activationTimer?.Stop();
        activation?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}

