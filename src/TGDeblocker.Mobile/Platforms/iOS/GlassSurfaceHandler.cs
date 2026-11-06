#if IOS
using CoreGraphics;
using CoreAnimation;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Handlers;
using UIKit;

namespace TGDeblocker.Mobile.Rendering;

public sealed class GlassSurfaceHandler : ViewHandler<GlassSurface, NativeGlassSurface>
{
    public static readonly IPropertyMapper<GlassSurface, GlassSurfaceHandler> Mapper = new PropertyMapper<GlassSurface, GlassSurfaceHandler>(ViewMapper)
    {
        [nameof(GlassSurface.Density)] = Update,
        [nameof(GlassSurface.ReduceMotion)] = Update,
        [nameof(GlassSurface.ReduceTransparency)] = Update,
        [nameof(GlassSurface.SelectedIndex)] = Update,
        [nameof(GlassSurface.IsPressed)] = Update,
        [nameof(GlassSurface.IsDark)] = Update,
    };
    public GlassSurfaceHandler() : base(Mapper) { }
    protected override NativeGlassSurface CreatePlatformView() => new();
    protected override void ConnectHandler(NativeGlassSurface platformView) { base.ConnectHandler(platformView); platformView.Update(VirtualView); }
    protected override void DisconnectHandler(NativeGlassSurface platformView) { platformView.Stop(); base.DisconnectHandler(platformView); }
    static void Update(GlassSurfaceHandler handler, GlassSurface view) => handler.PlatformView?.Update(view);
}

public sealed class NativeGlassSurface : UIView
{
    readonly CAGradientLayer _background = new();
    readonly CAShapeLayer _details = new();
    readonly UIVisualEffectView _container = new();
    readonly UIVisualEffectView _glass = new();
    readonly UIVisualEffectView _indicator = new();
    readonly NSObject _accessibilityObserver;
    GlassSurface? _model;
    int _selected;
    bool _pressed, _autoLift;
    NSTimer? _releaseTimer;

    public NativeGlassSurface()
    {
        UserInteractionEnabled = false;
        BackgroundColor = UIColor.Clear;
        _background.Colors = new[] { UIColor.White.CGColor, UIColor.White.CGColor };
        _background.StartPoint = new CGPoint(0, 0);
        _background.EndPoint = new CGPoint(1, 1);
        // Transparent outside the native glass: the scrolling content is below us.
        _details.FillColor = UIColor.Clear.CGColor;
        _details.StrokeColor = UIColor.FromRGBA(.62f, .77f, .85f, .28f).CGColor;
        _details.LineWidth = 1.2f;

        _container.UserInteractionEnabled = false;
        _glass.UserInteractionEnabled = false;
        _glass.ClipsToBounds = true;
        _glass.Layer.CornerRadius = 32;
        _indicator.UserInteractionEnabled = false;
        _indicator.ClipsToBounds = true;
        _indicator.Layer.CornerRadius = 27;
        _container.ContentView.AddSubview(_glass);
        _container.ContentView.AddSubview(_indicator);
        AddSubview(_container);
        _accessibilityObserver = NSNotificationCenter.DefaultCenter.AddObserver(UIView.ReduceTransparencyStatusDidChangeNotification, _ => { if (_model is not null) Update(_model); });
    }

    public void Update(GlassSurface model)
    {
        _model = model;
        // Explicitly match the effective app theme, including an in-app override.
        OverrideUserInterfaceStyle = model.IsDark ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;
        _background.Colors = model.IsDark
            ? new[] { UIColor.FromRGB(23, 33, 43).CGColor, UIColor.FromRGB(23, 33, 43).CGColor }
            : new[] { UIColor.White.CGColor, UIColor.White.CGColor };
        _details.StrokeColor = model.IsDark ? UIColor.FromRGBA(.34f, .45f, .53f, .27f).CGColor : UIColor.FromRGBA(.62f, .77f, .85f, .28f).CGColor;
        bool opaque = model.ReduceTransparency || UIAccessibility.IsReduceTransparencyEnabled;
        if (opaque)
        {
            _container.Effect = null;
            _glass.Effect = null;
            _indicator.Effect = null;
            _glass.BackgroundColor = model.IsDark ? UIColor.FromRGB(35, 46, 60) : UIColor.FromRGB(241, 241, 241);
            _indicator.BackgroundColor = model.IsDark ? UIColor.FromRGB(54, 69, 84) : UIColor.FromRGB(225, 234, 239);
        }
        else
        {
            if (OperatingSystem.IsIOSVersionAtLeast(26))
            {
                _container.Effect = new UIGlassContainerEffect();
                var glass = UIGlassEffect.Create(UIGlassEffectStyle.Regular);
                glass.TintColor = model.IsDark
                    ? UIColor.FromRGBA(.26f, .37f, .45f, (float)(model.Density * .16))
                    : UIColor.FromRGBA(.84f, .93f, .98f, (float)(model.Density * .22));
                _glass.Effect = glass;
                var selection = UIGlassEffect.Create(UIGlassEffectStyle.Regular);
                selection.Interactive = true;
                selection.TintColor = model.IsDark
                    ? UIColor.FromRGBA(.26f, .37f, .45f, (float)(.05 + model.Density * .14))
                    : UIColor.FromRGBA(.84f, .93f, .98f, (float)(.06 + model.Density * .18));
                _indicator.Effect = selection;
                _indicator.BackgroundColor = UIColor.Clear;
            }
            else
            {
                _container.Effect = null;
                _glass.Effect = UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemMaterial);
                _indicator.Effect = null;
                _indicator.BackgroundColor = UIColor.FromRGBA(1f, 1f, 1f, .22f);
            }
            _glass.BackgroundColor = UIColor.Clear;
        }
        var selectionChanged = _selected != model.SelectedIndex;
        bool reduced = model.ReduceMotion || UIAccessibility.IsReduceMotionEnabled;
        if (model.IsPressed || reduced)
        {
            _releaseTimer?.Invalidate(); _releaseTimer?.Dispose(); _releaseTimer = null; _autoLift = false;
        }
        else if (selectionChanged && Window is not null)
        {
            _releaseTimer?.Invalidate(); _releaseTimer?.Dispose();
            _autoLift = true;
            _releaseTimer = NSTimer.CreateScheduledTimer(TimeSpan.FromMilliseconds(160), _ =>
            {
                _releaseTimer?.Dispose(); _releaseTimer = null; _autoLift = false;
                if (_model is not null) Update(_model);
            });
        }
        bool nextPressed = (model.IsPressed || _autoLift) && !reduced;
        var changed = selectionChanged || _pressed != nextPressed;
        _selected = model.SelectedIndex;
        _pressed = nextPressed;
        SetNeedsLayout();
        if (changed && !model.ReduceMotion && !UIAccessibility.IsReduceMotionEnabled && Window is not null)
        {
            if (OperatingSystem.IsIOSVersionAtLeast(17))
                UIView.Animate(.28, 0, .85f, 0, UIViewAnimationOptions.BeginFromCurrentState | UIViewAnimationOptions.AllowUserInteraction, LayoutIfNeeded, null);
            else UIView.Animate(.28, 0, UIViewAnimationOptions.BeginFromCurrentState | UIViewAnimationOptions.AllowUserInteraction, LayoutIfNeeded, () => { });
        }
        else LayoutIfNeeded();
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _background.Frame = Bounds;
        _details.Frame = Bounds;
        using (var path = new CGPath())
        {
            double w = Bounds.Width, h = Bounds.Height;
            path.MoveToPoint(0, (nfloat)(h - 61));
            path.AddCurveToPoint(new CGPoint(w * .3, h - 68), new CGPoint(w * .7, h - 54), new CGPoint(w, h - 61));
            path.MoveToPoint(0, (nfloat)(h - 38));
            path.AddCurveToPoint(new CGPoint(w * .3, h - 33), new CGPoint(w * .7, h - 44), new CGPoint(w, h - 38));
            _details.Path = path;
        }
        _container.Frame = Bounds;
        var width = Math.Max(0, Bounds.Width - 32);
        _glass.Frame = new CGRect(16, Math.Max(0, Bounds.Height - 80), width, 64);
        var tabWidth = width / 4;
        var lift = _pressed ? 3 : 0;
        _indicator.Frame = new CGRect(16 + _selected * tabWidth + 4 - lift, Math.Max(0, Bounds.Height - 75) - lift, Math.Max(0, tabWidth - 8 + 2 * lift), 54 + 2 * lift);
    }

    public void Stop()
    {
        NSNotificationCenter.DefaultCenter.RemoveObserver(_accessibilityObserver);
        _accessibilityObserver.Dispose();
        _releaseTimer?.Invalidate(); _releaseTimer?.Dispose(); _releaseTimer = null;
        _model = null;
    }
}
#endif
