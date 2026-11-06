namespace TGDeblocker.Mobile.Rendering;

// Shared layout contract; values are device-independent units.
public static class GlassDockMetrics
{
    public const double Height = 60;
    public const double BottomInset = 8;
    public const double HorizontalInset = 24;
    public const double MaximumWidth = 360;
    public const double SelectedInsetX = 8;
    public const double SelectedInsetY = 4;
    public static double WidthFor(double viewWidth) => Math.Min(Math.Max(0, viewWidth - 2 * HorizontalInset), MaximumWidth);
}
