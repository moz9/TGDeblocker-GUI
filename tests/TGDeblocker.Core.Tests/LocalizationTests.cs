using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TGDeblocker.App;
using TGDeblocker.Appearance;

namespace TGDeblocker.Core.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("auto", "ru-RU", "ru")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("en", "ru-RU", "en")]
    [InlineData("ru", "en-US", "ru")]
    [InlineData(null, "ru-RU", "ru")]
    public void ResolvesSystemLanguageAndManualOverrides(string? preference, string systemCulture, string expected)
    {
        Assert.Equal(expected, AppStrings.ResolveLanguage(preference, CultureInfo.GetCultureInfo(systemCulture)));
    }

    [Fact]
    public void TranslatesKnownStringsAndPreservesRussian()
    {
        Assert.Equal("Settings", AppStrings.Text("Настройки", "en"));
        Assert.Equal("Настройки", AppStrings.Text("Настройки", "ru"));
        Assert.Equal("Sample hint", AppStrings.Text("Sample hint", "en"));
    }

    [Theory]
    [InlineData("Telegram просит повторить попытку через 37 с.", "Telegram asks you to retry in 37 seconds.")]
    [InlineData("Telegram отклонил запрос (код 400).", "Telegram rejected the request (code 400).")]
    [InlineData("Не удалось подтвердить выход. Проверьте список устройств в Telegram перед новой попыткой.", "Could not confirm sign-out. Check your devices in Telegram before trying again.")]
    [InlineData("Неизвестная ошибка", "Could not complete the action. Check the connection and try again.")]
    public void TranslatesErrorsAndPreservesRelevantValues(string original, string expected)
    {
        Assert.Equal(expected, AppStrings.Message(original, "en"));
        Assert.Equal(original, AppStrings.Message(original, "ru"));
    }

    [Fact]
    public void AppearanceJsonPreservesLanguageAndOtherPreferences()
    {
        var old = JsonSerializer.Deserialize("{\"Density\":0.6}", LocalizationJsonContext.Default.AppearanceSettings)!;
        Assert.Equal("auto", old.Language);
        Assert.Equal("system", old.Theme);
        old.Language = "en";
        old.Theme = "dark";
        old.ReduceMotion = true;
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(old, LocalizationJsonContext.Default.AppearanceSettings), LocalizationJsonContext.Default.AppearanceSettings)!;
        Assert.Equal("en", restored.Language);
        Assert.Equal("dark", restored.Theme);
        Assert.Equal(0.6, restored.Density);
        Assert.True(restored.ReduceMotion);
        Assert.False(restored.ReduceTransparency);
    }

    [Theory]
    [InlineData("light", true, false)]
    [InlineData("dark", false, true)]
    [InlineData("system", false, false)]
    [InlineData("system", true, true)]
    [InlineData(null, true, true)]
    [InlineData("invalid", false, false)]
    public void ThemeFollowsSystemUnlessExplicitlyOverridden(string? preference, bool systemDark, bool expectedDark)
    {
        Assert.Equal(expectedDark, ThemePreference.IsDark(preference, systemDark));
    }

    [Fact]
    public void AutomaticThemeKeepsFollowingSubsequentSystemChanges()
    {
        string saved = ThemePreference.Normalize(null);
        Assert.Equal("system", saved);
        Assert.False(ThemePreference.IsDark(saved, false));
        Assert.True(ThemePreference.IsDark(saved, true));
        Assert.Equal("system", ThemePreference.Normalize("unknown"));
    }
}

[JsonSerializable(typeof(AppearanceSettings))]
internal partial class LocalizationJsonContext : JsonSerializerContext;
