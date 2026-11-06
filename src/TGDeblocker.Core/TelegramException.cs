using System.Globalization;
using System.Text.RegularExpressions;

namespace TGDeblocker.Core;

public sealed class TelegramLogoutUnconfirmedException : Exception
{
    internal TelegramLogoutUnconfirmedException()
        : base("Не удалось подтвердить выход. Проверьте список устройств в Telegram перед новой попыткой.") { }
}

public sealed class TelegramException : Exception
{
    public int Code { get; }
    public TimeSpan? RetryAfter { get; }

    internal TelegramException(int code, string message, TimeSpan? retryAfter = null) : base(message)
    {
        Code = code;
        RetryAfter = retryAfter;
    }

    internal static TelegramException FromTdLib(int code, string serverMessage)
    {
        // Server messages are untrusted and may contain request data. Only show local strings.
        if (code == 429 || serverMessage.StartsWith("FLOOD_WAIT_", StringComparison.Ordinal))
        {
            var match = Regex.Match(serverMessage, @"(?:FLOOD_WAIT_|retry after )(?<seconds>[0-9]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (int.TryParse(match.Groups["seconds"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var seconds))
            {
                return new TelegramException(code, $"Telegram просит повторить попытку через {seconds} с.",
                    TimeSpan.FromSeconds(seconds));
            }

            return new TelegramException(code, "Telegram ограничил частоту запросов. Повторите попытку позже.");
        }

        var message = serverMessage switch
        {
            "API_ID_INVALID" or "API_ID_PUBLISHED_FLOOD" => "Telegram отклонил API ID или API Hash.",
            "PHONE_NUMBER_INVALID" => "Проверьте номер телефона и код страны.",
            "PHONE_NUMBER_BANNED" => "Telegram ограничил вход для этого номера.",
            "PHONE_CODE_INVALID" or "PHONE_CODE_EMPTY" or "CODE_INVALID" or "EMAIL_CODE_INVALID" =>
                "Проверьте код подтверждения.",
            "PHONE_CODE_EXPIRED" or "EMAIL_CODE_EXPIRED" => "Срок действия кода подтверждения истёк.",
            "EMAIL_INVALID" or "EMAIL_NOT_ALLOWED" => "Telegram отклонил адрес почты.",
            "PASSWORD_HASH_INVALID" or "PASSWORD_INVALID" => "Проверьте облачный пароль Telegram.",
            "FRESH_RESET_AUTHORISATION_FORBIDDEN" or "FRESH_RESET_AUTHORIZATION_FORBIDDEN" =>
                "Telegram пока не позволяет завершать другие сеансы с этого устройства. Используйте более ранний сеанс или повторите позже.",
            "SESSION_REVOKED" or "SESSION_EXPIRED" or "AUTH_KEY_UNREGISTERED" =>
                "Этот сеанс Telegram завершён. Войдите снова.",
            _ => $"Telegram отклонил запрос (код {code}).",
        };

        if (serverMessage.StartsWith("EMAIL_UNCONFIRMED_", StringComparison.Ordinal))
            message = "Почта восстановления ждёт подтверждения. Обновите состояние облачного пароля.";

        return new TelegramException(code, message);
    }
}
