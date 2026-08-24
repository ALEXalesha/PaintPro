using PaintPro.Models;
using SkiaSharp;

namespace PaintPro.Services;

/// <summary>
/// Shared lift/erase logic for floating pickups.
///
/// SelectTool, QuadTool and the canvas handle-drag code all need the same two operations,
/// and used to carry three near-identical copies of the lazy erase. One copy means the
/// source layer, the erase colour and the OriginalQuad snapshot stay consistent between
/// them.
/// </summary>
public static class PickupOps
{
    /// <summary>
    /// Colour the source area is erased to. The bottom layer is the opaque paper of the
    /// document, so it goes back to white; anything above must become transparent or it
    /// would punch a white hole through the layers below.
    /// </summary>
    public static SKColor EraseColor(Document doc, Layer layer)
        => doc.Layers.Count > 0 && ReferenceEquals(doc.Layers[0], layer)
            ? SKColors.White
            : SKColors.Transparent;

    /// <summary>Lift a rectangular region of the active layer into a floating pickup.</summary>
    public static void PromoteRect(Document doc, SKRect rect)
        => Promote(doc, rect, quad: null);

    /// <summary>Lift a 4-point region: the bbox pixels are lifted, the polygon becomes the clip.</summary>
    public static void PromoteQuad(Document doc, IReadOnlyList<SKPoint> corners)
        => Promote(doc, Bounds(corners), corners.ToArray());

    private static void Promote(Document doc, SKRect rect, SKPoint[]? quad)
    {
        if (doc.ActiveLayer is not PixelLayer pl) return;
        var clamped = SKRectI.Intersect(SKRectI.Round(rect), new SKRectI(0, 0, pl.Width, pl.Height));
        if (!clamped.HasArea()) return;

        var raw = pl.ExtractRegion(clamped);
        // Фон вокруг рисунка выкусывается только там, где он и есть фон: у бумаги, то
        // есть у нижнего слоя, и только у прямоугольного выделения.
        //
        // Многоугольнику форму задаёт он сам, маской, и белое внутри него - такая же
        // часть выделенного, как сам рисунок. Заливка же шла от рамки габарита, а её
        // углы лежат ВНЕ многоугольника: белое снаружи и белое внутри соединены, и
        // выкусывалось всё разом. Пользователь двигал кусок бумаги с рисунком, а получал
        // рисунок на прозрачном - сквозь него просвечивало то, что лежит ниже, при том
        // что на прежнем месте оставалась ровно та же белая бумага.
        //
        // На верхнем слое фона нет вовсе: там пусто, а белое - нарисовано. Выкусывать
        // его значит терять пиксели, которые пользователь сам и положил; заметно это
        // становилось на непрозрачной картинке, где до края выделения доходит белое -
        // небо на фотографии, залитая белым фигура. В Electron-версии слой один, и
        // кеинг там всегда про бумагу.
        var pickupBitmap = quad is null && ReferenceEquals(doc.Layers[0], pl)
            ? Keyed(raw)
            : raw;
        var pickup = new FloatingPickup(pickupBitmap,
            new SKRect(clamped.Left, clamped.Top, clamped.Right, clamped.Bottom))
        {
            Quad = quad,
            // Форма на момент подъёма - именно её выкусывают из слоя. Пока она
            // снималась при первом перемещении, перетаскивание угла ДО перемещения
            // подменяло её: из слоя вырезалась новая форма, то есть область, которую
            // пользователь не выделял, а часть выделенной оставалась лежать на месте.
            OriginalQuad = quad is null ? null : (SKPoint[])quad.Clone(),
            SourceLayerId = pl.Id,
            // Snapshot the layer as it is now (before any lazy-erase) so the eventual
            // commit can record an undoable before/after diff, and Escape can put the
            // lifted pixels back.
            PreEditSnapshot = pl.ExtractRegion(new SKRectI(0, 0, pl.Width, pl.Height)),
        };
        // С чем сравнивать при коммите: подъём сам по себе холста не меняет, и отличить
        // «подняли и положили обратно» от настоящего перемещения можно только так.
        pickup.RememberOrigin();
        doc.FloatingPickup = pickup;
    }

    /// <summary>Выкусить бумагу вокруг рисунка и отпустить исходный битмап.</summary>
    private static SKBitmap Keyed(SKBitmap raw)
    {
        using (raw) return BitmapKeying.KeyOutBackground(raw, SKColors.White);
    }

    /// <summary>
    /// Сдвинуть поднятый объект на (dx, dy) - вместе с его quad-маской.
    ///
    /// Одна операция на всех инструментах: маска живёт в тех же координатах документа,
    /// что и габарит, и её обязано двигать то же самое перемещение. Пока сдвиг был
    /// написан отдельно в каждом инструменте, «Выделение» двигало один габарит: маска
    /// оставалась на прежнем месте, и объект, поднятый многоугольником, срезался ею на
    /// ходу - до полного исчезновения, стоило отвести его на свою ширину. Достаётся это
    /// «Выделению» через хоткей поворота: он поднимает многоугольник, не трогая активный
    /// инструмент.
    /// </summary>
    public static void Translate(FloatingPickup fp, float dx, float dy)
    {
        fp.X += dx;
        fp.Y += dy;
        if (fp.Quad is not { } quad) return;
        for (int i = 0; i < quad.Length; i++)
            quad[i] = new SKPoint(quad[i].X + dx, quad[i].Y + dy);
    }

    /// <summary>
    /// Antipattern §6: erase the original area on the source layer at the FIRST
    /// move/scale/rotate, never at lift time. Idempotent via OriginalAreaErased.
    /// </summary>
    public static void EnsureLazyErase(Document doc, FloatingPickup fp)
    {
        if (fp.OriginalAreaErased) return;
        var pl = doc.FindPixelLayer(fp.SourceLayerId) ?? doc.ActiveLayer as PixelLayer;
        if (pl is null) return;

        using var canvas = new SKCanvas(pl.Bitmap);
        using var paint = new SKPaint
        {
            Color = EraseColor(doc, pl),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            // Src so an erase to transparent actually clears instead of compositing nothing.
            BlendMode = SKBlendMode.Src,
        };

        // Выкусываем форму, которую подняли, а не ту, что на пикапе сейчас: перетаскивание
        // угла меняет маску, но не то, что было взято из слоя.
        if ((fp.OriginalQuad ?? fp.Quad) is { } quad)
        {
            fp.OriginalQuad ??= (SKPoint[])quad.Clone();
            using var path = new SKPath();
            path.MoveTo(quad[0]);
            path.LineTo(quad[1]);
            path.LineTo(quad[2]);
            path.LineTo(quad[3]);
            path.Close();
            canvas.DrawPath(path, paint);
        }
        else
        {
            canvas.DrawRect(fp.OriginalBBox, paint);
        }
        fp.OriginalAreaErased = true;
    }

    /// <summary>
    /// Точка мыши в системе координат пикапа: поворот снят, остальное как было.
    ///
    /// Пикап рисуется повёрнутым (<see cref="Document.DrawPickup"/> крутит канву), а хранит
    /// неповёрнутые габарит и углы quad'а. Значит всё, что сравнивает мышь с этой
    /// геометрией, обязано сначала снять поворот - иначе сравнение идёт с фигурой, которой
    /// на экране нет.
    /// </summary>
    public static SKPoint ToLocal(FloatingPickup fp, SKPoint p)
        => fp.Rotation == 0f ? p : GeometryMath.Rotate(p, fp.Center, -fp.Rotation);

    /// <summary>
    /// Попал ли клик в само тело пикапа (не в ручку).
    ///
    /// Повёрнутый объект проверяется по своей форме, а не по габариту: у прямоугольника,
    /// повёрнутого на 45°, углы вылезают за габарит, а сам габарит по углам пуст. Пока
    /// проверка шла по неповёрнутому <see cref="FloatingPickup.CurrentBBox"/>, клик по
    /// видимому краю объекта прижимал его к холсту и начинал новое выделение, зато клик по
    /// пустому углу габарита - таскал. Многоугольник так же: тянется он за то, что нарисовано,
    /// а не за описанный вокруг прямоугольник. В Electron-версии это <c>hitFloating</c>.
    /// </summary>
    public static bool HitBody(FloatingPickup fp, SKPoint point)
    {
        var local = ToLocal(fp, point);
        return fp.Quad is { } quad
            ? GeometryMath.PointInPolygon(quad, local)
            : fp.CurrentBBox.Contains(local);
    }

    /// <summary>
    /// Index of the corner within <paramref name="radius"/> document px of the point, or -1.
    ///
    /// Хват не может быть больше четверти меньшей стороны фигуры. Зона хвата задана в
    /// экранных пикселях и о размере фигуры ничего не знает: у многоугольника со стороной
    /// меньше двух её диаметров четыре зоны смыкаются в середине и накрывают его целиком.
    /// Такое выделение нельзя было ни поднять - проверка углов идёт первой, и клик в самую
    /// его середину читался как хват угла, - ни, уже поднятое, потащить за тело: вместо
    /// объекта уезжал ближайший угол. Начиналось это на 20 пикселях документа при масштабе
    /// 1:1, то есть на любом выделении меньше ногтя, а нижняя граница у выделения - 4
    /// пикселя (см. QuadTool.OnPointerUp). Ограничение оставляет середину телу при любом
    /// размере, не трогая хват на крупных фигурах.
    /// </summary>
    public static int HitCorner(IReadOnlyList<SKPoint> corners, SKPoint p, float radius)
    {
        radius = MathF.Min(radius, ShapeLimit(corners));
        float best = radius * radius;
        int hit = -1;
        for (int i = 0; i < corners.Count; i++)
        {
            var dx = corners[i].X - p.X;
            var dy = corners[i].Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 <= best) { best = d2; hit = i; }
        }
        return hit;
    }

    /// <summary>
    /// Наибольший хват, при котором зоны углов ещё не смыкаются: половина меньшей стороны
    /// габарита. На такой зоне каждый угол забирает не больше половины каждой своей
    /// стороны, а середина фигуры (она дальше - на диагональ) остаётся телу.
    /// </summary>
    private static float ShapeLimit(IReadOnlyList<SKPoint> corners)
    {
        if (corners.Count == 0) return 0f;
        var b = Bounds(corners);
        return MathF.Min(b.Width, b.Height) / 2f;
    }

    private static SKRect Bounds(IReadOnlyList<SKPoint> pts)
    {
        float minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].X < minX) minX = pts[i].X;
            if (pts[i].X > maxX) maxX = pts[i].X;
            if (pts[i].Y < minY) minY = pts[i].Y;
            if (pts[i].Y > maxY) maxY = pts[i].Y;
        }
        return new SKRect(minX, minY, maxX, maxY);
    }
}
