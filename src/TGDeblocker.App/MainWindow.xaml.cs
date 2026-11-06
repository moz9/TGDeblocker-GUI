using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TGDeblocker.Core;
using TGDeblocker.Appearance;
using TGDeblocker.App.Rendering;
using TGDeblocker.App.Updating;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TGDeblocker.App;

public sealed partial class MainWindow : Window
{
    private static readonly bool VisualCheck = Environment.GetCommandLineArgs().Contains("--visual-check");
    private readonly AppearanceSettings _appearance = VisualCheck ? new() : AppearanceSettings.Load();
    private readonly UISettings _ui = new();
    private readonly AccessibilitySettings _accessibility = new();
    private ITelegramService? _service;
    private Action<TelegramStatus>? _statusHandler;
    private TelegramAccount? _account;
    private int _viewRevision;
    private bool _pendingReady;
    private string? _pendingPhone;
    private string? _expectedPhone;
    private ConnectionSettings? _connection = VisualCheck ? null : ConnectionSettings.Load();
    private string _currentPage = "home";
    private bool _busy, _closed, _loginRequested;
    private bool _skipSavedSession;
    private bool _closing, _allowClose, _updateHandoff;
    private Task? _serviceShutdown;
    private TaskCompletionSource? _operationFinished;
    private CancellationTokenSource? _operation;
    private Slider? _densitySlider;
    private ContentDialog? _activeDialog;
    private LiveGlassLayer? _liveGlass;
    private string? _liveGlassStartupError;
    private bool _nativeGlassFallback;
    private readonly List<double> _liveUpdateMilliseconds = [];
    private bool _sliderPressed;
    private bool _sliderDirectTouch;
    private bool _highContrastSubscribed, _colorSubscribed, _systemDark;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _accessibilityPoll;
    private bool _lastHighContrast;
    private string? _visualTracePath;
    private readonly List<object> _visualTrace = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualTraceTimer;
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed, FontSize = 12 };
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    private readonly SolidColorBrush _accent = Brush(40, 106, 232);
    private readonly SolidColorBrush _muted = Brush(86, 101, 124);
    private readonly SolidColorBrush _surface = Brush(250, 252, 255);
    private readonly SolidColorBrush _border = Brush(221, 229, 241);
    private readonly SolidColorBrush _track = Brush(225, 234, 239);
    private readonly SolidColorBrush _trackValue = Brush(64, 167, 227);
    private SolidColorBrush Accent => _accent;
    private SolidColorBrush Muted => _muted;
    private bool _dark;
    private Button[] Tabs => [HomeTab, DevicesTab, SecurityTab, SettingsTab];
    private string Language => AppStrings.ResolveLanguage(_appearance.Language, System.Globalization.CultureInfo.CurrentUICulture);
    private string L(string original) => AppStrings.Text(original, Language);
    private void ApplyLanguage()
    {
        HomeLabel.Text = Language == "ru" ? "Начало" : "Home";
        DevicesLabel.Text = L("Устройства"); SecurityLabel.Text = L("Защита"); SettingsLabel.Text = L("Настройки");
        _cancel.Content = L("Отмена");
        StatusLabel.Text = _account != null ? (_service?.IsDemo == true ? L("Демо · ") : "") + AccountLabel
            : _service?.IsDemo == true ? L("Демо") : L("Аккаунт не подключён");
        Notice.IsOpen = false;
        UpdateGeometry();
    }

    public MainWindow()
    {
        InitializeComponent();
        Glass.LiveContentMode = true;
        Glass.FrameChanged += UpdateLiveGlass;
        if (VisualCheck)
            Root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.F6) { Glass.LensEnabled = !Glass.LensEnabled; e.Handled = true; }
                if (e.Key == Windows.System.VirtualKey.F7) { Glass.BlurEnabled = !Glass.BlurEnabled; e.Handled = true; }
            }), true);
        if (VisualCheck)
        {
            var args = Environment.GetCommandLineArgs();
            _appearance.Theme = args.Contains("--light") ? "light" : args.Contains("--dark") ? "dark" : "system";
        }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(420, VisualCheck && Environment.GetCommandLineArgs().Contains("--small") ? 520 : 590));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += OnAppWindowClosing;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 360;
            presenter.PreferredMinimumHeight = 520;
        }
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        Root.Loaded += OnLoaded;
        Root.SizeChanged += (_, _) => UpdateGeometry();
        Navigation.SizeChanged += (_, _) => UpdateGeometry();
        PageScroll.ViewChanged += (_, _) => UpdateSliderGeometry();
        foreach (var tab in Tabs)
        {
            tab.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
            {
                if (e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
                    Glass.SetSelectionPressed(true, e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch);
            }), true);
            foreach (var routedEvent in new[] { UIElement.PointerReleasedEvent, UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent })
                tab.AddHandler(routedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Glass.SetSelectionPressed(false)), true);
        }
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                Glass.SetSelectionPressed(false);
                _sliderPressed = false;
                UpdateSliderGeometry();
            }
        };
        _ui.AdvancedEffectsEnabledChanged += AccessibilityChanged;
        _ui.AnimationsEnabledChanged += AccessibilityChanged;
        try { _accessibility.HighContrastChanged += HighContrastChanged; _highContrastSubscribed = true; }
        catch (System.Runtime.InteropServices.COMException) { }
        try { _ui.ColorValuesChanged += ColorValuesChanged; _colorSubscribed = true; }
        catch (System.Runtime.InteropServices.COMException) { }
        _lastHighContrast = _accessibility.HighContrast;
        _systemDark = ReadSystemDark();
        if (!_highContrastSubscribed || !_colorSubscribed)
        {
            // Unpackaged Windows can reject WinRT notifications; poll only on that fallback.
            _accessibilityPoll = DispatcherQueue.CreateTimer();
            _accessibilityPoll.Interval = TimeSpan.FromMilliseconds(750);
            _accessibilityPoll.Tick += (_, _) =>
            {
                bool contrast = _accessibility.HighContrast, dark = ReadSystemDark();
                if (contrast != _lastHighContrast || dark != _systemDark)
                { _lastHighContrast = contrast; _systemDark = dark; ApplyAppearance(); }
            };
            _accessibilityPoll.Start();
        }
        Closed += OnClosed;
        _cancel.HorizontalAlignment = HorizontalAlignment.Left;
        _cancel.VerticalAlignment = VerticalAlignment.Top;
        _cancel.Margin = new Thickness(160, 0, 0, 0);
        _cancel.Click += (_, _) => _operation?.Cancel();
        Root.Children.Add(_cancel);
        ApplyAppearance();
        ApplyLanguage();
        ShowHome();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        Glass.LensEnabled = !args.Contains("--no-lens");
        Glass.BlurEnabled = !args.Contains("--no-blur");
        try { _liveGlass = LiveGlassLayer.Attach(ContentSource, GlassHost); }
        catch (Exception error) { _liveGlassStartupError = error.Message; UseNativeGlassFallback(); }
        UpdateLiveGlass(Glass.CurrentFrame);
        UpdateGeometry();
        if (args.Contains("--demo")) await RunAsync(StartDemoAsync);
        else if (args.Contains("--resume-session") && CanResumeSavedSession) await RunAsync(ResumeSavedSessionAsync);
        try { UpdatePaths.CleanOldStaging(AppIdentity.DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdateValidationException) { }
        var diagnosticIndex = Array.IndexOf(args, "--optics-output");
        if (diagnosticIndex >= 0 && diagnosticIndex + 1 < args.Length)
        {
            try
            {
                Directory.CreateDirectory(Path.GetFullPath(args[diagnosticIndex + 1]));
                _currentPage = "settings";
                await OpenCurrentPageAsync();
                await Task.Delay(800);
                // Live surfaces must be checked in the real window; legacy Win2D
                // captures intentionally do not represent this Composition path.
                if (!Glass.LiveContentMode) await Glass.SaveDiagnosticsAsync(Path.GetFullPath(args[diagnosticIndex + 1]));
                if (VisualCheck)
                {
                    _visualTracePath = Path.Combine(Path.GetFullPath(args[diagnosticIndex + 1]), "runtime.json");
                    _visualTraceTimer = DispatcherQueue.CreateTimer();
                    _visualTraceTimer.Interval = TimeSpan.FromMilliseconds(40);
                    _visualTraceTimer.Tick += (_, _) =>
                    {
                        if (_visualTrace.Count < 1500 && Glass.MotionState.TimerRunning)
                            _visualTrace.Add(new { At = Environment.TickCount64, Value = _densitySlider?.Value, State = Glass.MotionState });
                    };
                    _visualTraceTimer.Start();
                    WriteVisualTrace();
                }
            }
            catch (Exception error)
            {
                if (VisualCheck) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "visual-error.log"), error.ToString());
                ShowNotice(L("Не удалось сохранить проверку материала."), InfoBarSeverity.Warning);
            }
        }
    }

    private void AccessibilityChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyAppearance);
    private void UpdateLiveGlass(GlassFrameState frame)
    {
        long started = VisualCheck ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _liveGlass?.Update(frame);
        if (_liveGlass?.IsAvailable == false) UseNativeGlassFallback();
        if (VisualCheck && _liveGlass != null && _liveUpdateMilliseconds.Count < 3000)
            _liveUpdateMilliseconds.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void UseNativeGlassFallback()
    {
        _nativeGlassFallback = true;
        Navigation.Background = _surface;
        if (_densitySlider is null) return;
        _densitySlider.IsEnabled = false;
        _densitySlider.Resources["SliderThumbBackground"] = _trackValue;
        _densitySlider.Resources["SliderThumbBackgroundDisabled"] = _trackValue;
        _densitySlider.Resources["SliderOuterThumbBackground"] = _surface;
    }
    private void WriteVisualTrace()
    {
        if (_visualTracePath == null) return;
        File.WriteAllText(_visualTracePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            _ui.AdvancedEffectsEnabled, _ui.AnimationsEnabled,
            _appearance.Density, _appearance.Theme, _appearance.ReduceMotion, _appearance.ReduceTransparency,
            Glass.TransparencyEnabled, EffectiveReduceMotion = Glass.ReduceMotion, Glass.HighContrast, Glass.LastRenderError,
            LiveRenderError = _liveGlass?.LastRenderError,
            LiveStartupError = _liveGlassStartupError,
            LiveSamplingActive = _liveGlass?.SamplingActive,
            LiveSourceWidth = _liveGlass?.SourceSize.Width, LiveSourceHeight = _liveGlass?.SourceSize.Height,
            LivePatchCount = _liveGlass?.PatchCount,
            Glass.LensEnabled, Glass.BlurEnabled,
            UpdateMilliseconds = _liveUpdateMilliseconds,
            State = Glass.MotionState, Samples = _visualTrace
        }));
    }
    private void HighContrastChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyAppearance);
    private void ColorValuesChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyAppearance);
    private bool ReadSystemDark()
    {
        var color = _ui.GetColorValue(UIColorType.Background);
        return color.R + color.G + color.B < 384;
    }

    private void ApplyAppearance()
    {
        _systemDark = ReadSystemDark();
        _lastHighContrast = _accessibility.HighContrast;
        _dark = ThemePreference.IsDark(_appearance.Theme, _systemDark);
        bool contrast = _accessibility.HighContrast;
        Glass.HighContrast = contrast;
        Glass.IsDark = _dark;
        Glass.Density = _appearance.Density;
        Glass.TransparencyEnabled = _ui.AdvancedEffectsEnabled && !contrast && !_appearance.ReduceTransparency;
        Glass.ReduceMotion = !_ui.AnimationsEnabled || _appearance.ReduceMotion;
        Root.RequestedTheme = contrast ? ElementTheme.Default : _dark ? ElementTheme.Dark : ElementTheme.Light;
        if (_activeDialog != null) _activeDialog.RequestedTheme = Root.RequestedTheme;
        var palette = TelegramPalette.For(_dark);
        Root.Background = new SolidColorBrush(contrast ? _ui.GetColorValue(UIColorType.Background) : App.ParseColor(palette.Background));
        _accent.Color = contrast ? _ui.GetColorValue(UIColorType.Accent) : App.ParseColor(palette.Link);
        _muted.Color = contrast ? _ui.GetColorValue(UIColorType.Foreground) : App.ParseColor(palette.Muted);
        _surface.Color = contrast ? _ui.GetColorValue(UIColorType.Background) : App.ParseColor(palette.Surface);
        _border.Color = contrast ? _ui.GetColorValue(UIColorType.Foreground) : App.ParseColor(palette.Border);
        _track.Color = contrast ? _ui.GetColorValue(UIColorType.Foreground) : App.ParseColor(palette.Track);
        _trackValue.Color = contrast ? _ui.GetColorValue(UIColorType.Accent) : App.ParseColor(_dark ? palette.Link : palette.Accent);
        StatusLabel.Foreground = Muted;
        var captionForeground = contrast ? _ui.GetColorValue(UIColorType.Foreground) : App.ParseColor(palette.Text);
        AppWindow.TitleBar.ButtonForegroundColor = captionForeground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = captionForeground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = contrast ? _ui.GetColorValue(UIColorType.Accent) : App.ParseColor(palette.Surface);
        AppWindow.TitleBar.ButtonHoverForegroundColor = captionForeground;
        if (_densitySlider != null) _densitySlider.IsEnabled = Glass.TransparencyEnabled && !_nativeGlassFallback;
        UpdateGeometry();
    }

    private void SaveAppearance()
    {
        ApplyAppearance();
        if (VisualCheck) return;
        try { _appearance.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowNotice(L("Оформление изменено, но сохранить его на этом компьютере не удалось."), InfoBarSeverity.Warning); }
    }

    private void UpdateGeometry()
    {
        if (!Navigation.IsLoaded || Navigation.ActualWidth <= 0) return;
        var position = Navigation.TransformToVisual(Glass).TransformPoint(new Point());
        var selected = Tabs.First(t => (string)t.Tag == _currentPage);
        var selectedPosition = selected.TransformToVisual(Glass).TransformPoint(new Point());
        Glass.SetGeometry(new Rect(position.X - 6, position.Y - 6, Navigation.ActualWidth + 12, 58),
            new Rect(selectedPosition.X, selectedPosition.Y, selected.ActualWidth, 46));
        foreach (var tab in Tabs)
        {
            tab.Foreground = tab == selected ? Accent : Muted;
            AutomationProperties.SetHelpText(tab, tab == selected ? L("Текущий раздел") : L("Открыть раздел"));
        }
        UpdateSliderGeometry();
    }

    private void UpdateSliderGeometry()
    {
        if (_currentPage != "settings" || _densitySlider is not { IsLoaded: true } slider || slider.ActualWidth < 40)
        { Glass.SetSliderGeometry(Rect.Empty, Rect.Empty, false); return; }
        var thumb = FindPart(slider, "HorizontalThumb");
        var trackPart = FindPart(slider, "HorizontalTrackRect");
        if (thumb == null || trackPart == null) return;
        var point = thumb.TransformToVisual(Glass).TransformPoint(new Point());
        var trackPoint = trackPart.TransformToVisual(Glass).TransformPoint(new Point());
        var center = new Point(point.X + thumb.ActualWidth / 2, point.Y + thumb.ActualHeight / 2);
        // Follow the framework's actual thumb geometry, including header, DPI and scroll offsets.
        var viewport = PageScroll.TransformToVisual(Glass).TransformPoint(new Point());
        if (center.Y < viewport.Y || center.Y > viewport.Y + PageScroll.ActualHeight)
        { Glass.SetSliderGeometry(Rect.Empty, Rect.Empty, false); return; }
        Glass.SetSliderGeometry(new Rect(trackPoint.X, trackPoint.Y, trackPart.ActualWidth, trackPart.ActualHeight),
            new Rect(center.X - 12, center.Y - 12, 24, 24), _sliderPressed, _sliderDirectTouch);
    }

    private static FrameworkElement? FindPart(DependencyObject root, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            var found = FindPart(child, name); if (found != null) return found;
        }
        return null;
    }

    private async void NavigateClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _currentPage = (string)((Button)sender).Tag;
        Notice.IsOpen = false;
        await OpenCurrentPageAsync();
    }

    private async Task OpenCurrentPageAsync()
    {
        _sliderPressed = false;
        _densitySlider = null;
        PageScroll.ChangeView(null, 0, null);
        switch (_currentPage)
        {
            case "devices": await RunAsync(ShowDevicesAsync); break;
            case "security": await RunAsync(ShowSecurityAsync); break;
            case "settings": ShowSettings(); break;
            default: ShowHome(); break;
        }
        UpdateGeometry();
    }

    private TextBlock Text(string value, double size = 15, bool muted = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (muted) text.Foreground = Muted;
        return text;
    }
    private StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 16 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    private Border Card(UIElement child, double padding = 16) => new()
    {
        Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(16),
        Background = _surface,
        BorderBrush = _border,
        BorderThickness = new Thickness(1)
    };
    private Button Action(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = label, MinHeight = 38, Padding = new Thickness(14, 8, 14, 8), CornerRadius = new CornerRadius(12) };
        if (primary) { button.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; button.HorizontalAlignment = HorizontalAlignment.Stretch; }
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }
    private void Header(string eyebrow, string title, string subtitle)
    {
        _viewRevision++;
        Page.Spacing = 16;
        PageScroll.ChangeView(null, 0, null, true);
        Page.Children.Clear();
        var heading = Text(title, 22); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var header = Stack(heading); if (!string.IsNullOrWhiteSpace(subtitle)) header.Children.Add(Text(subtitle, 13, true)); header.Spacing = 6;
        Page.Children.Add(header);
    }
    private void ShowHome()
    {
        if (_loginRequested && _service?.Status.Step != AuthorizationStep.Ready) { ShowLogin(); return; }
        _viewRevision++; Page.Children.Clear(); Page.Spacing = 16;
        var mark = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")), Width = 68, Height = 68, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        Page.Children.Add(mark);
        var title = Text(_account == null ? L("Войти в Telegram") : _service?.IsDemo == true ? L(_account.FirstName) : _account.FirstName, 23); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center;
        Page.Children.Add(title);
        if (_account != null)
        {
            var label = Text(AccountLabel, 13, true); label.HorizontalAlignment = HorizontalAlignment.Center; Page.Children.Add(label);
            Page.Children.Add(Action(L("Проверить устройства"), async () => { _currentPage = "devices"; await ShowDevicesAsync(); UpdateGeometry(); }, true));
            Page.Children.Add(Action(L("Закрыть подключение"), DisconnectAsync));
            return;
        }
        var caption = Text(L("Проверьте устройства и защитите аккаунт."), 13, true); caption.HorizontalAlignment = HorizontalAlignment.Center; caption.TextAlignment = TextAlignment.Center; Page.Children.Add(caption);
        if (!_skipSavedSession && CanResumeSavedSession)
        {
            Page.Children.Add(Action(L("Продолжить с сохранённым аккаунтом"), ResumeSavedSessionAsync, true));
            Page.Children.Add(Action(L("Ввести номер"), () => { _skipSavedSession = true; ShowHome(); return Task.CompletedTask; }));
            return;
        }
        var phone = new TextBox { Header = L("Номер телефона"), PlaceholderText = "+7 900 000-00-00", Margin = new Thickness(0, 8, 0, 0), MaxLength = 24 };
        AutomationProperties.SetName(phone, L("Номер телефона Telegram"));
        Page.Children.Add(phone);
        Page.Children.Add(Action(L("Продолжить"), async () =>
        {
            var normalized = System.Text.RegularExpressions.Regex.Replace(phone.Text, @"[\s()\-]", "");
            if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^\+[1-9][0-9]{6,14}$")) throw new InvalidOperationException(L("Введите номер с кодом страны, начиная с +."));
            if (_connection == null && !await ConfigureConnectionAsync()) return;
            _pendingPhone = normalized; _expectedPhone = normalized; _loginRequested = true;
            if (AppIdentity.QaId is not null) throw new InvalidOperationException(L("В тестовой сборке доступен только демонстрационный режим."));
            await ReplaceServiceAsync(new TelegramService());
            await _service!.StartAsync(_connection!.ApiId, _connection.ApiHash, Token);
        }, true));
        var demo = new HyperlinkButton { Content = L("Попробовать без входа"), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(8, 4, 8, 4) };
        demo.Click += async (_, _) => await RunAsync(StartDemoAsync); Page.Children.Add(demo);
        var unofficial = Text(L("Неофициальное приложение на основе Telegram API."), 11, true);
        unofficial.TextAlignment = TextAlignment.Center; Page.Children.Add(unofficial);
    }

    private async Task<bool> ConfigureConnectionAsync()
    {
        var id = new TextBox { Header = "API ID", Text = _connection?.ApiId.ToString() ?? "" };
        var hash = new PasswordBox { Header = "API Hash", Password = _connection?.ApiHash ?? "" };
        var content = Stack(Text(L("Ключи вашего приложения Telegram."), 13, true), Link(L("Получить на my.telegram.org"), "https://my.telegram.org/apps"), id, hash);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L("Подключение Telegram"), Content = content, PrimaryButtonText = L("Сохранить"), CloseButtonText = L("Отмена"), DefaultButton = ContentDialogButton.Primary };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) { hash.Password = ""; return false; }
        if (!int.TryParse(id.Text.Trim(), out var apiId) || apiId <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(hash.Password.Trim(), "^[a-fA-F0-9]{32}$")) throw new InvalidOperationException(L("Проверьте API ID и API Hash."));
        var settings = new ConnectionSettings(apiId, hash.Password.Trim()); hash.Password = "";
        settings.Save(); _connection = settings; return true;
    }

    private HyperlinkButton Link(string label, string uri) => new() { Content = label, NavigateUri = new Uri(uri), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };

    private void ShowLogin()
    {
        var step = _service?.Status.Step ?? AuthorizationStep.NotStarted;
        if (step is AuthorizationStep.NotStarted or AuthorizationStep.Failed or AuthorizationStep.Closed) { _loginRequested = false; ShowHome(); return; }
        Header("", step == AuthorizationStep.Code ? L("Код подтверждения") : step == AuthorizationStep.Password ? L("Ваш пароль") : L("Вход в Telegram"), "");
        var form = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (step is AuthorizationStep.Phone or AuthorizationStep.Code or AuthorizationStep.Password or AuthorizationStep.Email or AuthorizationStep.EmailCode)
        {
            var label = step switch { AuthorizationStep.Phone => L("Номер телефона"), AuthorizationStep.Code => L("Код входа"), AuthorizationStep.Password => L("Пароль двухэтапной проверки"), AuthorizationStep.Email => L("Почта для входа"), _ => L("Код из письма") };
            if (!string.IsNullOrWhiteSpace(_service?.Status.Detail)) form.Children.Add(Text(L(_service.Status.Detail!), 14, true));
            if (step == AuthorizationStep.Code) form.Children.Add(Text(L("Введите код, отправленный Telegram."), 13, true));
            TextBox? entry = null; PasswordBox? secret = null;
            if (step == AuthorizationStep.Password) { secret = new PasswordBox { Header = label, MinWidth = 0 }; form.Children.Add(secret); }
            else { entry = new TextBox { Header = label, MinWidth = 0, PlaceholderText = step == AuthorizationStep.Phone ? "+7 900 000-00-00" : "", MaxLength = 256 }; form.Children.Add(entry); }
            form.Children.Add(Action(L("Продолжить  →"), async () =>
            {
                var value = secret?.Password ?? entry!.Text.Trim();
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(L("Заполните поле, чтобы продолжить."));
                switch (step)
                {
                    case AuthorizationStep.Phone:
                        value = System.Text.RegularExpressions.Regex.Replace(value, @"[\s()\-]", "");
                        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^\+[1-9][0-9]{6,14}$")) throw new InvalidOperationException(L("Введите номер с кодом страны, начиная с +."));
                        _expectedPhone = value;
                        await _service!.SubmitPhoneAsync(value, Token); break;
                    case AuthorizationStep.Code: await _service!.SubmitCodeAsync(value, Token); break;
                    case AuthorizationStep.Password: await _service!.SubmitPasswordAsync(value, Token); break;
                    case AuthorizationStep.Email: await _service!.SubmitEmailAsync(value, Token); break;
                    case AuthorizationStep.EmailCode: await _service!.SubmitEmailCodeAsync(value, Token); break;
                }
                if (secret != null) secret.Password = "";
                if (entry != null && step != AuthorizationStep.Phone) entry.Text = "";
            }, true));
        }
        else
        {
            form.Children.Add(Text(step == AuthorizationStep.Unsupported ? L("Этот способ входа пока не поддерживается. Продолжите в официальном приложении Telegram.") : L("Ожидаем ответ Telegram…")));
            form.Children.Add(Link(L("Официальные приложения Telegram"), "https://telegram.org/apps"));
        }
        Page.Children.Add(form);
        Page.Children.Add(Action(L("Вернуться к началу"), async () => { await DisconnectAsync(); }));
    }

    private CancellationToken Token => _operation?.Token ?? CancellationToken.None;
    private bool CanResumeSavedSession => !VisualCheck && AppIdentity.QaId is null && _connection is not null &&
        File.Exists(Path.Combine(AppIdentity.DataDirectory, "session-v1", "database-key.dpapi")) &&
        Directory.Exists(Path.Combine(AppIdentity.DataDirectory, "session-v1", "database"));
    private async Task ResumeSavedSessionAsync()
    {
        if (!CanResumeSavedSession) return;
        _pendingPhone = null; _expectedPhone = null; _loginRequested = true; _currentPage = "home";
        await ReplaceServiceAsync(new TelegramService());
        ShowLogin();
        await _service!.StartAsync(_connection!.ApiId, _connection.ApiHash, Token);
    }
    private async Task StartDemoAsync()
    {
        await ReplaceServiceAsync(new DemoTelegramService());
        await _service!.StartAsync(1, "demo", Token);
    }
    private async Task ReplaceServiceAsync(ITelegramService service)
    {
        if (_serviceShutdown is { IsFaulted: true } or { IsCanceled: true }) throw new InvalidOperationException(L("Перезапустите программу, чтобы снова подключиться."));
        if (_serviceShutdown is not null) { await _serviceShutdown.WaitAsync(Token); _serviceShutdown = null; }
        _pendingReady = false; _account = null;
        if (_service != null) { _service.StatusChanged -= _statusHandler; await _service.DisposeAsync(); }
        _service = service;
        _statusHandler = status => OnStatusChanged(service, status);
        _service.StatusChanged += _statusHandler;
        DemoBanner.IsOpen = service.IsDemo;
        StatusRow.Height = new GridLength(service.IsDemo ? 22 : 0);
    }
    private void OnStatusChanged(ITelegramService source, TelegramStatus status) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed || source != _service || source.Status != status) return;
        var ready = status.Step == AuthorizationStep.Ready;
        StatusLabel.Text = _service?.IsDemo == true ? L("Демо") : ready ? AccountLabel : "";
        StatusDot.Fill = ready ? Brush(47, 153, 117) : Muted;
        if (ready)
        {
            _loginRequested = false; _pendingReady = true;
            if (!_busy) _ = HandleReadyAsync();
        }
        else if (_service?.IsDemo != true && _currentPage != "settings")
        {
            _currentPage = "home"; _loginRequested = true; ShowLogin();
            if (status.Step == AuthorizationStep.Phone && _pendingPhone != null && !_busy) _ = SendPendingPhoneAsync();
        }
        UpdateGeometry();
    });

    private async Task HandleReadyAsync()
    {
        if (!_pendingReady || _busy || _closed) return;
        _pendingReady = false;
        await RunAsync(async () =>
        {
            var account = await _service!.GetAccountAsync(Token);
            if (!_service.IsDemo && _expectedPhone != null && _expectedPhone.TrimStart('+') != account.PhoneNumber.TrimStart('+'))
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = L("Сохранён другой аккаунт"),
                    Content = Text($"{account.FirstName} {account.LastName}\n+{account.PhoneNumber.TrimStart('+')}\n\n" + L("Вы ввели другой номер. Продолжить с сохранённым аккаунтом?"), 14),
                    PrimaryButtonText = L("Продолжить"), CloseButtonText = L("Отмена"), DefaultButton = ContentDialogButton.Close
                };
                if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) { await DisconnectAsync(); return; }
            }
            _account = account;
            StatusLabel.Text = (_service.IsDemo ? L("Демо · ") : "") + AccountLabel;
            StatusRow.Height = new GridLength(22);
            _pendingPhone = null; _expectedPhone = null;
            _currentPage = "devices"; await ShowDevicesAsync();
        });
    }

    private async Task SendPendingPhoneAsync()
    {
        if (_pendingPhone == null || _busy || _service?.Status.Step != AuthorizationStep.Phone) return;
        var phone = _pendingPhone; _pendingPhone = null;
        await RunAsync(() => _service.SubmitPhoneAsync(phone, Token));
    }

    private string AccountLabel
    {
        get
        {
            if (_account == null) return L("Аккаунт не подтверждён");
            var phone = _account.PhoneNumber.TrimStart('+');
            var masked = phone.Length > 4 ? "+" + phone[..1] + "•••" + phone[^4..] : L("номер скрыт");
            return string.Join(" ", new[] { _service?.IsDemo == true ? L(_account.FirstName) : _account.FirstName, _service?.IsDemo == true ? L(_account.LastName) : _account.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))) + " · " + masked;
        }
    }
    private string StepName(AuthorizationStep step) => step switch
    { AuthorizationStep.Phone => L("номер телефона"), AuthorizationStep.Code => L("код входа"), AuthorizationStep.Password => L("пароль"), AuthorizationStep.Email or AuthorizationStep.EmailCode => L("подтверждение почты"), AuthorizationStep.Ready => L("готово"), AuthorizationStep.Unsupported => L("нужен другой способ входа"), AuthorizationStep.Failed => L("ошибка подключения"), AuthorizationStep.Closed => L("подключение закрыто"), _ => L("подключение") };

    private bool RequireLogin(string title)
    {
        if (_service?.Status.Step == AuthorizationStep.Ready && _account != null) return true;
        Header("", title, L("Войдите в Telegram, чтобы продолжить."));
        Page.Children.Add(Action(L("Войти"), () => { _currentPage = "home"; _loginRequested = false; ShowHome(); UpdateGeometry(); return Task.CompletedTask; }, true));
        return false;
    }
    private async Task ShowDevicesAsync()
    {
        if (!RequireLogin(L("Ваши устройства"))) return;
        Header("", L("Устройства"), "");
        var revision = _viewRevision;
        var sessions = await _service!.GetSessionsAsync(Token);
        if (_closed || _currentPage != "devices" || revision != _viewRevision) return;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        tools.Children.Add(Action(L("Обновить"), ShowDevicesAsync));
        if (sessions.Any(s => !s.IsCurrent)) tools.Children.Add(Action(L("Завершить другие сеансы"), async () =>
        {
            if (!await ConfirmAsync(L("Завершить все другие сеансы?"), L("Telegram выйдет из аккаунта на остальных устройствах, включая ваши. Сеанс этой программы останется активным."), L("Завершить сеансы"))) return;
            await _service.TerminateOtherSessionsAsync(Token);
            await RefreshAfterChangeAsync(ShowDevicesAsync, _service.IsDemo ? L("Демо: другие сеансы завершены.") : L("Telegram завершил другие сеансы."));
        }));
        Page.Children.Add(tools);
        foreach (var session in sessions.OrderByDescending(s => s.IsCurrent))
        {
            var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 8, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new FontIcon { Glyph = session.Platform.Contains("Android", StringComparison.OrdinalIgnoreCase) || session.Platform.Contains("iOS", StringComparison.OrdinalIgnoreCase) ? "\uE8EA" : "\uE7F4", FontSize = 24, Foreground = Accent });
            var name = Text(session.DeviceModel, 15); name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var body = Stack(name, Text($"{session.ApplicationName} · {(_service.IsDemo ? L(session.Location) : session.Location)}", 12, true), Text(session.IsCurrent ? L("Этот сеанс") : $"{session.LastActive.ToLocalTime():dd.MM HH:mm}", 11, true)); body.Spacing = 3;
            Grid.SetColumn(body, 1); grid.Children.Add(body);
            var row = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10), Padding = new Thickness(4) };
            AutomationProperties.SetName(row, session.DeviceModel + (session.IsCurrent ? L(", этот сеанс") : L(", сведения о сеансе")));
            row.Click += async (_, _) => await RunAsync(async () =>
            {
                var location = _service.IsDemo ? L(session.Location) : session.Location;
                var details = $"{session.ApplicationName} {session.ApplicationVersion}\n{session.SystemVersion}\n{location}\nIP: {session.IpAddress}\n{L("Активность:")} {session.LastActive.ToLocalTime():dd.MM.yyyy HH:mm}";
                if (session.IsCurrent)
                {
                    await ShowDialogAsync(new ContentDialog { XamlRoot = Root.XamlRoot, Title = session.DeviceModel, Content = Text(details, 14), CloseButtonText = L("Закрыть") }); return;
                }
                if (!await ConfirmAsync(session.DeviceModel, details + L("\n\nЗавершение сеанса потребует нового входа на этом устройстве."), L("Завершить сеанс"))) return;
                await _service.TerminateSessionAsync(session.Id, Token);
                await RefreshAfterChangeAsync(ShowDevicesAsync, _service.IsDemo ? L("Демо: сеанс завершён.") : L("Сеанс завершён."));
            });
            Page.Children.Add(row);
        }
        if (sessions.Count == 0) Page.Children.Add(Text(L("Telegram вернул пустой список. Обновите его, чтобы проверить состояние."), 15, true));
    }

    private async Task ShowSecurityAsync()
    {
        if (!RequireLogin(L("Защита аккаунта"))) return;
        Header("", L("Защита"), "");
        var revision = _viewRevision;
        var state = await _service!.GetPasswordStateAsync(Token);
        if (_closed || _currentPage != "security" || revision != _viewRevision) return;
        var summary = Stack(Text(L("Двухэтапная проверка"), 16), Text(state.HasPassword ? L("Включена") : L("Выключена"), 13, true), Text(L("Почта восстановления"), 16), Text(state.HasRecoveryEmail ? L("Подключена") : L("Не подключена"), 13, true));
        Page.Children.Add(summary);
        Page.Children.Add(Action(state.HasPassword ? L("Изменить пароль") : L("Установить пароль"), () => { ShowPasswordEditor(state); return Task.CompletedTask; }, true));
        if (!string.IsNullOrEmpty(state.PendingRecoveryEmailPattern))
        {
            var emailCode = new TextBox { Header = L("Код подтверждения почты"), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
            Page.Children.Add(Stack(Text(state.PendingRecoveryEmailPattern, 14), emailCode, Action(L("Подтвердить почту"), async () =>
            {
                await _service.ConfirmRecoveryEmailAsync(emailCode.Text.Trim(), Token); emailCode.Text = "";
                await RefreshAfterChangeAsync(ShowSecurityAsync, _service.IsDemo ? L("Демо: почта настроена.") : L("Почта восстановления настроена. Telegram больше не ждёт код."));
            }, true)));
        }
    }

    private void ShowPasswordEditor(TelegramPasswordState state)
    {
        Header("", L("Новый пароль"), "");
        var current = new PasswordBox { Header = L("Текущий пароль"), Visibility = state.HasPassword ? Visibility.Visible : Visibility.Collapsed };
        var password = new PasswordBox { Header = L("Новый пароль"), PlaceholderText = L("Не менее 8 символов") };
        var repeat = new PasswordBox { Header = L("Повторите новый пароль") };
        var email = new TextBox { Header = L("Почта восстановления (необязательно)"), PlaceholderText = state.HasRecoveryEmail ? L("Пусто — оставить текущую") : "you@example.com", MaxLength = 254 };
        var form = Stack(current, password, repeat, email); form.Spacing = 10;
        form.MaxWidth = 540; form.HorizontalAlignment = HorizontalAlignment.Stretch;
        form.Children.Add(Action(state.HasPassword ? L("Изменить пароль") : L("Включить защиту"), async () =>
        {
            if (password.Password.Length < 8) throw new InvalidOperationException(L("Выберите пароль не короче 8 символов."));
            if (password.Password != repeat.Password) throw new InvalidOperationException(L("Пароли не совпадают."));
            if (state.HasPassword && string.IsNullOrEmpty(current.Password)) throw new InvalidOperationException(L("Введите текущий пароль."));
            var recovery = string.IsNullOrWhiteSpace(email.Text) ? null : email.Text.Trim();
            if (recovery != null && (!System.Net.Mail.MailAddress.TryCreate(recovery, out var address) || address.Address != recovery)) throw new InvalidOperationException(L("Проверьте адрес электронной почты."));
            if (!await ConfirmAsync(L("Сохранить защиту аккаунта?"), L("Новый пароль потребуется для следующих входов в Telegram. Сохраните его в надёжном месте.") + (recovery == null ? L("\nПочта останется без изменений.") : L("\nTelegram отправит код на указанную вами почту.")), L("Сохранить"))) return;
            var result = await _service!.SetPasswordAsync(current.Password, password.Password, "", recovery, Token);
            current.Password = password.Password = repeat.Password = "";
            await RefreshAfterChangeAsync(ShowSecurityAsync, _service.IsDemo ? L("Демо: защита обновлена.") : result.PendingRecoveryEmailPattern != null ? L("Пароль сохранён. Подтвердите почту кодом из письма.") : L("Telegram подтвердил изменение пароля."));
        }, true));
        Page.Children.Add(form);
        Page.Children.Add(Action(L("Назад"), ShowSecurityAsync));
    }

    private void ShowSettings()
    {
        Header("", L("Настройки"), "");
        Page.Spacing = 12;
        var language = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 170 };
        AutomationProperties.SetName(language, L("Язык"));
        language.Items.Add(L("Автоматически")); language.Items.Add("Русский"); language.Items.Add("English");
        language.SelectedIndex = _appearance.Language == "ru" ? 1 : _appearance.Language == "en" ? 2 : 0;
        language.SelectionChanged += (_, _) =>
        {
            string next = language.SelectedIndex == 1 ? "ru" : language.SelectedIndex == 2 ? "en" : "auto";
            if (_appearance.Language == next) return;
            _appearance.Language = next; ApplyLanguage(); SaveAppearance(); ShowSettings();
        };
        var languageRow = new Grid { ColumnSpacing = 12 };
        languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var languageLabel = Text(L("Язык"), 14); languageLabel.VerticalAlignment = VerticalAlignment.Center;
        languageRow.Children.Add(languageLabel); Grid.SetColumn(language, 1); languageRow.Children.Add(language);
        Page.Children.Add(languageRow);
        var theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 170 };
        AutomationProperties.SetName(theme, L("Тема"));
        theme.Items.Add(L("По системе")); theme.Items.Add(L("Светлая")); theme.Items.Add(L("Тёмная"));
        theme.SelectedIndex = _appearance.Theme == "light" ? 1 : _appearance.Theme == "dark" ? 2 : 0;
        theme.SelectionChanged += (_, _) =>
        {
            string next = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system";
            if (_appearance.Theme == next) return;
            _appearance.Theme = next; SaveAppearance();
        };
        var themeRow = new Grid { ColumnSpacing = 12 };
        themeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        themeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var themeLabel = Text(L("Тема"), 14); themeLabel.VerticalAlignment = VerticalAlignment.Center;
        themeRow.Children.Add(themeLabel); Grid.SetColumn(theme, 1); themeRow.Children.Add(theme);
        Page.Children.Add(themeRow);
        _sliderPressed = false;
        _densitySlider = new Slider { Header = "Liquid Glass", Minimum = 0, Maximum = 100, StepFrequency = 1, Value = _appearance.Density * 100, MinHeight = 60, HorizontalAlignment = HorizontalAlignment.Stretch, IsThumbToolTipEnabled = false };
        // Only paint is delegated to the glass renderer. Range input, capture and keyboard focus stay native.
        foreach (var key in new[] { "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed", "SliderThumbBackgroundDisabled", "SliderOuterThumbBackground", "SliderThumbBorderBrush" })
            _densitySlider.Resources[key] = new SolidColorBrush(Colors.Transparent);
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            _densitySlider.Resources["SliderTrackFill" + state] = _track;
            _densitySlider.Resources["SliderTrackValueFill" + state] = _trackValue;
        }
        AutomationProperties.SetName(_densitySlider, L("Liquid Glass: от прозрачного к плотному"));
        _densitySlider.ValueChanged += (_, e) => { _appearance.Density = e.NewValue / 100; SaveAppearance(); UpdateSliderGeometry(); };
        _densitySlider.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            if (_densitySlider == null || !e.GetCurrentPoint(_densitySlider).Properties.IsLeftButtonPressed) return;
            _sliderPressed = true;
            _sliderDirectTouch = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;
            UpdateSliderGeometry();
            if (VisualCheck) _visualTrace.Add(new { Event = "pressed", At = Environment.TickCount64, State = Glass.MotionState });
        }), true);
        foreach (var routedEvent in new[] { UIElement.PointerReleasedEvent, UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent })
            _densitySlider.AddHandler(routedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
            {
                if (VisualCheck) _visualTrace.Add(new { Event = routedEvent == UIElement.PointerCaptureLostEvent ? "capture-lost" : routedEvent == UIElement.PointerCanceledEvent ? "canceled" : "released", At = Environment.TickCount64, Down = e.GetCurrentPoint(_densitySlider).Properties.IsLeftButtonPressed, State = Glass.MotionState });
                _sliderPressed = false; UpdateSliderGeometry();
                if (VisualCheck) WriteVisualTrace();
            }), true);
        _densitySlider.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
                or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown)
                Glass.PulseSliderKeyboard();
        }), true);
        _densitySlider.LostFocus += (_, _) => { _sliderPressed = false; UpdateSliderGeometry(); };
        _densitySlider.SizeChanged += (_, _) => UpdateSliderGeometry();
        _densitySlider.Loaded += (_, _) => UpdateSliderGeometry();
        _densitySlider.LayoutUpdated += (_, _) => UpdateSliderGeometry();
        if (_nativeGlassFallback) UseNativeGlassFallback();
        var labels = new Grid(); labels.Children.Add(Text(L("Прозрачный"), 13, true)); var dense = Text(L("Плотный"), 13, true); dense.HorizontalAlignment = HorizontalAlignment.Right; labels.Children.Add(dense);
        var glassSettings = Stack(_densitySlider, labels); glassSettings.Spacing = 2;
        // The slider sits directly over the renderer's own scene, not over an opaque card.
        Page.Children.Add(glassSettings);
        var reduceTransparency = new CheckBox { Content = L("Непрозрачные поверхности"), IsChecked = _appearance.ReduceTransparency };
        var reduceMotion = new CheckBox { Content = L("Уменьшить движение"), IsChecked = _appearance.ReduceMotion };
        reduceTransparency.Click += (_, _) => { _appearance.ReduceTransparency = reduceTransparency.IsChecked == true; SaveAppearance(); };
        reduceMotion.Click += (_, _) => { _appearance.ReduceMotion = reduceMotion.IsChecked == true; SaveAppearance(); };
        var comfort = Stack(reduceTransparency, reduceMotion, Action(L("Сбросить оформление"), () =>
        {
            _appearance.Theme = "system"; _appearance.Density = .12;
            _appearance.ReduceMotion = false; _appearance.ReduceTransparency = false;
            theme.SelectedIndex = 0; reduceMotion.IsChecked = false; reduceTransparency.IsChecked = false;
            if (_densitySlider != null) _densitySlider.Value = 12;
            SaveAppearance(); return Task.CompletedTask;
        })); comfort.Spacing = 8; Page.Children.Add(comfort);
        var about = Stack(Action(L("Подключение Telegram"), async () => { await ConfigureConnectionAsync(); }),
            Action(L("Проверить обновления"), CheckForUpdatesAsync), Action(L("О программе"), ShowAboutAsync));
        about.Spacing = 6;
        if (_service != null) about.Children.Add(Action(_service.IsDemo ? L("Выйти из демонстрации") : L("Закрыть локальное подключение"), DisconnectAsync));
        Page.Children.Add(about);
        ApplyAppearance();
    }

    private async Task DisconnectAsync()
    {
        if (_service != null) { _service.StatusChanged -= _statusHandler; var previous = _service; _service = null; await previous.DisposeAsync(); }
        _pendingReady = false; _account = null; _pendingPhone = null; _expectedPhone = null; StatusRow.Height = new GridLength(0);
        _loginRequested = false; _currentPage = "home"; DemoBanner.IsOpen = false;
        StatusLabel.Text = L("Аккаунт не подключён"); StatusDot.Fill = Muted;
        ShowHome(); UpdateGeometry();
    }

    private async Task ShowAboutAsync()
    {
        var content = Stack(Text("TGDeblocker " + AppIdentity.Version, 19),
            Text(L("Неофициальное приложение на основе Telegram API."), 14, true),
            Link(L("Оригинальная программа — Astaro212"), "https://github.com/Astaro212/TGDeblocker"),
            Link(L("Графическая версия — moz9"), "https://github.com/" + AppIdentity.Repository));
        await ShowDialogAsync(new ContentDialog { XamlRoot = Root.XamlRoot, Title = L("О программе"), Content = content, CloseButtonText = L("Закрыть") });
    }

    private async Task CheckForUpdatesAsync()
    {
        string? stagedFile = null;
        bool disconnectedForUpdate = false;
        try
        {
            string installDirectory;
            try { installDirectory = WindowsUpdate.RequireInstalledApplication(); }
            catch (Exception e) when (e is UpdateValidationException or IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                await ShowDialogAsync(new ContentDialog { XamlRoot = Root.XamlRoot, Title = L("Обновления"),
                    Content = Stack(Text(L("Установите программу через установщик, чтобы обновлять её здесь. Настройки и вход сохранятся.")),
                        Link(L("Скачать установщик"), "https://github.com/" + AppIdentity.Repository + "/releases/latest")), CloseButtonText = L("Закрыть") });
                return;
            }
            using var http = WindowsUpdate.CreateHttpClient();
            var updates = new UpdateClient(http, WindowsUpdate.CreatePolicy());
            ShowNotice(L("Проверяем обновления…"), InfoBarSeverity.Informational);
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var release = await updates.CheckAsync(AppIdentity.Version, checkTimeout.Token);
            if (release is null) { ShowNotice(L("Установлена последняя версия."), InfoBarSeverity.Success); return; }
            var confirm = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L("Доступна новая версия") + " " + release.Version,
                Content = Text(L("Программа обновится и откроется снова. Настройки и вход в аккаунт сохранятся.")),
                PrimaryButtonText = L("Обновить и перезапустить"), CloseButtonText = L("Позже"), DefaultButton = ContentDialogButton.Close };
            if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
            Token.ThrowIfCancellationRequested();
            stagedFile = Path.Combine(UpdatePaths.CreateStagingDirectory(AppIdentity.DataDirectory), release.FileName);
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
            var progress = new Progress<double>(value => { if (!_closed) ShowNotice(L("Загружаем обновление…") + " " + (int)(value * 100) + "%", InfoBarSeverity.Informational); });
            await updates.DownloadAsync(release, AppIdentity.Version, stagedFile, progress, downloadTimeout.Token);
            ShowNotice(L("Завершаем работу перед обновлением…"), InfoBarSeverity.Informational);
            _cancel.Visibility = Visibility.Collapsed;
            _updateHandoff = true;
            var restartMode = VisualCheck ? "visual" : _service?.IsDemo == true ? "demo" : _account is not null && _service?.Status.Step == AuthorizationStep.Ready ? "session" : "normal";
            disconnectedForUpdate = _service is not null;
            // RunAsync serializes actions. Never wait for this same action as if it were an external operation.
            await ShutdownServiceAsync().WaitAsync(TimeSpan.FromSeconds(20));
            SaveAppearance();
            await WindowsUpdate.StartInstallerAsync(stagedFile, release, installDirectory, restartMode, Token);
            stagedFile = null; // Setup owns the running file until its process exits.
            _allowClose = true;
            Close();
            Application.Current.Exit();
        }
        catch (OperationCanceledException) { ShowNotice(L("Обновление отменено или превышено время ожидания. Текущая версия сохранена."), InfoBarSeverity.Warning); }
        catch (UpdateValidationException) { ShowNotice(L("Не удалось подтвердить подлинность обновления. Установка отменена."), InfoBarSeverity.Error); }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ShowNotice(L("Не удалось обновить программу. Текущая версия и данные сохранены. Повторите попытку позже."), InfoBarSeverity.Error); }
        finally
        {
            _updateHandoff = false;
            if (disconnectedForUpdate && !_closed)
            {
                _pendingReady = false; _pendingPhone = null; _expectedPhone = null; _account = null;
                _loginRequested = false; _currentPage = "home"; StatusRow.Height = new GridLength(0);
                StatusLabel.Text = L("Аккаунт не подключён"); DemoBanner.IsOpen = false;
                ShowHome(); UpdateGeometry();
            }
            if (stagedFile is not null)
            {
                try { File.Delete(stagedFile); Directory.Delete(Path.GetDirectoryName(stagedFile)!); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.RequestedTheme = Root.RequestedTheme;
        _activeDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(_activeDialog, dialog)) _activeDialog = null; }
    }
    private async Task<bool> ConfirmAsync(string title, string text, string action)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = Text((_service?.IsDemo == true ? L("ДЕМОНСТРАЦИЯ · Учебные данные\n") : "") + AccountLabel + "\n\n" + text, 14), PrimaryButtonText = action, CloseButtonText = L("Отмена"), DefaultButton = ContentDialogButton.Close };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed || _closing) return;
        _busy = true; PageScroll.IsEnabled = false; foreach (var tab in Tabs) tab.IsEnabled = false;
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = new CancellationTokenSource(); _cancel.Visibility = Visibility.Visible;
        Notice.IsOpen = false;
        try { await action(); }
        catch (OperationCanceledException) { _pendingPhone = null; ShowNotice(L("Ожидание отменено. Отправленный запрос мог выполниться; обновите данные перед повтором."), InfoBarSeverity.Warning); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (_operation.IsCancellationRequested) _pendingPhone = null;
            _operation.Dispose(); _operation = null; _busy = false;
            if (!_closed)
            {
                PageScroll.IsEnabled = true; foreach (var tab in Tabs) tab.IsEnabled = true; _cancel.Visibility = Visibility.Collapsed;
                UpdateGeometry();
            }
            _operationFinished.TrySetResult();
            if (_pendingReady && !_closed && !_closing) await HandleReadyAsync();
            else if (_pendingPhone != null && !_closed && !_closing) await SendPendingPhoneAsync();
        }
    }
    private async Task RefreshAfterChangeAsync(Func<Task> refresh, string confirmed)
    {
        try { await refresh(); ShowNotice(confirmed, InfoBarSeverity.Success); }
        catch (Exception)
        { ShowNotice(confirmed + L(" Обновить данные не удалось. Откройте раздел заново."), InfoBarSeverity.Warning); }
    }
    private void ShowError(Exception exception)
    {
        if (exception is TimeoutException)
        {
            ShowNotice(L("Telegram не ответил вовремя. Результат неизвестен: обновите состояние перед повтором действия."), InfoBarSeverity.Warning);
            return;
        }
        var message = exception switch
        {
            TelegramLogoutUnconfirmedException logout => AppStrings.Message(logout.Message, Language),
            TelegramException telegram => AppStrings.Message(telegram.Message, Language),
            InvalidOperationException validation => AppStrings.Message(validation.Message, Language),
            DllNotFoundException => L("Не найдена библиотека TDLib. Запустите программу из полной папки сборки."),
            _ => L("Не удалось выполнить действие. Проверьте подключение и повторите попытку.")
        };
        ShowNotice(message, InfoBarSeverity.Error);
    }
    private void ShowNotice(string message, InfoBarSeverity severity)
    { Notice.Title = severity == InfoBarSeverity.Error ? L("Действие не выполнено") : ""; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
    private Task ShutdownServiceAsync()
    {
        if (_serviceShutdown is not null) return _serviceShutdown;
        if (_service is null) return Task.CompletedTask;
        var previous = _service; _service = null;
        previous.StatusChanged -= _statusHandler;
        return _serviceShutdown = previous.DisposeAsync().AsTask();
    }
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing || _updateHandoff) return;
        _closing = true; _operation?.Cancel(); _activeDialog?.Hide();
        try
        {
            if (_busy && _operationFinished is not null) await _operationFinished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await ShutdownServiceAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _allowClose = true; Close();
        }
        catch (Exception)
        {
            if (!_busy && _serviceShutdown is { IsCompleted: true }) { _allowClose = true; Close(); return; }
            _closing = false;
            ShowNotice(L("Не удалось завершить работу. Подождите и попробуйте закрыть программу ещё раз."), InfoBarSeverity.Warning);
        }
    }
    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true; _operation?.Cancel();
        if (VisualCheck) WriteVisualTrace();
        Glass.FrameChanged -= UpdateLiveGlass;
        _liveGlass?.Dispose();
        _visualTraceTimer?.Stop();
        if (_colorSubscribed) _ui.ColorValuesChanged -= ColorValuesChanged;
        _ui.AdvancedEffectsEnabledChanged -= AccessibilityChanged;
        _ui.AnimationsEnabledChanged -= AccessibilityChanged;
        if (_highContrastSubscribed) _accessibility.HighContrastChanged -= HighContrastChanged;
        _accessibilityPoll?.Stop();
        try { await ShutdownServiceAsync(); } catch (Exception) { /* The native window has already closed. */ }
    }
}
