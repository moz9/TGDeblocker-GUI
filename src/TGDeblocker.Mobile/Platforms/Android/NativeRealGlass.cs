#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Views;
using System.Diagnostics;

namespace TGDeblocker.Mobile.Rendering;

// API 33+: records the app's own scroll subtree, excluding this overlay and its controls.
// The RecordingCanvas is GPU-backed; no screenshot or bitmap readback is used.
[System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
internal sealed class NativeRealGlass : Android.Views.View
{
    readonly RenderNode _source = new("TGDeblocker content under navigation");
    readonly RuntimeShader? _shader;
    RenderEffect? _effect;
    RenderEffect? _dockBlur;
    float _dockBlurDpr;
    bool _failed, _sourceReleased;
    bool? _availability;
    int _availabilityVersion;
    readonly GlassSpring _left = new(.34, .84), _right = new(.25, .86), _lift = new(.22, .86);
    readonly TickRunner _tick;
    GlassSurface? _model;
    bool _dirty = true, _started, _scheduled, _stopped;
    bool? _lastDark;
    readonly int _diagnostic;
    int _lastSelection = -1;
    bool _wasPressed;
    long _last, _autoRelease;

    public NativeRealGlass(Context context) : base(context)
    {
#if DEBUG
        _diagnostic = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetIntExtra("glass_diagnostic", 0) ?? 0;
#endif
        try { _shader = new RuntimeShader(ShaderCode); }
        catch (Exception ex) { Android.Util.Log.Error("TGRealGlass", "Shader unavailable: " + ex.GetType().Name); _failed = true; }
        SetWillNotDraw(false);
        ImportantForAccessibility = ImportantForAccessibility.No;
        Clickable = false;
        _tick = new TickRunner(this);
    }
    public void Update(GlassSurface model)
    {
        if (!ReferenceEquals(_model, model))
        {
            if (_model is not null) { _model.BackdropInvalidated -= SourceChanged; if (_model.BackdropSource is not null) _model.BackdropSource.HandlerChanged -= BackdropHandlerChanged; }
            _model = model;
            _model.BackdropInvalidated += SourceChanged;
            if (_model.BackdropSource is not null) _model.BackdropSource.HandlerChanged += BackdropHandlerChanged;
            _dirty = true; _availability = null;
        }
        if (_lastDark != model.IsDark) { _lastDark = model.IsDark; _dirty = true; }
        double targetPosition = model.SelectionPosition >= 0 ? model.SelectionPosition : model.SelectedIndex;
        double left = model.IsSlider ? model.Progress : targetPosition / 4.0;
        double right = model.IsSlider ? left : left + .25;
        bool selectionChanged = !model.IsSlider && _lastSelection >= 0 && _lastSelection != model.SelectedIndex;
        bool targetChanged = _started && Math.Abs(left - _left.Target) > .000001;
        if (!model.IsSlider) _lastSelection = model.SelectedIndex;
        if (model.IsSlider) { _left.Response = .13; _left.Damping = 1; }
        else if (targetChanged)
        {
            // During drag, compare to the previous target so reversal swaps the
            // leading/trailing edge immediately without discarding either velocity.
            bool forward = model.IsPressed ? left >= _left.Target : left >= (_left.Value + _right.Value - .25) * .5;
            _left.Response = model.IsPressed ? (forward ? .19 : .12) : forward ? .34 : .25;
            _left.Damping = model.IsPressed ? (forward ? .88 : .85) : forward ? .84 : .86;
            _right.Response = model.IsPressed ? (forward ? .12 : .19) : forward ? .25 : .34;
            _right.Damping = model.IsPressed ? (forward ? .85 : .88) : forward ? .86 : .84;
        }
        if (!_started || model.ReduceMotion || model.ReduceTransparency || !Android.Animation.ValueAnimator.AreAnimatorsEnabled())
        { _left.Snap(left); _right.Snap(right); _lift.Snap(0); _started = true; _autoRelease = 0; }
        else
        {
            _left.Target = left; _right.Target = right;
            if (model.IsPressed)
            {
                if (!_wasPressed) { _lift.Response = .20; _lift.Damping = .86; }
                _autoRelease = 0; _lift.Target = 1;
            }
            else if (_wasPressed)
            {
                _lift.Response = .34; _lift.Damping = .88;
                if (_lift.Value < .1)
                { _lift.Response = .18; _lift.Target = .65; _autoRelease = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * .14); }
                else { _autoRelease = 0; _lift.Target = 0; }
            }
            else if (selectionChanged && model.SelectionPosition < 0)
            { _lift.Response = .20; _lift.Damping = .86; _autoRelease = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * .18); _lift.Target = 1; }
            // A density/theme/layout update must not restart or cancel an in-flight lift.
        }
        _wasPressed = model.IsPressed;
        Invalidate();
        ResumePending();
    }
    void SourceChanged() { _dirty = true; Invalidate(); }
    void BackdropHandlerChanged(object? sender, EventArgs args) => SourceChanged();
    void ReportAvailability(bool available)
    {
        if (_availability == available) return;
        _availability = available;
        var version = ++_availabilityVersion;
        Post(() =>
        {
            if (_stopped || version != _availabilityVersion) return;
            _model?.ReportRendererAvailability(available);
            if (available) { _dirty = true; Invalidate(); }
        });
    }
    void ReleaseSource()
    {
        if (_sourceReleased) return;
        _source.SetRenderEffect(null);
        _effect?.Dispose(); _effect = null;
        _dockBlur?.Dispose(); _dockBlur = null; _dockBlurDpr = 0;
        _source.DiscardDisplayList(); _dirty = true; _sourceReleased = true;
    }
    void ResumePending()
    {
        if (_stopped || !IsAttachedToWindow || WindowVisibility != ViewStates.Visible || _scheduled || _model is null) return;
        if (_left.Value == _left.Target && _right.Value == _right.Target && _lift.Value == _lift.Target && _autoRelease == 0) return;
        _scheduled = true; _last = Stopwatch.GetTimestamp(); PostOnAnimation(_tick);
    }
    void Tick()
    {
        if (_stopped || !IsAttachedToWindow || WindowVisibility != ViewStates.Visible) { _scheduled = false; return; }
        long now = Stopwatch.GetTimestamp(); double dt = (now - _last) / (double)Stopwatch.Frequency; _last = now;
        if (_autoRelease != 0 && now >= _autoRelease) { _autoRelease = 0; _lift.Target = 0; }
        if (!Android.Animation.ValueAnimator.AreAnimatorsEnabled()) { _left.Snap(_left.Target); _right.Snap(_right.Target); _lift.Snap(0); _autoRelease = 0; }
        bool a = _left.Step(dt), b = _right.Step(dt), c = _lift.Step(dt);
        Invalidate();
        if (a || b || c || _autoRelease != 0) PostOnAnimation(_tick); else _scheduled = false;
    }
    protected override void OnSizeChanged(int w, int h, int oldw, int oldh) { base.OnSizeChanged(w, h, oldw, oldh); _dirty = true; }
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_stopped || _model is null || Width <= 0 || Height <= 0) return;
        if (_model.ReduceTransparency || !canvas.IsHardwareAccelerated || _failed || _shader is null)
        { ReleaseSource(); ReportAvailability(false); DrawFallback(canvas); return; }
        if (_model.BackdropSource?.Handler?.PlatformView is not Android.Views.View source || source.Width <= 0)
        { ReleaseSource(); ReportAvailability(false); DrawFallback(canvas); return; }
        try
        {
            float dpr = Resources?.DisplayMetrics?.Density ?? 1;
            int top = Math.Max(0, Height - (int)Math.Ceiling((GlassDockMetrics.Height + GlassDockMetrics.BottomInset + 44) * dpr));
            if (_dirty)
            {
                int[] here = new int[2], there = new int[2]; GetLocationOnScreen(here); source.GetLocationOnScreen(there);
                _source.SetPosition(0, top, Width, Height);
                _sourceReleased = false;
                var recording = _source.BeginRecording(Width, Height - top);
                try
                {
                    recording.ClipRect(0, 0, Width, Height - top);
                    recording.DrawColor(_model.IsDark ? Android.Graphics.Color.Rgb(23, 33, 43) : Android.Graphics.Color.White);
                    recording.Save(); recording.Translate(there[0] - here[0] - source.ScrollX, there[1] - here[1] - top - source.ScrollY);
                    source.Draw(recording);
                    recording.Restore();
                }
                finally { _source.EndRecording(); }
                _dirty = false; _sourceReleased = false;
            }
            _shader.SetFloatUniform("uSize", Width, Height);
            _shader.SetFloatUniform("uDpr", dpr);
            _shader.SetFloatUniform("uOrigin", top);
            _shader.SetFloatUniform("uDiagnostic", _diagnostic);
            _shader.SetFloatUniform("uState", (float)_left.Value, (float)_right.Value, (float)_lift.Value, (float)_model.Density);
            _shader.SetFloatUniform("uFlags", _model.IsDark ? 1f : 0f, _model.ReduceTransparency ? 1f : 0f, _model.IsSlider ? 1f : 0f);
            _shader.SetFloatUniform("uInsets", (float)_model.SliderInsets.Left, (float)_model.SliderInsets.Right);
            _shader.SetFloatUniform("uDock", (float)GlassDockMetrics.Height, (float)GlassDockMetrics.BottomInset,
                (float)GlassDockMetrics.HorizontalInset, (float)GlassDockMetrics.MaximumWidth);
            _shader.SetFloatUniform("uSelectInset", (float)GlassDockMetrics.SelectedInsetX, (float)GlassDockMetrics.SelectedInsetY);
            _shader.SetFloatUniform("uVelocity", (float)_left.Velocity);
            RenderEffect? nextEffect = null, previousBlur = null;
            try
            {
                if (!_model.IsSlider && _diagnostic != 2)
                {
                    if (_dockBlur is null || _dockBlurDpr != dpr)
                    {
                        var blur = RenderEffect.CreateBlurEffect(3 * dpr, 3 * dpr, Shader.TileMode.Clamp!);
                        previousBlur = _dockBlur; _dockBlur = blur; _dockBlurDpr = dpr;
                    }
                    using var runtime = RenderEffect.CreateRuntimeShaderEffect(_shader, "content");
                    nextEffect = RenderEffect.CreateChainEffect(runtime, _dockBlur);
                }
                else nextEffect = RenderEffect.CreateRuntimeShaderEffect(_shader, "content");
                _source.SetRenderEffect(nextEffect);
                _effect?.Dispose(); _effect = nextEffect; nextEffect = null;
            }
            finally { nextEffect?.Dispose(); previousBlur?.Dispose(); }
            canvas.DrawRenderNode(_source);
            ReportAvailability(true);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("TGRealGlass", "Rendering unavailable: " + ex.GetType().Name);
            _failed = true; ReleaseSource(); ReportAvailability(false);
            DrawFallback(canvas);
        }
    }
    void DrawFallback(Canvas canvas)
    {
        if (_model is null || _model.IsSlider) return; // Native SeekBar remains available.
        float d = Resources?.DisplayMetrics?.Density ?? 1;
        float width = (float)GlassDockMetrics.WidthFor(Width / d) * d;
        float x = (Width - width) / 2;
        float top = Height - (float)(GlassDockMetrics.BottomInset + GlassDockMetrics.Height) * d;
        float height = (float)GlassDockMetrics.Height * d;
        using var paint = new Android.Graphics.Paint { AntiAlias = true, Color = _model.IsDark ? Android.Graphics.Color.Rgb(35, 46, 60) : Android.Graphics.Color.Rgb(241, 245, 248) };
        canvas.DrawRoundRect(x, top, x + width, top + height, height / 2, height / 2, paint);
        paint.Color = _model.IsDark ? Android.Graphics.Color.Rgb(49, 91, 123) : Android.Graphics.Color.Rgb(200, 228, 246);
        float left = x + width * (float)_left.Value + (float)GlassDockMetrics.SelectedInsetX * d;
        float right = x + width * (float)_right.Value - (float)GlassDockMetrics.SelectedInsetX * d;
        float insetY = (float)GlassDockMetrics.SelectedInsetY * d;
        if (right > left) canvas.DrawRoundRect(left, top + insetY, right, top + height - insetY, (height - 2 * insetY) / 2, (height - 2 * insetY) / 2, paint);
    }
    protected override void OnDetachedFromWindow()
    {
        RemoveCallbacks(_tick); _scheduled = false;
        if (!_stopped) { ReleaseSource(); ReportAvailability(false); }
        base.OnDetachedFromWindow();
    }
    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        if (!_stopped) { _dirty = true; Invalidate(); ResumePending(); }
    }
    protected override void OnWindowVisibilityChanged(ViewStates visibility)
    {
        base.OnWindowVisibilityChanged(visibility);
        if (_stopped) return;
        if (visibility != ViewStates.Visible) { RemoveCallbacks(_tick); _scheduled = false; ReleaseSource(); ReportAvailability(false); }
        else { _dirty = true; Invalidate(); ResumePending(); }
    }
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true; ++_availabilityVersion; RemoveCallbacks(_tick);
        if (_model is not null) { _model.ReportRendererAvailability(false); _model.BackdropInvalidated -= SourceChanged; if (_model.BackdropSource is not null) _model.BackdropSource.HandlerChanged -= BackdropHandlerChanged; }
        ReleaseSource(); _model = null;
        _shader?.Dispose(); _source.Dispose();
    }
    sealed class TickRunner(NativeRealGlass owner) : Java.Lang.Object, Java.Lang.IRunnable { public void Run() => owner.Tick(); }

    const string ShaderCode = """
uniform shader content;
uniform float2 uSize;
uniform float uDpr;
uniform float uOrigin;
uniform float uDiagnostic;
uniform float4 uState;
uniform float3 uFlags;
uniform float2 uInsets;
uniform float4 uDock;
uniform float2 uSelectInset;
uniform float uVelocity;
float box(float2 p,float2 b,float r) { float2 q=abs(p)-b+r; return min(max(q.x,q.y),0.0)+length(max(q,0.0))-r; }
float profile(float t) { return pow(max(0.0,1.0-pow(1.0-t,3.0)),1.0/3.0); }
float bend(float t) { float a=max(t-.002,0.0), b=min(t+.002,1.0); float ang=t<.001?1.5707963:atan((profile(b)-profile(a))/(b-a)); return tan(ang-asin(sin(ang)/1.5))/tan(1.5707963-asin(1.0/1.5)); }
half4 main(float2 pos) {
 float d=uDpr; float2 p=(pos+float2(0.0,uOrigin))/d, size=uSize/d;
 bool slider=uFlags.z>.5;
 float up=clamp(uState.z,0.0,1.0);
 float dockW=max(0.0,min(size.x-2.0*uDock.z,uDock.w));
 float dockX=(size.x-dockW)*.5, dockY=size.y-uDock.y-uDock.x*.5;
 float sliderX=uInsets.x+(size.x-uInsets.x-uInsets.y)*clamp(uState.x,0.0,1.0);
 float stretch=1.0+min(.20,abs(uVelocity)*.06)*up;
 float radius=12.0+up*14.0;
 // Keep the native value/centre exact at both ends; constrain only the lens size.
 radius=min(radius,max(1.0,(min(sliderX,size.x-sliderX)-1.0)/stretch));
 float2 center=slider?float2(sliderX,size.y*.5):float2(size.x*.5,dockY);
 float2 halfSize=slider?float2(radius*stretch,radius/sqrt(stretch)):float2(dockW*.5,uDock.x*.5);
 float shapeRadius=slider?radius/sqrt(stretch):uDock.x*.5;
 float dist=box(p-center,halfSize,shapeRadius);
 float segW=dockW*.25, baseW=max(1.0,segW-2.0*uSelectInset.x);
 float rawW=(uState.y-uState.x)*dockW-2.0*uSelectInset.x;
 float selW=clamp(rawW,baseW*.84,baseW*1.24)+up*12.0;
 float selH=uDock.x-2.0*uSelectInset.y+up*16.0;
 float2 sc=float2(dockX+(uState.x+uState.y)*dockW*.5,dockY);
 float2 sb=float2(max(1.0,selW*.5),max(1.0,selH*.5));
 float sradius=min(min(sb.x,sb.y),26.0+up*9.0);
 float sd=box(p-sc,sb,sradius);
 float activeDist=slider?dist:min(dist,sd);
 // Composite a soft shadow underneath the antialiased shape on both sides of
 // its edge; this keeps premultiplied alpha continuous at activeDist == 1.
 float shadow=(1.0-smoothstep(1.0,8.0,activeDist))*(uFlags.x>.5?.15:.04);
 if(activeDist>1.0) return half4(0.0,0.0,0.0,half(shadow));
 float alpha=1.0-smoothstep(-1.0,1.0,activeDist);
 float2 rel=p-center;
 float2 normal=normalize(float2(box(rel+float2(.5,0.0),halfSize,shapeRadius)-box(rel-float2(.5,0.0),halfSize,shapeRadius),box(rel+float2(0.0,.5),halfSize,shapeRadius)-box(rel-float2(0.0,.5),halfSize,shapeRadius))+float2(.0001));
 float2 displacement=-normal*bend(clamp(-dist/18.0,0.0,1.0))*(slider?26.0*smoothstep(0.0,.65,up):9.0);
 float selection=slider?0.0:1.0-smoothstep(-1.0,1.0,sd);
 if (!slider && sd<0.0) {
   float2 sr=p-sc;
   float2 sn=normalize(float2(box(sr+float2(.5,0.0),sb,sradius)-box(sr-float2(.5,0.0),sb,sradius),box(sr+float2(0.0,.5),sb,sradius)-box(sr-float2(0.0,.5),sb,sradius))+float2(.0001));
   displacement=-sn*bend(clamp(-sd/13.0,0.0,1.0))*(9.0+up*18.0);
 }
 if(uDiagnostic==1.0) displacement=float2(0.0);
 float2 uv=pos+displacement*d;
 half4 sample;
 if(uDiagnostic==2.0) sample=content.eval(uv);
 else if(slider) {
   float r=.8*d;
   sample=(content.eval(uv)*4.0+content.eval(uv+float2(r,0))+content.eval(uv-float2(r,0))+content.eval(uv+float2(0,r))+content.eval(uv-float2(0,r)))/8.0;
 } else sample=content.eval(uv);
 half3 tint=slider?mix(half3(.90,.96,1.0),half3(.27,.38,.46),half(uFlags.x)):mix(half3(.97,.985,1.0),half3(.16,.21,.26),half(uFlags.x));
 // Retain a clear, raised selected lens while giving stationary dock labels
 // a little more separation from moving text underneath.
 half mixAmount=half(slider?.06+uState.w*.18:mix(.36,.20,selection*up)+uState.w*.30);
 half3 rgb=mix(sample.rgb,tint,mixAmount);
 half3 selectedTint=mix(half3(.62,.83,.96),half3(.22,.48,.67),half(uFlags.x));
 rgb=mix(rgb,selectedTint,half(selection*(.30+up*.08)));
 if(slider) rgb=mix(half3(1.0),rgb,half(smoothstep(0.0,.8,up)));
 float rim=(1.0-smoothstep(0.0,2.0,abs(activeDist)))*(slider?.27+.14*up:.06+.06*up);
 rgb=mix(rgb,mix(half3(.39,.55,.64),half3(1.0),half(uFlags.x)),half(rim));
 // The selected capsule has its own edge even while it lies inside the dock union.
 float selectionRim=slider?0.0:(1.0-smoothstep(0.0,1.6,abs(sd)))*(.02+.12*up);
 rgb=mix(rgb,mix(half3(.40,.66,.81),half3(.77,.89,.96),half(uFlags.x)),half(selectionRim));
 if(uFlags.y>.5) rgb=mix(half3(.93,.94,.95),half3(.14,.18,.23),half(uFlags.x));
 return half4(rgb*half(alpha),half(alpha+shadow*(1.0-alpha)));
}
""";
}
#endif
