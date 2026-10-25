using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DianXu.Core;

namespace DianXu.Services;

internal static class DesktopDiagnostics
{
    internal static async Task RunAsync(string directory)
    {
        var log = new List<string>();
        void Check(bool condition, string label)
        {
            log.Add((condition ? "PASS " : "FAIL ") + label);
            File.WriteAllLines(Path.Combine(directory, "desktop-tests.txt"), log);
            if (!condition) throw new InvalidOperationException(label);
        }
        var previewSettings = new AppSettings
        {
            Points = [new(520, 360, ClickButton.Left, 1, WaitUnit.Seconds), new(850, 460, ClickButton.Right, 3, WaitUnit.Seconds), new(1120, 620, ClickButton.Left, 500, WaitUnit.Milliseconds)],
            Limited = true, ClickLimit = 100
        };
        var preview = new MainWindow(new SettingsStore(Path.Combine(directory, "preview-settings")), new SettingsLoadResult(previewSettings, null), false);
        RenderWindowContent(preview, Path.Combine(directory, "main-window.png"), 1000, 750);
        preview.Close();
        var empty = new MainWindow(new SettingsStore(Path.Combine(directory, "empty-settings")), new SettingsLoadResult(new AppSettings(), null), false);
        RenderWindowContent(empty, Path.Combine(directory, "empty-window.png"), 1000, 750); empty.Close();
        var editor = new PointEditorWindow(previewSettings.Points[0], true);
        RenderWindowContent(editor, Path.Combine(directory, "point-editor.png"), 470, 510); editor.Close();
        Check(true, "Main, empty-state and point-editor previews rendered");

        Native.GetCursorPos(out var originalCursor);
        var instructions = new TextBlock { Text = "安全输入测试\n仅点击本测试窗口，完成后自动关闭。", FontSize = 17, Margin = new Thickness(22), Foreground = Brushes.SlateGray };
        var target = new Window
        {
            Title = "点序 · 安全测试", Width = 550, Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, Topmost = true, Background = Brushes.White,
            Content = instructions
        };
        var hidden = new Window { Width = 1, Height = 1, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        GlobalMouseHook? mouse = null; GlobalHotkeys? hotkeys = null;
        nint blockingWindow = 0;
        ClickEngine? activeEngine = null;
        try
        {
            target.Show(); target.Activate();
            await target.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(180);
            nint hwnd = new WindowInteropHelper(target).Handle;
            var messages = new List<int>();
            HwndSource source = HwndSource.FromHwnd(hwnd)!;
            source.AddHook((nint window, int msg, nint wp, nint lp, ref bool handled) =>
            { if (msg is Native.WmLeftDown or Native.WmRightDown) messages.Add(msg); return 0; });
            Point screenA = target.PointToScreen(new Point(110, 170));
            Point screenB = target.PointToScreen(new Point(275, 170));
            Point screenC = target.PointToScreen(new Point(440, 170));
            ClickPoint Make(Point p, ClickButton button, decimal wait) => new((int)p.X, (int)p.Y, button, wait, WaitUnit.Milliseconds);
            ClickPoint a = Make(screenA, ClickButton.Left, 35), b = Make(screenB, ClickButton.Right, 45), c = Make(screenC, ClickButton.Left, 25);
            var guarded = new GuardedOutput(hwnd);
            activeEngine = new ClickEngine(guarded);
            int physicalStops = 0;
            var injectedClicks = new List<(ClickButton Button, int X, int Y)>();
            mouse = new GlobalMouseHook(target.Dispatcher);
            mouse.InjectedClickObserved += (button, x, y) => injectedClicks.Add((button, x, y));
            mouse.ManualDoubleClick += () => { physicalStops++; activeEngine.Stop(); };
            mouse.SetRunning(true);
            var result = await activeEngine.RunAsync(new ClickPlan([a, b, c], 10)).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(80);
            Check(result.Completed && result.Count == 10, "Real SendInput runs exactly 10 clicks");
            Check(messages.SequenceEqual(new[] { Native.WmLeftDown, Native.WmRightDown, Native.WmLeftDown, Native.WmLeftDown, Native.WmRightDown, Native.WmLeftDown, Native.WmLeftDown, Native.WmRightDown, Native.WmLeftDown, Native.WmLeftDown }), "Target receives expected left/right order without extra clicks");
            Check(injectedClicks.Count(click => click.Button == ClickButton.Left) == 7 && physicalStops == 0, "Low-level hook identifies generated clicks; no false double-click stop");
            var expectedPixels = Enumerable.Range(0, 10).Select(index => new[] { a, b, c }[index % 3]).Select(point => (point.Button, point.X, point.Y));
            // Inspect the actual injected down-event coordinates, not the cursor later: a real user may move it after the click.
            Check(injectedClicks.SequenceEqual(expectedPixels), "Every injected click reaches its exact requested physical pixel: " + string.Join("; ", injectedClicks));
            var signal = new FirstProgress();
            var task = activeEngine.RunAsync(new ClickPlan([a with { WaitValue = 30000 }], null), signal);
            await signal.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew(); activeEngine.Stop(); result = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(result.Count == 1 && !result.Completed && watch.ElapsedMilliseconds < 1000, "Real click loop cancels a 30-second wait promptly");

            nint hotkeyWindow = new WindowInteropHelper(hidden).EnsureHandle();
            hotkeys = new GlobalHotkeys(hotkeyWindow);
            Check(hotkeys.TryConfigure(0x75, 0x77, out string error), "Default F6/F8 can be registered: " + error);
            var toggled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var emergency = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hotkeys.Toggle += () => toggled.TrySetResult();
            hotkeys.Emergency += () => { activeEngine.Stop(); emergency.TrySetResult(); };
            Native.SetForegroundWindow(hwnd);
            SendKey(0x75);
            await toggled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(true, "F6 delivered globally while a different window has focus");
            signal = new FirstProgress(); task = activeEngine.RunAsync(new ClickPlan([a with { WaitValue = 30000 }], null), signal);
            await signal.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SendKey(0x77); await emergency.Task.WaitAsync(TimeSpan.FromSeconds(3));
            result = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(result.Count == 1 && !result.Completed, "Global F8 stops real input loop from target window");
            blockingWindow = hwnd;
            bool blocked = Native.RegisterHotKey(blockingWindow, 0x5201, 0x4000, 0x76);
            if (blocked)
            {
                Check(!hotkeys.TryConfigure(0x76, 0x77, out _) && hotkeys.Ready, "Conflicting replacement is rejected and old shortcuts restored");
                Native.UnregisterHotKey(blockingWindow, 0x5201); blockingWindow = 0;
            }
            else log.Add("SKIP F7 already occupied; deliberate shortcut conflict test not available");
            Check(physicalStops == 0, "Injected input throughout diagnostics never triggers manual-double-click stop");
            hotkeys.Dispose(); hotkeys = null; mouse.SetRunning(false);
            await VerifyMainWindowAsync(directory, guarded, target, messages, a, b, c, Check);
            File.WriteAllText(Path.Combine(directory, "environment.json"), JsonSerializer.Serialize(new
            {
                Framework = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                VirtualScreen = new { Native.VirtualScreen.Left, Native.VirtualScreen.Top, Native.VirtualScreen.Right, Native.VirtualScreen.Bottom },
                RealPhysicalDoubleClick = "Requires user mouse hardware verification; physical-event detector covered by unit tests."
            }, new JsonSerializerOptions { WriteIndented = true }));
            log.Add("MANUAL CHECK: real physical double-click; alternate display/scaling configurations; elevated target windows.");
            File.WriteAllLines(Path.Combine(directory, "desktop-tests.txt"), log);
        }
        finally
        {
            activeEngine?.Stop();
            if (blockingWindow != 0) Native.UnregisterHotKey(blockingWindow, 0x5201);
            hotkeys?.Dispose(); mouse?.Dispose(); hidden.Close(); target.Close();
            Native.SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }
    private static async Task VerifyMainWindowAsync(string directory, IClickOutput output, Window target, List<int> messages,
        ClickPoint a, ClickPoint b, ClickPoint c, Action<bool, string> check)
    {
        var windows = new List<MainWindow>();
        T Element<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
        void Press(MainWindow window, string name) => Element<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task WaitUntilAsync(Func<bool> condition, string label)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(label);
                await Task.Delay(15);
            }
        }
        MainWindow Create(SettingsStore store, AppSettings settings)
        {
            var window = new MainWindow(store, new SettingsLoadResult(settings, null), true, output);
            windows.Add(window); window.Show(); return window;
        }
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "ui-settings"));
            var settings = new AppSettings { Points = [a, b, c], Limited = true, ClickLimit = 10, MinimizeOnStart = true };
            var main = Create(store, settings);
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var grid = Element<DataGrid>(main, "PointGrid");
            var count = Element<TextBlock>(main, "ExecutedCountText");
            var emergency = Element<Button>(main, "EmergencyButton");
            check(count.Text == "0" && !emergency.IsEnabled, "Main window restores points without automatically clicking");
            grid.SelectedIndex = 0; Press(main, "DownButton");
            check(((PointRow)grid.Items[1]).Value == a && store.Load().Settings.Points[1] == a, "Reordering points updates the screen and saved settings");
            Press(main, "UpButton");
            check(((PointRow)grid.Items[0]).Value == a, "Moving a point back restores execution order");
            int before = messages.Count;
            Press(main, "StartButton");
            check(main.WindowState == WindowState.Minimized && !Element<FrameworkElement>(main, "PointToolbar").IsEnabled,
                "Starting through the real UI minimizes the window and locks editing");
            await WaitUntilAsync(() => !emergency.IsEnabled, "UI limited run did not finish");
            await Task.Delay(60);
            check(count.Text == "10" && main.WindowState == WindowState.Normal && Element<TextBlock>(main, "CurrentPointText").Text.Contains("指定"),
                "Exact-count completion updates statistics and restores the main window");
            check(messages.Skip(before).Count() == 10, "Main-window execution sends exactly ten actual clicks");

            Element<RadioButton>(main, "UnlimitedRadio").IsChecked = true;
            before = messages.Count; target.Activate(); SendKey(0x75);
            await WaitUntilAsync(() => emergency.IsEnabled && messages.Count > before, "Global UI start did not click");
            var stopWatch = Stopwatch.StartNew(); SendKey(0x77);
            await WaitUntilAsync(() => !emergency.IsEnabled, "Global UI emergency stop did not finish");
            check(stopWatch.ElapsedMilliseconds < 1000 && Element<TextBlock>(main, "CurrentPointText").Text == "紧急停止" && main.WindowState == WindowState.Normal,
                "Global F6 starts and F8 stops the full application while another window is focused");

            Press(main, "AddButton");
            await WaitUntilAsync(() => Application.Current.Windows.OfType<SelectionOverlay>().Any(window => window.IsActive), "Point picker did not become active");
            SendKey(0x1B);
            await WaitUntilAsync(() => main.IsVisible && !Application.Current.Windows.OfType<SelectionOverlay>().Any(), "Escape did not cancel point picking");
            check(grid.Items.Count == 3, "Escape cancels screen picking without adding a point or leaving an overlay");
            main.Close();
            var saved = store.Load().Settings;
            var restored = Create(store, saved);
            await restored.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(Element<DataGrid>(restored, "PointGrid").Items.Count == 3 && Element<TextBlock>(restored, "ExecutedCountText").Text == "0"
                && !Element<Button>(restored, "EmergencyButton").IsEnabled && Element<RadioButton>(restored, "UnlimitedRadio").IsChecked == true,
                "Closing and reopening restores configuration without auto-start");
            restored.Close();

            var closingSettings = new AppSettings { Points = [a with { WaitValue = 30000 }], Limited = false, MinimizeOnStart = true };
            var closing = Create(new SettingsStore(Path.Combine(directory, "close-settings")), closingSettings);
            await closing.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            before = messages.Count; Press(closing, "StartButton");
            await WaitUntilAsync(() => messages.Count > before, "Close test did not receive its first click");
            bool closed = false; closing.Closed += (_, _) => closed = true;
            stopWatch.Restart(); closing.Close();
            await WaitUntilAsync(() => closed, "Closing a running app did not cancel its wait");
            check(stopWatch.ElapsedMilliseconds < 1000, "Closing during a thirty-second wait cancels clicking and releases the window promptly");
        }
        finally
        {
            foreach (var window in windows) window.Close();
            await Task.Delay(80);
        }
    }
    private static void RenderWindowContent(Window window, string path, int width, int height)
    {
        var content = (UIElement)window.Content; window.Content = null;
        var root = new Border { Background = window.Background, Child = content, Width = width, Height = height };
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
    private static void SendKey(ushort key)
    {
        Native.Input Make(uint flags) => new() { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Vk = key, Flags = flags, ExtraInfo = Native.InputMarker } } };
        if (Native.SendInput(2, [Make(0), Make(2)], Marshal.SizeOf<Native.Input>()) != 2) throw new InvalidOperationException("Test keyboard injection failed.");
    }
    private sealed class FirstProgress : IProgress<ClickProgress>
    { public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public void Report(ClickProgress value) => First.TrySetResult(); }
    private sealed class GuardedOutput(nint expectedWindow) : IClickOutput
    {
        private readonly WindowsClickOutput _output = new();
        public void Click(ClickPoint point)
        {
            nint actual = Native.GetAncestor(Native.WindowFromPoint(new Native.Point(point.X, point.Y)), 2);
            if (actual != expectedWindow || !Native.IsWindow(expectedWindow)) throw new InvalidOperationException("Safety guard: target is not the diagnostic window. No input sent.");
            _output.Click(point);
        }
    }
}
