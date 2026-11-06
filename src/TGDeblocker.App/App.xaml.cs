using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TGDeblocker.Appearance;

namespace TGDeblocker.App;

public partial class App : Application
{
    private Window? _window;
    private Mutex? _instanceMutex;
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
    public App()
    {
        if (Environment.GetCommandLineArgs().Contains("--visual-check"))
            UnhandledException += (_, e) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "visual-error.log"), e.Exception.ToString());
        try
        {
            InitializeComponent();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), error.ToString());
            throw;
        }
    }

    private static ResourceDictionary ThemeResources(TelegramColors colors)
    {
        var resources = new ResourceDictionary();
        void Brush(string key, string color) => resources[key] = new SolidColorBrush(ParseColor(color));
        Brush("TextFillColorPrimaryBrush", colors.Text);
        Brush("TextFillColorSecondaryBrush", colors.Muted);
        Brush("ControlFillColorDefaultBrush", colors.Surface);
        Brush("ControlFillColorSecondaryBrush", colors.Border);
        Brush("ControlFillColorTertiaryBrush", colors.Border);
        Brush("ControlStrongStrokeColorDefaultBrush", colors.Muted);
        Brush("ControlSolidFillColorDefaultBrush", colors.Surface);
        Brush("SolidBackgroundFillColorBaseBrush", colors.Background);
        Brush("SolidBackgroundFillColorSecondaryBrush", colors.Surface);
        Brush("ContentDialogBackground", colors.Background);
        Brush("ComboBoxDropDownBackground", colors.Background);
        foreach (var state in new[] { "Default", "Secondary", "Tertiary" })
            Brush("AccentFillColor" + state + "Brush", colors.Button);
        Brush("AccentTextFillColorPrimaryBrush", colors.Link);
        Brush("TextOnAccentFillColorPrimaryBrush", colors.ButtonText);
        Brush("TextOnAccentFillColorSecondaryBrush", colors.ButtonText);
        resources["SystemAccentColor"] = ParseColor(colors.Accent);
        return resources;
    }

    internal static Windows.UI.Color ParseColor(string hex)
    {
        var rgb = Convert.ToUInt32(hex.AsSpan(1).ToString(), 16);
        return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (!Environment.GetCommandLineArgs().Contains("--setup-launch") && Mutex.TryOpenExisting(AppIdentity.MutexName + ".Setup", out var setupMutex))
            {
                setupMutex.Dispose();
                var russian = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
                MessageBoxW(0, russian ? "Дождитесь завершения установки TGDeblocker." : "Wait for TGDeblocker Setup to finish.", "TGDeblocker", 0x40);
                Exit(); return;
            }
            _instanceMutex = new Mutex(false, AppIdentity.MutexName, out var firstInstance);
            if (!firstInstance)
            {
                _instanceMutex.Dispose(); _instanceMutex = null;
                var russian = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
                MessageBoxW(0, russian ? "TGDeblocker уже запущен. Откройте существующее окно." : "TGDeblocker is already running. Open its existing window.", "TGDeblocker", 0x40);
                Exit(); return;
            }
            Resources.ThemeDictionaries["Light"] = ThemeResources(TelegramPalette.Light);
            Resources.ThemeDictionaries["Dark"] = ThemeResources(TelegramPalette.Dark);
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception error)
        {
            // Startup only: no credentials or Telegram responses exist at this point.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), error.ToString());
            throw;
        }
    }
}
