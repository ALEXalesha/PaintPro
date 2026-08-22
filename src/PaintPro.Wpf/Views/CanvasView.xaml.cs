using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PaintPro.Models;
using PaintPro.Services;
using PaintPro.Tools;
using PaintPro.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace PaintPro.Views;

/// <summary>
/// Hosts the SKElement and the WPF overlay (selection/handles).
/// Routes pointer input to the active ITool, applies Zoom, and exposes a
/// PixelPositionChanged event the status bar listens to.
/// </summary>
public partial class CanvasView : UserControl
{
    private MainViewModel? _vm;

    /// <summary>Document pixel coords under the cursor, or null when outside the canvas.</summary>
    public event Action<SKPoint?>? PixelPositionChanged;
    public event Action<SKColor>? PixelColorChanged;

    public CanvasView()
    {
        InitializeComponent();
        Skia.PaintSurface += OnPaintSurface;

        // Pointer events — captured on the Skia element so handles in overlay don't steal them.
        Skia.MouseDown += OnSkiaMouseDown;
        Skia.MouseMove += OnSkiaMouseMove;
        Skia.MouseUp   += OnSkiaMouseUp;
        // Захват мыши может уйти посреди штриха: чужое окно вышло вперёд, Alt+Tab,
        // модальный диалог. MouseUp тогда не придёт совсем, а вместе с ним не придёт и
        // запись штриха в слой: нарисованное оставалось висеть превью до следующего клика
        // и пропадало, а битмап размером с холст утекал. Ручки это пережили ещё в 1.8.0
        // (Overlay.LostMouseCapture), сам холст - нет.
        Skia.LostMouseCapture += (_, _) => EndCanvasGesture();
        Skia.MouseLeave += (_, _) => PixelPositionChanged?.Invoke(null);

        // Ctrl+wheel zoom with focal point under the cursor.
        // PreviewMouseWheel (tunneling) intercepts before ScrollViewer's bubbling MouseWheel,
        // otherwise ScrollViewer would scroll instead of letting us zoom.
        PreviewMouseWheel += OnPreviewMouseWheel;

        // Перетаскивание ручек слушает Overlay, а не сами ручки. DrawOverlay каждый кадр
        // делает Children.Clear() и пересоздаёт ручки заново, а WPF снимает захват мыши с
        // элемента, который убрали из дерева. Ручка, за которую тянули, исчезала на первом
        // же кадре, и дальше события шли туда, куда попал курсор: у боковых ручек движение
        // поперёк оси выводило его за 12 пикселей ручки, жест обрывался, а следующее
        // движение попадало на холст и уходило активному инструменту. Overlay же остаётся
        // на месте, чистятся только его дети, так что захват держится до отпускания.
        Overlay.MouseMove += OnHandleMouseMove;
        Overlay.MouseLeftButtonUp += OnHandleMouseUp;
        Overlay.LostMouseCapture += (_, _) => EndHandleDrag();

        // Ctrl+wheel zoom is wired by the host (MainWindow) so it can centre on cursor properly.
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm) Attach(vm);
        };
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm) Attach(vm);
        };
    }

    private void Attach(MainViewModel vm)
    {
        if (_vm == vm) return;
        if (_vm is not null)
        {
            _vm.InvalidateCanvas -= Refresh;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Document.PropertyChanged -= OnDocPropertyChanged;
        }
        _vm = vm;
        _vm.InvalidateCanvas += Refresh;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Document.PropertyChanged += OnDocPropertyChanged;
        UpdateLayout2();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Zoom)) UpdateLayout2();
        Refresh();
    }
    private void OnDocPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Document.CanvasWidth) or nameof(Document.CanvasHeight))
            UpdateLayout2();
        Refresh();
    }

    /// <summary>Recompute viewport size from canvas size × zoom.</summary>
    private void UpdateLayout2()
    {
        if (_vm is null) return;
        // Round to whole pixels — fractional sizes cause WriteableBitmap blur and snap-jitter.
        var w = Math.Round(_vm.Document.CanvasWidth  * _vm.Zoom);
        var h = Math.Round(_vm.Document.CanvasHeight * _vm.Zoom);
        CanvasFrame.Width = Skia.Width = Overlay.Width = w;
        CanvasFrame.Height = Skia.Height = Overlay.Height = h;
        ContentRoot.Width  = w + 80;
        ContentRoot.Height = h + 80;
        CanvasFrame.Margin = Skia.Margin = Overlay.Margin = new Thickness(40);
        DrawOverlay();
    }

    // ───────── vsync-coalesced rendering ─────────
    // Coalesce all redraw requests within a single composition frame into one render.
    // Without this, mouse-move events (200-1000 Hz) each trigger full SKElement
    // software-rasterization (~5 MB pixel copy per frame at HiDPI) and the UI thread
    // becomes the bottleneck.
    private bool _renderQueued;

    public void Refresh()
    {
        if (!CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        QueueRender();
    }

    /// <summary>Queue a single render for the next composition frame.</summary>
    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        CompositionTarget.Rendering += OnCompositionRender;
    }

    private void OnCompositionRender(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnCompositionRender;
        _renderQueued = false;
        Skia.InvalidateVisual();
        DrawOverlay();
    }

    // ───────── Skia rendering ─────────
    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_vm is null) return;
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        // Compute device-pixel scale from element size vs document size — this preserves
        // crisp pixel rendering when zoomed.
        var docW = _vm.Document.CanvasWidth;
        var infoW = e.Info.Width;
        var sx = (float)infoW / docW;
        canvas.Scale(sx, sx);

        // When zoomed in past 1:1 we want crisp nearest-neighbour pixels so the user
        // sees individual document pixels. When zoomed out, linear sampling smooths the result.
        var samplePaint = new SKPaint
        {
            FilterQuality = sx > 1f ? SKFilterQuality.None : SKFilterQuality.Low,
        };

        foreach (var layer in _vm.Document.Layers)
        {
            if (layer is PixelLayer pl && pl.Visible)
            {
                samplePaint.Color = SKColors.White.WithAlpha((byte)(255 * pl.Opacity));
                canvas.DrawBitmap(pl.Bitmap, 0, 0, samplePaint);
            }
            else
            {
                layer.Render(canvas);
            }
        }

        // Active tool's preview bitmap (rubber-banded shape, brush ghost).
        // Tools draw their stroke opaque and report the alpha separately, so the preview
        // has to be composited at that alpha to match what will land on the layer.
        var preview = _vm.ActiveToolInstance.PreviewBitmap;
        if (preview is not null)
        {
            samplePaint.Color = SKColors.White.WithAlpha(_vm.ActiveToolInstance.PreviewAlpha);
            canvas.DrawBitmap(preview, 0, 0, samplePaint);
        }

        samplePaint.Dispose();

        // Floating pickup on top.
        if (_vm.Document.FloatingPickup is { } fp)
            Document.DrawPickup(canvas, fp);
    }

    // ───────── Overlay (selection / handles) ─────────
    private void DrawOverlay()
    {
        if (_vm is null) { Overlay.Children.Clear(); return; }
        Overlay.Children.Clear();
        double s = _vm.Zoom;
        bool busy = _vm.ToolContext.IsDrawing;

        // Render rect selection with marching ants.
        if (_vm.Document.Selection is RectSelection rs)
        {
            var r = MakeMarchingAntsRect(rs.Rect.Width * s, rs.Rect.Height * s,
                Color.FromRgb(0x5B, 0x8D, 0xEF));
            Canvas.SetLeft(r, rs.Rect.Left * s);
            Canvas.SetTop(r,  rs.Rect.Top  * s);
            Overlay.Children.Add(r);
        }
        // Render polygon selection (quad) with marching ants.
        else if (_vm.Document.Selection is PolygonSelection ps)
        {
            var poly = new Polygon
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x9D, 0x5B, 0xEF)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false,
                Points = new PointCollection(ps.Corners
                    .Select(c => new System.Windows.Point(c.X * s, c.Y * s))),
            };
            AnimateMarchingAnts(poly);
            Overlay.Children.Add(poly);
            // Corner dots: purely a hint that the corners are draggable. QuadTool does the
            // hit-testing itself on the Skia element, so these must not swallow clicks.
            foreach (var c in ps.Corners) AddQuadCornerDot(c, s);
        }

        // Render floating pickup bbox + handles.
        if (_vm.Document.FloatingPickup is { } fp)
        {
            var bbox = fp.CurrentBBox;
            var frame = new Rectangle
            {
                Width  = bbox.Width  * s,
                Height = bbox.Height * s,
                Stroke = new SolidColorBrush(Color.FromRgb(0xEF, 0xC8, 0x5B)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 6, 4 },
                IsHitTestVisible = false,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            };
            if (fp.Rotation != 0)
                frame.RenderTransform = new RotateTransform(fp.Rotation * 180 / Math.PI);
            Canvas.SetLeft(frame, bbox.Left * s);
            Canvas.SetTop(frame,  bbox.Top  * s);
            AnimateMarchingAnts(frame);
            Overlay.Children.Add(frame);

            // 8 resize handles + 1 rotate handle. Hidden during active drawing (antipattern #2).
            if (!busy)
            {
                AddResizeHandle(fp, ResizeHandle.NW, s, Cursors.SizeNWSE);
                AddResizeHandle(fp, ResizeHandle.N,  s, Cursors.SizeNS);
                AddResizeHandle(fp, ResizeHandle.NE, s, Cursors.SizeNESW);
                AddResizeHandle(fp, ResizeHandle.E,  s, Cursors.SizeWE);
                AddResizeHandle(fp, ResizeHandle.SE, s, Cursors.SizeNWSE);
                AddResizeHandle(fp, ResizeHandle.S,  s, Cursors.SizeNS);
                AddResizeHandle(fp, ResizeHandle.SW, s, Cursors.SizeNESW);
                AddResizeHandle(fp, ResizeHandle.W,  s, Cursors.SizeWE);
                AddRotateHandle(fp, s);
            }

            if (fp.Quad is { } quad)
                foreach (var c in quad) AddQuadCornerDot(c, s);
        }
    }

    /// <summary>Small marker on a quad corner. Not hit-testable: the tool owns corner drags.</summary>
    private void AddQuadCornerDot(SKPoint corner, double zoom)
    {
        const double size = 9;
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size,
            Fill = new SolidColorBrush(Color.FromRgb(0x9D, 0x5B, 0xEF)),
            Stroke = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF8)),
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, corner.X * zoom - size / 2);
        Canvas.SetTop(dot,  corner.Y * zoom - size / 2);
        Overlay.Children.Add(dot);
    }

    /// <summary>Build a Rectangle with running marching-ants stroke animation.</summary>
    private Rectangle MakeMarchingAntsRect(double w, double h, Color color)
    {
        var r = new Rectangle
        {
            Width = w, Height = h,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            IsHitTestVisible = false,
            Fill = Brushes.Transparent,
        };
        AnimateMarchingAnts(r);
        return r;
    }

    /// <summary>Animate StrokeDashOffset → continuous marching motion on the compositor thread.</summary>
    private static void AnimateMarchingAnts(Shape shape)
    {
        var anim = new DoubleAnimation
        {
            From = 8, To = 0,
            Duration = new Duration(TimeSpan.FromSeconds(0.6)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        // Freeze for perf (animations run on the WPF compositor thread).
        anim.Freeze();
        shape.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
    }

    private void AddResizeHandle(FloatingPickup fp, ResizeHandle handle, double zoom, Cursor cursor)
    {
        const double size = 12;
        var localPos = GeometryMath.LocalHandlePosition(fp.X, fp.Y, fp.Width, fp.Height, handle);
        var worldPos = fp.Rotation == 0
            ? localPos
            : GeometryMath.Rotate(localPos, fp.Center, fp.Rotation);

        var r = new Rectangle
        {
            Width = size, Height = size,
            Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF8)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)),
            StrokeThickness = 1.5,
            Cursor = cursor,
            RadiusX = 2, RadiusY = 2,
            Tag = handle,
        };
        Canvas.SetLeft(r, worldPos.X * zoom - size / 2);
        Canvas.SetTop(r,  worldPos.Y * zoom - size / 2);
        r.MouseLeftButtonDown += OnHandleMouseDown;
        Overlay.Children.Add(r);
    }

    private void AddRotateHandle(FloatingPickup fp, double zoom)
    {
        const double size = 14;
        const double offset = 28; // pixels above the top edge (in unrotated local coords)
        var localPos = new SKPoint(fp.X + fp.Width / 2f, fp.Y - (float)offset);
        var worldPos = fp.Rotation == 0
            ? localPos
            : GeometryMath.Rotate(localPos, fp.Center, fp.Rotation);

        var rot = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size,
            Fill = new SolidColorBrush(Color.FromRgb(0x9D, 0x5B, 0xEF)),
            Stroke = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF8)),
            StrokeThickness = 2,
            Cursor = Cursors.Hand,
            Tag = "rotate",
        };
        Canvas.SetLeft(rot, worldPos.X * zoom - size / 2);
        Canvas.SetTop(rot,  worldPos.Y * zoom - size / 2);
        rot.MouseLeftButtonDown += OnHandleMouseDown;
        Overlay.Children.Add(rot);

        // Small connecting line from the handle down to the bbox top — visual cue.
        var topPos = fp.Rotation == 0
            ? new SKPoint(fp.X + fp.Width / 2f, fp.Y)
            : GeometryMath.Rotate(new SKPoint(fp.X + fp.Width / 2f, fp.Y), fp.Center, fp.Rotation);
        var line = new Line
        {
            X1 = topPos.X * zoom, Y1 = topPos.Y * zoom,
            X2 = worldPos.X * zoom, Y2 = worldPos.Y * zoom,
            Stroke = new SolidColorBrush(Color.FromRgb(0x9D, 0x5B, 0xEF)),
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            Opacity = 0.7,
        };
        Overlay.Children.Add(line);
    }

    // ───────── Handle drag handlers ─────────
    private object? _draggingHandle;
    private SKPoint _dragStartMouseDoc;
    private float _rotationAtDragStart;

    private void OnHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement el) return;
        _draggingHandle = el.Tag;
        if (_vm.Document.FloatingPickup is { } fp)
        {
            PickupOps.EnsureLazyErase(_vm.Document, fp);
        }
        var p = e.GetPosition(Overlay);
        _dragStartMouseDoc = new SKPoint((float)(p.X / _vm.Zoom), (float)(p.Y / _vm.Zoom));
        _rotationAtDragStart = _vm.Document.FloatingPickup?.Rotation ?? 0;
        _vm.ToolContext.IsDrawing = true;
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm is null || _draggingHandle is null) return;
        if (_vm.Document.FloatingPickup is not { } fp) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(Overlay);
        var docPos = new SKPoint((float)(p.X / _vm.Zoom), (float)(p.Y / _vm.Zoom));

        switch (_draggingHandle)
        {
            case ResizeHandle rh:
                fp.ApplyResize(rh, docPos);
                break;
            case string s when s == "rotate":
                var delta = GeometryMath.AngleBetween(fp.Center, _dragStartMouseDoc, docPos);
                fp.SetRotation(_rotationAtDragStart + delta);
                break;
        }
        Refresh();
        e.Handled = true;
    }

    private void OnHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingHandle is null) return;
        Overlay.ReleaseMouseCapture();
        EndHandleDrag();
        e.Handled = true;
    }

    /// <summary>
    /// Свернуть жест. Зовётся и по отпусканию кнопки, и по потере захвата - окно могло
    /// уйти из фокуса, а бросить IsDrawing включённым значит спрятать ручки навсегда.
    /// </summary>
    private void EndHandleDrag()
    {
        if (_draggingHandle is null) return;
        _draggingHandle = null;
        if (_vm is not null) _vm.ToolContext.IsDrawing = false;
        Refresh();
    }

    // ───────── Pointer routing ─────────
    private bool _captured;

    /// <summary>
    /// Drag anchor for the Hand tool, in ScrollViewer (viewport) coordinates.
    /// Deliberately not document coordinates: scrolling moves the canvas under the cursor,
    /// so a document-space delta partly undoes the scroll that produced it and the view
    /// jitters. Viewport coordinates are unaffected by the scroll offset.
    /// </summary>
    private System.Windows.Point? _panAnchor;

    /// <summary>Последняя позиция курсора в координатах документа: ею завершается жест, потерявший захват.</summary>
    private SKPoint _lastDocPos;
    private SKPoint ToDoc(System.Windows.Point p)
    {
        var s = _vm?.Zoom ?? 1;
        return new SKPoint((float)(p.X / s), (float)(p.Y / s));
    }

    private void OnSkiaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        // MouseDown fires for every button. Without this a right-click would run the
        // active tool — with a brush selected that means a stray dot plus a history entry.
        if (e.ChangedButton != MouseButton.Left) return;
        Skia.CaptureMouse();
        _captured = true;
        if (_vm.ActiveTool == ToolKind.Hand) _panAnchor = e.GetPosition(Scroll);
        var pos = ToDoc(e.GetPosition(Skia));
        _lastDocPos = pos;
        _vm.ActiveToolInstance.OnPointerDown(pos, _vm.ToolContext);
        QueueRender();
    }

    // Status-bar updates throttled to ~30 Hz (per spec §"Статусбар").
    private DateTime _lastStatusUpdate = DateTime.MinValue;

    private void OnSkiaMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm is null) return;
        var pos = ToDoc(e.GetPosition(Skia));
        _lastDocPos = pos;
        var now = DateTime.UtcNow;
        if ((now - _lastStatusUpdate).TotalMilliseconds > 33)
        {
            _lastStatusUpdate = now;
            if (pos.X < 0 || pos.Y < 0 || pos.X >= _vm.Document.CanvasWidth || pos.Y >= _vm.Document.CanvasHeight)
                PixelPositionChanged?.Invoke(null);
            else
            {
                PixelPositionChanged?.Invoke(pos);
                PixelColorChanged?.Invoke(_vm.Document.SampleComposite((int)pos.X, (int)pos.Y));
            }
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (_panAnchor is { } anchor)
            {
                var cursor = e.GetPosition(Scroll);
                Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset - (cursor.X - anchor.X));
                Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset - (cursor.Y - anchor.Y));
                _panAnchor = cursor;
            }
            _vm.ActiveToolInstance.OnPointerMove(pos, _vm.ToolContext);
            QueueRender();
        }
        Cursor = _vm.ActiveToolInstance.GetCursor(pos) ?? Cursors.Arrow;
    }

    private void OnSkiaMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        if (e.ChangedButton != MouseButton.Left) return;
        _panAnchor = null;
        // Сначала снимаем флаг, потом отпускаем захват: ReleaseMouseCapture синхронно
        // стреляет LostMouseCapture, и обработчик потери завершил бы жест вторым разом.
        if (_captured) { _captured = false; Skia.ReleaseMouseCapture(); }
        var pos = ToDoc(e.GetPosition(Skia));
        _lastDocPos = pos;
        _vm.ActiveToolInstance.OnPointerUp(pos, _vm.ToolContext);
        QueueRender();
    }

    /// <summary>
    /// Завершить жест на холсте, когда захват ушёл сам. Инструмент дорисовывает по
    /// последней известной позиции: штрих уже сделан пользователем, терять его незачем.
    /// </summary>
    private void EndCanvasGesture()
    {
        if (!_captured || _vm is null) return;
        _captured = false;
        _panAnchor = null;
        _vm.ActiveToolInstance.OnPointerUp(_lastDocPos, _vm.ToolContext);
        QueueRender();
    }

    // ───────── Ctrl+wheel zoom centred on the cursor ─────────
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        // Document-space coords under the cursor BEFORE zoom (these stay constant).
        var inSkia = e.GetPosition(Skia);
        var oldZoom = _vm.Zoom;
        var docX = inSkia.X / oldZoom;
        var docY = inSkia.Y / oldZoom;

        // Bump zoom (discrete steps per spec).
        var step = (float)oldZoom;
        var newZoom = (double)GeometryMath.NextZoomStep(step, e.Delta > 0);
        if (Math.Abs(newZoom - oldZoom) < 1e-6) { e.Handled = true; return; }
        _vm.Zoom = newZoom;

        // Cursor position relative to ScrollViewer viewport (does not change with zoom).
        var inScroll = e.GetPosition(Scroll);

        // After WPF re-layouts (Skia.Width changed via VM PropertyChanged → UpdateLayout2),
        // scroll so that (docX,docY) is at the same viewport pixel as (inScroll.X,inScroll.Y).
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // ContentRoot embeds Skia with Margin=40 on all sides.
            var contentX = docX * newZoom + Skia.Margin.Left;
            var contentY = docY * newZoom + Skia.Margin.Top;
            Scroll.ScrollToHorizontalOffset(contentX - inScroll.X);
            Scroll.ScrollToVerticalOffset(contentY - inScroll.Y);
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        e.Handled = true;
    }
}
