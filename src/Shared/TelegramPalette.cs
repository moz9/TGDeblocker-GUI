namespace TGDeblocker.Appearance;

// Telegram Desktop's default/day and night palette. Small light-theme text uses
// a darker blue/gray for contrast; glass keeps these hues instead of a purple tint.
public sealed record TelegramColors(string Background, string Text, string Muted,
    string Accent, string Link, string Button, string ButtonText, string Surface,
    string Border, string Track);

public static class TelegramPalette
{
    public static readonly TelegramColors Light = new("#FFFFFF", "#222222", "#707070",
        "#40A7E3", "#0D75B1", "#40A7E3", "#17212B", "#F1F1F1", "#E0E0E0", "#E1EAEF");
    public static readonly TelegramColors Dark = new("#17212B", "#F5F5F5", "#8C9EAF",
        "#5288C1", "#6AB3F3", "#2F6EA5", "#FFFFFF", "#232E3C", "#313C49", "#364554");
    public static TelegramColors For(bool dark) => dark ? Dark : Light;
}
