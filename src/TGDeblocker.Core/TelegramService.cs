using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace TGDeblocker.Core;

public sealed record TelegramClientInfo(
    string DeviceModel, string SystemLanguageCode, string ApplicationVersion, string? SystemVersion = null);

public sealed class TelegramService : ITelegramService
{
    private static readonly TelegramClientInfo WindowsClient = new("Windows Desktop", "ru", "1.0");
    private readonly Func<ITdJsonTransport> _createTransport;
    private readonly Func<CancellationToken, ValueTask<TelegramSessionStorage>> _openStorage;
    private readonly TelegramClientInfo _clientInfo;
    private readonly SemaphoreSlim _commands = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycle = new();
    private TelegramStatus _status = new(AuthorizationStep.NotStarted);
    private ITdJsonTransport? _transport;
    private Task? _disposeTask;
    private bool _logoutUnconfirmed;

    public TelegramService() : this(() => new NativeTdJsonClient(),
        _ => ValueTask.FromResult(SessionStorage.OpenDefault()), WindowsClient)
    {
    }

    /// <summary>Uses the supplied native TDLib client and protected storage for this platform.</summary>
    public TelegramService(Func<ITdJsonClient> createClient,
        Func<CancellationToken, ValueTask<TelegramSessionStorage>> openStorage, TelegramClientInfo clientInfo)
        : this(() => new TdJsonTransport(createClient()), openStorage, clientInfo)
    {
        ArgumentNullException.ThrowIfNull(createClient);
    }

    internal TelegramService(Func<ITdJsonTransport> createTransport,
        Func<CancellationToken, ValueTask<TelegramSessionStorage>> openStorage, TelegramClientInfo? clientInfo = null)
    {
        ArgumentNullException.ThrowIfNull(openStorage);
        _clientInfo = clientInfo ?? WindowsClient;
        ArgumentException.ThrowIfNullOrWhiteSpace(_clientInfo.DeviceModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(_clientInfo.SystemLanguageCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(_clientInfo.ApplicationVersion);
        _createTransport = createTransport;
        _openStorage = openStorage;
    }

    public bool IsDemo => false;
    public TelegramStatus Status => Volatile.Read(ref _status);
    public event Action<TelegramStatus>? StatusChanged;

    public Task StartAsync(int apiId, string apiHash, CancellationToken ct = default) => RunAsync(async token =>
    {
        RequireStep(AuthorizationStep.NotStarted);
        if (apiId <= 0 || string.IsNullOrWhiteSpace(apiHash))
            throw new TelegramException(0, "Введите API ID и API Hash Telegram.");

        SetStatus(new(AuthorizationStep.Starting));
        try
        {
            var storage = await _openStorage(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                _transport = _createTransport();
                _transport.Updated += ApplyUpdate;
                _transport.Faulted += ApplyTransportFailure;

                var initial = await SendAsync(Request("getAuthorizationState"), token).ConfigureAwait(false);
                Expect(initial, "authorizationStateWaitTdlibParameters");
                var response = await SendAsync(new Dictionary<string, object?>
                {
                    ["@type"] = "setTdlibParameters",
                    ["use_test_dc"] = false,
                    ["database_directory"] = storage.DatabaseDirectory,
                    ["database_encryption_key"] = Convert.ToBase64String(storage.Key),
                    ["use_file_database"] = false,
                    ["use_chat_info_database"] = false,
                    ["use_message_database"] = false,
                    ["use_secret_chats"] = false,
                    ["api_id"] = apiId,
                    ["api_hash"] = apiHash.Trim(),
                    ["system_language_code"] = _clientInfo.SystemLanguageCode,
                    ["device_model"] = _clientInfo.DeviceModel,
                    ["system_version"] = _clientInfo.SystemVersion ?? "",
                    ["application_version"] = _clientInfo.ApplicationVersion,
                }, token).ConfigureAwait(false);
                Expect(response, "ok");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(storage.Key);
            }
        }
        catch (Exception error)
        {
            await CloseTransportAsync().ConfigureAwait(false);
            if (!_shutdown.IsCancellationRequested)
            {
                var detail = error is TelegramException or TimeoutException
                    ? error.Message : "Не удалось запустить TDLib или открыть защищённое хранилище сеанса.";
                SetStatus(new(AuthorizationStep.Failed, detail));
                if (error is not (TelegramException or TimeoutException or OperationCanceledException))
                    throw new TelegramException(0, detail);
            }

            throw;
        }

        return true;
    }, ct);

    public Task SubmitPhoneAsync(string value, CancellationToken ct = default) =>
        SubmitAsync(AuthorizationStep.Phone, "setAuthenticationPhoneNumber", "phone_number", value.Trim(), ct);

    public Task SubmitCodeAsync(string value, CancellationToken ct = default) =>
        SubmitAsync(AuthorizationStep.Code, "checkAuthenticationCode", "code", value.Trim(), ct);

    public Task SubmitPasswordAsync(string value, CancellationToken ct = default) =>
        SubmitAsync(AuthorizationStep.Password, "checkAuthenticationPassword", "password", value, ct);

    public Task SubmitEmailAsync(string value, CancellationToken ct = default) =>
        SubmitAsync(AuthorizationStep.Email, "setAuthenticationEmailAddress", "email_address", value.Trim(), ct);

    public Task SubmitEmailCodeAsync(string value, CancellationToken ct = default) => RunAsync(async token =>
    {
        RequireStep(AuthorizationStep.EmailCode);
        RequireValue(value);
        var code = new Dictionary<string, object?> { ["@type"] = "emailAddressAuthenticationCode", ["code"] = value.Trim() };
        Expect(await SendAsync(new Dictionary<string, object?>
        {
            ["@type"] = "checkAuthenticationEmailCode",
            ["code"] = code,
        }, token).ConfigureAwait(false), "ok");
        return true;
    }, ct);

    public Task<TelegramAccount> GetAccountAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        RequireStep(AuthorizationStep.Ready);
        var user = await SendAsync(Request("getMe"), token).ConfigureAwait(false);
        Expect(user, "user");

        string? username = null;
        if (user.TryGetProperty("usernames", out var usernames) && usernames.ValueKind == JsonValueKind.Object &&
            usernames.TryGetProperty("active_usernames", out var active) && active.ValueKind == JsonValueKind.Array &&
            active.GetArrayLength() > 0)
        {
            username = active[0].GetString();
        }

        return new TelegramAccount(user.GetProperty("id").GetInt64(), ReadText(user, "first_name"),
            ReadText(user, "last_name"), username, ReadText(user, "phone_number"));
    }, ct);

    public Task<IReadOnlyList<TelegramSession>> GetSessionsAsync(CancellationToken ct = default) =>
        RunAsync(GetSessionsCoreAsync, ct);

    public Task TerminateSessionAsync(long sessionId, CancellationToken ct = default) => RunAsync(async token =>
    {
        var sessions = await GetSessionsCoreAsync(token).ConfigureAwait(false);
        var target = sessions.FirstOrDefault(session => session.Id == sessionId);
        if (target is null || target.IsCurrent)
            throw new TelegramException(0, "Выберите другой активный сеанс.");

        RequireStep(AuthorizationStep.Ready);
        var request = Request("terminateSession");
        // TDLib represents int64 values as decimal strings in its JSON interface.
        request["session_id"] = sessionId.ToString(CultureInfo.InvariantCulture);
        Expect(await SendAsync(request, token).ConfigureAwait(false), "ok");
        return true;
    }, ct);

    public Task TerminateOtherSessionsAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        RequireStep(AuthorizationStep.Ready);
        Expect(await SendAsync(Request("terminateAllOtherSessions"), token).ConfigureAwait(false), "ok");
        return true;
    }, ct);

    public Task LogOutAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        if (_logoutUnconfirmed)
            throw new TelegramLogoutUnconfirmedException();

        RequireStep(AuthorizationStep.Ready);
        var closure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveClosure(TelegramStatus status)
        {
            if (status.Step == AuthorizationStep.Closed)
                closure.TrySetResult(true);
            else if (status.Step == AuthorizationStep.Failed)
                closure.TrySetResult(false);
        }

        StatusChanged += ObserveClosure;
        try
        {
            RequireStep(AuthorizationStep.Ready);
            token.ThrowIfCancellationRequested();
            try
            {
                Expect(await SendAsync(Request("logOut"), token).ConfigureAwait(false), "ok");
                if (!await closure.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false))
                    throw new TelegramLogoutUnconfirmedException();
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException or
                TelegramLogoutUnconfirmedException or TelegramException { Code: <= 0 or >= 500 })
            {
                _logoutUnconfirmed = true;
                if (error is TelegramLogoutUnconfirmedException)
                    throw;

                throw new TelegramLogoutUnconfirmedException();
            }
        }
        finally
        {
            StatusChanged -= ObserveClosure;
        }

        return true;
    }, ct);

    public Task<TelegramPasswordState> GetPasswordStateAsync(CancellationToken ct = default) =>
        RunAsync(GetPasswordStateCoreAsync, ct);

    public Task<TelegramPasswordState> SetPasswordAsync(
        string currentPassword, string newPassword, string hint, string? recoveryEmail, CancellationToken ct = default) =>
        RunAsync(async token =>
        {
            RequireStep(AuthorizationStep.Ready);
            RequireValue(newPassword);
            if (recoveryEmail is not null)
                RequireValue(recoveryEmail);

            var response = await SendAsync(new Dictionary<string, object?>
            {
                ["@type"] = "setPassword",
                ["old_password"] = currentPassword,
                ["new_password"] = newPassword,
                ["new_hint"] = hint,
                ["set_recovery_email_address"] = recoveryEmail is not null,
                ["new_recovery_email_address"] = recoveryEmail?.Trim() ?? "",
            }, token).ConfigureAwait(false);
            return ParsePasswordState(response);
        }, ct);

    public Task<TelegramPasswordState> ConfirmRecoveryEmailAsync(string code, CancellationToken ct = default) =>
        RunAsync(async token =>
        {
            RequireValue(code);
            var state = await GetPasswordStateCoreAsync(token).ConfigureAwait(false);
            if (state.PendingRecoveryEmailPattern is null)
                throw new TelegramException(0, "Telegram не ждёт подтверждения почты восстановления.");

            RequireStep(AuthorizationStep.Ready);
            var request = Request("checkRecoveryEmailAddressCode");
            request["code"] = code.Trim();
            var updated = ParsePasswordState(await SendAsync(request, token).ConfigureAwait(false));
            // TDLib can return a refreshed password state for invalid and expired codes.
            if (!updated.HasRecoveryEmail || updated.PendingRecoveryEmailPattern is not null)
                throw new TelegramException(0, "Telegram не подтвердил почту. Проверьте код и срок его действия.");

            return updated;
        }, ct);

    private Task SubmitAsync(AuthorizationStep step, string type, string field, string value, CancellationToken ct) =>
        RunAsync(async token =>
        {
            RequireStep(step);
            RequireValue(value);
            var request = Request(type);
            request[field] = value;
            Expect(await SendAsync(request, token).ConfigureAwait(false), "ok");
            return true;
        }, ct);

    private async Task<IReadOnlyList<TelegramSession>> GetSessionsCoreAsync(CancellationToken ct)
    {
        RequireStep(AuthorizationStep.Ready);
        var response = await SendAsync(Request("getActiveSessions"), ct).ConfigureAwait(false);
        Expect(response, "sessions");
        return response.GetProperty("sessions").EnumerateArray().Select(session => new TelegramSession(
            ParseSessionId(session.GetProperty("id")),
            session.GetProperty("is_current").GetBoolean(),
            ReadText(session, "device_model"), ReadText(session, "platform"), ReadText(session, "system_version"),
            ReadText(session, "application_name"), ReadText(session, "application_version"),
            ReadText(session, "ip_address"), ReadText(session, "location"),
            DateTimeOffset.FromUnixTimeSeconds(session.GetProperty("last_active_date").GetInt32())))
            .OrderByDescending(session => session.IsCurrent).ThenByDescending(session => session.LastActive).ToArray();
    }

    private async Task<TelegramPasswordState> GetPasswordStateCoreAsync(CancellationToken ct)
    {
        RequireStep(AuthorizationStep.Ready);
        return ParsePasswordState(await SendAsync(Request("getPasswordState"), ct).ConfigureAwait(false));
    }

    private static TelegramPasswordState ParsePasswordState(JsonElement response)
    {
        Expect(response, "passwordState");
        var pending = response.TryGetProperty("recovery_email_address_code_info", out var info) &&
            info.ValueKind == JsonValueKind.Object;
        return new TelegramPasswordState(response.GetProperty("has_password").GetBoolean(),
            ReadText(response, "password_hint"), response.GetProperty("has_recovery_email_address").GetBoolean(),
            pending ? ReadText(info, "email_address_pattern") : null,
            pending ? info.GetProperty("length").GetInt32() : 0);
    }

    private void ApplyUpdate(JsonElement update)
    {
        if (_shutdown.IsCancellationRequested || ReadText(update, "@type") != "updateAuthorizationState")
            return;

        var state = update.GetProperty("authorization_state");
        var status = ReadText(state, "@type") switch
        {
            "authorizationStateWaitTdlibParameters" => null,
            "authorizationStateWaitPhoneNumber" => new(AuthorizationStep.Phone),
            "authorizationStateWaitCode" => new(AuthorizationStep.Code),
            "authorizationStateWaitPassword" => new(AuthorizationStep.Password, ReadText(state, "password_hint")),
            "authorizationStateWaitEmailAddress" => new(AuthorizationStep.Email),
            "authorizationStateWaitEmailCode" => new(AuthorizationStep.EmailCode,
                state.TryGetProperty("code_info", out var info) ? ReadText(info, "email_address_pattern") : null),
            "authorizationStateReady" => new(AuthorizationStep.Ready),
            "authorizationStateLoggingOut" or "authorizationStateClosing" => new(AuthorizationStep.Closing),
            "authorizationStateClosed" => new(AuthorizationStep.Closed),
            "authorizationStateWaitRegistration" => new(AuthorizationStep.Unsupported,
                "Telegram требует зарегистрировать аккаунт. Завершите регистрацию в официальном приложении."),
            "authorizationStateWaitOtherDeviceConfirmation" => new(AuthorizationStep.Unsupported,
                "Telegram требует подтверждение на другом устройстве. Откройте официальный клиент Telegram."),
            _ => new TelegramStatus(AuthorizationStep.Unsupported,
                "Telegram запрашивает способ входа, недоступный в этом приложении."),
        };

        if (status is not null)
            SetStatus(status);
    }

    private void ApplyTransportFailure()
    {
        if (!_shutdown.IsCancellationRequested)
            SetStatus(new(AuthorizationStep.Failed, "Обмен данными с TDLib прерван."));
    }

    private void SetStatus(TelegramStatus status)
    {
        if (Interlocked.Exchange(ref _status, status) == status)
            return;

        // A UI observer cannot interrupt the TDLib receiver or an account operation.
        foreach (var callback in StatusChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<TelegramStatus>)callback)(status); }
            catch (Exception) { }
        }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        CancellationTokenSource linked;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        }

        using (linked)
        {
            await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return await action(linked.Token).ConfigureAwait(false); }
            finally { _commands.Release(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        await _commands.WaitAsync().ConfigureAwait(false);
        try { await CloseTransportAsync().ConfigureAwait(false); }
        finally
        {
            SetStatus(new(AuthorizationStep.Closed));
            _commands.Release();
            _shutdown.Dispose();
        }
    }

    private async ValueTask CloseTransportAsync()
    {
        if (_transport is null)
            return;

        var transport = _transport;
        _transport = null;
        transport.Updated -= ApplyUpdate;
        transport.Faulted -= ApplyTransportFailure;
        await transport.DisposeAsync().ConfigureAwait(false);
    }

    private Task<JsonElement> SendAsync(Dictionary<string, object?> request, CancellationToken ct) =>
        (_transport ?? throw new TelegramException(0, "Сначала подключите Telegram.")).SendAsync(request, ct);

    private static Dictionary<string, object?> Request(string type) => new() { ["@type"] = type };

    private void RequireStep(AuthorizationStep step)
    {
        if (Status.Step != step)
            throw new TelegramException(0, "Действие недоступно на текущем этапе входа.");
    }

    private static void RequireValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new TelegramException(0, "Заполните обязательное поле.");
    }

    private static void Expect(JsonElement message, string type)
    {
        if (ReadText(message, "@type") != type)
            throw new TelegramException(0, "TDLib вернула неожиданный ответ.");
    }

    private static string ReadText(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long ParseSessionId(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt64();
}
