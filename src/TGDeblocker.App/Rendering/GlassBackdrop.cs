using System;
using System.Numerics;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TGDeblocker.App.Rendering;

/// <summary>Read-only motion values for an isolated diagnostic run.</summary>
public readonly record struct GlassMotionSnapshot(
    bool SelectionPressed, bool SliderPressed, bool Animate, bool TimerRunning,
    double Lift, double SliderLift, double SelectionLeft, double SelectionRight,
    double SelectionTargetLeft, double SelectionTargetRight, double SliderPosition,
    double SliderTarget, double SliderVelocity);

/// <summary>Current shape and material values in DIPs relative to the backdrop.</summary>
public readonly record struct GlassFrameState(
    Rect Navigation, Rect Selection, Rect SliderTrack, Rect SliderThumb, Rect SliderTargetThumb,
    double SelectionLift, double SliderLift, double SliderActivity, double SliderVelocity, double Density,
    bool IsDark, bool TransparencyEnabled, bool HighContrast, bool ReduceMotion, bool LensEnabled,
    bool BlurEnabled, bool LiveContentMode, double Width, double Height);

/// <summary>Draws only the application's own background and glass; input and text belong above it.</summary>
public sealed class GlassBackdrop : UserControl
{
    private readonly CanvasControl canvas = new();
    private readonly DispatcherQueueTimer motionTimer;
    private readonly Stopwatch motionClock = new();
    private Rect selectionTarget;
    private readonly GlassSpring left = new(.34, .84), right = new(.24, .8), lift = new(.2, .86);
    private readonly GlassSpring sliderPosition = new(.13, 1), sliderLift = new(.18, .88);
    private double selectionReleaseAt, sliderReleaseAt;
    private bool selectionPressed;
    private CanvasRenderTarget? sliderScene;
    private bool sliderSceneDirty = true;
    private bool reduceMotion, liveContentMode;
    private GlassFrameState? lastPublishedFrame;
    private CanvasRenderTarget? scene;
    private float scenePhase;
    private CanvasBitmap? navMap, selectionMap, sliderMap;
    private (int Width, int Height, int Radius) navMapSize, selectionMapSize, sliderMapSize;
    private Rect navigation, selection, sliderTrack, sliderThumb;
    private double density = 0.18;
    private bool transparencyEnabled = true, lensEnabled = true, blurEnabled = true, sliderPressed, highContrast, isDark;

    public GlassBackdrop()
    {
        motionTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        motionTimer.Interval = TimeSpan.FromMilliseconds(16);
        motionTimer.Tick += OnMotionTick;
        IsHitTestVisible = false;
        Content = canvas;
        canvas.CreateResources += (_, _) => { DisposeScene(); DisposeMaps(); };
        canvas.Draw += Draw;
        SizeChanged += (_, _) => { DisposeScene(); InvalidateFrame(); };
        Unloaded += (_, _) => { SnapMotion(); DisposeScene(); DisposeMaps(); };
    }

    /// <summary>Switches off legacy scene drawing while keeping motion and input state alive.</summary>
    public bool LiveContentMode
    {
        get => liveContentMode;
        set
        {
            if (liveContentMode == value) return;
            liveContentMode = value;
            canvas.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            if (value) { DisposeScene(); DisposeMaps(); }
            InvalidateFrame();
        }
    }

    /// <summary>Raised on the UI thread only when the effective frame changes.</summary>
    public event Action<GlassFrameState>? FrameChanged;
    public GlassFrameState CurrentFrame
    {
        get
        {
            double active = Animate ? Math.Clamp(sliderLift.Value, 0, 1) : 0;
            double smooth = Math.Clamp(active / .65, 0, 1); smooth = smooth * smooth * (3 - 2 * smooth);
            return new GlassFrameState(navigation, selection, sliderTrack, AnimatedSliderThumb(active), sliderThumb,
                Animate ? Math.Clamp(lift.Value, 0, 1.05) : 0, active, smooth, sliderPosition.Velocity, density,
                isDark, transparencyEnabled, highContrast, reduceMotion, lensEnabled, blurEnabled, liveContentMode,
                ActualWidth, ActualHeight);
        }
    }

    private void InvalidateFrame()
    {
        if (!liveContentMode) canvas.Invalidate();
        var frame = CurrentFrame;
        if (lastPublishedFrame is GlassFrameState previous && previous == frame) return;
        lastPublishedFrame = frame;
        FrameChanged?.Invoke(frame);
    }

    public double Density { get => density; set { density = Math.Clamp(double.IsFinite(value) ? value : 0.18, 0, 1); InvalidateFrame(); } }
    public bool TransparencyEnabled { get => transparencyEnabled; set { transparencyEnabled = value; if (!value) SnapMotion(); InvalidateFrame(); } }
    public bool IsDark { get => isDark; set { if (isDark == value) return; isDark = value; DisposeScene(); InvalidateFrame(); } }
    public bool HighContrast { get => highContrast; set { highContrast = value; if (value) SnapMotion(); InvalidateFrame(); } }
    public bool LensEnabled { get => lensEnabled; set { lensEnabled = value; InvalidateFrame(); } }
    public bool BlurEnabled { get => blurEnabled; set { blurEnabled = value; InvalidateFrame(); } }
    public bool ReduceMotion
    {
        get => reduceMotion;
        set
        {
            reduceMotion = value;
            if (value) SnapMotion();
            InvalidateFrame();
        }
    }
    public string? LastRenderError { get; private set; }
    public GlassMotionSnapshot MotionState => new(selectionPressed, sliderPressed, Animate, motionTimer.IsRunning,
        lift.Value, sliderLift.Value, left.Value, right.Value, left.Target, right.Target,
        sliderPosition.Value, sliderPosition.Target, sliderPosition.Velocity);

    private bool Animate => !reduceMotion && transparencyEnabled && !highContrast;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private void Kick()
    {
        if (!Animate) { SnapMotion(); InvalidateFrame(); return; }
        if (!motionTimer.IsRunning) { motionClock.Restart(); motionTimer.Start(); }
        InvalidateFrame();
    }

    private void SnapMotion()
    {
        motionTimer.Stop(); motionClock.Reset();
        left.Snap(selectionTarget.X); right.Snap(selectionTarget.X + selectionTarget.Width);
        lift.Snap(0); sliderLift.Snap(0);
        sliderPosition.Snap(sliderThumb.Width > 1 ? sliderThumb.X + sliderThumb.Width / 2 : 0);
        selection = selectionTarget; selectionPressed = sliderPressed = false;
        selectionReleaseAt = sliderReleaseAt = 0; sliderSceneDirty = true;
    }

    public void SetGeometry(Rect navigation, Rect selection)
    {
        bool resize = this.navigation.Width != navigation.Width || this.navigation.Height != navigation.Height;
        bool first = this.selection.Width <= 1 || selection.Width <= 1;
        bool changed = selection.X != selectionTarget.X || selection.Width != selectionTarget.Width;
        this.navigation = navigation;
        selectionTarget = selection;
        if (!Animate || resize || first)
        {
            left.Snap(selection.X); right.Snap(selection.X + selection.Width); lift.Snap(0);
            this.selection = selection;
        }
        else if (changed)
        {
            bool movingRight = selection.X + selection.Width / 2 >= (left.Value + right.Value) / 2;
            left.Response = movingRight ? .34 : .24; left.Damping = movingRight ? .84 : .8;
            right.Response = movingRight ? .24 : .34; right.Damping = movingRight ? .8 : .84;
            left.Target = selection.X; right.Target = selection.X + selection.Width;
            if (!selectionPressed) { lift.Target = .7; lift.Response = .2; selectionReleaseAt = Now + .18; }
            Kick();
        }
        InvalidateFrame();
    }

    public void SetSelectionPressed(bool pressed, bool directTouch = false)
    {
        selectionPressed = pressed; selectionReleaseAt = 0;
        lift.Response = pressed ? .2 : .34; lift.Damping = pressed ? .86 : .88;
        lift.Target = Animate && pressed ? (directTouch ? 1 : .7) : 0;
        Kick();
    }

    public void SetSliderGeometry(Rect track, Rect thumb, bool pressed, bool directTouch = false)
    {
        if (track.Width <= 1 || track.Height <= 1 || thumb.Width <= 1 || thumb.Height <= 1 ||
            !double.IsFinite(track.X) || !double.IsFinite(track.Y) || !double.IsFinite(thumb.X) || !double.IsFinite(thumb.Y))
        {
            bool changed = sliderThumb.Width > 1 || sliderPressed || sliderLift.Value != 0;
            sliderTrack = sliderThumb = new Rect(0, 0, 0, 0);
            sliderPressed = false; sliderReleaseAt = 0; sliderLift.Snap(0); sliderPosition.Snap(0);
            if (changed) { sliderSceneDirty = true; InvalidateFrame(); }
            return;
        }
        bool first = sliderThumb.Width <= 1 || sliderTrack.Width != track.Width;
        bool wasPressed = sliderPressed;
        bool geometryChanged = !sliderTrack.Equals(track) || !sliderThumb.Equals(thumb);
        sliderTrack = track; sliderThumb = thumb; sliderPressed = pressed;
        if (geometryChanged) sliderSceneDirty = true;
        double x = thumb.X + thumb.Width / 2;
        if (!Animate || first) sliderPosition.Snap(x); else sliderPosition.Target = x;
        if (pressed != wasPressed)
        {
            sliderReleaseAt = 0; sliderLift.Response = pressed ? .18 : .34; sliderLift.Damping = .88;
            if (Animate && !pressed && wasPressed && sliderLift.Value < .1)
            {
                // A click or quick drag can end before the first frame. Give it a short,
                // interruptible optical response instead of cancelling the lift unseen.
                sliderLift.Response = .16; sliderLift.Target = .35;
                sliderReleaseAt = Now + .14;
            }
            else sliderLift.Target = Animate && pressed ? (directTouch ? 1 : .7) : 0;
        }
        if (Animate && (pressed != wasPressed || Math.Abs(sliderPosition.Value - x) > .01)) Kick();
        else if (geometryChanged) InvalidateFrame();
    }
    public void PulseSliderKeyboard()
    {
        if (!Animate || sliderPressed) return;
        sliderLift.Response = .18; sliderLift.Damping = .88; sliderLift.Target = .35;
        sliderReleaseAt = Now + .14; Kick();
    }

    private void OnMotionTick(DispatcherQueueTimer sender, object args)
    {
        if (!Animate) { SnapMotion(); InvalidateFrame(); return; }
        double dt = Math.Clamp(motionClock.Elapsed.TotalSeconds, 0, .05); motionClock.Restart();
        if (selectionReleaseAt > 0 && Now >= selectionReleaseAt && !selectionPressed)
        { selectionReleaseAt = 0; lift.Target = 0; lift.Response = .34; lift.Damping = .88; }
        if (sliderReleaseAt > 0 && Now >= sliderReleaseAt && !sliderPressed)
        { sliderReleaseAt = 0; sliderLift.Target = 0; sliderLift.Response = .34; sliderLift.Damping = .88; }
        bool active = left.Step(dt) | right.Step(dt) | lift.Step(dt, .002, .02)
            | sliderPosition.Step(dt) | sliderLift.Step(dt, .002, .02);
        selection = SelectionShape(selectionTarget, left.Value, right.Value, lift.Value);
        if (!active && selectionReleaseAt == 0 && sliderReleaseAt == 0) { sender.Stop(); motionClock.Reset(); }
        InvalidateFrame();
    }
    private static Rect SelectionShape(Rect target, double leftEdge, double rightEdge, double liftValue)
    {
        double up = Math.Clamp(liftValue, 0, 1.05), baseWidth = target.Width;
        double w = Math.Clamp(rightEdge - leftEdge, baseWidth * .84, baseWidth * 1.24) + up * 12;
        double h = target.Height + up * 20;
        return new Rect((leftEdge + rightEdge - w) / 2, target.Y + (target.Height - h) / 2, w, h);
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (liveContentMode) return;
        float width = (float)sender.ActualWidth, height = (float)sender.ActualHeight;
        if (width <= 0 || height <= 0) return;
        try
        {
            Render(args.DrawingSession, sender, width, height, lensEnabled, blurEnabled);
            LastRenderError = null;
        }
        catch (Exception ex)
        {
            LastRenderError = ex.Message;
            // GPU/effect failure must not prevent the native controls from remaining usable.
            if (highContrast)
            {
                var colors = new UISettings();
                var background = colors.GetColorValue(UIColorType.Background);
                var foreground = colors.GetColorValue(UIColorType.Foreground);
                var accent = colors.GetColorValue(UIColorType.Accent);
                args.DrawingSession.Clear(background);
                DrawHighContrast(args.DrawingSession, navigation, background, foreground, false);
                DrawHighContrast(args.DrawingSession, selection, background, accent, true);
                DrawHighContrast(args.DrawingSession, sliderTrack, background, foreground, false);
                DrawHighContrastProgress(args.DrawingSession, sliderTrack, sliderPosition.Value, accent);
                DrawHighContrast(args.DrawingSession, sliderThumb, background, accent, true);
            }
            else
            {
                args.DrawingSession.Clear(isDark ? Color.FromArgb(255, 23, 33, 43) : Color.FromArgb(255, 255, 255, 255));
                DrawSolid(args.DrawingSession, navigation, false);
                DrawSolid(args.DrawingSession, selection, true);
                DrawSliderTrack(args.DrawingSession, sliderThumb.X + sliderThumb.Width / 2);
                DrawSolid(args.DrawingSession, sliderThumb, true);
            }
        }
    }

    private void Render(CanvasDrawingSession ds, ICanvasResourceCreator creator, float width, float height, bool lens, bool blur, bool? transparentOverride = null, bool? highContrastOverride = null, float phase = 0)
    {
        if (highContrastOverride ?? highContrast)
        {
            var colors = new UISettings();
            var background = colors.GetColorValue(UIColorType.Background);
            var foreground = colors.GetColorValue(UIColorType.Foreground);
            var accent = colors.GetColorValue(UIColorType.Accent);
            ds.Clear(background);
            DrawHighContrast(ds, navigation, background, foreground, false);
            DrawHighContrast(ds, selection, background, accent, true);
            DrawHighContrast(ds, sliderTrack, background, foreground, false);
            DrawHighContrastProgress(ds, sliderTrack, sliderPosition.Value, accent);
            DrawHighContrast(ds, sliderThumb, background, accent, true);
            return;
        }
        EnsureScene(creator, width, height, phase);
        ds.DrawImage(scene);
        if (!(transparentOverride ?? transparencyEnabled))
        {
            DrawSolid(ds, navigation, false);
            DrawSolid(ds, selection, true);
            DrawSliderTrack(ds, sliderThumb.X + sliderThumb.Width / 2);
            DrawSolid(ds, sliderThumb, true);
            return;
        }
        DrawGlass(ds, creator, navigation, false, lens, blur);
        DrawGlass(ds, creator, selection, true, lens, blur);
        DrawSlider(ds, creator, lens, blur);
    }

    private void EnsureScene(ICanvasResourceCreator creator, float width, float height, float phase)
    {
        if (scene != null && scene.Size.Width == Math.Ceiling(width) && scene.Size.Height == Math.Ceiling(height) && scenePhase == phase) return;
        DisposeScene();
        scenePhase = phase;
        scene = new CanvasRenderTarget(creator, (float)Math.Ceiling(width), (float)Math.Ceiling(height), 96);
        using var d = scene.CreateDrawingSession();
        d.Clear(isDark ? Color.FromArgb(255, 23, 33, 43) : Color.FromArgb(255, 255, 255, 255));
        // This is the application's own source scene, not a capture of the desktop or foreground controls.
        using var bg = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(creator,
            isDark ? Color.FromArgb(255, 23, 33, 43) : Color.FromArgb(255, 255, 255, 255),
            isDark ? Color.FromArgb(255, 27, 42, 55) : Color.FromArgb(255, 246, 251, 254));
        bg.StartPoint = Vector2.Zero; bg.EndPoint = new Vector2(0, height);
        d.FillRectangle(0, 0, width, height, bg);
        // Fine, low-contrast ribbons remain mostly in the bottom 120 DIP, behind the dock.
        for (int i = 0; i < 16; i++)
        {
            using var path = new CanvasPathBuilder(creator);
            float offset = i * 12 + phase;
            path.BeginFigure(-32, height - 118 + offset);
            path.AddCubicBezier(new Vector2(width * .25f, height - 155 + offset),
                new Vector2(width * .58f, height - 24 + offset),
                new Vector2(width + 32, height - 105 + offset));
            path.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(path);
            d.DrawGeometry(geometry, isDark ? Color.FromArgb((byte)(i % 3 == 0 ? 110 : 56), 106, 160, 198) : Color.FromArgb((byte)(i % 3 == 0 ? 125 : 70), 111, 160, 186), i % 3 == 0 ? 1.2f : .8f);
        }
    }
    private void DrawGlass(CanvasDrawingSession ds, ICanvasResourceCreator creator, Rect rect, bool selected, bool lens, bool blur, bool slider = false, ICanvasImage? source = null, double activity = 0)
    {
        if (rect.Width <= 1 || rect.Height <= 1) return;
        // Quantize shape, not the map transform: each cached SDF is generated for the actual rendered size.
        float w = Math.Max(1, (float)Math.Round(rect.Width)), h = Math.Max(1, (float)Math.Round(rect.Height));
        float x = (float)(rect.X + (rect.Width - w) / 2), y = (float)(rect.Y + (rect.Height - h) / 2);
        float up = selected && !slider && Animate ? (float)Math.Clamp(lift.Value, 0, 1.05) : 0;
        float radius = (float)Math.Floor(Math.Min(h / 2, slider ? h / 2 : selected ? 24 + up * 14 : 26));
        using var shape = CanvasGeometry.CreateRoundedRectangle(creator, x, y, w, h, radius, radius);
        // A restrained contact shadow and directional rim make the refracted surface legible
        // over the app's deliberately calm background without obscuring the source texture.
        ds.DrawRoundedRectangle(x, y + 1, w, h, radius, radius,
            isDark ? Color.FromArgb(95, 0, 0, 0) : Color.FromArgb(42, 46, 88, 112), 2);
        using (ds.CreateLayer(1, shape))
        {
            using var blurEffect = blur ? new GaussianBlurEffect { Source = source ?? scene!, BlurAmount = selected ? 1.2f : 1.8f, BorderMode = EffectBorderMode.Hard } : null;
            ICanvasImage image = (ICanvasImage?)blurEffect ?? source ?? scene!;
            using var positionedMap = lens ? new Transform2DEffect { Source = GetMap(creator, w, h, radius, selected, slider), TransformMatrix = Matrix3x2.CreateTranslation(x, y) } : null;
            using var displacementEffect = lens ? new DisplacementMapEffect { Source = image, Displacement = positionedMap, Amount = slider ? (float)(52 * activity) : selected ? 14 + 36 * up : 32f,
                XChannelSelect = EffectChannelSelect.Red, YChannelSelect = EffectChannelSelect.Green } : null;
            ds.DrawImage((ICanvasImage?)displacementEffect ?? image);
            byte alpha = slider ? (byte)Math.Clamp((int)Math.Round(255 * (.98 * (1 - activity) + (.04 + density * .2) * activity)), 0, 255) : (byte)Math.Round((selected ? 38 : 24) + density * (selected ? 88 : 76));
            byte shade = isDark ? (byte)(slider ? Math.Round(245 - 207 * activity) : 35) : (byte)255;
            ds.FillRoundedRectangle(x, y, w, h, radius, radius, Color.FromArgb(alpha, shade, shade, isDark && !slider ? (byte)48 : shade));
        }
        using var rim = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(creator,
            isDark ? Color.FromArgb(175, 219, 239, 255) : Color.FromArgb(225, 255, 255, 255),
            isDark ? Color.FromArgb(90, 92, 145, 187) : Color.FromArgb(120, 78, 135, 168));
        rim.StartPoint = new Vector2(x, y); rim.EndPoint = new Vector2(x + w * .5f, y + h);
        ds.DrawRoundedRectangle(x + .5f, y + .5f, Math.Max(0, w - 1), Math.Max(0, h - 1), radius, radius, rim, 1.3f);
        using var innerLight = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(creator,
            isDark ? Color.FromArgb(80, 225, 242, 255) : Color.FromArgb(180, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255));
        innerLight.StartPoint = new Vector2(x, y); innerLight.EndPoint = new Vector2(x, y + h * .65f);
        ds.DrawRoundedRectangle(x + 2, y + 2, Math.Max(0, w - 4), Math.Max(0, h - 4), Math.Max(0, radius - 1), Math.Max(0, radius - 1), innerLight, 1);
        if (slider)
            ds.DrawRoundedRectangle(x + 1, y + 1, Math.Max(0, w - 2), Math.Max(0, h - 2), radius, radius,
                isDark ? Color.FromArgb(140, 154, 198, 225) : Color.FromArgb(155, 69, 128, 164), 1.2f);
    }

    private void DrawSliderTrack(CanvasDrawingSession d, double center)
    {
        if (sliderTrack.Width <= 1 || sliderTrack.Height <= 1) return;
        float x = (float)sliderTrack.X, y = (float)sliderTrack.Y;
        float w = (float)sliderTrack.Width, h = (float)sliderTrack.Height, r = h / 2;
        d.FillRoundedRectangle(x, y, w, h, r, r, isDark ? Color.FromArgb(255, 77, 91, 112) : Color.FromArgb(255, 205, 213, 225));
        float filled = (float)Math.Clamp(center - sliderTrack.X, 0, sliderTrack.Width);
        if (filled > 1) d.FillRoundedRectangle(x, y, filled, h, r, r, isDark ? Color.FromArgb(255, 106, 179, 243) : Color.FromArgb(255, 64, 167, 227));
    }

    private void EnsureSliderScene(ICanvasResourceCreator creator)
    {
        if (scene == null) return;
        if (sliderScene == null || sliderScene.Size != scene.Size)
        {
            sliderScene?.Dispose();
            sliderScene = new CanvasRenderTarget(creator, (float)scene.Size.Width, (float)scene.Size.Height, 96);
            sliderSceneDirty = true;
        }
        if (!sliderSceneDirty) return;
        using var d = sliderScene.CreateDrawingSession();
        d.Clear(Color.FromArgb(0, 0, 0, 0));
        d.DrawImage(scene);
        DrawSliderTrack(d, sliderThumb.X + sliderThumb.Width / 2);
        sliderSceneDirty = false;
    }

    private Rect AnimatedSliderThumb(double active)
    {
        if (sliderThumb.Width <= 1 || sliderThumb.Height <= 1) return new Rect(0, 0, 0, 0);
        double size = Math.Max(sliderThumb.Width, sliderThumb.Height) + active * 25;
        double normalizedVelocity = sliderTrack.Width > 1 ? sliderPosition.Velocity / sliderTrack.Width : 0;
        double stretch = 1 + Math.Min(.2, Math.Abs(normalizedVelocity) * .06) * active;
        double w = size * stretch, h = size / Math.Sqrt(stretch);
        return new Rect(sliderPosition.Value - w / 2, sliderThumb.Y + (sliderThumb.Height - h) / 2, w, h);
    }

    private void DrawSlider(CanvasDrawingSession ds, ICanvasResourceCreator creator, bool lens, bool blur)
    {
        if (sliderThumb.Width <= 1 || sliderThumb.Height <= 1) return;
        EnsureSliderScene(creator);
        DrawSliderTrack(ds, sliderThumb.X + sliderThumb.Width / 2);
        double active = Animate ? Math.Clamp(sliderLift.Value, 0, 1) : 0;
        double smooth = Math.Clamp(active / .65, 0, 1); smooth = smooth * smooth * (3 - 2 * smooth);
        var thumb = AnimatedSliderThumb(active);
        double w = thumb.Width, h = thumb.Height;
        if (active < .002)
        {
            float r = (float)h / 2;
            ds.FillRoundedRectangle((float)thumb.X, (float)thumb.Y, (float)w, (float)h, r, r, Color.FromArgb(255, 255, 255, 255));
            ds.DrawRoundedRectangle((float)thumb.X + .5f, (float)thumb.Y + .5f, (float)w - 1, (float)h - 1, r, r,
                Color.FromArgb(145, 92, 125, 175), 1.2f);
            return;
        }
        DrawGlass(ds, creator, thumb, true, lens, blur, true, sliderScene, smooth);
    }
    private static void DrawHighContrastProgress(CanvasDrawingSession ds, Rect track, double center, Color accent)
    {
        float width = (float)Math.Clamp(center - track.X, 0, track.Width);
        if (width <= 1 || track.Height <= 1) return;
        float radius = (float)track.Height / 2;
        ds.FillRoundedRectangle((float)track.X, (float)track.Y, width, (float)track.Height, radius, radius, accent);
    }

    private static void DrawHighContrast(CanvasDrawingSession ds, Rect rect, Color background, Color border, bool selected)
    {
        if (rect.Width <= 1 || rect.Height <= 1) return;
        float x = (float)rect.X, y = (float)rect.Y, w = (float)rect.Width, h = (float)rect.Height;
        float r = Math.Min(h / 2, selected ? 24 : 26);
        ds.FillRoundedRectangle(x, y, w, h, r, r, background);
        ds.DrawRoundedRectangle(x + 1, y + 1, Math.Max(0, w - 2), Math.Max(0, h - 2), r, r, border, selected ? 2 : 1);
    }

    private void DrawSolid(CanvasDrawingSession ds, Rect rect, bool selected)
    {
        if (rect.Width <= 1 || rect.Height <= 1) return;
        float r = Math.Min((float)rect.Height / 2, selected ? 24 : 26);
        ds.FillRoundedRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, r, r,
            isDark ? (selected ? Color.FromArgb(255, 49, 66, 83) : Color.FromArgb(255, 35, 46, 60))
                   : (selected ? Color.FromArgb(255, 222, 240, 250) : Color.FromArgb(255, 250, 253, 255)));
    }

    private CanvasBitmap GetMap(ICanvasResourceCreator creator, float width, float height, float radius, bool selected, bool slider)
    {
        int w = Math.Max(1, (int)Math.Ceiling(width)), h = Math.Max(1, (int)Math.Ceiling(height));
        var existing = slider ? sliderMap : selected ? selectionMap : navMap;
        if (existing != null && (slider ? sliderMapSize : selected ? selectionMapSize : navMapSize) == (w, h, (int)radius)) return existing;
        existing?.Dispose();
        var data = new byte[w * h * 4];
        double bezel = Math.Min(Math.Clamp(Math.Min(width, height) * .35, 8, 24), Math.Min(width, height) / 2);
        for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++)
        {
            double px = xx + .5, py = yy + .5;
            double d = Sdf(px, py, width, height, radius);
            double dx = Sdf(px + .5, py, width, height, radius) - Sdf(px - .5, py, width, height, radius);
            double dy = Sdf(px, py + .5, width, height, radius) - Sdf(px, py - .5, width, height, radius);
            double length = Math.Sqrt(dx * dx + dy * dy);
            double amount = d <= 0 && length > 1e-6 ? DeflectionTable[(int)Math.Round(Math.Clamp(-d / bezel, 0, 1) * 512)] : 0;
            int k = (yy * w + xx) * 4;
            // Win2D displacement uses channel .5 as zero and BGRA bytes.
            data[k] = 0; data[k + 1] = Channel(-dy / Math.Max(length, 1e-6) * amount);
            data[k + 2] = Channel(-dx / Math.Max(length, 1e-6) * amount); data[k + 3] = 255;
        }
        var result = CanvasBitmap.CreateFromBytes(creator, data, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        if (slider) { sliderMap = result; sliderMapSize = (w, h, (int)radius); }
        else if (selected) { selectionMap = result; selectionMapSize = (w, h, (int)radius); }
        else { navMap = result; navMapSize = (w, h, (int)radius); }
        return result;
    }
    private static readonly double[] DeflectionTable = CreateDeflectionTable();
    private static double[] CreateDeflectionTable()
    {
        var result = new double[513];
        for (int i = 0; i < result.Length; i++) result[i] = Deflection(i / 512.0);
        return result;
    }
    private static byte Channel(double v) => (byte)Math.Clamp((int)Math.Round(127.5 + v * 127.5), 0, 255);
    private static double Sdf(double x, double y, double w, double h, double r)
    {
        double qx = Math.Abs(x - w / 2) - w / 2 + r, qy = Math.Abs(y - h / 2) - h / 2 + r;
        return Math.Min(Math.Max(qx, qy), 0) + Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) - r;
    }
    private static double Profile(double t) => Math.Pow(1 - Math.Pow(1 - t, 3), 1.0 / 3);
    internal static double Deflection(double t)
    {
        double theta = Math.PI / 2;
        if (t > .001) { double a = Math.Max(t - .002, 0), b = Math.Min(t + .002, 1); theta = Math.Atan((Profile(b) - Profile(a)) / (b - a)); }
        double transmitted = Math.Asin(Math.Sin(theta) / 1.5), edge = Math.PI / 2 - Math.Asin(1 / 1.5);
        return Math.Tan(theta - transmitted) / Math.Tan(edge);
    }
    private void DisposeScene() { scene?.Dispose(); scene = null; sliderScene?.Dispose(); sliderScene = null; sliderSceneDirty = true; }
    private void DisposeMaps() { navMap?.Dispose(); selectionMap?.Dispose(); sliderMap?.Dispose(); navMap = selectionMap = sliderMap = null; }

    /// <summary>Writes full-scene A/B images for visual and pixel validation.</summary>
    public async Task SaveDiagnosticsAsync(string directory)
    {
        if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) throw new InvalidOperationException("The backdrop must be laid out first.");
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var captures = new System.Collections.Generic.List<(string Name, CanvasRenderTarget Target)>();
        try
        {
            float width = (float)canvas.ActualWidth, height = (float)canvas.ActualHeight;
            // Render every frame without awaiting, so the selection and foreground geometry are identical.
            void Capture(string name, bool lens = true, bool blur = true, bool transparent = true, bool hc = false, float phase = 0)
            {
                var target = new CanvasRenderTarget(canvas, (float)Math.Ceiling(width), (float)Math.Ceiling(height), 96);
                captures.Add((name, target));
                using var ds = target.CreateDrawingSession();
                Render(ds, canvas, width, height, lens, blur, transparent, hc, phase);
            }
            Capture("glass-full.png");
            Capture("glass-no-lens.png", lens: false);
            Capture("glass-no-blur.png", blur: false);
            Capture("glass-source-shifted.png", phase: 18);
            Capture("glass-opaque.png", transparent: false);
            Capture("glass-high-contrast.png", transparent: false, hc: true);
            bool previousDark = isDark;
            try
            {
                isDark = false; DisposeScene();
                Capture("light-glass-full.png");
                Capture("light-glass-no-lens.png", lens: false);
                Capture("light-glass-no-blur.png", blur: false);
                Capture("light-glass-opaque.png", transparent: false);
                isDark = true; DisposeScene();
                Capture("dark-glass-full.png");
                Capture("dark-glass-no-lens.png", lens: false);
                Capture("dark-glass-no-blur.png", blur: false);
                Capture("dark-glass-opaque.png", transparent: false);
            }
            finally { isDark = previousDark; DisposeScene(); }


            // Render deterministic phase snapshots without changing live targets, velocities or preferences.
            var oldSelection = selection; var oldSliderThumb = sliderThumb;
            double oldLift = lift.Value, oldSliderLift = sliderLift.Value;
            double oldSliderPosition = sliderPosition.Value, oldSliderVelocity = sliderPosition.Velocity;
            bool oldMotion = reduceMotion, oldTransparency = transparencyEnabled, oldHighContrast = highContrast;
            try
            {
                reduceMotion = false; transparencyEnabled = true; highContrast = false;
                if (selectionTarget.Width > 1)
                {
                    double shift = Math.Min(selectionTarget.Width * .55, Math.Max(0, navigation.Right - selectionTarget.Right));
                    if (shift < 1) shift = -Math.Min(selectionTarget.Width * .55, Math.Max(0, selectionTarget.X - navigation.X));
                    var dl = new GlassSpring(shift >= 0 ? .34 : .24, shift >= 0 ? .84 : .8);
                    var dr = new GlassSpring(shift >= 0 ? .24 : .34, shift >= 0 ? .8 : .84);
                    var du = new GlassSpring(.2, .86);
                    dl.Snap(selectionTarget.X); dr.Snap(selectionTarget.Right); du.Snap(0);
                    dl.Target += shift; dr.Target += shift; du.Target = .7;
                    for (int i = 0; i < 4; i++) { dl.Step(1.0 / 60); dr.Step(1.0 / 60); du.Step(1.0 / 60, .002, .02); }
                    lift.Value = du.Value; selection = SelectionShape(selectionTarget, dl.Value, dr.Value, du.Value);
                    Capture("picker-lift.png");
                    for (int i = 0; i < 5; i++) { dl.Step(1.0 / 60); dr.Step(1.0 / 60); du.Step(1.0 / 60, .002, .02); }
                    lift.Value = du.Value; selection = SelectionShape(selectionTarget, dl.Value, dr.Value, du.Value);
                    Capture("picker-transfer.png");
                    dl.Target = selectionTarget.X; dr.Target = selectionTarget.Right;
                    dl.Response = shift >= 0 ? .24 : .34; dr.Response = shift >= 0 ? .34 : .24;
                    du.Target = 0; du.Response = .34; du.Damping = .88;
                    for (int i = 0; i < 5; i++) { dl.Step(1.0 / 60); dr.Step(1.0 / 60); du.Step(1.0 / 60, .002, .02); }
                    lift.Value = du.Value; selection = SelectionShape(selectionTarget, dl.Value, dr.Value, du.Value);
                    Capture("picker-retarget.png");
                    for (int i = 0; i < 180; i++) { dl.Step(1.0 / 60); dr.Step(1.0 / 60); du.Step(1.0 / 60, .002, .02); }
                    lift.Value = du.Value; selection = SelectionShape(selectionTarget, dl.Value, dr.Value, du.Value);
                    Capture("picker-land.png");
                }
                if (sliderThumb.Width > 1 && sliderTrack.Width > 1)
                {
                    double initial = sliderThumb.X + sliderThumb.Width / 2;
                    var pos = new GlassSpring(.13, 1); var up = new GlassSpring(.18, .88);
                    pos.Snap(initial); up.Snap(0);
                    sliderPosition.Value = pos.Value; sliderPosition.Velocity = 0; sliderLift.Value = 0; sliderSceneDirty = true;
                    Capture("slider-rest.png");
                    up.Target = .7;
                    for (int i = 0; i < 9; i++) up.Step(1.0 / 60, .002, .02);
                    sliderLift.Value = up.Value;
                    Capture("slider-held.png");
                    Capture("slider-held-no-lens.png", lens: false);
                    double delta = Math.Min(sliderTrack.Width * .22, sliderTrack.Right - initial);
                    if (delta < 1) delta = -Math.Min(sliderTrack.Width * .22, initial - sliderTrack.X);
                    pos.Target = initial + delta;
                    sliderThumb = new Rect(sliderThumb.X + delta, sliderThumb.Y, sliderThumb.Width, sliderThumb.Height);
                    for (int i = 0; i < 3; i++) pos.Step(1.0 / 60);
                    sliderPosition.Value = pos.Value; sliderPosition.Velocity = pos.Velocity; sliderSceneDirty = true;
                    Capture("slider-move.png");
                    up.Target = 0; up.Response = .34;
                    for (int i = 0; i < 180; i++) { pos.Step(1.0 / 60); up.Step(1.0 / 60, .002, .02); }
                    sliderPosition.Value = pos.Value; sliderPosition.Velocity = pos.Velocity; sliderLift.Value = up.Value;
                    Capture("slider-land.png");
                }            }
            finally
            {
                selection = oldSelection; sliderThumb = oldSliderThumb; lift.Value = oldLift; sliderLift.Value = oldSliderLift;
                sliderPosition.Value = oldSliderPosition; sliderPosition.Velocity = oldSliderVelocity;
                reduceMotion = oldMotion; transparencyEnabled = oldTransparency; highContrast = oldHighContrast;
                sliderSceneDirty = true;
            }
            foreach (var capture in captures)
            {
                var file = await folder.CreateFileAsync(capture.Name, CreationCollisionOption.ReplaceExisting);
                await capture.Target.SaveAsync(file.Path, CanvasBitmapFileFormat.Png);
            }
        }
        finally
        {
            foreach (var capture in captures) capture.Target.Dispose();
            // A diagnostic source phase must never remain visible in the live renderer.
            DisposeScene();
            InvalidateFrame();
        }
    }
}
