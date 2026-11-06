using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TGDeblocker.App.Rendering;

/// <summary>Optics over an explicit, live XAML visual. Input and foreground stay in XAML.</summary>
public sealed class LiveGlassLayer : IDisposable
{
    private readonly FrameworkElement source;
    private readonly FrameworkElement host;
    private readonly Visual sourceVisual;
    private readonly CompositionVisualSurface surface;
    private readonly ContainerVisual root;
    private readonly OpticalShape navigation, selection, thumb;
    private readonly CompositionEffectFactory? blurFactory;
    private readonly string? blurError;
    private readonly UISettings systemColors = new();
    private GlassFrameState frame;
    private Vector2 sourceSize;
    private bool sampling, failed, disposed;

    private LiveGlassLayer(FrameworkElement source, FrameworkElement host)
    {
        this.source = source;
        this.host = host;
        sourceVisual = ElementCompositionPreview.GetElementVisual(source);
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        surface = compositor.CreateVisualSurface();
        root = compositor.CreateContainerVisual();
        root.RelativeSizeAdjustment = Vector2.One;
        try
        {
            using var blur = new GaussianBlurEffect
            {
                Name = "Blur",
                Source = new CompositionEffectSourceParameter("Content"),
                BlurAmount = 2.3f,
                BorderMode = EffectBorderMode.Hard
            };
            // LayerVisual supplies its flattened children for the effect's input.
            blurFactory = compositor.CreateEffectFactory(blur, ["Blur.BlurAmount"]);
        }
        catch (Exception ex)
        {
            blurError = ex.Message;
        }

        thumb = new(root, surface, blurFactory);
        navigation = new(root, surface, blurFactory);
        selection = new(root, surface, blurFactory);
        ElementCompositionPreview.SetElementChildVisual(host, root);
        source.SizeChanged += OnSourceSizeChanged;
    }

    /// <summary>The loaded host must be outside the sampled subtree, in the same XAML root.</summary>
    public static LiveGlassLayer Attach(FrameworkElement source, FrameworkElement host) => new(source, host);

    public string? LastRenderError { get; private set; }
    public bool IsAvailable { get; private set; } = true;
    public bool SamplingActive => sampling && !failed && !disposed;
    public Size SourceSize => new(sourceSize.X, sourceSize.Y);
    public int PatchCount => navigation.PatchCount + selection.PatchCount + thumb.PatchCount;

    public void Update(GlassFrameState frame)
    {
        if (disposed || !IsAvailable) return;
        this.frame = frame;
        if (failed)
        {
            RenderFallback(frame);
            return;
        }

        bool transparent = frame.TransparencyEnabled && !frame.HighContrast;
        var size = new Vector2((float)source.ActualWidth, (float)source.ActualHeight);
        bool canSample = transparent && size.X > 0 && size.Y > 0;
        try
        {
            SetSource(canSample, size);
            var origin = canSample ? host.TransformToVisual(source).TransformPoint(default) : default;
            Render(frame, new Vector2((float)origin.X, (float)origin.Y), canSample);
            LastRenderError = frame.BlurEnabled && canSample ? blurError : null;
        }
        catch (Exception ex)
        {
            failed = true;
            LastRenderError = ex.Message;
            RenderFallback(frame);
        }
    }

    private void RenderFallback(GlassFrameState frame)
    {
        try
        {
            SetSource(false, default);
            Render(frame, Vector2.Zero, false);
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            LastRenderError += "\nOpaque fallback: " + ex.Message;
            try { ElementCompositionPreview.SetElementChildVisual(host, null); }
            catch (Exception detachError) { LastRenderError += "\nDetach: " + detachError.Message; }
        }
    }

    private void OnSourceSizeChanged(object sender, SizeChangedEventArgs args) => Update(frame);

    private void SetSource(bool enable, Vector2 size)
    {
        if (enable && sourceSize != size)
        {
            surface.SourceSize = size;
            sourceSize = size;
        }
        if (sampling == enable) return;
        surface.SourceVisual = enable ? sourceVisual : null;
        sampling = enable;
    }

    private void Render(GlassFrameState frame, Vector2 sourceOrigin, bool transparent)
    {
        bool dark = frame.IsDark;
        float density = (float)Math.Clamp(frame.Density, 0, 1);
        float lift = (float)Math.Clamp(frame.SelectionLift, 0, 1.05);
        float activity = (float)Math.Clamp(frame.SliderActivity, 0, 1);
        bool lens = transparent && frame.LensEnabled;
        bool blur = transparent && frame.BlurEnabled;
        var background = frame.HighContrast ? systemColors.GetColorValue(UIColorType.Background) : default;
        var foreground = frame.HighContrast ? systemColors.GetColorValue(UIColorType.Foreground) : default;
        var accent = frame.HighContrast ? systemColors.GetColorValue(UIColorType.Accent) : default;

        var light = dark ? Color.FromArgb(168, 210, 236, 255) : Color.FromArgb(240, 255, 255, 255);
        var edge = dark ? Color.FromArgb(100, 106, 163, 202) : Color.FromArgb(134, 68, 118, 152);
        var navTint = transparent
            ? Tint(0.10f + density * .46f, dark)
            : dark ? Color.FromArgb(255, 35, 46, 60) : Color.FromArgb(255, 250, 253, 255);
        var pickTint = transparent
            ? Tint(.16f + density * .42f, dark)
            : dark ? Color.FromArgb(255, 49, 66, 83) : Color.FromArgb(255, 222, 240, 250);
        var thumbTint = transparent && activity > 0
            ? Tint(1 - activity * (.87f - density * .23f), dark)
            : Color.FromArgb(255, 255, 255, 255);

        navigation.Render(frame.Navigation, 26, sourceOrigin, transparent, lens ? 15 : 0, blur ? 2.3f : 0,
            frame.HighContrast ? background : navTint, frame.HighContrast ? foreground : light,
            frame.HighContrast ? foreground : edge);
        selection.Render(frame.Selection, 24 + 14 * lift, sourceOrigin, transparent, lens ? 7 + 13 * lift : 0,
            blur ? 1.2f : 0, frame.HighContrast ? background : pickTint, frame.HighContrast ? accent : light,
            frame.HighContrast ? accent : edge);
        thumb.Render(frame.SliderThumb, float.MaxValue, sourceOrigin, transparent && activity > 0,
            lens ? activity * 18 : 0, blur ? activity * 1.35f : 0, frame.HighContrast ? background : thumbTint,
            frame.HighContrast ? accent : light, frame.HighContrast ? accent : Color.FromArgb(160, 84, 132, 174));
    }

    private static Color Tint(float opacity, bool dark) => Color.FromArgb((byte)Math.Round(255 * opacity),
        dark ? (byte)29 : (byte)255, dark ? (byte)40 : (byte)255, dark ? (byte)53 : (byte)255);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        IsAvailable = false;
        source.SizeChanged -= OnSourceSizeChanged;
        ElementCompositionPreview.SetElementChildVisual(host, null);
        surface.SourceVisual = null;
        root.Children.RemoveAll();
        selection.Dispose();
        navigation.Dispose();
        thumb.Dispose();
        blurFactory?.Dispose();
        root.Dispose();
        surface.Dispose();
    }

    private sealed class OpticalShape : IDisposable
    {
        private const int BandCount = 3;
        private const int QuarterSegments = 4;
        private const float Bezel = .64f; // fraction of the corner radius
        private static readonly float[] Profile = Enumerable.Range(0, BandCount + 1)
            .Select(i => (float)GlassBackdrop.Deflection((double)i / BandCount)).ToArray();

        private readonly Compositor compositor;
        private readonly CompositionVisualSurface surface;
        private readonly List<IDisposable> resources = [];
        private readonly ContainerVisual shape;
        private readonly RectangleClip clip;
        private readonly LayerVisual optics;
        private readonly SpriteVisual aligned;
        private readonly CompositionSurfaceBrush alignedBrush;
        private readonly CompositionEffectBrush? blur;
        private readonly SpriteVisual tint;
        private readonly CompositionColorBrush tintBrush;
        private readonly ShapeVisual light;
        private readonly CompositionRoundedRectangleGeometry lightGeometry;
        private readonly CompositionColorGradientStop lightTop, lightBottom;
        private readonly List<(int Side, int Band, Patch Patch)> flat = [];
        private readonly List<(Vector2 Corner, Matrix3x2 Inverse, Matrix3x2 Basis, Matrix3x2 Bend, Patch Patch)> curves = [];
        private (Rect Rect, float Radius, Vector2 Origin, bool Sample, float Bend, float Blur,
            Color Tint, Color Light, Color Edge) previous;

        public int PatchCount => flat.Count + curves.Count;

        public OpticalShape(ContainerVisual root, CompositionVisualSurface surface, CompositionEffectFactory? blurFactory)
        {
            compositor = root.Compositor;
            this.surface = surface;
            shape = Own(compositor.CreateContainerVisual());
            shape.IsVisible = false;
            root.Children.InsertAtTop(shape);
            clip = Own(compositor.CreateRectangleClip());
            shape.Clip = clip;

            optics = Own(compositor.CreateLayerVisual());
            optics.IsVisible = false;
            shape.Children.InsertAtBottom(optics);
            blur = blurFactory is null ? null : Own(blurFactory.CreateBrush());
            alignedBrush = MakeSurfaceBrush();
            aligned = Own(compositor.CreateSpriteVisual());
            aligned.Brush = alignedBrush;
            optics.Children.InsertAtBottom(aligned);

            tint = Own(compositor.CreateSpriteVisual());
            tint.Brush = tintBrush = Own(compositor.CreateColorBrush());
            shape.Children.InsertAtTop(tint);

            light = Own(compositor.CreateShapeVisual());
            shape.Children.InsertAtTop(light);
            lightGeometry = Own(compositor.CreateRoundedRectangleGeometry());
            var stroke = Own(compositor.CreateSpriteShape(lightGeometry));
            stroke.StrokeThickness = 1.5f;
            var gradient = Own(compositor.CreateLinearGradientBrush());
            gradient.StartPoint = Vector2.Zero;
            gradient.EndPoint = new Vector2(.28f, 1);
            gradient.ColorStops.Add(lightTop = Own(compositor.CreateColorGradientStop(0, default)));
            gradient.ColorStops.Add(lightBottom = Own(compositor.CreateColorGradientStop(1, default)));
            stroke.StrokeBrush = gradient;
            light.Shapes.Add(stroke);
        }

        public void Render(Rect rect, float radius, Vector2 origin, bool sample, float bend, float blurAmount,
            Color tintColor, Color top, Color bottom)
        {
            var state = (rect, radius, origin, sample, bend, blurAmount, tintColor, top, bottom);
            if (state == previous) return;
            var old = previous;
            previous = state;
            shape.IsVisible = !rect.IsEmpty && rect.Width > 1 && rect.Height > 1;
            if (!shape.IsVisible) return;

            var size = new Vector2((float)rect.Width, (float)rect.Height);
            var offset = new Vector2((float)rect.X, (float)rect.Y);
            radius = Math.Min(radius, Math.Min(size.X, size.Y) / 2);
            shape.Offset = new Vector3(offset, 0);
            shape.Size = optics.Size = aligned.Size = tint.Size = light.Size = size;
            clip.Right = size.X;
            clip.Bottom = size.Y;
            clip.TopLeftRadius = clip.TopRightRadius = clip.BottomRightRadius = clip.BottomLeftRadius = new Vector2(radius);
            tintBrush.Color = tintColor;
            lightGeometry.Offset = new Vector2(.8f);
            lightGeometry.Size = Vector2.Max(Vector2.Zero, size - new Vector2(1.6f));
            lightGeometry.CornerRadius = new Vector2(Math.Max(0, radius - .8f));
            lightTop.Color = top;
            lightBottom.Color = bottom;

            if (sample != old.Sample) optics.IsVisible = sample;
            if (!sample) return;
            var sourcePosition = offset + origin;
            if (blur is not null && (!old.Sample || blurAmount != old.Blur))
            {
                optics.Effect = blurAmount > 0 ? blur : null;
                if (blurAmount > 0) blur.Properties.InsertScalar("Blur.BlurAmount", blurAmount);
            }
            if (old.Sample && rect == old.Rect && radius == Math.Min(old.Radius, Math.Min(size.X, size.Y) / 2)
                && origin == old.Origin && bend == old.Bend) return;
            alignedBrush.Offset = -sourcePosition;

            bool refract = bend > .02f;
            if (refract && flat.Count == 0) BuildBezel();
            if (refract) bend = StabilizeBend(bend, radius);
            // Adjacent pieces agree at their vertices. The rounded clip preserves the exact silhouette.
            foreach (var (side, band, patch) in flat)
            {
                patch.Visual.IsVisible = refract;
                if (refract) PlaceFlat(patch, side, band, size, sourcePosition, radius, bend);
            }
            foreach (var (corner, inverse, basis, displacement, patch) in curves)
            {
                patch.Visual.IsVisible = refract;
                if (!refract) continue;
                var cornerOffset = corner * (size - new Vector2(radius));
                patch.Visual.Offset = new Vector3(cornerOffset, 0);
                patch.Visual.Scale = new Vector3(radius, radius, 1);
                var target = basis + displacement * (bend / radius);
                var sampling = inverse * target * Matrix3x2.CreateScale(radius) * Matrix3x2.CreateTranslation(sourcePosition + cornerOffset);
                Matrix3x2.Invert(sampling, out var brushTransform);
                patch.Brush.TransformMatrix = brushTransform;
            }
        }

        private static float StabilizeBend(float bend, float radius)
        {
            bend = Math.Min(bend, .9f * radius);
            float bandSize = radius * Bezel / BandCount;
            for (int band = 0; band < BandCount; band++)
            {
                // Near a fold, two depth samples coincide. Keep an invertible mapping on both sides
                // of the fold and use the same bend on the straight sides and their adjoining corners.
                float fold = bandSize / (Profile[band] - Profile[band + 1]);
                float difference = bend - fold;
                float clearance = .015f * fold;
                if (Math.Abs(difference) < clearance) bend = fold + MathF.CopySign(clearance, difference);
            }
            return bend;
        }

        private void PlaceFlat(Patch patch, int side, int band, Vector2 size, Vector2 sourcePosition, float radius, float bend)
        {
            bool vertical = side >= 2;
            bool reverse = (side & 1) != 0;
            float bandSize = radius * Bezel / BandCount;
            float longSize = (vertical ? size.Y : size.X) - 2 * radius;
            patch.Visual.IsVisible = longSize > .01f;
            if (!patch.Visual.IsVisible) return;
            float normalOffset = reverse ? (vertical ? size.X : size.Y) - (band + 1) * bandSize : band * bandSize;
            var start = vertical ? new Vector2(normalOffset, radius) : new Vector2(radius, normalOffset);
            patch.Visual.Offset = new Vector3(start, 0);
            patch.Visual.Size = vertical ? new Vector2(bandSize, longSize) : new Vector2(longSize, bandSize);

            float firstShift = reverse ? -bend * Profile[band + 1] : bend * Profile[band];
            float normalScale = 1 + bend * (Profile[band + 1] - Profile[band]) / bandSize;
            var scale = vertical ? new Vector2(normalScale, 1) : new Vector2(1, normalScale);
            var shift = vertical ? new Vector2(firstShift, 0) : new Vector2(0, firstShift);
            var sampling = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(sourcePosition + start + shift);
            Matrix3x2.Invert(sampling, out var brushTransform);
            patch.Brush.TransformMatrix = brushTransform;
        }

        private void BuildBezel()
        {
            for (int band = 0; band < BandCount; band++)
            {
                for (int side = 0; side < 4; side++) flat.Add((side, band, MakePatch()));
                for (int corner = 0; corner < 4; corner++)
                {
                    var anchor = new Vector2(corner % 2, corner / 2);
                    for (int segment = 0; segment < QuarterSegments; segment++)
                    {
                        var a = Vertex(anchor, segment, band);
                        var b = Vertex(anchor, segment + 1, band);
                        var c = Vertex(anchor, segment, band + 1);
                        var d = Vertex(anchor, segment + 1, band + 1);
                        AddTriangle(anchor, a, b, c);
                        AddTriangle(anchor, b, d, c);
                    }
                }
            }
        }

        private static (Vector2 Position, Vector2 Bend) Vertex(Vector2 corner, int segment, int band)
        {
            float angle = segment * MathF.PI / (2 * QuarterSegments);
            var normal = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (2 * corner - Vector2.One);
            return (Vector2.One - corner + normal * (1 - Bezel * band / BandCount), -normal * Profile[band]);
        }

        private void AddTriangle(Vector2 corner, (Vector2 Position, Vector2 Bend) a,
            (Vector2 Position, Vector2 Bend) b, (Vector2 Position, Vector2 Bend) c)
        {
            var patch = MakePatch();
            patch.Visual.Size = Vector2.One;
            var polygon = Own(CanvasGeometry.CreatePolygon(CanvasDevice.GetSharedDevice(), [a.Position, b.Position, c.Position]));
            var path = Own(compositor.CreatePathGeometry(new CompositionPath(polygon)));
            patch.Visual.Clip = Own(compositor.CreateGeometricClip(path));
            var basis = TriangleMatrix(a.Position, b.Position, c.Position);
            Matrix3x2.Invert(basis, out var inverse);
            curves.Add((corner, inverse, basis, TriangleMatrix(a.Bend, b.Bend, c.Bend), patch));
        }

        private static Matrix3x2 TriangleMatrix(Vector2 a, Vector2 b, Vector2 c) =>
            new(b.X - a.X, b.Y - a.Y, c.X - a.X, c.Y - a.Y, a.X, a.Y);

        private Patch MakePatch()
        {
            var visual = Own(compositor.CreateSpriteVisual());
            var brush = MakeSurfaceBrush();
            visual.Brush = brush;
            optics.Children.InsertAtTop(visual);
            return new(visual, brush);
        }

        private CompositionSurfaceBrush MakeSurfaceBrush()
        {
            var brush = Own(compositor.CreateSurfaceBrush(surface));
            brush.Stretch = CompositionStretch.None;
            brush.HorizontalAlignmentRatio = brush.VerticalAlignmentRatio = 0;
            brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            return brush;
        }

        private T Own<T>(T resource) where T : IDisposable
        {
            resources.Add(resource);
            return resource;
        }

        public void Dispose()
        {
            for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
        }

        private readonly record struct Patch(SpriteVisual Visual, CompositionSurfaceBrush Brush);
    }
}
