using System.Globalization;
using System.Text.RegularExpressions;
using TGDeblocker.App;
using TGDeblocker.Appearance;
using TGDeblocker.Core;
using TGDeblocker.Mobile.Rendering;
using TGDeblocker.Mobile.Services;

namespace TGDeblocker.Mobile;

public sealed class MainPage : ContentPage
{
    private readonly GlassSurface _glass = new() { InputTransparent = true };
    private readonly VerticalStackLayout _body = new() { Spacing = 14, Padding = new Thickness(24, 10, 24, 116) };
    private readonly ScrollView _scroll;
    private readonly Grid _tabs = new() { Margin = new Thickness(16), HeightRequest = 64, ColumnSpacing = 0 };
    private readonly Label _title = new() { Text = "TGDeblocker", FontSize = 15, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private readonly ActivityIndicator _progress = new() { WidthRequest = 20, HeightRequest = 20, IsVisible = false, Color = Color.FromArgb(TelegramPalette.Light.Link) };
    private readonly Button _cancel = new() { FontSize = 12, Padding = 6, IsVisible = false, BackgroundColor = Colors.Transparent };
    private ITelegramService? _service;
    private TelegramAccount? _account;
    private CancellationTokenSource? _operation;
    private Task _connectionClose = Task.CompletedTask;
    private string? _pendingPhone, _expectedPhone;
    private bool _busy, _pendingStatus, _suspended, _destroyed, _presentingAlerts;
    private readonly Queue<(string Title, string Message)> _deferredAlerts = new();
    private int _page;
    private GlassSlider? _densitySlider;
    private string Language => AppStrings.ResolveLanguage(Preferences.Default.Get("language", "auto"), CultureInfo.CurrentUICulture);
    private string T(string ru, string en) => Language == "ru" ? ru : en;
    private CancellationToken Token => _operation?.Token ?? CancellationToken.None;
    private static Color Ink => Color.FromArgb(TelegramPalette.Light.Text);
    private static Color Muted => Color.FromArgb(TelegramPalette.Light.Muted);
    private static Color DarkInk => Color.FromArgb(TelegramPalette.Dark.Text);
    private static Color DarkMuted => Color.FromArgb(TelegramPalette.Dark.Muted);
    private static Color DarkAccent => Color.FromArgb(TelegramPalette.Dark.Link);
    private string Identity => _account == null ? T("Аккаунт не подтверждён", "Account not confirmed") : $"{_account.FirstName} · +{_account.PhoneNumber.TrimStart('+')}";

    public MainPage()
    {
        this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb(TelegramPalette.Light.Background), Color.FromArgb(TelegramPalette.Dark.Background));
        _title.SetAppThemeColor(Label.TextColorProperty, Ink, DarkInk);
        _cancel.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb(TelegramPalette.Light.Link), DarkAccent);
        _progress.SetAppThemeColor(ActivityIndicator.ColorProperty, Color.FromArgb(TelegramPalette.Light.Link), DarkAccent);
        SafeAreaEdges = new Microsoft.Maui.SafeAreaEdges(SafeAreaRegions.Container);
        var layout = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(96) } };
#if ANDROID
        bool liveBackdrop = OperatingSystem.IsAndroidVersionAtLeast(33);
#else
        bool liveBackdrop = true;
#endif
        if (!liveBackdrop) { layout.Add(_glass); Grid.SetRowSpan(_glass, 3); }
        var header = new Grid { Padding = new Thickness(24, 10, 20, 2), ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
        header.Add(_title); header.Add(_progress, 1); header.Add(_cancel, 2);
        _cancel.Clicked += (_, _) => _operation?.Cancel();
        layout.Add(header);
        _scroll = new ScrollView { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Never };
        layout.Add(_scroll, 0, 1); Grid.SetRowSpan(_scroll, 2);
        _glass.BackdropSource = _scroll;
        _scroll.Scrolled += (_, _) => _glass.InvalidateBackdrop();
        _body.ChildAdded += (_, _) => _glass.InvalidateBackdrop();
        _body.ChildRemoved += (_, _) => _glass.InvalidateBackdrop();
        if (liveBackdrop) { layout.Add(_glass); Grid.SetRowSpan(_glass, 3); }
        layout.Add(_tabs, 0, 2);
        Content = layout;
        RefreshNavigation(); ApplyAppearance(); ShowHome();
    }

    private void ApplyAppearance()
    {
        _glass.Density = Preferences.Default.Get("density", .12);
        _glass.ReduceMotion = Preferences.Default.Get("reduceMotion", false);
        _glass.ReduceTransparency = Preferences.Default.Get("reduceTransparency", false);
        _glass.SelectedIndex = _page;
        _cancel.Text = T("Отмена", "Cancel");
        UpdateTheme();
    }

    // Recolour existing controls through theme bindings; keep text, focus and scroll intact.
    public void UpdateTheme()
    {
        bool isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        _glass.IsDark = isDark;
        _glass.InvalidateBackdrop();
        if (_densitySlider is not null) _densitySlider.IsDark = isDark;
    }

    private void RefreshNavigation()
    {
        _tabs.Clear(); _tabs.ColumnDefinitions.Clear();
        string[] labels = [T("Начало", "Home"), T("Устройства", "Devices"), T("Защита", "Security"), T("Настройки", "Settings")];
        for (int i = 0; i < labels.Length; i++)
        {
            int target = i;
            _tabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var button = new Button { Text = labels[i], FontSize = 11, Padding = 2, CornerRadius = 24, BackgroundColor = Colors.Transparent };
            button.SetAppThemeColor(Button.TextColorProperty, i == _page ? Color.FromArgb(TelegramPalette.Light.Link) : Muted, i == _page ? DarkAccent : DarkMuted);
            SemanticProperties.SetDescription(button, labels[i]);
            button.Pressed += (_, _) => _glass.SetSelectionPressed(true);
            button.Released += (_, _) => _glass.SetSelectionPressed(false);
            button.Clicked += async (_, _) => await RunAsync(async () => { _page = target; RefreshNavigation(); ApplyAppearance(); await ShowPageAsync(); });
            _tabs.Add(button, i);
        }
    }

    private Label Text(string text, double size = 15, bool muted = false)
    {
        var label = new Label { Text = text, FontSize = size, LineBreakMode = LineBreakMode.WordWrap };
        label.SetAppThemeColor(Label.TextColorProperty, muted ? Muted : Ink, muted ? DarkMuted : DarkInk);
        return label;
    }
    private Entry Field(string placeholder, bool secret = false, Keyboard? keyboard = null)
    {
        var entry = new Entry { Placeholder = placeholder, IsPassword = secret, Keyboard = keyboard ?? Keyboard.Default, FontSize = 16, MaxLength = 256 };
        entry.SetAppThemeColor(Entry.TextColorProperty, Ink, DarkInk);
        entry.SetAppThemeColor(Entry.PlaceholderColorProperty, Muted, DarkMuted);
        entry.TextChanged += (_, _) => _glass.InvalidateBackdrop();
        FollowNativeFieldTheme(entry);
        return entry;
    }
    private static void FollowNativeFieldTheme(View field)
    {
#if ANDROID
        void Update()
        {
            if (field.Handler?.PlatformView is not Android.Views.View native || !native.PeerReference.IsValid) return;
            bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            int normal = Android.Graphics.Color.ParseColor(TelegramPalette.For(dark).Muted);
            int focused = Android.Graphics.Color.ParseColor(TelegramPalette.For(dark).Link);
            int disabled = Android.Graphics.Color.Argb(100, dark ? 126 : 100, dark ? 145 : 117, dark ? 173 : 139);
            try
            {
                native.BackgroundTintList = new Android.Content.Res.ColorStateList(
                    [ [ -Android.Resource.Attribute.StateEnabled ], [ Android.Resource.Attribute.StateFocused ], [] ],
                    [ disabled, focused, normal ]);
            }
            catch (ObjectDisposedException) when (!native.PeerReference.IsValid)
            {
                // The handler was disconnected while an old theme notification was in flight.
            }
        }
        void TextColorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "TextColor") Update();
        }
        field.HandlerChanging += (_, _) => field.PropertyChanged -= TextColorChanged;
        field.HandlerChanged += (_, _) =>
        {
            field.PropertyChanged -= TextColorChanged;
            if (field.Handler is null) return;
            field.PropertyChanged += TextColorChanged;
            Update();
        };
        if (field.Handler is not null) { field.PropertyChanged += TextColorChanged; Update(); }
#endif
    }
    private Button Action(string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Text = label, CornerRadius = 14, Padding = new Thickness(14, 10), MinimumHeightRequest = 44, FontSize = 15 };
        button.SetAppThemeColor(Button.BackgroundColorProperty, Color.FromArgb(primary ? TelegramPalette.Light.Button : TelegramPalette.Light.Surface), Color.FromArgb(primary ? TelegramPalette.Dark.Button : TelegramPalette.Dark.Surface));
        button.SetAppThemeColor(Button.TextColorProperty, primary ? Color.FromArgb(TelegramPalette.Light.ButtonText) : Ink, primary ? Color.FromArgb(TelegramPalette.Dark.ButtonText) : DarkInk);
        button.Clicked += async (_, _) => await RunAsync(action);
        return button;
    }
    private void Header(string title)
    {
        _body.Clear();
        _densitySlider = null;
        _body.Add(Text(title, 24));
        if (_service?.IsDemo == true) _body.Add(Text(T("Демонстрация · учебные данные", "Demo · sample data"), 12, true));
        _ = _scroll.ScrollToAsync(0, 0, false);
    }
    private async Task ShowPageAsync()
    {
        switch (_page)
        {
            case 1: await ShowDevicesAsync(); break;
            case 2: await ShowSecurityAsync(); break;
            case 3: ShowSettings(); break;
            default: if (_service != null && _account == null) ShowAuthorization(); else ShowHome(); break;
        }
    }
    private void ShowHome()
    {
        Header(_account == null ? T("Войти в Telegram", "Sign in to Telegram") : _service?.IsDemo == true ? T("Демо", "Demo") : _account.FirstName);
        if (_account != null)
        {
            _body.Add(Text(Identity, 14, true));
            _body.Add(Action(T("Проверить устройства", "Review devices"), async () => { _page = 1; RefreshNavigation(); ApplyAppearance(); await ShowDevicesAsync(); }, true));
            _body.Add(Action(T("Отключить приложение", "Disconnect this app"), RevokeConnectionAsync));
            return;
        }
        _body.Add(Text(T("Проверьте устройства и защитите аккаунт.", "Review your devices and secure your account."), 14, true));
        var phone = Field(T("Номер с кодом страны", "Phone with country code"), keyboard: Keyboard.Telephone);
        phone.MaxLength = 24; _body.Add(phone);
        _body.Add(Action(T("Продолжить", "Continue"), async () =>
        {
            var value = Regex.Replace(phone.Text ?? "", @"[\s()\-]", "");
            if (!Regex.IsMatch(value, @"^\+[1-9][0-9]{6,14}$")) throw new InvalidOperationException(T("Введите номер с кодом страны, начиная с +.", "Enter a phone number with the country code, starting with +."));
            var credentials = await MobileTelegramFactory.GetApiCredentialsAsync(Token);
            if (credentials == null) { await ConfigureConnectionAsync(); credentials = await MobileTelegramFactory.GetApiCredentialsAsync(Token); }
            if (credentials == null) return;
            await ReplaceServiceAsync(MobileTelegramFactory.CreateService(Language));
            _pendingPhone = _expectedPhone = value;
            await _service!.StartAsync(credentials.ApiId, credentials.ApiHash, Token);
        }, true));
        _body.Add(Action(T("Попробовать без входа", "Try without signing in"), async () => { await ReplaceServiceAsync(new DemoTelegramService()); await _service!.StartAsync(1, "demo", Token); }));
        _body.Add(Text(T("Неофициальное приложение на основе Telegram API. Не связано с Telegram.", "Unofficial app using the Telegram API. Not affiliated with Telegram."), 12, true));
    }

    private async Task ReplaceServiceAsync(ITelegramService service)
    {
        try { await CloseConnectionAsync(); }
        catch { await service.DisposeAsync(); throw; }
        if (_destroyed || Token.IsCancellationRequested)
        {
            await service.DisposeAsync();
            throw new OperationCanceledException(Token);
        }
        _service = service; _account = null;
        service.StatusChanged += status => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_destroyed || !ReferenceEquals(service, _service) || service.Status != status) return;
            _pendingStatus = true;
            if (!_busy && !_suspended && !_presentingAlerts) _ = ProcessPendingAsync();
        });
    }
    private async Task HandleStatusAsync()
    {
        _pendingStatus = false;
        if (_service == null) return;
        if (_service.Status.Step == AuthorizationStep.Ready)
        {
            var account = await _service.GetAccountAsync(Token);
            if (_destroyed) return;
            if (_suspended) { _pendingStatus = true; return; }
            if (_expectedPhone != null && account.PhoneNumber.TrimStart('+') != _expectedPhone.TrimStart('+'))
            {
                bool accepted = await DisplayAlertAsync(T("Сохранён другой аккаунт", "A different account is saved"), $"{account.FirstName} · +{account.PhoneNumber.TrimStart('+')}", T("Использовать", "Use this account"), T("Отмена", "Cancel"));
                if (!accepted) { await CloseConnectionAsync(); ShowHome(); return; }
            }
            _account = account; _expectedPhone = _pendingPhone = null;
            await ShowPageAsync();
        }
        else if (_service.Status.Step == AuthorizationStep.Phone && _pendingPhone != null)
        { var number = _pendingPhone; _pendingPhone = null; await _service.SubmitPhoneAsync(number, Token); }
        else { _page = 0; RefreshNavigation(); ApplyAppearance(); ShowAuthorization(); }
    }
    private void ShowAuthorization()
    {
        var step = _service?.Status.Step ?? AuthorizationStep.NotStarted;
        Header(T("Вход в Telegram", "Sign in to Telegram"));
        if (step is AuthorizationStep.Phone or AuthorizationStep.Code or AuthorizationStep.Password or AuthorizationStep.Email or AuthorizationStep.EmailCode)
        {
            string label = step switch
            {
                AuthorizationStep.Phone => T("Номер телефона", "Phone number"),
                AuthorizationStep.Code => T("Код входа", "Sign-in code"),
                AuthorizationStep.Password => T("Пароль двухэтапной проверки", "Two-step verification password"),
                AuthorizationStep.Email => T("Почта для входа", "Sign-in email"),
                _ => T("Код из письма", "Email code")
            };
            var entry = Field(label, step == AuthorizationStep.Password, step is AuthorizationStep.Code or AuthorizationStep.EmailCode ? Keyboard.Numeric : Keyboard.Default);
            _body.Add(entry);
            _body.Add(Action(T("Продолжить", "Continue"), async () =>
            {
                string value = entry.Text ?? "";
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(T("Заполните поле, чтобы продолжить.", "Complete this field to continue."));
                switch (step)
                {
                    case AuthorizationStep.Phone:
                        value = Regex.Replace(value, @"[\s()\-]", "");
                        if (!Regex.IsMatch(value, @"^\+[1-9][0-9]{6,14}$")) throw new InvalidOperationException(T("Введите номер с кодом страны, начиная с +.", "Enter a phone number with the country code, starting with +."));
                        _expectedPhone = value; await _service!.SubmitPhoneAsync(value, Token); break;
                    case AuthorizationStep.Code: await _service!.SubmitCodeAsync(value.Trim(), Token); break;
                    case AuthorizationStep.Password: await _service!.SubmitPasswordAsync(value, Token); break;
                    case AuthorizationStep.Email: await _service!.SubmitEmailAsync(value.Trim(), Token); break;
                    default: await _service!.SubmitEmailCodeAsync(value.Trim(), Token); break;
                }
                entry.Text = "";
            }, true));
        }
        else _body.Add(Text(step is AuthorizationStep.Unsupported or AuthorizationStep.Failed
            ? T("Этот способ входа недоступен. Продолжите в официальном приложении Telegram.", "This sign-in method is unavailable. Continue in the official Telegram app.")
            : T("Ожидаем ответ Telegram…", "Waiting for Telegram…"), 14, true));
        _body.Add(Action(T("Вернуться к началу", "Back to start"), async () => { await CloseConnectionAsync(); ShowHome(); }));
    }
    private bool RequireAccount(string title)
    {
        Header(title);
        if (_service?.Status.Step == AuthorizationStep.Ready && _account != null) return true;
        _body.Add(Text(T("Сначала войдите в свой аккаунт.", "Sign in to your account first."), 14, true));
        _body.Add(Action(T("Войти", "Sign in"), () => { _page = 0; RefreshNavigation(); ApplyAppearance(); ShowHome(); return Task.CompletedTask; }, true));
        return false;
    }
    private async Task ShowDevicesAsync()
    {
        if (!RequireAccount(T("Устройства", "Devices"))) return;
        var sessions = await _service!.GetSessionsAsync(Token);
        foreach (var session in sessions.OrderByDescending(s => s.IsCurrent).ThenByDescending(s => s.LastActive))
        {
            var row = new VerticalStackLayout { Spacing = 4, Padding = 12 };
            row.Add(Text(session.DeviceModel, 16));
            row.Add(Text(session.IsCurrent ? T("Это устройство", "This device") : $"{session.ApplicationName} · {session.Location}", 13, true));
            row.Add(Action(T("Подробнее", "Details"), async () =>
            {
                string detail = $"{session.Platform} {session.SystemVersion}\n{session.ApplicationName} {session.ApplicationVersion}\n{session.IpAddress}\n{session.Location}\n{session.LastActive.ToLocalTime():g}";
                await DisplayAlertAsync(session.DeviceModel, detail, T("Готово", "Done"));
            }));
            if (!session.IsCurrent) row.Add(Action(T("Завершить сеанс", "End session"), async () =>
            {
                if (!await ConfirmAsync(T("Завершить сеанс?", "End this session?"), $"{session.DeviceModel}\n{session.Location}", T("Завершить", "End session"))) return;
                await _service.TerminateSessionAsync(session.Id, Token);
                await RefreshAfterChangeAsync(ShowDevicesAsync);
            }));
            var card = new Border { Content = row, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 } };
            card.SetAppTheme(Border.StrokeProperty, new SolidColorBrush(Color.FromArgb(TelegramPalette.Light.Border)), new SolidColorBrush(Color.FromArgb(TelegramPalette.Dark.Border)));
            card.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb(TelegramPalette.Light.Surface), Color.FromArgb(TelegramPalette.Dark.Surface));
            _body.Add(card);
        }
        if (sessions.Any(s => !s.IsCurrent)) _body.Add(Action(T("Завершить остальные сеансы", "End all other sessions"), async () =>
        {
            if (!await ConfirmAsync(T("Завершить остальные сеансы?", "End all other sessions?"), T("Останется только это подключение.", "Only this connection will remain active."), T("Завершить", "End sessions"))) return;
            await _service.TerminateOtherSessionsAsync(Token); await RefreshAfterChangeAsync(ShowDevicesAsync);
        }));
    }
    private async Task ShowSecurityAsync()
    {
        if (!RequireAccount(T("Защита", "Security"))) return;
        var state = await _service!.GetPasswordStateAsync(Token);
        _body.Add(Text(state.HasPassword ? T("Двухэтапная проверка включена", "Two-step verification is enabled") : T("Пароль не установлен", "No password is set"), 17));
        _body.Add(Text(state.HasRecoveryEmail ? T("Почта для восстановления подключена", "Recovery email is connected") : T("Почта для восстановления не подтверждена", "Recovery email is not confirmed"), 14, true));
        if (state.PendingRecoveryEmailPattern != null)
        {
            var code = Field(T("Код из письма", "Email code"), keyboard: Keyboard.Numeric); _body.Add(code);
            _body.Add(Action(T("Подтвердить почту", "Confirm email"), async () =>
            { await _service.ConfirmRecoveryEmailAsync(code.Text ?? "", Token); code.Text = ""; await RefreshAfterChangeAsync(ShowSecurityAsync); }, true));
        }
        _body.Add(Action(state.HasPassword ? T("Изменить пароль", "Change password") : T("Настроить пароль", "Set a password"), () => { ShowPasswordForm(state); return Task.CompletedTask; }, true));
    }
    private void ShowPasswordForm(TelegramPasswordState state)
    {
        Header(T("Двухэтапная проверка", "Two-step verification"));
        var current = Field(T("Текущий пароль", "Current password"), true);
        var next = Field(T("Новый пароль", "New password"), true);
        var repeat = Field(T("Повторите пароль", "Repeat password"), true);
        var hint = Field(T("Подсказка", "Hint"));
        var email = Field(T("Почта для восстановления", "Recovery email"), keyboard: Keyboard.Email);
        if (state.HasPassword) _body.Add(current);
        _body.Add(next); _body.Add(repeat); _body.Add(hint); _body.Add(email);
        _body.Add(Text(T("Оставьте почту пустой, чтобы сохранить текущую.", "Leave the email blank to keep the current one."), 12, true));
        _body.Add(Action(T("Сохранить", "Save"), async () =>
        {
            if (string.IsNullOrWhiteSpace(next.Text)) throw new InvalidOperationException(T("Введите новый пароль.", "Enter a new password."));
            if (next.Text != repeat.Text) throw new InvalidOperationException(T("Пароли не совпадают.", "The passwords do not match."));
            if (!string.IsNullOrWhiteSpace(email.Text) && !System.Net.Mail.MailAddress.TryCreate(email.Text.Trim(), out _)) throw new InvalidOperationException(T("Проверьте адрес почты.", "Check the email address."));
            if (!await ConfirmAsync(T("Сохранить пароль?", "Save this password?"), T("Убедитесь, что вы запомнили новый пароль.", "Make sure you remember the new password."), T("Сохранить", "Save"))) return;
            await _service!.SetPasswordAsync(current.Text ?? "", next.Text, hint.Text ?? "", string.IsNullOrWhiteSpace(email.Text) ? null : email.Text.Trim(), Token);
            current.Text = next.Text = repeat.Text = "";
            await RefreshAfterChangeAsync(ShowSecurityAsync);
        }, true));
        _body.Add(Action(T("Назад", "Back"), ShowSecurityAsync));
    }

    private void ShowSettings()
    {
        Header(T("Настройки", "Settings"));
        void AddChoice(string caption, Picker picker)
        {
            picker.SetAppThemeColor(Picker.TextColorProperty, Ink, DarkInk);
            picker.SetAppThemeColor(Picker.TitleColorProperty, Muted, DarkMuted);
            FollowNativeFieldTheme(picker);
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 18 };
            var label = Text(caption, 14); label.VerticalOptions = LayoutOptions.Center;
            row.Add(label); row.Add(picker, 1); _body.Add(row);
        }
        var theme = new Picker { Title = T("Тема", "Theme"), ItemsSource = new[] { T("По системе", "System"), T("Светлая", "Light"), T("Тёмная", "Dark") } };
        theme.SelectedIndex = ThemePreference.Normalize(Preferences.Default.Get("theme", "system")) switch { "light" => 1, "dark" => 2, _ => 0 };
        theme.SelectedIndexChanged += (_, _) =>
        {
            Preferences.Default.Set("theme", theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" });
            if (Application.Current is App app) app.ApplyThemePreference();
        };
        AddChoice(T("Тема", "Theme"), theme);
        var languages = new Picker { Title = T("Язык", "Language"), ItemsSource = new[] { T("Автоматически", "Automatic"), "Русский", "English" } };
        languages.SelectedIndex = Preferences.Default.Get("language", "auto") switch { "ru" => 1, "en" => 2, _ => 0 };
        languages.SelectedIndexChanged += (_, _) => { Preferences.Default.Set("language", languages.SelectedIndex switch { 1 => "ru", 2 => "en", _ => "auto" }); RefreshNavigation(); ApplyAppearance(); ShowSettings(); };
        AddChoice(T("Язык", "Language"), languages);
        _body.Add(Text("Liquid Glass", 15));
        var slider = new GlassSlider { Value = _glass.Density, Density = _glass.Density, ReduceMotion = _glass.ReduceMotion, ReduceTransparency = _glass.ReduceTransparency, IsEnabled = !_glass.ReduceTransparency, IsDark = _glass.IsDark, HeightRequest = 58 };
        _densitySlider = slider;
        slider.ValueChanged += (_, e) => { Preferences.Default.Set("density", e.NewValue); _glass.Density = slider.Density = e.NewValue; };
        _body.Add(slider);
        var systemTransparency = Text(T("Прозрачность отключена в настройках устройства.", "Transparency is disabled in your device settings."), 12, true);
        void UpdateTransparencyNotice() => systemTransparency.IsVisible = slider.EffectiveReduceTransparency && !slider.ReduceTransparency;
        slider.EffectiveReduceTransparencyChanged += (_, _) => UpdateTransparencyNotice();
        UpdateTransparencyNotice(); _body.Add(systemTransparency);
        var labels = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        labels.Add(Text(T("Прозрачный", "Transparent"), 12, true)); labels.Add(Text(T("Плотный", "Dense"), 12, true), 1); _body.Add(labels);
        void AddToggle(string label, string key, Action<bool> apply)
        {
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            var caption = Text(label, 14); caption.VerticalOptions = LayoutOptions.Center;
            var toggle = new Switch { IsToggled = Preferences.Default.Get(key, false) };
            toggle.Toggled += (_, e) => { Preferences.Default.Set(key, e.Value); apply(e.Value); };
            row.Add(caption); row.Add(toggle, 1); _body.Add(row);
        }
        AddToggle(T("Непрозрачные поверхности", "Opaque surfaces"), "reduceTransparency", value => { _glass.ReduceTransparency = slider.ReduceTransparency = value; slider.IsEnabled = !value; UpdateTransparencyNotice(); });
        AddToggle(T("Уменьшить движение", "Reduce motion"), "reduceMotion", value => _glass.ReduceMotion = slider.ReduceMotion = value);
        _body.Add(Action(T("Сбросить оформление", "Reset appearance"), () =>
        {
            Preferences.Default.Set("density", .12); Preferences.Default.Set("reduceTransparency", false); Preferences.Default.Set("reduceMotion", false);
            Preferences.Default.Set("theme", "system");
            if (Application.Current is App app) app.ApplyThemePreference();
            ApplyAppearance(); ShowSettings(); return Task.CompletedTask;
        }));
        _body.Add(Action(T("Конфиденциальность", "Privacy"), ShowPrivacyAsync));
        _body.Add(Action(T("Подключение Telegram", "Telegram connection"), ConfigureConnectionAsync));
        if (_service != null) _body.Add(Action(_service.IsDemo ? T("Выйти из демонстрации", "Leave demo") : T("Отключить приложение", "Disconnect this app"), RevokeConnectionAsync));
#if ANDROID && DEBUG
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.GetBooleanExtra("glass_test_scroll", false) == true)
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), async () => await _scroll.ScrollToAsync(0, 165, false));
#endif
    }
    private async Task ConfigureConnectionAsync()
    {
        string? id = await DisplayPromptAsync(T("Подключение Telegram", "Telegram connection"), "API ID", T("Продолжить", "Continue"), T("Отмена", "Cancel"), keyboard: Keyboard.Numeric);
        if (id == null) return;
        string? hash = await DisplayPromptAsync(T("Подключение Telegram", "Telegram connection"), "API Hash", T("Сохранить", "Save"), T("Отмена", "Cancel"));
        if (hash == null) return;
        if (!int.TryParse(id, out var apiId) || apiId <= 0 || !Regex.IsMatch(hash.Trim(), "^[a-fA-F0-9]{32}$")) throw new InvalidOperationException(T("Проверьте API ID и API Hash.", "Check the API ID and API Hash."));
        await MobileTelegramFactory.SaveApiCredentialsAsync(new TelegramApiCredentials(apiId, hash.Trim()), Token);
    }
    private Task ShowPrivacyAsync() => DisplayAlertAsync(T("Конфиденциальность", "Privacy"), T(
        "Приложение соединяется напрямую с Telegram через TDLib. Номер, коды и пароль передаются Telegram для входа. Коды и пароль не сохраняются приложением. Зашифрованный сеанс хранится на этом устройстве; ключ защищён системным хранилищем. Рекламы, аналитики и серверов разработчика нет. Демо работает без сети. «Отключить приложение» выполняет выход из этого приложения и не удаляет аккаунт Telegram.\n\nНеофициальное приложение. Правила обработки данных Telegram: https://telegram.org/privacy",
        "The app connects directly to Telegram through TDLib. Your phone number, codes and password are sent to Telegram to sign in. The app does not save codes or passwords. The encrypted session is stored on this device; its key is protected by the system secure store. There are no ads, analytics or developer servers. Demo mode works offline. Disconnecting signs you out of this app and does not delete your Telegram account.\n\nUnofficial app. Telegram's privacy policy: https://telegram.org/privacy"), T("Готово", "Done"));
    private async Task RevokeConnectionAsync()
    {
        if (_service == null) return;
        if (!_service.IsDemo)
        {
            if (!await ConfirmAsync(T("Отключить приложение?", "Disconnect this app?"), T("Будет выполнен выход из этого приложения. Остальные устройства останутся подключены.", "You will be signed out of this app. Your other devices will remain connected."), T("Отключить", "Disconnect"))) return;
            await _service.LogOutAsync(Token);
            try { await MobileTelegramFactory.ForgetLocalSessionAfterLogoutAsync(Token); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            { await DisplayAlertAsync(T("Выход выполнен", "Signed out"), T("Локальный ключ пока сохранён: приложение не смогло подтвердить очистку данных сеанса.", "The local key was retained because the app could not confirm that the session files were cleared."), T("Готово", "Done")); }
        }
        await CloseConnectionAsync(); _page = 0; RefreshNavigation(); ApplyAppearance(); ShowHome();
    }
    private async Task<bool> ConfirmAsync(string title, string message, string accept) => await DisplayAlertAsync(title,
        (_service?.IsDemo == true ? T("Демонстрация · учебные данные\n", "Demo · sample data\n") : "") + Identity + "\n\n" + message, accept, T("Отмена", "Cancel"));
    private async Task RefreshAfterChangeAsync(Func<Task> refresh)
    {
        try { await refresh(); }
        catch { await ShowOrDeferAlertAsync(T("Изменение подтверждено", "Change confirmed"), T("Не удалось обновить экран. Откройте раздел заново.", "Could not refresh the screen. Open this section again.")); }
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _suspended || _destroyed || _presentingAlerts) return;
        _busy = true; _body.IsEnabled = false; _tabs.IsEnabled = false;
        _progress.IsVisible = _progress.IsRunning = _cancel.IsVisible = true;
        _operation = new CancellationTokenSource();
        try { await action(); }
        catch (OperationCanceledException) { await NoticeAsync(T("Ожидание отменено. Отправленный запрос мог выполниться; обновите данные перед повтором.", "Waiting was cancelled. A sent request may have completed; refresh the data before retrying.")); }
        catch (TimeoutException) { await NoticeAsync(T("Telegram не ответил вовремя. Результат неизвестен: обновите данные перед повтором.", "Telegram did not respond in time. The result is unknown; refresh before retrying.")); }
        catch (TelegramLogoutUnconfirmedException) { await NoticeAsync(T("Результат выхода неизвестен. Проверьте список устройств в официальном Telegram, прежде чем повторять действие.", "The sign-out result is unknown. Check the device list in the official Telegram app before retrying.")); }
        catch (Exception ex) { await NoticeAsync(ex is TelegramException or InvalidOperationException ? AppStrings.Message(ex.Message, Language) : T("Не удалось выполнить действие. Проверьте подключение и повторите попытку.", "Could not complete the action. Check the connection and try again.")); }
        finally
        {
            _operation.Dispose(); _operation = null; _busy = false;
            if (!_destroyed)
            {
                _body.IsEnabled = _tabs.IsEnabled = true;
                _progress.IsVisible = _progress.IsRunning = _cancel.IsVisible = false;
                await ProcessPendingAsync();
            }
        }
    }
    private Task NoticeAsync(string message) => ShowOrDeferAlertAsync(T("Действие не выполнено", "Action not completed"), message);
    private async Task ShowOrDeferAlertAsync(string title, string message)
    {
        if (_destroyed) return;
        if (_suspended) { _deferredAlerts.Enqueue((title, message)); return; }
        try { await DisplayAlertAsync(title, message, T("Готово", "Done")); }
        catch (Exception) { if (!_destroyed) _deferredAlerts.Enqueue((title, message)); }
    }
    private async Task ProcessPendingAsync()
    {
        if (_destroyed || _suspended || _busy || _presentingAlerts) return;
        _presentingAlerts = true;
        try
        {
            while (!_destroyed && !_suspended && !_busy && _deferredAlerts.TryPeek(out var alert))
            {
                try { await DisplayAlertAsync(alert.Title, alert.Message, T("Готово", "Done")); }
                catch (Exception) { return; } // Keep the alert for the next foreground opportunity.
                if (_destroyed) return;
                _deferredAlerts.Dequeue();
            }
        }
        finally { _presentingAlerts = false; }
        if (!_destroyed && !_suspended && !_busy && _pendingStatus)
            await RunAsync(HandleStatusAsync);
    }
    private Task CloseConnectionAsync()
    {
        var old = _service; _service = null; _account = null; _pendingPhone = _expectedPhone = null; _pendingStatus = false;
        if (old != null) _connectionClose = CloseAfterAsync(_connectionClose, old);
        return _connectionClose;
    }
    private static async Task CloseAfterAsync(Task previous, ITelegramService service)
    {
        try { await previous; }
        finally { await service.DisposeAsync(); }
    }
    public void Suspend()
    {
        if (_destroyed) return;
        _suspended = true;
        ReleaseGlassPress();
    }
    private void ReleaseGlassPress()
    {
        _glass.SetSelectionPressed(false);
        if (_densitySlider?.Content is Grid grid)
            foreach (var child in grid.Children)
                if (child is GlassSurface sliderSurface) sliderSurface.SetSelectionPressed(false);
    }
    public void Resume()
    {
        if (_destroyed) return;
        _suspended = false;
        _ = ProcessPendingAsync();
    }
    public Task ShutdownAsync()
    {
        if (_destroyed) return _connectionClose;
        _destroyed = true; _suspended = true;
        _operation?.Cancel(); ReleaseGlassPress();
        _deferredAlerts.Clear();
        return CloseConnectionAsync();
    }
}
