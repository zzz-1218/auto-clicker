using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DianXu.Core;

namespace DianXu.Services;

internal sealed class GlobalMouseHook : IDisposable
{
    private readonly Native.HookProc _callback;
    private readonly Dispatcher _dispatcher;
    private readonly ManualDoubleClickDetector _detector = new(Native.GetDoubleClickTime());
    private nint _handle;
    private bool _running, _selecting, _selectionLeftDown, _selectionRightDown;
    private Native.Point _selected;
    internal event Action? ManualDoubleClick;
    internal event Action<int, int>? PositionSelected;
    internal event Action? SelectionCancelled;
    internal event Action<ClickButton, int, int>? InjectedClickObserved;

    public GlobalMouseHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = Callback;
        _handle = Native.SetWindowsHookEx(Native.WhMouseLl, _callback, Native.GetModuleHandle(null), 0);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用鼠标监听，暂不能安全运行连点。");
    }

    public void SetRunning(bool running) { _running = running; _detector.Reset(); }
    public void BeginSelection()
    {
        _detector.Reset(); _selectionLeftDown = false; _selectionRightDown = false; _selecting = true;
    }
    public void EndSelection() => _selecting = false;

    private nint Callback(int code, nint message, nint data)
    {
        if (code < 0) return Native.CallNextHookEx(_handle, code, message, data);
        var info = Marshal.PtrToStructure<Native.MouseHookData>(data);
        int msg = (int)message;
        bool injected = (info.Flags & 3) != 0 || info.ExtraInfo == Native.InputMarker;
        if (injected && msg is Native.WmLeftDown or Native.WmRightDown)
            InjectedClickObserved?.Invoke(msg == Native.WmLeftDown ? ClickButton.Left : ClickButton.Right, info.Point.X, info.Point.Y);
        if (_selecting && !injected)
        {
            if (msg == Native.WmLeftDown) { _selected = info.Point; _selectionLeftDown = true; return 1; }
            if (msg == Native.WmLeftUp && _selectionLeftDown)
            {
                _selectionLeftDown = false; _selecting = false;
                int x = _selected.X, y = _selected.Y;
                _dispatcher.BeginInvoke(() => PositionSelected?.Invoke(x, y));
                return 1;
            }
            if (msg == Native.WmRightDown) { _selectionRightDown = true; return 1; }
            if (msg == Native.WmRightUp && _selectionRightDown)
            {
                _selectionRightDown = false; _selecting = false;
                _dispatcher.BeginInvoke(() => SelectionCancelled?.Invoke());
                return 1;
            }
        }
        // Keep swallowing a captured release even if Escape cancelled while a button was down.
        if (!injected && msg == Native.WmLeftUp && _selectionLeftDown) { _selectionLeftDown = false; return 1; }
        if (!injected && msg == Native.WmRightUp && _selectionRightDown) { _selectionRightDown = false; return 1; }
        if (_running && msg == Native.WmLeftDown && _detector.ObserveLeftDown(info.Time, injected))
        {
            _running = false;
            // This callback must remain short. Stop cancels the engine but never waits for its thread.
            ManualDoubleClick?.Invoke();
        }
        return Native.CallNextHookEx(_handle, code, message, data);
    }

    public void Dispose()
    {
        if (_handle != 0) { Native.UnhookWindowsHookEx(_handle); _handle = 0; }
        GC.KeepAlive(_callback);
    }
}
