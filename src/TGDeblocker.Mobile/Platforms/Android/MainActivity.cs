using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;

namespace TGDeblocker.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        ApplyThemePreference(Microsoft.Maui.Storage.Preferences.Default.Get("theme", "system"));
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateSystemBars(Microsoft.Maui.Controls.Application.Current?.RequestedTheme == Microsoft.Maui.ApplicationModel.AppTheme.Dark);
    }

    public void ApplyThemePreference(string preference)
    {
        var mode = preference switch
        {
            "light" => AppCompatDelegate.ModeNightNo,
            "dark" => AppCompatDelegate.ModeNightYes,
            _ => AppCompatDelegate.ModeNightFollowSystem
        };
        if (AppCompatDelegate.DefaultNightMode != mode)
            AppCompatDelegate.DefaultNightMode = mode;
    }

    public void UpdateSystemBars(bool isDark)
    {
        if (Window?.DecorView is { } decor)
        {
            var background = Android.Graphics.Color.ParseColor(TGDeblocker.Appearance.TelegramPalette.For(isDark).Background);
            // On edge-to-edge Android the decor is behind transparent system bars.
            decor.SetBackgroundColor(background);
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) Window.NavigationBarContrastEnforced = false;
            // Android 15 ignores this for gesture navigation but still uses it for
            // three-button navigation, including the automatic icon contrast decision.
            Window.SetNavigationBarColor(background);
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
            {
                Window.SetStatusBarColor(background);
            }
            var insets = WindowCompat.GetInsetsController(Window, decor);
            if (insets is not null)
            {
                insets.AppearanceLightStatusBars = !isDark;
                insets.AppearanceLightNavigationBars = !isDark;
            }
        }
    }
}
