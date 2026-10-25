using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DianXu.Services;

namespace DianXu;

public partial class SelectionOverlay : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    public event Action? CancelRequested;
    public SelectionOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var r = Native.VirtualScreen;
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0040);
        };
        _timer.Tick += (_, _) => UpdateCursor();
        Loaded += (_, _) => { UpdateCursor(); _timer.Start(); Activate(); };
        Closed += (_, _) => _timer.Stop();
    }
    private void UpdateCursor()
    {
        if (!Native.GetCursorPos(out var p)) return;
        Point local = PointFromScreen(new Point(p.X, p.Y));
        HorizontalLine.X1 = 0; HorizontalLine.X2 = ActualWidth; HorizontalLine.Y1 = HorizontalLine.Y2 = local.Y;
        VerticalLine.Y1 = 0; VerticalLine.Y2 = ActualHeight; VerticalLine.X1 = VerticalLine.X2 = local.X;
        CoordinateText.Text = $"X  {p.X}     Y  {p.Y}";
        CanvasPosition(PositionCard, Math.Clamp(local.X + 24, 8, Math.Max(8, ActualWidth - 256)), Math.Clamp(local.Y + 24, 8, Math.Max(8, ActualHeight - 112)));
    }
    private static void CanvasPosition(UIElement element, double x, double y)
    { System.Windows.Controls.Canvas.SetLeft(element, x); System.Windows.Controls.Canvas.SetTop(element, y); }
    private void OverlayKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; CancelRequested?.Invoke(); } }
}
