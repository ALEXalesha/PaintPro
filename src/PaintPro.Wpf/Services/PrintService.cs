using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Печать листа (1.35.0). До неё в C#-версии печати не было вовсе, а в Electron-версии
/// Ctrl+P печатал всё окно; с 1.20.0 / 1.35.0 обе печатают одну картинку листа.
///
/// Лист раскладывается так же, как в Electron-версии (там это @media print): поля 10 мм,
/// картинка в своём размере (пиксель холста - точка, 1/96 дюйма), а если не влезает - вся
/// уменьшается до страницы с сохранением пропорций; по середине по ширине, от верхнего
/// края. Ориентацию страницы здесь подсказываем заранее по форме картинки: в системном
/// окне печати её легко не заметить, а холст по умолчанию 900 × 600 - лёжа.
/// </summary>
public static class PrintService
{
    /// <summary>Поля страницы: 10 мм в точках WPF (1/96 дюйма).</summary>
    public const double MarginDip = 10.0 / 25.4 * 96.0;

    /// <summary>Где на странице лежит картинка такого размера.</summary>
    public static Rect FitOnPage(double imageWidth, double imageHeight, double pageWidth, double pageHeight,
                                 double margin = MarginDip)
    {
        double availW = Math.Max(1, pageWidth - 2 * margin);
        double availH = Math.Max(1, pageHeight - 2 * margin);
        double scale = Math.Min(1.0, Math.Min(availW / imageWidth, availH / imageHeight));
        double w = imageWidth * scale, h = imageHeight * scale;
        return new Rect(margin + (availW - w) / 2, margin, w, h);
    }

    /// <summary>Лёжа, если картинка шире, чем выше.</summary>
    public static PageOrientation PreferredOrientation(int width, int height)
        => width > height ? PageOrientation.Landscape : PageOrientation.Portrait;

    /// <summary>Картинка холста для WPF: те же пиксели, premultiplied BGRA.</summary>
    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        using var copy = bitmap.Copy(SKColorType.Bgra8888);
        var source = BitmapSource.Create(copy.Width, copy.Height, 96, 96, PixelFormats.Pbgra32, null,
                                         copy.GetPixels(), copy.ByteCount, copy.RowBytes);
        source.Freeze();
        return source;
    }

    /// <summary>Одна страница с картинкой листа.</summary>
    public static FixedDocument BuildDocument(BitmapSource image, Size page, double margin = MarginDip)
    {
        var rect = FitOnPage(image.PixelWidth, image.PixelHeight, page.Width, page.Height, margin);
        var picture = new Image { Source = image, Width = rect.Width, Height = rect.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        FixedPage.SetLeft(picture, rect.X);
        FixedPage.SetTop(picture, rect.Y);
        var fixedPage = new FixedPage { Width = page.Width, Height = page.Height, Background = Brushes.White };
        fixedPage.Children.Add(picture);
        var content = new PageContent();
        ((System.Windows.Markup.IAddChild)content).AddChild(fixedPage);
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = page;
        doc.Pages.Add(content);
        return doc;
    }

    /// <summary>
    /// Системное окно печати и сама печать. False - пользователь передумал.
    /// </summary>
    public static bool PrintWithDialog(BitmapSource image)
    {
        var dialog = new PrintDialog();
        try
        {
            dialog.PrintTicket.PageOrientation = PreferredOrientation(image.PixelWidth, image.PixelHeight);
        }
        catch (Exception)
        {
            // Принтера может не быть вовсе, а у некоторых драйверов билет не настраивается:
            // ориентация - только подсказка, окно печати откроется и без неё.
        }
        if (dialog.ShowDialog() != true) return false;
        var page = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        dialog.PrintDocument(BuildDocument(image, page).DocumentPaginator, "Paint Pro");
        return true;
    }
}
