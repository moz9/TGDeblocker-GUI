using TGDeblocker.Appearance;

namespace TGDeblocker.Mobile.Rendering;

// Persistent native buttons own semantics; the separate GlassSurface owns the material.
internal sealed class NavigationBar : Grid
{
    readonly GlassSurface _surface;
    readonly Button[] _buttons = new Button[4];
    readonly Label[] _labels = new Label[4];
    readonly GraphicsView[] _icons = new GraphicsView[4];
    readonly NavigationGlyph[] _glyphs = new NavigationGlyph[4];
    int _selected, _preview = -1;
    bool _dark;
#if ANDROID
    readonly Android.Views.View?[] _nativeButtons = new Android.Views.View?[4];
    readonly EventHandler<Android.Views.View.TouchEventArgs>[] _touchHandlers = new EventHandler<Android.Views.View.TouchEventArgs>[4];
    int _pointer = -1, _touchOwner = -1;
    double _pointerPosition;
    bool _pointerInside;
#endif
    public event Action<int>? SelectionCommitted;

    public NavigationBar(GlassSurface surface)
    {
        _surface = surface;
        HeightRequest = GlassDockMetrics.Height;
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.End;
        Margin = new Thickness(0, 0, 0, GlassDockMetrics.BottomInset);
        ColumnSpacing = 0;
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var glyph = _glyphs[i] = new NavigationGlyph(i);
            var icon = _icons[i] = new GraphicsView { Drawable = glyph, WidthRequest = 25, HeightRequest = 25, InputTransparent = true, HorizontalOptions = LayoutOptions.Center };
            var label = _labels[i] = new Label { FontSize = 11.5, LineBreakMode = LineBreakMode.NoWrap, HorizontalTextAlignment = TextAlignment.Center, InputTransparent = true };
            var content = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
            content.SetValue(AutomationProperties.ExcludedWithChildrenProperty, true);
            content.Add(icon); content.Add(label);
            var button = _buttons[i] = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0, CornerRadius = 30, MinimumHeightRequest = 48, AutomationId = "NavigationTab" + i };
            button.Clicked += (_, _) => SelectionCommitted?.Invoke(index);
#if ANDROID
            _touchHandlers[i] = (_, args) => OnTouch(index, args);
            button.HandlerChanging += (_, _) => Detach(index);
            button.HandlerChanged += (_, _) => Attach(index);
#else
            button.Pressed += (_, _) => { _surface.SelectionPosition = index; _surface.SetSelectionPressed(true); };
            button.Released += (_, _) => CancelInteraction();
#endif
            var cell = new Grid();
            cell.Add(button); cell.Add(content);
            this.Add(cell, i);
        }
        Unloaded += (_, _) => CancelInteraction();
    }

    public void SetAvailableWidth(double width) => WidthRequest = GlassDockMetrics.WidthFor(width);

    public void Update(string[] labels, int selected, bool dark)
    {
        _selected = selected; _dark = dark;
        for (int i = 0; i < 4; i++)
        {
            _labels[i].Text = labels[i];
            SemanticProperties.SetDescription(_buttons[i], labels[i]);
#if ANDROID
            if (_nativeButtons[i] is { } native && native.PeerReference.IsValid) native.Selected = i == selected;
#endif
        }
        UpdateColors();
    }

    void UpdateColors()
    {
        int active = _preview >= 0 ? _preview : _selected;
        var palette = TelegramPalette.For(_dark);
        for (int i = 0; i < 4; i++)
        {
            bool selected = i == active;
            var color = Color.FromArgb(selected ? palette.Link : palette.Text);
            _labels[i].TextColor = color;
            _labels[i].FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
            _glyphs[i].Color = color;
            _glyphs[i].Selected = selected;
            _icons[i].Invalidate();
        }
    }

    public void CancelInteraction()
    {
#if ANDROID
        if (_touchOwner >= 0 && _nativeButtons[_touchOwner] is { } owner && owner.PeerReference.IsValid)
            owner.Parent?.RequestDisallowInterceptTouchEvent(false);
        _pointer = _touchOwner = -1;
#endif
        _surface.SelectionPosition = -1;
        _surface.SetSelectionPressed(false);
        _preview = -1;
        UpdateColors();
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == IsEnabledProperty.PropertyName && !IsEnabled && _surface is not null)
            CancelInteraction();
    }

#if ANDROID
    void Attach(int index)
    {
        Detach(index);
        if (_buttons[index].Handler?.PlatformView is not Android.Views.View native) return;
        _nativeButtons[index] = native;
        native.Touch += _touchHandlers[index];
        native.Selected = index == _selected;
        if (native is Google.Android.Material.Button.MaterialButton material)
            material.RippleColor = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        native.Elevation = 0;
        native.StateListAnimator = null;
    }

    void Detach(int index)
    {
        if (_touchOwner == index) CancelInteraction();
        if (_nativeButtons[index] is { } native && native.PeerReference.IsValid)
            native.Touch -= _touchHandlers[index];
        _nativeButtons[index] = null;
    }

    void OnTouch(int index, Android.Views.View.TouchEventArgs args)
    {
        var touch = args.Event;
        if (touch is null || !IsEnabled || Width <= 0) return;
        args.Handled = true;
        var native = _nativeButtons[index];
        if (native is null || !native.PeerReference.IsValid) return;
        float density = native.Resources?.DisplayMetrics?.Density ?? 1;
        switch (touch.ActionMasked)
        {
            case Android.Views.MotionEventActions.Down:
                CancelInteraction();
                _pointer = touch.GetPointerId(0); _touchOwner = index;
                _pointerPosition = index; _pointerInside = true; _preview = index;
                native.Parent?.RequestDisallowInterceptTouchEvent(true);
                _surface.SelectionPosition = index;
                _surface.SetSelectionPressed(true);
                UpdateColors();
                break;
            case Android.Views.MotionEventActions.Move:
                if (_pointer < 0 || _touchOwner != index) return;
                int pointerIndex = touch.FindPointerIndex(_pointer);
                if (pointerIndex < 0) { CancelInteraction(); return; }
                double x = index * Width / 4 + touch.GetX(pointerIndex) / density;
                double y = touch.GetY(pointerIndex) / density;
                _pointerInside = y >= -20 && y <= Height + 20 && x >= -24 && x <= Width + 24;
                _pointerPosition = Math.Clamp(x / (Width / 4) - .5, 0, 3);
                _surface.SelectionPosition = _pointerInside ? _pointerPosition : _selected;
                int preview = _pointerInside ? (int)Math.Round(_pointerPosition, MidpointRounding.AwayFromZero) : _selected;
                if (_preview != preview) { _preview = preview; UpdateColors(); }
                break;
            case Android.Views.MotionEventActions.Up:
                if (_pointer < 0 || _touchOwner != index) return;
                int upIndex = touch.FindPointerIndex(_pointer);
                if (upIndex < 0) { CancelInteraction(); return; }
                double endX = index * Width / 4 + touch.GetX(upIndex) / density;
                double endY = touch.GetY(upIndex) / density;
                _pointerInside = endY >= -20 && endY <= Height + 20 && endX >= -24 && endX <= Width + 24;
                _pointerPosition = Math.Clamp(endX / (Width / 4) - .5, 0, 3);
                int target = (int)Math.Round(_pointerPosition, MidpointRounding.AwayFromZero);
                bool commit = _pointerInside;
                // Commit before releasing the preview so the lens never retargets to the old tab.
                if (commit && _nativeButtons[target] is { } targetView && targetView.PeerReference.IsValid)
                    targetView.PerformClick();
                CancelInteraction();
                break;
            case Android.Views.MotionEventActions.Cancel:
            case Android.Views.MotionEventActions.PointerDown:
                CancelInteraction();
                break;
        }
    }
#endif

    sealed class NavigationGlyph(int index) : IDrawable
    {
        public Color Color = Colors.Black;
        public bool Selected;
        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.SaveState();
            canvas.Translate((rect.Width - 24) / 2, (rect.Height - 24) / 2);
            canvas.StrokeColor = Color; canvas.FillColor = Color;
            canvas.StrokeSize = Selected ? 2.1f : 1.85f;
            canvas.StrokeLineCap = LineCap.Round; canvas.StrokeLineJoin = LineJoin.Round;
            var path = new PathF();
            switch (index)
            {
                case 0:
                    if (Selected) canvas.FillCircle(12, 7, 3.4f); else canvas.DrawCircle(12, 7, 3.4f);
                    path.MoveTo(4, 21); path.CurveTo(4, 13, 20, 13, 20, 21);
                    canvas.DrawPath(path);
                    break;
                case 1:
                    canvas.DrawRoundedRectangle(1.5f, 3, 15, 11.5f, 1.8f);
                    canvas.DrawLine(8, 15, 8, 19); canvas.DrawLine(4.5f, 19, 11.5f, 19);
                    canvas.DrawRoundedRectangle(15, 9, 7, 12.5f, 1.6f);
                    canvas.DrawLine(17.8f, 18.5f, 19.2f, 18.5f);
                    break;
                case 2:
                    path.MoveTo(12, 2); path.LineTo(21, 5); path.LineTo(21, 11);
                    path.CurveTo(21, 16.5f, 16, 20.5f, 12, 22);
                    path.CurveTo(8, 20.5f, 3, 16.5f, 3, 11); path.LineTo(3, 5); path.Close();
                    canvas.DrawPath(path);
                    canvas.DrawLine(8, 11.8f, 10.8f, 14.5f); canvas.DrawLine(10.8f, 14.5f, 16.5f, 8.8f);
                    break;
                default:
                    for (int tooth = 0; tooth < 8; tooth++)
                        for (int point = 0; point < 4; point++)
                        {
                            double angle = (tooth * 45 + (point switch { 0 => -22.5, 1 => -13, 2 => 13, _ => 22.5 })) * Math.PI / 180;
                            double radius = point is 1 or 2 ? 10.2 : 8.1;
                            float x = 12 + (float)(Math.Cos(angle) * radius), y = 12 + (float)(Math.Sin(angle) * radius);
                            if (tooth == 0 && point == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                        }
                    path.Close(); canvas.DrawPath(path); canvas.DrawCircle(12, 12, 3.3f);
                    break;
            }
            canvas.RestoreState();
        }
    }
}
