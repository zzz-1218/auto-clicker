using System.Globalization;
using System.Windows;
using DianXu.Core;
using DianXu.Services;

namespace DianXu;

public partial class PointEditorWindow : Window
{
    public ClickPoint Point { get; private set; }
    public bool RequestedRepick { get; private set; }
    public PointEditorWindow(ClickPoint point, bool adding)
    {
        InitializeComponent(); Point = point;
        Title = HeadingText.Text = adding ? "添加点击位置" : "编辑点击位置";
        XBox.Text = point.X.ToString(CultureInfo.InvariantCulture); YBox.Text = point.Y.ToString(CultureInfo.InvariantCulture);
        LeftRadio.IsChecked = point.Button == ClickButton.Left; RightRadio.IsChecked = point.Button == ClickButton.Right;
        WaitBox.Text = point.WaitValue.ToString(CultureInfo.CurrentCulture);
        UnitBox.SelectedIndex = (int)point.Unit;
    }
    private bool ReadPoint()
    {
        if (!int.TryParse(XBox.Text.Trim(), out int x) || !int.TryParse(YBox.Text.Trim(), out int y))
        { ErrorText.Text = "请填写有效的整数坐标。"; return false; }
        if (!decimal.TryParse(WaitBox.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value))
        { ErrorText.Text = "请填写有效的等待时间。"; return false; }
        var point = new ClickPoint(x, y, RightRadio.IsChecked == true ? ClickButton.Right : ClickButton.Left, value, (WaitUnit)UnitBox.SelectedIndex);
        if (point.GetError() is string error) { ErrorText.Text = error; return false; }
        if (!Native.IsPointOnScreen(x, y)) { ErrorText.Text = "这个坐标不在当前连接的显示器上，请重新选点。"; return false; }
        Point = point; return true;
    }
    private void SaveClick(object sender, RoutedEventArgs e) { if (ReadPoint()) DialogResult = true; }
    private void RepickClick(object sender, RoutedEventArgs e)
    {
        // Allow a disconnected old coordinate to be replaced; preserve valid timing/button edits.
        if (decimal.TryParse(WaitBox.Text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value))
        {
            var candidate = Point with { Button = RightRadio.IsChecked == true ? ClickButton.Right : ClickButton.Left, WaitValue = value, Unit = (WaitUnit)UnitBox.SelectedIndex };
            if (candidate.GetError() is null) Point = candidate;
        }
        RequestedRepick = true; DialogResult = false;
    }
}
