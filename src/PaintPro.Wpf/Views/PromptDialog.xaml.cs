using System.Windows;

namespace PaintPro.Views;

/// <summary>
/// Simple modal text-input dialog. Returns null on cancel, the entered string otherwise.
/// Drop-in replacement for Microsoft.VisualBasic.Interaction.InputBox without dragging
/// in VisualBasic.Core or WinForms.
/// </summary>
public partial class PromptDialog : Window
{
    public string Result { get; private set; } = "";

    public PromptDialog(string prompt, string title, string initial = "")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public static string? Show(string prompt, string title, string initial = "", Window? owner = null)
    {
        var dlg = new PromptDialog(prompt, title, initial) { Owner = owner ?? Application.Current?.MainWindow };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) { Result = Input.Text; DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void OnBackgroundDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
}
