using System.IO;
using System.Windows;
using DianXu.Core;
using DianXu.Services;

namespace DianXu;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--diagnostics")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string directory = e.Args.Length > 1 ? Path.GetFullPath(e.Args[1]) : Path.Combine(AppContext.BaseDirectory, "diagnostics");
            try { Directory.CreateDirectory(directory); await DesktopDiagnostics.RunAsync(directory); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "diagnostic-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        try
        {
            _mutex = new Mutex(false, @"Local\DianXu.ScreenClicker");
            try { _ownsMutex = _mutex.WaitOne(0); } catch (AbandonedMutexException) { _ownsMutex = true; }
            if (!_ownsMutex) { MessageBox.Show("点序已经在运行。请在任务栏打开已有窗口，或使用已设置的快捷键停止。", "点序"); Shutdown(); return; }
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DianXu");
            int settingArgument = Array.IndexOf(e.Args, "--settings-dir");
            if (settingArgument >= 0 && settingArgument + 1 < e.Args.Length) directory = Path.GetFullPath(e.Args[settingArgument + 1]);
            var store = new SettingsStore(directory);
            MainWindow = new MainWindow(store, store.Load()); MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败：" + ex.Message, "点序", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    { if (_ownsMutex) _mutex?.ReleaseMutex(); _mutex?.Dispose(); base.OnExit(e); }
}
