namespace PaintPro.Models;

/// <summary>All tool identifiers, also used as the value of the hotkey-bound enum.</summary>
public enum ToolKind
{
    Pencil,
    Brush,
    Marker,
    Eraser,
    Fill,
    Picker,
    Text,
    Line,
    Rect,
    Ellipse,
    Triangle,
    Star,
    Arrow,
    Heart,
    Select,
    Quad,
    Crop,
    Hand,
}

/// <summary>Convenience helpers around <see cref="ToolKind"/>.</summary>
public static class ToolKindExtensions
{
    /// <summary>True for tools that produce a freehand or shape stroke (not selection/navigation).</summary>
    public static bool IsDrawingTool(this ToolKind k) => k switch
    {
        ToolKind.Pencil or ToolKind.Brush or ToolKind.Marker or ToolKind.Eraser
            or ToolKind.Fill or ToolKind.Line or ToolKind.Rect or ToolKind.Ellipse
            or ToolKind.Triangle or ToolKind.Star or ToolKind.Arrow or ToolKind.Heart => true,
        _ => false,
    };

    /// <summary>True for shape tools (Line, Rect, Ellipse, Triangle, Star, Arrow, Heart).</summary>
    public static bool IsShapeTool(this ToolKind k) => k is
        ToolKind.Line or ToolKind.Rect or ToolKind.Ellipse
        or ToolKind.Triangle or ToolKind.Star or ToolKind.Arrow or ToolKind.Heart;
}
