namespace TGDeblocker.Core;

public sealed class DemoTelegramService : ITelegramService
{
    private readonly bool _walkThroughAuthentication;
    private readonly List<TelegramSession> _sessions;
    private TelegramPasswordState _passwordState = new(false, "", false, null, 0);

    public DemoTelegramService(bool walkThroughAuthentication = false)
    {
        _walkThroughAuthentication = walkThroughAuthentication;
        var now = DateTimeOffset.UtcNow;
        _sessions =
        [
            new(1, true, "TGDeblocker Demo", "Windows", "11", "TGDeblocker Demo", "1.0",
                "192.0.2.10", "Пример", now),
            new(2, false, "Pixel Demo", "Android", "15", "Telegram", "11.0",
                "198.51.100.21", "Пример", now.AddHours(-2)),
            new(3, false, "Desktop Demo", "Windows", "11", "Telegram Desktop", "5.0",
                "203.0.113.42", "Пример", now.AddDays(-1)),
        ];
    }

    public bool IsDemo => true;
    public TelegramStatus Status { get; private set; } = new(AuthorizationStep.NotStarted);
    public event Action<TelegramStatus>? StatusChanged;

    public Task StartAsync(int apiId, string apiHash, CancellationToken ct = default) =>
        Advance(AuthorizationStep.NotStarted,
            _walkThroughAuthentication ? AuthorizationStep.Phone : AuthorizationStep.Ready, "demo", ct);

    public Task SubmitPhoneAsync(string value, CancellationToken ct = default) =>
        Advance(AuthorizationStep.Phone, AuthorizationStep.Email, value, ct);

    public Task SubmitEmailAsync(string value, CancellationToken ct = default) =>
        Advance(AuthorizationStep.Email, AuthorizationStep.EmailCode, value, ct, "d***@example.test");

    public Task SubmitEmailCodeAsync(string value, CancellationToken ct = default) =>
        Advance(AuthorizationStep.EmailCode, AuthorizationStep.Code, value, ct);

    public Task SubmitCodeAsync(string value, CancellationToken ct = default) =>
        Advance(AuthorizationStep.Code, AuthorizationStep.Password, value, ct, "Демо-подсказка");

    public Task SubmitPasswordAsync(string value, CancellationToken ct = default) =>
        Advance(AuthorizationStep.Password, AuthorizationStep.Ready, value, ct);

    public Task<TelegramAccount> GetAccountAsync(CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        return Task.FromResult(new TelegramAccount(1, "Демо", "Аккаунт", "demo_account", "12025550110"));
    }

    public Task<IReadOnlyList<TelegramSession>> GetSessionsAsync(CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        return Task.FromResult<IReadOnlyList<TelegramSession>>(_sessions.ToArray());
    }

    public Task TerminateSessionAsync(long sessionId, CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        if (_sessions.RemoveAll(session => session.Id == sessionId && !session.IsCurrent) == 0)
            throw new TelegramException(0, "Выберите другой активный сеанс.");

        return Task.CompletedTask;
    }

    public Task TerminateOtherSessionsAsync(CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        _sessions.RemoveAll(session => !session.IsCurrent);
        return Task.CompletedTask;
    }

    public Task LogOutAsync(CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        SetStatus(new(AuthorizationStep.Closed));
        return Task.CompletedTask;
    }

    public Task<TelegramPasswordState> GetPasswordStateAsync(CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        return Task.FromResult(_passwordState);
    }

    public Task<TelegramPasswordState> SetPasswordAsync(
        string currentPassword, string newPassword, string hint, string? recoveryEmail, CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        RequireValue(newPassword);
        if (_passwordState.HasPassword)
            RequireValue(currentPassword);
        if (recoveryEmail is not null)
            RequireValue(recoveryEmail);

        _passwordState = _passwordState with { HasPassword = true, Hint = hint };
        if (recoveryEmail is not null)
        {
            _passwordState = _passwordState with
            {
                PendingRecoveryEmailPattern = "d***@example.test",
                PendingRecoveryEmailCodeLength = 6,
            };
        }

        return Task.FromResult(_passwordState);
    }

    public Task<TelegramPasswordState> ConfirmRecoveryEmailAsync(string code, CancellationToken ct = default)
    {
        RequireStep(AuthorizationStep.Ready, ct);
        RequireValue(code);
        if (_passwordState.PendingRecoveryEmailPattern is null)
            throw new TelegramException(0, "Telegram не ждёт подтверждения почты восстановления.");

        _passwordState = _passwordState with
        {
            HasRecoveryEmail = true,
            PendingRecoveryEmailPattern = null,
            PendingRecoveryEmailCodeLength = 0,
        };
        return Task.FromResult(_passwordState);
    }

    public ValueTask DisposeAsync()
    {
        if (Status.Step != AuthorizationStep.Closed)
            SetStatus(new(AuthorizationStep.Closed));

        return ValueTask.CompletedTask;
    }

    private Task Advance(AuthorizationStep expected, AuthorizationStep next, string value, CancellationToken ct,
        string? detail = null)
    {
        RequireStep(expected, ct);
        RequireValue(value);
        SetStatus(new(next, detail));
        return Task.CompletedTask;
    }

    private void RequireStep(AuthorizationStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Status.Step != step)
            throw new TelegramException(0, "Действие недоступно на текущем этапе входа.");
    }

    private static void RequireValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new TelegramException(0, "Введите тестовое значение.");
    }

    private void SetStatus(TelegramStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
