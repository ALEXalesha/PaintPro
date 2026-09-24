using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PaintPro.Views;

/// <summary>
/// Замена MessageBox в цветах темы. Вызывается так же: текст, заголовок, кнопки, значок
/// (значок не рисуется - смысл несёт текст, а цветной значок Windows 95 на стекле чужой).
/// </summary>
public partial class GlassMessage : Window
{
    private MessageBoxResult _result;

    public GlassMessage(string text, string title, MessageBoxButton buttons)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = text;

        var list = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Отмена", MessageBoxResult.Cancel) },
            MessageBoxButton.YesNo => new[] { ("Да", MessageBoxResult.Yes), ("Нет", MessageBoxResult.No) },
            MessageBoxButton.YesNoCancel => new[] { ("Да", MessageBoxResult.Yes), ("Нет", MessageBoxResult.No), ("Отмена", MessageBoxResult.Cancel) },
            _ => new[] { ("OK", MessageBoxResult.OK) },
        };
        // Escape - то, что закрывает без действия: «Отмена», иначе «Нет», иначе «OK».
        var escape = list.Any(b => b.Item2 == MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
                   : list.Any(b => b.Item2 == MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        _result = escape;
        for (var i = 0; i < list.Length; i++)
        {
            var (label, result) = list[i];
            var button = new Button
            {
                Content = label,
                MinWidth = 84,
                Style = (Style)FindResource("GhostButton"),
                IsDefault = i == 0,
                IsCancel = result == escape,
            };
            if (i == 0) button.Background = (System.Windows.Media.Brush)FindResource("AccentGrad");
            button.Click += (_, _) => { _result = result; DialogResult = true; };
            Buttons.Children.Add(button);
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _result = escape; DialogResult = false; }
        };
    }

    public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, Window? owner = null)
    {
        var dialog = new GlassMessage(text, title, buttons);
        var parent = owner ?? Application.Current?.MainWindow;
        if (parent is not null && parent.IsVisible) dialog.Owner = parent;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }

    public static MessageBoxResult Show(string text) => Show(text, "Paint Pro");

    private void OnBackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
