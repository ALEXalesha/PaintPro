using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaintPro.Tools;

namespace PaintPro.Views;

/// <summary>
/// Окно текста: шрифт, размер, B / I / U и сама надпись. Выбор запоминается до следующей
/// надписи, как панель текста Electron-версии.
/// </summary>
public partial class TextDialog : Window
{
    /// <summary>Шрифты - тот же список и в том же порядке, что в Electron-версии (#text-font).</summary>
    public static readonly IReadOnlyList<(string Label, string Family)> Fonts = new[]
    {
        ("Sans-serif", "Segoe UI"),
        ("Arial", "Arial"),
        ("Times New Roman", "Times New Roman"),
        ("Georgia", "Georgia"),
        ("Courier New", "Courier New"),
        ("Comic Sans", "Comic Sans MS"),
        ("Impact", "Impact"),
        ("Verdana", "Verdana"),
    };

    /// <summary>Последний выбор: следующая надпись начинается с него.</summary>
    public static TextStyle Last { get; private set; } = TextStyle.Default;

    private bool _ready;

    public TextDialog(string initial = "")
    {
        InitializeComponent();
        foreach (var (label, family) in Fonts)
            FontBox.Items.Add(new ComboBoxItem { Content = label, FontFamily = new FontFamily(family), Tag = family });
        var index = Fonts.Select(f => f.Family).ToList().IndexOf(Last.Family);
        FontBox.SelectedIndex = index < 0 ? 0 : index;
        SizeBox.Text = ((int)Last.Size).ToString();
        BoldToggle.IsChecked = Last.Bold;
        ItalicToggle.IsChecked = Last.Italic;
        UnderlineToggle.IsChecked = Last.Underline;
        Input.Text = initial;
        _ready = true;
        ApplyPreview();
        PreviewKeyDown += OnKeys;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public string ResultText { get; private set; } = "";
    public TextStyle ResultStyle { get; private set; } = TextStyle.Default;

    /// <summary>Показать окно; «Готово» - текст и оформление, «Отмена» или Escape - null.</summary>
    public static (string Text, TextStyle Style)? Show(Window? owner = null)
    {
        var dialog = new TextDialog { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? (dialog.ResultText, dialog.ResultStyle) : null;
    }

    private TextStyle CurrentStyle() => new(
        TextStyle.ClampSize(SizeBox.Text),
        (FontBox.SelectedItem as ComboBoxItem)?.Tag as string ?? TextStyle.Default.Family,
        BoldToggle.IsChecked == true,
        ItalicToggle.IsChecked == true,
        UnderlineToggle.IsChecked == true);

    /// <summary>Поле ввода показывает надпись тем же начертанием, каким она ляжет.</summary>
    private void ApplyPreview()
    {
        if (!_ready) return;
        var s = CurrentStyle();
        Input.FontFamily = new FontFamily(s.Family);
        Input.FontWeight = s.Bold ? FontWeights.Bold : FontWeights.Normal;
        Input.FontStyle = s.Italic ? FontStyles.Italic : FontStyles.Normal;
        Input.TextDecorations = s.Underline ? TextDecorations.Underline : null;
        Input.FontSize = Math.Clamp(s.Size, 12, 36); // в поле - в разумных пределах, на холсте - как задано
    }

    private void OnStyleChanged(object sender, RoutedEventArgs e) => ApplyPreview();

    private void OnSizeChanged(object sender, TextChangedEventArgs e) => ApplyPreview();

    private void OnSizeLostFocus(object sender, RoutedEventArgs e) =>
        SizeBox.Text = ((int)TextStyle.ClampSize(SizeBox.Text)).ToString();

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { OnOk(sender, e); e.Handled = true; }
    }

    private void OnBackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ResultText = Input.Text;
        ResultStyle = CurrentStyle();
        Last = ResultStyle;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
