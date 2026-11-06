using Microsoft.Maui.Controls;

namespace TGDeblocker.Mobile.Rendering;

// A visual-only layer. Input and accessible names belong to the native controls above it.
public class GlassSurface : View
{
    public static readonly BindableProperty DensityProperty = BindableProperty.Create(nameof(Density), typeof(double), typeof(GlassSurface), 0.28d, coerceValue: (_, value) => Math.Clamp((double)value, 0, 1));
    public static readonly BindableProperty ReduceMotionProperty = BindableProperty.Create(nameof(ReduceMotion), typeof(bool), typeof(GlassSurface), false);
    public static readonly BindableProperty ReduceTransparencyProperty = BindableProperty.Create(nameof(ReduceTransparency), typeof(bool), typeof(GlassSurface), false);
    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(GlassSurface), 0, coerceValue: (_, value) => Math.Clamp((int)value, 0, 3));
    public static readonly BindableProperty IsPressedProperty = BindableProperty.Create(nameof(IsPressed), typeof(bool), typeof(GlassSurface), false);
    public static readonly BindableProperty IsDarkProperty = BindableProperty.Create(nameof(IsDark), typeof(bool), typeof(GlassSurface), false);
    internal static readonly BindableProperty IsSliderProperty = BindableProperty.Create(nameof(IsSlider), typeof(bool), typeof(GlassSurface), false);
    internal static readonly BindableProperty ProgressProperty = BindableProperty.Create(nameof(Progress), typeof(double), typeof(GlassSurface), 0d, coerceValue: (_, value) => Math.Clamp((double)value, 0, 1));
    internal static readonly BindableProperty SliderInsetsProperty = BindableProperty.Create(nameof(SliderInsets), typeof(Thickness), typeof(GlassSurface), new Thickness(22, 0));

    public double Density { get => (double)GetValue(DensityProperty); set => SetValue(DensityProperty, value); }
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public bool ReduceTransparency { get => (bool)GetValue(ReduceTransparencyProperty); set => SetValue(ReduceTransparencyProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public bool IsPressed { get => (bool)GetValue(IsPressedProperty); set => SetValue(IsPressedProperty, value); }
    public bool IsDark { get => (bool)GetValue(IsDarkProperty); set => SetValue(IsDarkProperty, value); }
    internal bool IsSlider { get => (bool)GetValue(IsSliderProperty); set => SetValue(IsSliderProperty, value); }
    internal double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    internal Thickness SliderInsets { get => (Thickness)GetValue(SliderInsetsProperty); set => SetValue(SliderInsetsProperty, value); }
    public View? BackdropSource { get; set; }
    public event Action? BackdropInvalidated;
    public void InvalidateBackdrop() => BackdropInvalidated?.Invoke();
    internal event Action<bool>? RendererAvailabilityChanged;
    internal void ReportRendererAvailability(bool available) => RendererAvailabilityChanged?.Invoke(available);
    public void SetSelectionPressed(bool pressed) => IsPressed = pressed;
}
