using System.Windows;
using DianXu.Core;

namespace DianXu;

public partial class HotkeyWindow : Window
{
    private readonly Func<int, int, string?> _apply;
    public HotkeyWindow(int toggle, int emergency, Func<int, int, string?> apply)
    {
        InitializeComponent(); _apply = apply;
        // F12 is reserved by Windows for debuggers; offer F1–F11.
        string[] names = Enumerable.Range(1, 11).Select(i => $"F{i}").ToArray();
        ToggleBox.ItemsSource = names; EmergencyBox.ItemsSource = names;
        ToggleBox.SelectedIndex = toggle - 0x70; EmergencyBox.SelectedIndex = emergency - 0x70;
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        int toggle = 0x70 + ToggleBox.SelectedIndex, emergency = 0x70 + EmergencyBox.SelectedIndex;
        string? error = _apply(toggle, emergency);
        if (error is not null) { ErrorText.Text = error; return; }
        DialogResult = true;
    }
}
