using System.Runtime.InteropServices;
using System.Windows.Interop;
using DianXu.Core;

namespace DianXu.Services;

internal sealed class GlobalHotkeys : IDisposable
{
    private const int ToggleId = 0x5101, EmergencyId = 0x5102;
    private readonly nint _window;
    private readonly HwndSource _source;
    private int _toggle, _emergency;
    public bool Ready { get; private set; }
    public event Action? Toggle;
    public event Action? Emergency;

    public GlobalHotkeys(nint window)
    {
        _window = window;
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("无法绑定窗口快捷键。");
        _source.AddHook(HandleMessage);
    }

    public bool TryConfigure(int toggle, int emergency, out string error)
    {
        if (!AppSettings.IsSupportedKey(toggle) || !AppSettings.IsSupportedKey(emergency) || toggle == emergency)
        { error = "两个快捷键必须不同，且应为功能键。"; return false; }
        bool hadOld = Ready; int oldToggle = _toggle, oldEmergency = _emergency;
        Unregister();
        if (Register(toggle, emergency, out error)) { _toggle = toggle; _emergency = emergency; return true; }
        if (hadOld && !Register(oldToggle, oldEmergency, out _)) error += " 原快捷键也未能恢复，请重新设置。";
        return false;
    }

    private bool Register(int toggle, int emergency, out string error)
    {
        if (!Native.RegisterHotKey(_window, ToggleId, 0x4000, (uint)toggle))
        { error = $"{AppSettings.KeyName(toggle)} 无法注册，可能已被其他程序占用（错误 {Marshal.GetLastWin32Error()}）。请选择其他键。"; return false; }
        if (!Native.RegisterHotKey(_window, EmergencyId, 0x4000, (uint)emergency))
        {
            int code = Marshal.GetLastWin32Error(); Native.UnregisterHotKey(_window, ToggleId);
            error = $"{AppSettings.KeyName(emergency)} 无法注册，可能已被其他程序占用（错误 {code}）。请选择其他键。";
            return false;
        }
        Ready = true; error = ""; return true;
    }

    private nint HandleMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Native.WmHotkey) return 0;
        if ((int)wParam == ToggleId) { handled = true; Toggle?.Invoke(); }
        if ((int)wParam == EmergencyId) { handled = true; Emergency?.Invoke(); }
        return 0;
    }
    private void Unregister()
    {
        Native.UnregisterHotKey(_window, ToggleId); Native.UnregisterHotKey(_window, EmergencyId); Ready = false;
    }
    public void Dispose() { Unregister(); _source.RemoveHook(HandleMessage); }
}
