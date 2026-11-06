namespace TGDeblocker.Appearance;

public static class ThemePreference
{
    public static string Normalize(string? preference) => preference is "light" or "dark" ? preference : "system";

    public static bool IsDark(string? preference, bool systemDark) => Normalize(preference) switch
    {
        "light" => false,
        "dark" => true,
        _ => systemDark
    };
}
