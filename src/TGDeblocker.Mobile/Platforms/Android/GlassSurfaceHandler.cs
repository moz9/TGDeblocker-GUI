#if ANDROID
using Android.Content;
using Android.Opengl;
using Android.Views;
using Java.Nio;
using Javax.Microedition.Khronos.Egl;
using Javax.Microedition.Khronos.Opengles;
using Microsoft.Maui;
using Microsoft.Maui.Handlers;
using System.Diagnostics;

namespace TGDeblocker.Mobile.Rendering;

public sealed class GlassSurfaceHandler : ViewHandler<GlassSurface, Android.Views.View>
{
    public static readonly IPropertyMapper<GlassSurface, GlassSurfaceHandler> Mapper = new PropertyMapper<GlassSurface, GlassSurfaceHandler>(ViewMapper)
    {
        [nameof(GlassSurface.Density)] = Update,
        [nameof(GlassSurface.ReduceMotion)] = Update,
        [nameof(GlassSurface.ReduceTransparency)] = Update,
        [nameof(GlassSurface.SelectedIndex)] = Update,
        [nameof(GlassSurface.SelectionPosition)] = Update,
        [nameof(GlassSurface.IsPressed)] = Update,
        [nameof(GlassSurface.IsDark)] = Update,
        [nameof(GlassSurface.Progress)] = Update,
        [nameof(GlassSurface.IsSlider)] = Update,
        [nameof(GlassSurface.SliderInsets)] = Update,
    };
    public GlassSurfaceHandler() : base(Mapper) { }
    protected override Android.Views.View CreatePlatformView()
    {
        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context unavailable");
        return OperatingSystem.IsAndroidVersionAtLeast(33) ? new NativeRealGlass(context) : new NativeGlassSurface(context);
    }
    protected override void ConnectHandler(Android.Views.View platformView) { base.ConnectHandler(platformView); Update(this, VirtualView); }
    protected override void DisconnectHandler(Android.Views.View platformView) { if (platformView is NativeGlassSurface old) old.Stop(); else if (OperatingSystem.IsAndroidVersionAtLeast(33) && platformView is NativeRealGlass live) live.Stop(); base.DisconnectHandler(platformView); }
    static void Update(GlassSurfaceHandler handler, GlassSurface view) { if (handler.PlatformView is NativeGlassSurface old) old.Update(view); else if (OperatingSystem.IsAndroidVersionAtLeast(33) && handler.PlatformView is NativeRealGlass live) live.Update(view); }
}

public sealed class NativeGlassSurface : GLSurfaceView
{
    static WeakReference<NativeGlassSurface>? _pageSurface;
    readonly GlassRenderer _renderer;
    readonly TickRunner _tick;
    readonly ScrollListener _scrollListener;
    readonly GlassSpring _left = new(.34, .84), _right = new(.25, .86), _lift = new(.22, .86);
    bool _started, _scheduled, _stopped;
    long _last, _autoRelease;
    int _lastSelection = -1;
    bool _wasPressed;
    GlassSurface? _model;
    bool? _rendererStatus;

    public NativeGlassSurface(Context context) : base(context)
    {
        SetEGLContextClientVersion(2);
        _tick = new TickRunner(this);
        _scrollListener = new ScrollListener(this);
        int diagnostic = 0;
#if DEBUG
        diagnostic = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetIntExtra("glass_diagnostic", 0) ?? 0;
#endif
        _renderer = new GlassRenderer(Resources?.DisplayMetrics?.Density ?? 1f, diagnostic, available => Post(new StatusRunner(this, available)));
        SetRenderer(_renderer);
        RenderMode = Rendermode.WhenDirty;
        ImportantForAccessibility = ImportantForAccessibility.No;
        Clickable = false;
    }

    public void Update(GlassSurface model)
    {
        _model = model;
        if (!model.IsSlider) _pageSurface = new WeakReference<NativeGlassSurface>(this);
        if (_rendererStatus is bool available) model.ReportRendererAvailability(available);
        double targetPosition = model.SelectionPosition >= 0 ? model.SelectionPosition : model.SelectedIndex;
        var position = model.IsSlider ? model.Progress : targetPosition / 4.0;
        var right = model.IsSlider ? position : position + .25;
        bool selectionChanged = !model.IsSlider && _lastSelection >= 0 && _lastSelection != model.SelectedIndex;
        bool targetChanged = _started && Math.Abs(position - _left.Target) > .000001;
        if (!model.IsSlider) _lastSelection = model.SelectedIndex;
        if (model.IsSlider) { _left.Response = .13; _left.Damping = 1; }
        else if (targetChanged)
        {
            // During drag, compare to the previous target so reversal swaps the
            // leading/trailing edge immediately without discarding either velocity.
            bool forward = model.IsPressed ? position >= _left.Target : position >= (_left.Value + _right.Value - .25) * .5;
            _left.Response = model.IsPressed ? (forward ? .19 : .12) : forward ? .34 : .25;
            _left.Damping = model.IsPressed ? (forward ? .88 : .85) : forward ? .84 : .86;
            _right.Response = model.IsPressed ? (forward ? .12 : .19) : forward ? .25 : .34;
            _right.Damping = model.IsPressed ? (forward ? .85 : .88) : forward ? .86 : .84;
        }
        if (!_started || model.ReduceMotion || model.ReduceTransparency || !Android.Animation.ValueAnimator.AreAnimatorsEnabled())
        { _left.Snap(position); _right.Snap(right); _lift.Snap(0); _autoRelease = 0; _started = true; }
        else
        {
            _left.Target = position; _right.Target = right;
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
        }
        _wasPressed = model.IsPressed;
        Render();
        if (!_scheduled && (_left.Value != _left.Target || _right.Value != _right.Target || _lift.Value != _lift.Target))
        {
            _scheduled = true; _last = Stopwatch.GetTimestamp(); PostOnAnimation(_tick);
        }
    }

    void Tick()
    {
        if (_stopped || !IsAttachedToWindow) { _scheduled = false; return; }
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _last) / (double)Stopwatch.Frequency; _last = now;
        if (_autoRelease != 0 && now >= _autoRelease) { _autoRelease = 0; _lift.Target = 0; }
        if (!Android.Animation.ValueAnimator.AreAnimatorsEnabled())
        {
            _left.Snap(_left.Target); _right.Snap(_right.Target); _lift.Snap(0); _autoRelease = 0;
        }
        var a = _left.Step(dt); var b = _right.Step(dt); var c = _lift.Step(dt);
        Render();
        if (a || b || c || _autoRelease != 0) PostOnAnimation(_tick); else _scheduled = false;
    }

    void Render()
    {
        if (_stopped || _model is null) return;
        var scale = Resources?.DisplayMetrics?.Density ?? 1f;
        float originX = 0, originY = 0, sceneWidth = Width / scale, sceneHeight = Height / scale;
        if (_model.IsSlider && _pageSurface?.TryGetTarget(out var page) == true && page.Width > 0 && page.Height > 0)
        {
            int[] here = new int[2], there = new int[2];
            GetLocationOnScreen(here); page.GetLocationOnScreen(there);
            originX = (here[0] - there[0]) / scale; originY = (here[1] - there[1]) / scale;
            sceneWidth = page.Width / scale; sceneHeight = page.Height / scale;
        }
        _renderer.SetState((float)_left.Value, (float)_right.Value, (float)_lift.Value,
            (float)_model.Density, _model.ReduceTransparency, _model.IsSlider, _model.IsDark, (float)_model.SliderInsets.Left, (float)_model.SliderInsets.Right,
            originX, originY, sceneWidth, sceneHeight, (float)_left.Velocity);
        RequestRender();
    }
    public void Stop() { _stopped = true; _model = null; RemoveCallbacks(_tick); _scheduled = false; OnPause(); }
    protected override void OnAttachedToWindow() { base.OnAttachedToWindow(); ViewTreeObserver?.AddOnScrollChangedListener(_scrollListener); if (!_stopped) { OnResume(); if (_model is not null) Update(_model); } }
    protected override void OnDetachedFromWindow() { ViewTreeObserver?.RemoveOnScrollChangedListener(_scrollListener); RemoveCallbacks(_tick); _scheduled = false; OnPause(); base.OnDetachedFromWindow(); }
    protected override void OnSizeChanged(int w, int h, int oldw, int oldh) { base.OnSizeChanged(w, h, oldw, oldh); Render(); }
    sealed class TickRunner(NativeGlassSurface owner) : Java.Lang.Object, Java.Lang.IRunnable { public void Run() => owner.Tick(); }
    sealed class ScrollListener(NativeGlassSurface owner) : Java.Lang.Object, ViewTreeObserver.IOnScrollChangedListener { public void OnScrollChanged() { if (owner._model?.IsSlider == true) owner.Render(); } }
    sealed class StatusRunner(NativeGlassSurface owner, bool available) : Java.Lang.Object, Java.Lang.IRunnable { public void Run() { if (!owner._stopped) { owner._rendererStatus = available; owner._model?.ReportRendererAvailability(available); } } }
}

internal sealed class GlassRenderer(float density, int diagnostic, Action<bool> reportStatus) : Java.Lang.Object, GLSurfaceView.IRenderer
{
    readonly object _gate = new();
    readonly FloatBuffer _vertices = (FloatBuffer)ByteBuffer.AllocateDirect(8 * 4)!.Order(ByteOrder.NativeOrder()!)!.AsFloatBuffer()!.Put(new float[] { -1, -1, 1, -1, -1, 1, 1, 1 })!.Position(0)!;
    float _left, _right = .25f, _lift, _density = .28f, _insetLeft = 22, _insetRight = 22;
    float _originX, _originY, _sceneWidth = 1, _sceneHeight = 1, _velocity;
    bool _opaque, _slider, _dark;
    int _program, _width, _height;

    public void SetState(float left, float right, float lift, float materialDensity, bool opaque, bool slider, bool dark, float insetLeft, float insetRight, float originX, float originY, float sceneWidth, float sceneHeight, float velocity)
    { lock (_gate) { _left = left; _right = right; _lift = lift; _density = materialDensity; _opaque = opaque; _slider = slider; _dark = dark; _insetLeft = insetLeft; _insetRight = insetRight; _originX = originX; _originY = originY; _sceneWidth = Math.Max(1, sceneWidth); _sceneHeight = Math.Max(1, sceneHeight); _velocity = velocity; } }

    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        int vertex = Compile(GLES20.GlVertexShader, Vertex), fragment = Compile(GLES20.GlFragmentShader, Fragment);
        if (vertex == 0 || fragment == 0) { reportStatus(false); return; }
        _program = GLES20.GlCreateProgram(); GLES20.GlAttachShader(_program, vertex); GLES20.GlAttachShader(_program, fragment); GLES20.GlLinkProgram(_program);
        int[] ok = new int[1]; GLES20.GlGetProgramiv(_program, GLES20.GlLinkStatus, ok, 0);
        if (ok[0] == 0) { Android.Util.Log.Error("TGGlass", GLES20.GlGetProgramInfoLog(_program) ?? "Program link failed"); GLES20.GlDeleteProgram(_program); _program = 0; }
        GLES20.GlDeleteShader(vertex); GLES20.GlDeleteShader(fragment);
        reportStatus(_program != 0);
    }
    public void OnSurfaceChanged(IGL10? gl, int width, int height) { _width = width; _height = height; GLES20.GlViewport(0, 0, width, height); }
    public void OnDrawFrame(IGL10? gl)
    {
        float left, right, lift, material, insetLeft, insetRight, originX, originY, sceneWidth, sceneHeight, velocity; bool opaque, slider, dark;
        lock (_gate) { left = _left; right = _right; lift = _lift; material = _density; opaque = _opaque; slider = _slider; dark = _dark; insetLeft = _insetLeft; insetRight = _insetRight; originX = _originX; originY = _originY; sceneWidth = _sceneWidth; sceneHeight = _sceneHeight; velocity = _velocity; }
        if (dark) GLES20.GlClearColor(.090f, .129f, .169f, 1); else GLES20.GlClearColor(1, 1, 1, 1);
        GLES20.GlClear(GLES20.GlColorBufferBit);
        if (_program == 0 || _width <= 0 || _height <= 0) return;
        GLES20.GlUseProgram(_program);
        int a = GLES20.GlGetAttribLocation(_program, "aPos");
        _vertices.Position(0); GLES20.GlEnableVertexAttribArray(a); GLES20.GlVertexAttribPointer(a, 2, GLES20.GlFloat, false, 0, _vertices);
        GLES20.GlUniform2f(GLES20.GlGetUniformLocation(_program, "uSize"), _width / density, _height / density);
        GLES20.GlUniform1f(GLES20.GlGetUniformLocation(_program, "uDpr"), density);
        GLES20.GlUniform4f(GLES20.GlGetUniformLocation(_program, "uState"), left, right, lift, material);
        GLES20.GlUniform2f(GLES20.GlGetUniformLocation(_program, "uFlags"), opaque ? 1 : 0, slider ? 1 : 0);
        GLES20.GlUniform2f(GLES20.GlGetUniformLocation(_program, "uInsets"), insetLeft, insetRight);
        GLES20.GlUniform4f(GLES20.GlGetUniformLocation(_program, "uDock"), (float)GlassDockMetrics.Height,
            (float)GlassDockMetrics.BottomInset, (float)GlassDockMetrics.HorizontalInset, (float)GlassDockMetrics.MaximumWidth);
        GLES20.GlUniform2f(GLES20.GlGetUniformLocation(_program, "uSelectInset"), (float)GlassDockMetrics.SelectedInsetX, (float)GlassDockMetrics.SelectedInsetY);
        GLES20.GlUniform4f(GLES20.GlGetUniformLocation(_program, "uScene"), originX, originY, sceneWidth, sceneHeight);
        GLES20.GlUniform1f(GLES20.GlGetUniformLocation(_program, "uDiagnostic"), diagnostic);
        GLES20.GlUniform1f(GLES20.GlGetUniformLocation(_program, "uDark"), dark ? 1 : 0);
        GLES20.GlUniform1f(GLES20.GlGetUniformLocation(_program, "uVelocity"), velocity);
        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4); GLES20.GlDisableVertexAttribArray(a);
    }
    static int Compile(int type, string code)
    {
        int id = GLES20.GlCreateShader(type); GLES20.GlShaderSource(id, code); GLES20.GlCompileShader(id);
        int[] ok = new int[1]; GLES20.GlGetShaderiv(id, GLES20.GlCompileStatus, ok, 0);
        if (ok[0] != 0) return id;
        Android.Util.Log.Error("TGGlass", GLES20.GlGetShaderInfoLog(id) ?? "Shader compile failed"); GLES20.GlDeleteShader(id); return 0;
    }

    const string Vertex = "attribute vec2 aPos; void main(){ gl_Position=vec4(aPos,0.0,1.0); }";
    // Adapted from the skill's liquid-glass-webgl.js: rounded SDF, convex profile,
    // Snell edge displacement and sampled blurred source. It samples only this own scene.
    const string Fragment = """
precision highp float;
uniform vec2 uSize; uniform float uDpr; uniform vec4 uState; uniform vec2 uFlags; uniform vec2 uInsets; uniform vec4 uDock; uniform vec2 uSelectInset; uniform float uDiagnostic; uniform float uDark; uniform vec4 uScene; uniform float uVelocity;
float box(vec2 p, vec2 b, float r){vec2 q=abs(p)-b+r;return min(max(q.x,q.y),0.0)+length(max(q,0.0))-r;}
float heightProfile(float t){return pow(max(0.0,1.0-pow(1.0-t,3.0)),1.0/3.0);}
float bend(float t){float a=max(t-.002,0.0),b=min(t+.002,1.0);float angle=t<.001?1.5707963:atan((heightProfile(b)-heightProfile(a))/(b-a));return tan(angle-asin(sin(angle)/1.5))/tan(1.5707963-asin(1.0/1.5));}
vec3 bg(vec2 p){
 p+=uScene.xy;
 if(uDiagnostic==3.0)p.x+=24.;
 vec3 c=mix(vec3(1.),vec3(23.,33.,43.)/255.,uDark);
 // Quiet, thin lines are part of the actual scene behind the bottom dock.
 float curve1=abs(p.y-(uScene.w-61.+7.*sin(p.x*.021)));
 float curve2=abs(p.y-(uScene.w-38.+6.*sin(p.x*.017+1.2)));
 float detail=max(1.-smoothstep(.6,1.8,curve1),1.-smoothstep(.6,1.8,curve2));
 return mix(c,mix(vec3(.62,.77,.85),vec3(.34,.45,.53),uDark),detail*mix(.28,.27,uDark));
}
float shape(vec2 p,vec2 center,vec2 halfsize,float radius){return box(p-center,halfsize,radius);}
// The slider track belongs to the same sampled scene as the background, so
// the raised thumb refracts the real progress line rather than an unrelated image.
vec3 source(vec2 p){
 vec3 c=bg(p);if(uFlags.y<.5)return c;
 float start=uInsets.x,end=max(start,uSize.x-uInsets.y),x=mix(start,end,clamp(uState.x,0.,1.));
 vec2 mid=vec2((start+end)*.5,uSize.y*.5);
 float track=shape(p,mid,vec2(max(1.,(end-start)*.5),3.),3.);
 c=mix(c,mix(vec3(225.,234.,239.)/255.,vec3(54.,69.,84.)/255.,uDark),clamp(.5-track,0.,1.));
 float progress=shape(p,vec2((start+x)*.5,mid.y),vec2(max(1.,(x-start)*.5),3.),3.);
 return mix(c,mix(vec3(64.,167.,227.)/255.,vec3(106.,179.,243.)/255.,uDark),clamp(.5-progress,0.,1.));
}
vec3 blurred(vec2 p){vec3 c=source(p)*.28;c+=(source(p+vec2(6.,0.))+source(p-vec2(6.,0.))+source(p+vec2(0.,6.))+source(p-vec2(0.,6.)))*.12;c+=(source(p+vec2(6.,6.))+source(p+vec2(-6.,6.))+source(p+vec2(6.,-6.))+source(p-vec2(6.,6.)))*.06;return c;}
vec3 glass(vec2 p,vec2 center,vec2 halfsize,float radius,vec3 base,float amount,float selected){
 float d=shape(p,center,halfsize,radius);
 if(uFlags.x>.5){vec3 solid=mix(mix(vec3(241.)/255.,vec3(225.,234.,239.)/255.,selected),mix(vec3(35.,46.,60.)/255.,vec3(54.,69.,84.)/255.,selected),uDark);return mix(base,solid,clamp(.5-d,0.,1.));}
 // Soft, local contact shadow; foreground controls are outside this shader.
 float ds=shape(p-vec2(0.,3.),center,halfsize,radius);
 float shadow=(1.-smoothstep(0.,10.,ds))*smoothstep(0.,1.5,d)*mix(.13,.28,uDark);
 vec3 outside=mix(base,mix(vec3(.27,.40,.48),vec3(.01,.02,.03),uDark),shadow);
 if(d>1.5)return outside;
 vec2 e=vec2(.5,0.); vec2 n=normalize(vec2(shape(p+e.xy,center,halfsize,radius)-shape(p-e.xy,center,halfsize,radius),shape(p+e.yx,center,halfsize,radius)-shape(p-e.yx,center,halfsize,radius))+vec2(.00001));
 float t=clamp(-d/12.,0.,1.);vec2 off=-n*bend(t)*amount*(uDiagnostic==1.0?0.0:1.0);
 vec3 col=uDiagnostic==2.0?source(p+off):blurred(p+off);
 col=mix(col,mix(vec3(.84,.93,.98),vec3(.26,.37,.45),uDark),mix(.10,.16,uDark)+uState.w*mix(.22,.14,uDark));
 col=mix(col,mix(vec3(.62,.83,.96),vec3(.22,.48,.67),uDark),selected*(1.-uFlags.y)*(.30+clamp(uState.z,0.,1.)*.08));
 float rim=1.-smoothstep(0.,1.6,-d); float facing=dot(n,normalize(vec2(-.6,-.8)));
 float sheen=pow(1.-t,3.)*(.5+.5*facing);
 col+=rim*(mix(.08,.06,uDark)+mix(.09,.08,uDark)*max(facing,0.))+sheen*mix(.045,.07,uDark);
 return mix(outside,clamp(col,0.,1.),clamp(.5-d,0.,1.));
}
void main(){
 vec2 p=vec2(gl_FragCoord.x,uSize.y*uDpr-gl_FragCoord.y)/uDpr; vec3 c=source(p);
 if(uFlags.y>.5){
  float start=uInsets.x, end=max(start,uSize.x-uInsets.y); float x=mix(start,end,clamp(uState.x,0.,1.)); vec2 mid=vec2((start+end)*.5,uSize.y*.5);
  float up=clamp(uState.z,0.,1.);float radius=12.+up*14.;float stretch=1.+min(.20,abs(uVelocity)*.06)*up;
  radius=min(radius,max(1.,(min(x,uSize.x-x)-1.)/stretch));
  c=glass(p,vec2(x,mid.y),vec2(radius*stretch,radius/sqrt(stretch)),radius/sqrt(stretch),c,26.*smoothstep(0.,.65,up),1.);
 } else {
  float w=max(0.,min(uSize.x-2.*uDock.z,uDock.w));float x=(uSize.x-w)*.5;
  vec2 mid=vec2(uSize.x*.5,uSize.y-uDock.y-uDock.x*.5);c=glass(p,mid,vec2(w*.5,uDock.x*.5),uDock.x*.5,c,10.,0.);
  float up=clamp(uState.z,0.,1.);float l=clamp(uState.x,0.,1.),r=clamp(uState.y,0.,1.);
  float basew=max(1.,w*.25-2.*uSelectInset.x);float raww=(r-l)*w-2.*uSelectInset.x;
  float halfw=max(1.,(clamp(raww,basew*.84,basew*1.24)+up*12.)*.5);
  float halfh=max(1.,(uDock.x-2.*uSelectInset.y+up*16.)*.5);
  c=glass(p,vec2(x+(l+r)*w*.5,mid.y),vec2(halfw,halfh),min(23.+up*12.,min(halfw,halfh)),c,9.+up*18.,1.);
 }
 gl_FragColor=vec4(c,1.);
}
""";
}
#endif
