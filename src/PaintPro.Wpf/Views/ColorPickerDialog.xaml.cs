using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaintPro.Services;
using SkiaSharp;

namespace PaintPro.Views;

/// <summary>
/// Окно выбора цвета: квадрат оттенков, полоса тона, HEX. Арифметика - в
/// <see cref="ColorMath"/>, здесь только перевод положения мыши в числа и обратно.
/// </summary>
public partial class ColorPickerDialog : Window
{
    private ColorMath.Hsv _hsv;
    private bool _fromCode; // поле HEX меняем сами - не разбирать его обратно

    public SKColor Result { get; private set; }

    public ColorPickerDialog(SKColor initial)
    {
        InitializeComponent();
        OldSwatch.Background = new SolidColorBrush(ToWpf(initial));
        _hsv = ColorMath.ToHsv(initial);
        Result = initial;
        Loaded += (_, _) => { Refresh(updateHex: true); HexBox.Focus(); HexBox.SelectAll(); };
    }

    /// <summary>Показать окно; «Готово» - выбранный цвет, «Отмена» или Escape - null.</summary>
    public static SKColor? Show(SKColor initial, Window? owner = null)
    {
        var dialog = new ColorPickerDialog(initial) { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private static Color ToWpf(SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);

    private void Refresh(bool updateHex)
    {
        var color = ColorMath.FromHsv(_hsv);
        Result = color;
        HueFill.Fill = new SolidColorBrush(ToWpf(ColorMath.FromHsv(new ColorMath.Hsv(_hsv.H, 1, 1))));
        NewSwatch.Background = new SolidColorBrush(ToWpf(color));
        Canvas.SetLeft(SvMarker, _hsv.S * SvArea.Width - SvMarker.Width / 2);
        Canvas.SetTop(SvMarker, (1 - _hsv.V) * SvArea.Height - SvMarker.Height / 2);
        Canvas.SetTop(HueMarker, _hsv.H / 360 * HueArea.Height - HueMarker.Height / 2);
        if (updateHex)
        {
            _fromCode = true;
            HexBox.Text = ColorMath.ToHex(color);
            _fromCode = false;
        }
        HexBox.BorderBrush = (Brush)FindResource("GlassBorder");
    }

    private void SetSv(Point p)
    {
        _hsv = _hsv with
        {
            S = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1),
            V = Math.Clamp(1 - p.Y / SvArea.ActualHeight, 0, 1),
        };
        Refresh(updateHex: true);
    }

    private void SetHue(Point p)
    {
        // 359.999, а не 360: низ полосы - снова красный, но маркер пусть стоит внизу.
        _hsv = _hsv with { H = Math.Clamp(p.Y / HueArea.ActualHeight, 0, 0.99999) * 360 };
        Refresh(updateHex: true);
    }

    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        SvArea.CaptureMouse();
        SetSv(e.GetPosition(SvArea));
        e.Handled = true;
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (SvArea.IsMouseCaptured) SetSv(e.GetPosition(SvArea));
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        HueArea.CaptureMouse();
        SetHue(e.GetPosition(HueArea));
        e.Handled = true;
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (HueArea.IsMouseCaptured) SetHue(e.GetPosition(HueArea));
    }

    private void OnAreaUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_fromCode) return;
        if (ColorMath.ParseHex(HexBox.Text) is { } c)
        {
            // У серого нет тона, у чёрного - и насыщенности: ToHsv дал бы 0, и квадрат
            // прыгнул бы в красный. Недостающее берётся прежним, цвет - ровно как введён.
            var hsv = ColorMath.ToHsv(c);
            if (hsv.S == 0) hsv = hsv with { H = _hsv.H };
            if (hsv.V == 0) hsv = hsv with { H = _hsv.H, S = _hsv.S };
            _hsv = hsv;
            Refresh(updateHex: false);
            Result = c;
            NewSwatch.Background = new SolidColorBrush(ToWpf(c));
        }
        else
        {
            HexBox.BorderBrush = Brushes.IndianRed; // не цвет - видно, но ничего не ломается
        }
    }

    private void OnBackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
