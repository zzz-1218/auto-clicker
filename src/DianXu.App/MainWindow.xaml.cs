using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DianXu.Core;
using DianXu.Services;

namespace DianXu;

public sealed record PointRow(int Number, ClickPoint Value)
{
    public int X => Value.X;
    public int Y => Value.Y;
    public string ButtonText => Value.Button == ClickButton.Left ? "左键" : "右键";
    public string DelayText => Value.WaitValue.ToString("0.########", CultureInfo.CurrentCulture) + (Value.Unit == WaitUnit.Seconds ? " 秒" : " 毫秒");
}

public partial class MainWindow : Window
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly string? _loadWarning;
    private readonly bool _startServices;
    private readonly ObservableCollection<PointRow> _rows = [];
    private readonly ClickEngine _engine;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private GlobalMouseHook? _mouseHook;
    private GlobalHotkeys? _hotkeys;
    private bool _initializing = true, _servicesInitialized, _busy, _editing, _selecting, _closingRequested, _allowClose;
    private string _stopReason = "手动停止";
    private Task? _uiRunTask;
    private SelectionOverlay? _overlay;
    private TaskCompletionSource<(int X, int Y)?>? _selection;
    private LiveProgress? _liveProgress;

    public MainWindow(SettingsStore store, SettingsLoadResult load, bool startServices = true)
        : this(store, load, startServices, new WindowsClickOutput()) { }

    internal MainWindow(SettingsStore store, SettingsLoadResult load, bool startServices, IClickOutput clickOutput)
    {
        _engine = new ClickEngine(clickOutput);
        InitializeComponent(); _store = store; _settings = load.Settings; _loadWarning = load.Warning; _startServices = startServices;
        Width = Math.Min(Width, Math.Max(640, SystemParameters.WorkArea.Width - 30));
        Height = Math.Min(Height, Math.Max(560, SystemParameters.WorkArea.Height - 30));
        MinWidth = Math.Min(MinWidth, Width); MinHeight = Math.Min(MinHeight, Height);
        PointGrid.ItemsSource = _rows;
        foreach (var p in _settings.Points) _rows.Add(new PointRow(_rows.Count + 1, p));
        LimitedRadio.IsChecked = _settings.Limited; UnlimitedRadio.IsChecked = !_settings.Limited;
        LimitBox.Text = _settings.ClickLimit.ToString(CultureInfo.InvariantCulture); LimitBox.IsEnabled = _settings.Limited;
        MinimizeCheck.IsChecked = _settings.MinimizeOnStart;
        _initializing = false;
        UpdateRowsState(); UpdateHotkeyText();
        _progressTimer.Tick += (_, _) => { if (_liveProgress?.Latest is { } latest) DisplayProgress(latest); };
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_servicesInitialized || !_startServices) return;
        _servicesInitialized = true;
        try
        {
            _mouseHook = new GlobalMouseHook(Dispatcher);
            _mouseHook.ManualDoubleClick += () => RequestStop("手动双击停止");
            _mouseHook.PositionSelected += (x, y) => CompleteSelection((x, y));
            _mouseHook.SelectionCancelled += () => CompleteSelection(null);
            _hotkeys = new GlobalHotkeys(new WindowInteropHelper(this).Handle);
            _hotkeys.Toggle += () => { if (!_editing && !_selecting) ToggleRun(); };
            _hotkeys.Emergency += () => { if (_selecting) CompleteSelection(null); else RequestStop("紧急停止"); };
            if (!_hotkeys.TryConfigure(_settings.ToggleKey, _settings.EmergencyKey, out string error))
                ShowNotice(error + " 请点击“修改快捷键”处理后再开始。", true);
            else if (_loadWarning is not null) ShowNotice(_loadWarning, true);
        }
        catch (Exception ex)
        {
            StartButton.IsEnabled = false; PointToolbar.IsEnabled = false;
            ShowNotice("安全控制初始化失败，点击功能已禁用。" + ex.Message, true);
        }
    }

    private async void AddPointClick(object sender, RoutedEventArgs e) => await EditPointAsync(null);
    private async void EditPointClick(object sender, RoutedEventArgs e)
    { if (PointGrid.SelectedIndex >= 0) await EditPointAsync(PointGrid.SelectedIndex); }
    private async void PointGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(PointGrid, source) is DataGridRow && PointGrid.SelectedIndex >= 0)
            await EditPointAsync(PointGrid.SelectedIndex);
    }

    private async Task EditPointAsync(int? index)
    {
        if (_busy || _editing || _selecting || _mouseHook is null) return;
        _editing = true;
        try
        {
            ClickPoint point;
            if (index is int i) point = _rows[i].Value;
            else
            {
                var position = await PickPositionAsync();
                if (position is null) return;
                point = new ClickPoint(position.Value.X, position.Value.Y, ClickButton.Left, 1m, WaitUnit.Seconds);
            }
            while (!_closingRequested)
            {
                var editor = new PointEditorWindow(point, index is null) { Owner = this };
                if (editor.ShowDialog() == true)
                {
                    int selected = index ?? _rows.Count;
                    if (index is int existing) _rows[existing] = new PointRow(existing + 1, editor.Point);
                    else _rows.Add(new PointRow(_rows.Count + 1, editor.Point));
                    Renumber(selected); SaveSettings(); return;
                }
                if (!editor.RequestedRepick) return;
                point = editor.Point;
                var position = await PickPositionAsync();
                if (position is not null) point = point with { X = position.Value.X, Y = position.Value.Y };
            }
        }
        catch (Exception ex) { ShowNotice("位置设置未完成：" + ex.Message, true); }
        finally { _editing = false; }
    }

    private async Task<(int X, int Y)?> PickPositionAsync()
    {
        _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _selecting = true; EmergencyButton.IsEnabled = true;
        Hide();
        try
        {
            _overlay = new SelectionOverlay();
            _overlay.CancelRequested += () => CompleteSelection(null);
            _overlay.Closed += (_, _) => _selection?.TrySetResult(null);
            _overlay.Show(); _mouseHook!.BeginSelection();
            return await _selection.Task;
        }
        finally
        {
            _mouseHook?.EndSelection(); _overlay?.Close(); _overlay = null; _selection = null;
            _selecting = false; EmergencyButton.IsEnabled = _busy;
            if (!_closingRequested) { Show(); Activate(); }
        }
    }
    private void CompleteSelection((int X, int Y)? position)
    { _mouseHook?.EndSelection(); _selection?.TrySetResult(position); }

    private void MoveUpClick(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDownClick(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int offset)
    {
        int index = PointGrid.SelectedIndex, target = index + offset;
        if (_busy || index < 0 || target < 0 || target >= _rows.Count) return;
        _rows.Move(index, target); Renumber(target); SaveSettings();
    }
    private void DeletePointClick(object sender, RoutedEventArgs e)
    {
        int index = PointGrid.SelectedIndex;
        if (_busy || index < 0) return;
        _rows.RemoveAt(index); Renumber(Math.Min(index, _rows.Count - 1)); SaveSettings();
    }
    private void Renumber(int selected)
    {
        for (int i = 0; i < _rows.Count; i++) if (_rows[i].Number != i + 1) _rows[i] = new PointRow(i + 1, _rows[i].Value);
        PointGrid.SelectedIndex = selected; UpdateRowsState();
    }
    private void UpdateRowsState()
    {
        PointCountText.Text = $"{_rows.Count} 个位置";
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int selected = PointGrid.SelectedIndex;
        EditButton.IsEnabled = DeleteButton.IsEnabled = !_busy && selected >= 0;
        UpButton.IsEnabled = !_busy && selected > 0;
        DownButton.IsEnabled = !_busy && selected >= 0 && selected < _rows.Count - 1;
    }
    private void PointSelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_initializing) UpdateRowsState(); }
    private void ConfigurationChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        LimitBox.IsEnabled = LimitedRadio.IsChecked == true; SaveSettings();
    }
    private void LimitLostFocus(object sender, RoutedEventArgs e) { if (!_initializing) SaveSettings(); }

    private void StartStopClick(object sender, RoutedEventArgs e) => ToggleRun();
    private void ToggleRun()
    {
        if (_busy) { RequestStop("手动停止"); return; }
        if (_editing || _selecting) return;
        if (_mouseHook is null || _hotkeys?.Ready != true)
        { ShowNotice("全局安全控制尚未就绪，请先检查或修改快捷键。", true); return; }
        try
        {
            long? limit = null;
            if (LimitedRadio.IsChecked == true)
            {
                if (!long.TryParse(LimitBox.Text.Trim(), out long value) || value <= 0)
                    throw new ArgumentException("请填写大于零的整数点击次数。");
                limit = value;
            }
            var plan = new ClickPlan(_rows.Select(row => row.Value).ToArray(), limit); plan.Validate();
            for (int i = 0; i < plan.Points.Count; i++)
                if (!Native.IsPointOnScreen(plan.Points[i].X, plan.Points[i].Y))
                    throw new ArgumentException($"第 {i + 1} 个位置不在当前显示器上，请重新选点。");
            SaveSettings();
            _uiRunTask = ExecutePlanAsync(plan);
        }
        catch (Exception ex) { ShowNotice(ex.Message, true); }
    }
    private async Task ExecutePlanAsync(ClickPlan plan)
    {
        bool minimized = MinimizeCheck.IsChecked == true;
        _busy = true; _stopReason = "手动停止"; NoticeBorder.Visibility = Visibility.Collapsed;
        ExecutedCountText.Text = "0"; CurrentPointText.Text = "准备执行…";
        SetRunningUi(true); _mouseHook!.SetRunning(true);
        _liveProgress = new LiveProgress(); _progressTimer.Start();
        if (minimized) WindowState = WindowState.Minimized;
        try
        {
            ClickRunResult result = await _engine.RunAsync(plan, _liveProgress);
            ExecutedCountText.Text = result.Count.ToString("N0", CultureInfo.CurrentCulture);
            CurrentPointText.Text = result.Completed ? "已达到指定点击次数" : _stopReason;
            SetStatus(result.Completed ? "已完成" : "已停止", false);
        }
        catch (Exception ex)
        {
            if (_liveProgress.Latest is { } last) DisplayProgress(last);
            SetStatus("已停止", false); CurrentPointText.Text = "发生错误，已安全停止";
            ShowNotice(ex.Message, true);
        }
        finally
        {
            _progressTimer.Stop(); _liveProgress = null;
            _mouseHook.SetRunning(false); _busy = false; SetRunningUi(false);
            if (minimized && WindowState == WindowState.Minimized && !_closingRequested)
            { WindowState = WindowState.Normal; Activate(); }
        }
    }
    private void RequestStop(string reason)
    {
        if (!_busy) return;
        _stopReason = reason; _engine.Stop(); _mouseHook?.SetRunning(false);
        SetStatus("正在停止", true); StartButton.IsEnabled = false;
    }
    private void EmergencyStopClick(object sender, RoutedEventArgs e)
    { if (_selecting) CompleteSelection(null); else RequestStop("紧急停止"); }
    private void SetRunningUi(bool running)
    {
        PointToolbar.IsEnabled = PointGrid.IsEnabled = ConfigPanel.IsEnabled = HotkeyButton.IsEnabled = !running;
        StartButton.Content = running ? "■   停止点击" : "▶   开始点击";
        StartButton.IsEnabled = true; EmergencyButton.IsEnabled = running;
        if (running) SetStatus("运行中", true);
        UpdateRowsState();
    }
    private void SetStatus(string text, bool active)
    {
        StateText.Text = text;
        StateBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#EAF0FF" : "#EAF4EF"));
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#3869EF" : "#328365"));
        StateText.Foreground = StateDot.Fill = brush;
    }
    private void DisplayProgress(ClickProgress progress)
    {
        ExecutedCountText.Text = progress.Count.ToString("N0", CultureInfo.CurrentCulture);
        CurrentPointText.Text = $"正在执行位置 {progress.PointIndex + 1} / {_rows.Count}";
    }
    private void HotkeySettingsClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _editing || _hotkeys is null) return;
        _editing = true;
        try
        {
            var dialog = new HotkeyWindow(_settings.ToggleKey, _settings.EmergencyKey, (toggle, emergency) =>
            {
                if (!_hotkeys.TryConfigure(toggle, emergency, out string error)) return error;
                _settings.ToggleKey = toggle; _settings.EmergencyKey = emergency;
                UpdateHotkeyText(); SaveSettings(); return null;
            }) { Owner = this };
            if (dialog.ShowDialog() == true) ShowNotice("全局快捷键已更新。", false);
        }
        finally { _editing = false; }
    }
    private void UpdateHotkeyText() => HotkeySummary.Text = $"{AppSettings.KeyName(_settings.ToggleKey)} 启停  /  {AppSettings.KeyName(_settings.EmergencyKey)} 紧停";
    private void SaveSettings()
    {
        if (_initializing || !_startServices) return;
        _settings.Points = _rows.Select(row => row.Value).ToList();
        _settings.Limited = LimitedRadio.IsChecked == true;
        if (long.TryParse(LimitBox.Text.Trim(), out long count) && count > 0) _settings.ClickLimit = count;
        _settings.MinimizeOnStart = MinimizeCheck.IsChecked == true;
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowNotice("设置未能保存，但当前设置仍可使用：" + ex.Message, true); }
    }
    private void ShowNotice(string text, bool error)
    {
        NoticeText.Text = text; NoticeBorder.Visibility = Visibility.Visible;
        NoticeBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#FFF1E8" : "#EDF4FF"));
        NoticeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#9C6036" : "#426AA9"));
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && _busy)
        {
            e.Cancel = true;
            if (_closingRequested) return;
            _closingRequested = true; RequestStop("正在退出");
            if (_uiRunTask is not null) await _uiRunTask;
            _allowClose = true; Close(); return;
        }
        _closingRequested = true; CompleteSelection(null); _overlay?.Close();
        _engine.Stop(); _progressTimer.Stop(); SaveSettings(); _mouseHook?.Dispose(); _hotkeys?.Dispose();
    }
    private sealed class LiveProgress : IProgress<ClickProgress>
    {
        private ClickProgress? _latest;
        public ClickProgress? Latest => Volatile.Read(ref _latest);
        public void Report(ClickProgress value) => Volatile.Write(ref _latest, value);
    }
}
