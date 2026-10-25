using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DianXu.Core;
using DianXu.Services;

namespace DianXu.Tests;
internal static class Program
{
    private static int _passed;
    private static readonly ClickPoint A = new(10, 20, ClickButton.Left, 1, WaitUnit.Seconds);
    private static readonly ClickPoint B = new(30, 40, ClickButton.Right, 3, WaitUnit.Seconds);
    [STAThread]
    private static int Main(string[] args)
    {
        try { RunAll(args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, ".test-data")).GetAwaiter().GetResult(); Console.WriteLine($"PASS: {_passed} assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); _passed++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { Check(true, name); return; } throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name); }
    private static async Task RunAll(string directory)
    {
        Directory.CreateDirectory(directory);
        Throws<ArgumentException>(() => new ClickPlan([], null).Validate(), "Empty plan rejected");
        Throws<ArgumentException>(() => new ClickPlan([A], 0).Validate(), "Zero limit rejected");
        Throws<ArgumentException>(() => new ClickPlan([A], -1).Validate(), "Negative limit rejected");
        Check((A with { WaitValue = 0 }).GetError() is not null, "Zero delay rejected");
        Check((A with { WaitValue = -1 }).GetError() is not null, "Negative delay rejected");
        Check((A with { WaitValue = decimal.MaxValue }).GetError() is not null, "Extreme decimal rejected without overflow");
        Check((A with { WaitValue = 0.001m }).GetError() is null, "One millisecond accepted");
        Check((A with { WaitValue = 0.0009m }).GetError() is not null, "Sub-millisecond rejected");
        Check((A with { WaitValue = 86400 }).GetError() is null, "24 hour delay accepted");
        Check((A with { WaitValue = 86401 }).GetError() is not null, "Over 24 hours rejected");
        Check((A with { Button = (ClickButton)99 }).GetError() is not null, "Invalid button rejected");
        Check((A with { Unit = (WaitUnit)99 }).GetError() is not null, "Invalid unit rejected");

        var output = new RecordingOutput(); var delay = new ImmediateDelay();
        var engine = new ClickEngine(output, delay);
        var result = await engine.RunAsync(new ClickPlan([A, B, A with { X = 50, WaitValue = 2 }], 10)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Completed && result.Count == 10 && output.Points.Count == 10, "Exact 10 clicks with 3-point plan");
        Check(output.Points.Select(p => p.X).SequenceEqual(new[] { 10, 30, 50, 10, 30, 50, 10, 30, 50, 10 }), "Order wraps correctly");
        Check(output.Points[1].Button == ClickButton.Right, "Right button preserved");
        Check(delay.Values.Select(v => v.TotalSeconds).SequenceEqual(new double[] { 1, 3, 2, 1, 3, 2, 1, 3, 2 }), "Independent waits; no final wait");
        Check(!engine.IsRunning, "Engine resets after completion");
        result = await engine.RunAsync(new ClickPlan([B], 1));
        Check(result.Count == 1 && result.Completed && delay.Values.Count == 9, "Single click never waits afterward");

        var signal = new SignalProgress(); output = new RecordingOutput(); engine = new ClickEngine(output);
        Task<ClickRunResult> task = engine.RunAsync(new ClickPlan([A with { WaitValue = 30 }], null), signal);
        await signal.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Throws<InvalidOperationException>(() => engine.RunAsync(new ClickPlan([B], 1)), "Concurrent start rejected");
        var watch = Stopwatch.StartNew(); engine.Stop(); result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(watch.ElapsedMilliseconds < 1000, "Stop interrupts long wait promptly");
        Check(!result.Completed && result.Count == 1 && output.Points.Count == 1, "Cancelled run sends no next click");
        engine.Stop(); engine.Stop(); Check(!engine.IsRunning, "Repeated stop is safe");
        for (int i = 0; i < 20; i++)
        {
            signal = new SignalProgress(); task = engine.RunAsync(new ClickPlan([A with { WaitValue = 30 }], null), signal);
            await signal.First.Task.WaitAsync(TimeSpan.FromSeconds(5)); engine.Stop();
            result = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(result.Count == 1, $"Restart {i + 1} has independent count and no stale loop");
        }
        var gateDelay = new GateDelay(); output = new RecordingOutput(); engine = new ClickEngine(output, gateDelay);
        var mutable = new List<ClickPoint> { A, B };
        task = engine.RunAsync(new ClickPlan(mutable, 2));
        await gateDelay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); mutable[1] = A; mutable.Clear(); gateDelay.Release.TrySetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(output.Points.Count == 2 && output.Points[1] == B, "Running plan uses immutable snapshot");
        engine = new ClickEngine(new ThrowingOutput());
        try { await engine.RunAsync(new ClickPlan([A], 1)); throw new Exception("No failure"); } catch (IOException) { Check(!engine.IsRunning, "Input failure resets lifecycle"); }

        var detector = new ManualDoubleClickDetector(500);
        Check(!detector.ObserveLeftDown(100, true) && !detector.ObserveLeftDown(110, true), "Injected clicks never stop");
        Check(!detector.ObserveLeftDown(200, false) && detector.ObserveLeftDown(400, false), "Physical double-click stops");
        detector.Reset(); Check(!detector.ObserveLeftDown(100, false), "First physical click alone does not stop");
        detector.ObserveLeftDown(120, true); detector.ObserveLeftDown(140, true);
        Check(detector.ObserveLeftDown(300, false), "Injected clicks do not disrupt physical pair");
        detector.Reset(); detector.ObserveLeftDown(100, false);
        Check(!detector.ObserveLeftDown(700, false), "Slow physical clicks do not stop");
        Check(detector.ObserveLeftDown(800, false), "Slow click becomes first of next pair");
        detector.Reset(); detector.ObserveLeftDown(uint.MaxValue - 100, false);
        Check(detector.ObserveLeftDown(50, false), "Double-click handles timestamp rollover");
        detector.Reset(); Check(!detector.ObserveLeftDown(60, false), "New session resets physical click history");

        string settingsDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(settingsDirectory);
        Check(store.Load().Warning is null && !Directory.Exists(settingsDirectory), "Missing settings use defaults without writing");
        var settings = new AppSettings { Points = [A, B with { X = -1200 }], Limited = true, ClickLimit = 10, ToggleKey = 0x78, EmergencyKey = 0x79, MinimizeOnStart = false };
        store.Save(settings); var loaded = store.Load();
        Check(loaded.Warning is null && loaded.Settings.Points.SequenceEqual(settings.Points), "Settings round-trip including negative coordinates");
        Check(loaded.Settings.Limited && loaded.Settings.ClickLimit == 10 && !loaded.Settings.MinimizeOnStart, "Run options persist");
        Check(loaded.Settings.ToggleKey == 0x78 && loaded.Settings.EmergencyKey == 0x79, "Custom shortcuts persist");
        Check(!File.Exists(store.FilePath + ".tmp"), "Atomic save leaves no temporary file");
        File.WriteAllText(store.FilePath, "{bad json"); loaded = store.Load();
        Check(loaded.Warning is not null && loaded.Settings.Points.Count == 0, "Corrupt settings safely recover");
        Check(Directory.GetFiles(settingsDirectory, "settings.json.invalid-*").Length == 1, "Corrupt file preserved in backup");
        settings.ToggleKey = settings.EmergencyKey;
        Throws<FormatException>(() => store.Save(settings), "Duplicate shortcut rejected before write");
        File.WriteAllText(store.FilePath, "{\"Version\":2}");
        Check(store.Load().Warning is not null, "Future settings version rejected safely");
        File.WriteAllText(store.FilePath, "{\"Points\":[null]}");
        Check(store.Load().Warning is not null, "Null point from malformed JSON is safe");

        Check(Marshal.SizeOf<Native.Input>() == (Environment.Is64BitProcess ? 40 : 28), "Win32 INPUT structure size matches architecture");
        Check(Marshal.OffsetOf<Native.Input>(nameof(Native.Input.Data)).ToInt32() == (Environment.Is64BitProcess ? 8 : 4), "Win32 INPUT union aligned correctly");
        Check(Marshal.SizeOf<Native.MouseHookData>() == (Environment.Is64BitProcess ? 32 : 24), "Low-level hook structure layout correct");
    }
    private sealed class RecordingOutput : IClickOutput { public List<ClickPoint> Points { get; } = []; public void Click(ClickPoint point) => Points.Add(point); }
    private sealed class ThrowingOutput : IClickOutput { public void Click(ClickPoint point) => throw new IOException("Test input failure"); }
    private sealed class ImmediateDelay : IClickDelay
    { public List<TimeSpan> Values { get; } = []; public Task WaitAsync(TimeSpan value, CancellationToken token) { token.ThrowIfCancellationRequested(); Values.Add(value); return Task.CompletedTask; } }
    private sealed class SignalProgress : IProgress<ClickProgress>
    { public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public void Report(ClickProgress value) => First.TrySetResult(); }
    private sealed class GateDelay : IClickDelay
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync(TimeSpan delay, CancellationToken token) { Entered.TrySetResult(); return Release.Task.WaitAsync(token); }
    }
}
