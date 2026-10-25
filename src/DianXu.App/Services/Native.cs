using System.ComponentModel;
using System.Runtime.InteropServices;
using DianXu.Core;

namespace DianXu.Services;

internal static class Native
{
    internal const int WhMouseLl = 14, WmMouseMove = 0x0200, WmLeftDown = 0x0201, WmLeftUp = 0x0202,
        WmRightDown = 0x0204, WmRightUp = 0x0205, WmHotkey = 0x0312;
    internal static readonly nuint InputMarker = 0x4458434C;
    internal const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008,
        RightUp = 0x0010, VirtualDesk = 0x4000, Absolute = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData { public Point Point; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input { public uint Type; public InputUnion Data; }
    internal delegate nint HookProc(int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);

    internal static Rect VirtualScreen => new()
    {
        Left = GetSystemMetrics(76), Top = GetSystemMetrics(77),
        Right = GetSystemMetrics(76) + GetSystemMetrics(78), Bottom = GetSystemMetrics(77) + GetSystemMetrics(79)
    };
    internal static bool IsPointOnScreen(int x, int y) => MonitorFromPoint(new Point(x, y), 0) != 0;
}

internal sealed class WindowsClickOutput : IClickOutput
{
    public void Click(ClickPoint point)
    {
        if (!Native.IsPointOnScreen(point.X, point.Y))
            throw new InvalidOperationException($"位置 ({point.X}, {point.Y}) 已不在连接的显示器上，点击已停止。请重新选点。");
        Native.Rect screen = Native.VirtualScreen;
        int width = screen.Right - screen.Left, height = screen.Bottom - screen.Top;
        // Map to the centre of the physical pixel across the complete virtual desktop.
        int dx = (int)(((long)(point.X - screen.Left) * 65536 + 32768) / width);
        int dy = (int)(((long)(point.Y - screen.Top) * 65536 + 32768) / height);
        uint down = point.Button == ClickButton.Left ? Native.LeftDown : Native.RightDown;
        uint up = point.Button == ClickButton.Left ? Native.LeftUp : Native.RightUp;
        Native.Input Make(int x, int y, uint flags) => new()
        {
            Type = 0,
            Data = new Native.InputUnion { Mouse = new Native.MouseInput { Dx = x, Dy = y, Flags = flags, ExtraInfo = Native.InputMarker } }
        };
        Native.Input[] inputs = [Make(dx, dy, Native.MouseMove | Native.Absolute | Native.VirtualDesk), Make(0, 0, down), Make(0, 0, up)];
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            // Release any injected button if the system accepted only part of the packet.
            Native.SendInput(1, [Make(0, 0, up)], Marshal.SizeOf<Native.Input>());
            string detail = error == 0 ? "目标窗口可能有权限限制。" : new Win32Exception(error).Message;
            throw new InvalidOperationException("无法完整发送鼠标点击，已停止。" + detail + " 请检查目标窗口与本程序的权限级别。");
        }
    }
}
