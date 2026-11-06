using TGDeblocker.Appearance;

namespace TGDeblocker.Mobile;

public sealed class App : Application
{
    public App()
    {
        ApplyThemePreference();
        RequestedThemeChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateTheme);
    }

    public void ApplyThemePreference()
    {
        string preference = ThemePreference.Normalize(Preferences.Default.Get("theme", "system"));
#if ANDROID
        // Restore the OS configuration before MAUI resolves an automatic theme.
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is MainActivity activity)
            activity.ApplyThemePreference(preference);
#endif
        UserAppTheme = preference switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
        UpdateTheme();
    }

    private void UpdateTheme()
    {
        foreach (var window in Windows)
            if (window.Page is MainPage page) page.UpdateTheme();
#if ANDROID
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is MainActivity activity)
            activity.UpdateSystemBars(RequestedTheme == AppTheme.Dark);
#endif
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var window = new Window(page);
        window.Stopped += (_, _) => page.Suspend();
        window.Resumed += (_, _) => { ApplyThemePreference(); page.Resume(); };
        window.Destroying += async (_, _) =>
        {
            try { await page.ShutdownAsync(); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Mobile shutdown failed: {error.GetType().Name}"); }
        };
        return window;
    }
}
