using Microsoft.Maui.Controls;

namespace TGDeblocker.Mobile.Rendering;

// The actual Slider stays responsible for touch, keyboard and accessibility.
public sealed class GlassSlider : ContentView
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(nameof(Value), typeof(double), typeof(GlassSlider), 0.28d, BindingMode.TwoWay, propertyChanged: OnValueChanged, coerceValue: (_, value) => Math.Clamp((double)value, 0, 1));
    public static readonly BindableProperty DensityProperty = BindableProperty.Create(nameof(Density), typeof(double), typeof(GlassSlider), 0.28d, propertyChanged: OnAppearanceChanged, coerceValue: (_, value) => Math.Clamp((double)value, 0, 1));
    public static readonly BindableProperty ReduceMotionProperty = BindableProperty.Create(nameof(ReduceMotion), typeof(bool), typeof(GlassSlider), false, propertyChanged: OnAppearanceChanged);
    public static readonly BindableProperty ReduceTransparencyProperty = BindableProperty.Create(nameof(ReduceTransparency), typeof(bool), typeof(GlassSlider), false, propertyChanged: OnAppearanceChanged);
    public static readonly BindableProperty IsDarkProperty = BindableProperty.Create(nameof(IsDark), typeof(bool), typeof(GlassSlider), false, propertyChanged: OnAppearanceChanged);

    readonly Slider _slider;
    readonly GlassSurface? _surface = null;
    bool _effectiveReduceTransparency;
#if IOS
    Foundation.NSObject? _transparencyObserver;
#endif
#if ANDROID
    bool _rendererAvailable;
    Android.Content.Res.ColorStateList? _originalProgress, _originalTrack, _originalThumb;
#endif
    public event EventHandler<ValueChangedEventArgs>? ValueChanged;
    public event EventHandler? EffectiveReduceTransparencyChanged;
    public bool EffectiveReduceTransparency => _effectiveReduceTransparency;
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Density { get => (double)GetValue(DensityProperty); set => SetValue(DensityProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public bool ReduceTransparency { get => (bool)GetValue(ReduceTransparencyProperty); set => SetValue(ReduceTransparencyProperty, value); }
    public bool IsDark { get => (bool)GetValue(IsDarkProperty); set => SetValue(IsDarkProperty, value); }

    public GlassSlider()
    {
        HeightRequest = 56;
        _slider = new Slider { Minimum = 0, Maximum = 1, Value = Value, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center };
        _slider.ValueChanged += (_, e) => Value = e.NewValue;
#if IOS
        _slider.HandlerChanged += (_, _) => ApplyNativeTheme();
#endif
        _slider.DragStarted += (_, _) => { if (_surface is not null) _surface.IsPressed = true; };
        _slider.DragCompleted += (_, _) => { if (_surface is not null) _surface.IsPressed = false; };
        SemanticProperties.SetDescription(_slider, "Liquid Glass");
#if ANDROID
        _surface = new GlassSurface { IsSlider = true, Progress = Value, InputTransparent = true };
        _surface.RendererAvailabilityChanged += SetNativeTrackVisibility;
        var grid = new Grid();
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _surface.BackdropSource = _slider;
            grid.Children.Add(_slider);
            grid.Children.Add(_surface);
        }
        else { grid.Children.Add(_surface); grid.Children.Add(_slider); }
        _slider.HandlerChanged += (_, _) =>
        {
            if (_slider.Handler?.PlatformView is Android.Widget.SeekBar seek)
            {
                _originalProgress = seek.ProgressTintList;
                _originalTrack = seek.ProgressBackgroundTintList;
                _originalThumb = seek.ThumbTintList;
                UpdateSliderInsets(seek);
                SetNativeTrackVisibility(_rendererAvailable);
            }
        };
        _slider.SizeChanged += (_, _) => { if (_slider.Handler?.PlatformView is Android.Widget.SeekBar seek) UpdateSliderInsets(seek); };
        Content = grid;
#else
        // On iOS the system owns the slider and adapts its appearance and accessibility.
        Content = _slider;
#endif
        RefreshEffectiveTransparency();
        ApplyNativeTheme();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if IOS
        if (Handler is not null && _transparencyObserver is null)
            _transparencyObserver = Foundation.NSNotificationCenter.DefaultCenter.AddObserver(UIKit.UIView.ReduceTransparencyStatusDidChangeNotification, _ => RefreshEffectiveTransparency());
#endif
        RefreshEffectiveTransparency();
        ApplyNativeTheme();
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
#if IOS
        if (args.OldHandler is not null && _transparencyObserver is not null)
        {
            Foundation.NSNotificationCenter.DefaultCenter.RemoveObserver(_transparencyObserver);
            _transparencyObserver.Dispose();
            _transparencyObserver = null;
        }
#endif
        base.OnHandlerChanging(args);
    }

    void RefreshEffectiveTransparency()
    {
        var effective = ReduceTransparency;
#if IOS
        effective |= UIKit.UIAccessibility.IsReduceTransparencyEnabled;
#endif
#if ANDROID
        if (effective) SetNativeTrackVisibility(false);
#endif
        _slider.IsEnabled = !effective;
        if (_effectiveReduceTransparency == effective) return;
        _effectiveReduceTransparency = effective;
        EffectiveReduceTransparencyChanged?.Invoke(this, EventArgs.Empty);
    }

    void ApplyNativeTheme()
    {
#if IOS
        if (_slider.Handler?.PlatformView is UIKit.UIView view)
            view.OverrideUserInterfaceStyle = IsDark ? UIKit.UIUserInterfaceStyle.Dark : UIKit.UIUserInterfaceStyle.Light;
#elif ANDROID
        SetNativeTrackVisibility(_rendererAvailable);
#endif
    }

#if ANDROID
    void SetNativeTrackVisibility(bool rendererAvailable)
    {
        rendererAvailable &= !EffectiveReduceTransparency;
        _rendererAvailable = rendererAvailable;
        if (_slider.Handler?.PlatformView is not Android.Widget.SeekBar seek) return;
        // If GLES is unavailable, preserve the complete standard slider as a fallback.
        bool liveTrack = rendererAvailable && OperatingSystem.IsAndroidVersionAtLeast(33);
        _slider.MinimumTrackColor = rendererAvailable && !liveTrack ? Colors.Transparent : Color.FromArgb(IsDark ? "#6AB3F3" : "#40A7E3");
        _slider.MaximumTrackColor = rendererAvailable && !liveTrack ? Colors.Transparent : Color.FromArgb(IsDark ? "#364554" : "#E1EAEF");
        _slider.ThumbColor = rendererAvailable ? Colors.Transparent : Color.FromArgb(IsDark ? "#6AB3F3" : "#40A7E3");
        if (rendererAvailable)
        {
            var clear = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
            seek.ProgressTintList = liveTrack ? Android.Content.Res.ColorStateList.ValueOf(IsDark ? Android.Graphics.Color.Rgb(106, 179, 243) : Android.Graphics.Color.Rgb(64, 167, 227)) : clear;
            seek.ProgressBackgroundTintList = liveTrack ? Android.Content.Res.ColorStateList.ValueOf(IsDark ? Android.Graphics.Color.Rgb(54, 69, 84) : Android.Graphics.Color.Rgb(225, 234, 239)) : clear;
            seek.ThumbTintList = clear;
        }
        else
        {
            seek.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(IsDark ? Android.Graphics.Color.Rgb(106, 179, 243) : Android.Graphics.Color.Rgb(64, 167, 227));
            seek.ProgressBackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(IsDark ? Android.Graphics.Color.Rgb(54, 69, 84) : Android.Graphics.Color.Rgb(167, 183, 194));
            seek.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(IsDark ? Android.Graphics.Color.Rgb(106, 179, 243) : Android.Graphics.Color.Rgb(64, 167, 227));
        }
    }
    void UpdateSliderInsets(Android.Widget.SeekBar seek)
    {
        // SeekBar centres the thumb after subtracting ThumbOffset from its padded track.
        var scale = seek.Resources?.DisplayMetrics?.Density ?? 1f;
        var halfThumb = (seek.Thumb?.IntrinsicWidth ?? 0) / 2f;
        var offset = seek.ThumbOffset;
        var left = Math.Max(0, (seek.PaddingLeft - offset + halfThumb) / scale);
        var right = Math.Max(0, (seek.PaddingRight - offset + halfThumb) / scale);
        if (_surface is not null) _surface.SliderInsets = new Thickness(left, 0, right, 0);
    }
#endif

    static void OnValueChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var self = (GlassSlider)bindable;
        var value = (double)newValue;
        if (self._slider is not null && self._slider.Value != value) self._slider.Value = value;
        if (self._surface is not null) { self._surface.Progress = value; self._surface.InvalidateBackdrop(); }
        self.ValueChanged?.Invoke(self, new ValueChangedEventArgs((double)oldValue, value));
    }

    static void OnAppearanceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var self = (GlassSlider)bindable;
        self.RefreshEffectiveTransparency();
        self.ApplyNativeTheme();
        if (self._surface is null) return;
        self._surface.Density = self.Density;
        self._surface.ReduceMotion = self.ReduceMotion;
        self._surface.ReduceTransparency = self.ReduceTransparency;
        self._surface.IsDark = self.IsDark;
    }
}
