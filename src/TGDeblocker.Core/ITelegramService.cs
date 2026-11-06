namespace TGDeblocker.Core;

public enum AuthorizationStep
{
    NotStarted,
    Starting,
    Phone,
    Code,
    Password,
    Email,
    EmailCode,
    Ready,
    Unsupported,
    Closing,
    Closed,
    Failed,
}

public sealed record TelegramStatus(AuthorizationStep Step, string? Detail = null);

public sealed record TelegramAccount(long Id, string FirstName, string LastName, string? Username, string PhoneNumber)
{
    public override string ToString() => nameof(TelegramAccount);
}

public sealed record TelegramSession(
    long Id,
    bool IsCurrent,
    string DeviceModel,
    string Platform,
    string SystemVersion,
    string ApplicationName,
    string ApplicationVersion,
    string IpAddress,
    string Location,
    DateTimeOffset LastActive);

public sealed record TelegramPasswordState(
    bool HasPassword,
    string Hint,
    bool HasRecoveryEmail,
    string? PendingRecoveryEmailPattern,
    int PendingRecoveryEmailCodeLength);

public interface ITelegramService : IAsyncDisposable
{
    bool IsDemo { get; }
    TelegramStatus Status { get; }

    /// <summary>Raised from the thread processing the Telegram update.</summary>
    event Action<TelegramStatus>? StatusChanged;

    Task StartAsync(int apiId, string apiHash, CancellationToken ct = default);
    Task SubmitPhoneAsync(string value, CancellationToken ct = default);
    Task SubmitCodeAsync(string value, CancellationToken ct = default);
    Task SubmitPasswordAsync(string value, CancellationToken ct = default);
    Task SubmitEmailAsync(string value, CancellationToken ct = default);
    Task SubmitEmailCodeAsync(string value, CancellationToken ct = default);
    Task<TelegramAccount> GetAccountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TelegramSession>> GetSessionsAsync(CancellationToken ct = default);
    Task TerminateSessionAsync(long sessionId, CancellationToken ct = default);
    Task TerminateOtherSessionsAsync(CancellationToken ct = default);
    /// <summary>Ends this Telegram session and waits for TDLib to finish closing it.</summary>
    /// <exception cref="TelegramLogoutUnconfirmedException">The result is unknown after dispatch.</exception>
    Task LogOutAsync(CancellationToken ct = default);
    Task<TelegramPasswordState> GetPasswordStateAsync(CancellationToken ct = default);
    Task<TelegramPasswordState> SetPasswordAsync(
        string currentPassword, string newPassword, string hint, string? recoveryEmail,
        CancellationToken ct = default);
    Task<TelegramPasswordState> ConfirmRecoveryEmailAsync(string code, CancellationToken ct = default);
}
