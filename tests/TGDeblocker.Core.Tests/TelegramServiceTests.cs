using System.Text.Json;
using TGDeblocker.Core;

namespace TGDeblocker.Core.Tests;

public sealed class TelegramServiceTests
{
    private static readonly string TestDatabase = Path.Combine(Path.GetTempPath(), "tdlib-service-test");
    private const string NoPassword = """
        {"@type":"passwordState","has_password":false,"password_hint":"","has_recovery_email_address":false}
        """;
    private const string PendingEmail = """
        {"@type":"passwordState","has_password":true,"password_hint":"hint","has_recovery_email_address":false,
         "recovery_email_address_code_info":{"email_address_pattern":"a***@example.test","length":6}}
        """;
    private const string ConfirmedEmail = """
        {"@type":"passwordState","has_password":true,"password_hint":"hint","has_recovery_email_address":true}
        """;
    private const string ActiveSessions = """
        {"@type":"sessions","sessions":[
          {"id":"9007199254740999","is_current":false,"device_model":"Phone","last_active_date":1712345700},
          {"id":"-9007199254740999","is_current":true,"device_model":"PC","last_active_date":1712345600}
        ]}
        """;

    [Fact]
    public async Task ReadyAndUnsupportedStatesDoNotSubmitAccountRequests()
    {
        var transport = new FakeTransport();
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        await using var service = new TelegramService(() => transport,
            _ => ValueTask.FromResult(new TelegramSessionStorage(TestDatabase, key)));

        await service.StartAsync(42, "test-hash");
        Assert.Equal(TestDatabase, transport.Requests[1].GetProperty("database_directory").GetString());
        Assert.Equal(32, Convert.FromBase64String(transport.Requests[1].GetProperty("database_encryption_key").GetString()!).Length);
        Assert.All(key, value => Assert.Equal(0, value));

        transport.Authorize("authorizationStateWaitRegistration");
        Assert.Equal(AuthorizationStep.Unsupported, service.Status.Step);
        transport.Authorize("authorizationStateReady");
        Assert.Equal(AuthorizationStep.Ready, service.Status.Step);
        Assert.Equal(["getAuthorizationState", "setTdlibParameters"], transport.RequestTypes);
    }

    [Fact]
    public async Task AuthorizationSubmissionsRequireMatchingStepAndUseLoginEmailCodeType()
    {
        var transport = new FakeTransport();
        await using var service = CreateService(transport);
        await service.StartAsync(42, "test-hash");

        await Assert.ThrowsAsync<TelegramException>(() => service.SubmitCodeAsync("01234"));
        transport.Authorize("authorizationStateWaitPhoneNumber");
        await service.SubmitPhoneAsync(" +12025550101 ");
        transport.Authorize("authorizationStateWaitEmailAddress");
        await service.SubmitEmailAsync(" person@example.test ");
        transport.Authorize("authorizationStateWaitEmailCode", "\"code_info\":{\"email_address_pattern\":\"p***@example.test\"}");
        Assert.Equal("p***@example.test", service.Status.Detail);
        await service.SubmitEmailCodeAsync(" 012345 ");
        transport.Authorize("authorizationStateWaitCode");
        await service.SubmitCodeAsync("01234");
        transport.Authorize("authorizationStateWaitPassword", "\"password_hint\":\"test hint\"");
        Assert.Equal("test hint", service.Status.Detail);
        await service.SubmitPasswordAsync(" test password ");

        Assert.Equal(["getAuthorizationState", "setTdlibParameters", "setAuthenticationPhoneNumber",
            "setAuthenticationEmailAddress", "checkAuthenticationEmailCode", "checkAuthenticationCode",
            "checkAuthenticationPassword"], transport.RequestTypes);
        Assert.Equal("+12025550101", transport.Requests[2].GetProperty("phone_number").GetString());
        var loginEmailCode = transport.Requests[4].GetProperty("code");
        Assert.Equal("emailAddressAuthenticationCode", loginEmailCode.GetProperty("@type").GetString());
        Assert.Equal("012345", loginEmailCode.GetProperty("code").GetString());
        Assert.Equal(" test password ", transport.Requests[^1].GetProperty("password").GetString());
    }

    [Fact]
    public async Task RecoveryEmailIsChangedOnlyExplicitlyAndUsesItsOwnConfirmationRequest()
    {
        var transport = new FakeTransport();
        transport.Respond("getPasswordState", NoPassword);
        transport.Respond("getPasswordState", PendingEmail);
        transport.Respond("setPassword", PendingEmail);
        transport.Respond("setPassword", PendingEmail);
        transport.Respond("checkRecoveryEmailAddressCode", ConfirmedEmail);
        await using var service = CreateService(transport);
        await StartReadyAsync(service, transport);

        await Assert.ThrowsAsync<TelegramException>(() => service.ConfirmRecoveryEmailAsync("012345"));
        Assert.DoesNotContain("checkRecoveryEmailAddressCode", transport.RequestTypes);

        await service.SetPasswordAsync("old", "new", "hint", null);
        Assert.False(transport.Requests[^1].GetProperty("set_recovery_email_address").GetBoolean());
        var pending = await service.SetPasswordAsync("old", "new", "hint", "a@example.test");
        var request = transport.Requests[^1];
        Assert.True(request.GetProperty("set_recovery_email_address").GetBoolean());
        Assert.Equal("a@example.test", request.GetProperty("new_recovery_email_address").GetString());
        Assert.Equal("a***@example.test", pending.PendingRecoveryEmailPattern);
        Assert.Equal(6, pending.PendingRecoveryEmailCodeLength);

        var confirmed = await service.ConfirmRecoveryEmailAsync("012345");
        Assert.True(confirmed.HasRecoveryEmail);
        Assert.Null(confirmed.PendingRecoveryEmailPattern);
        Assert.Equal("checkRecoveryEmailAddressCode", transport.RequestTypes[^1]);
        Assert.Equal("012345", transport.Requests[^1].GetProperty("code").GetString());
        Assert.DoesNotContain("checkPasswordRecoveryCode", transport.RequestTypes);
        Assert.DoesNotContain("checkAuthenticationEmailCode", transport.RequestTypes);
    }

    [Fact]
    public async Task RecoveryEmailConfirmationRejectsPasswordStateThatStillHasPendingCode()
    {
        // TDLib 1.8.67 returns refreshed passwordState for CODE_INVALID or EMAIL_HASH_EXPIRED.
        var transport = new FakeTransport();
        transport.Respond("getPasswordState", PendingEmail);
        transport.Respond("checkRecoveryEmailAddressCode", PendingEmail);
        await using var service = CreateService(transport);
        await StartReadyAsync(service, transport);

        await Assert.ThrowsAsync<TelegramException>(() => service.ConfirmRecoveryEmailAsync("012345"));
        Assert.Equal("checkRecoveryEmailAddressCode", transport.RequestTypes[^1]);
    }

    [Fact]
    public async Task GetAccountUsesCurrentUserOnlyWhenAuthorizedAndOmitsPhoneFromStringRepresentation()
    {
        var transport = new FakeTransport();
        transport.Respond("getMe", """
            {"@type":"user","id":900719925474099,"first_name":"Test","last_name":"Owner",
             "usernames":{"active_usernames":["example_owner"],"editable_username":"disabled_example"},
             "phone_number":"12025550101"}
            """);
        transport.Respond("getMe", """
            {"@type":"user","id":900719925474099,"first_name":"Test","usernames":null}
            """);
        await using var service = CreateService(transport);
        await service.StartAsync(42, "test-hash");
        await Assert.ThrowsAsync<TelegramException>(() => service.GetAccountAsync());
        Assert.DoesNotContain("getMe", transport.RequestTypes);

        transport.Authorize("authorizationStateReady");
        var account = await service.GetAccountAsync();
        Assert.Equal(new TelegramAccount(900719925474099, "Test", "Owner", "example_owner", "12025550101"), account);
        Assert.DoesNotContain(account.PhoneNumber, account.ToString());
        Assert.Null((await service.GetAccountAsync()).Username);
    }

    [Fact]
    public async Task TerminationExcludesCurrentSessionAndPreservesInt64Identifier()
    {
        var transport = new FakeTransport();
        for (var request = 0; request < 4; request++)
            transport.Respond("getActiveSessions", ActiveSessions);
        await using var service = CreateService(transport);
        await StartReadyAsync(service, transport);

        var sessions = await service.GetSessionsAsync();
        Assert.True(sessions[0].IsCurrent);
        await Assert.ThrowsAsync<TelegramException>(() => service.TerminateSessionAsync(sessions[0].Id));
        await Assert.ThrowsAsync<TelegramException>(() => service.TerminateSessionAsync(77));
        Assert.DoesNotContain("terminateSession", transport.RequestTypes);
        await service.TerminateSessionAsync(sessions[1].Id);
        Assert.Equal("9007199254740999", transport.Requests[^1].GetProperty("session_id").GetString());
        await service.TerminateOtherSessionsAsync();
        Assert.Equal("terminateAllOtherSessions", transport.RequestTypes[^1]);
    }

    [Fact]
    public async Task LogOutWaitsForClosedUpdateAfterRequestAcknowledgement()
    {
        var transport = new FakeTransport();
        await using var service = CreateService(transport);
        await service.StartAsync(42, "test-hash");
        await Assert.ThrowsAsync<TelegramException>(() => service.LogOutAsync());
        Assert.DoesNotContain("logOut", transport.RequestTypes);
        transport.Authorize("authorizationStateReady");

        var logout = service.LogOutAsync();
        Assert.Equal("logOut", transport.RequestTypes[^1]);
        Assert.False(logout.IsCompleted);
        transport.Authorize("authorizationStateLoggingOut");
        Assert.False(logout.IsCompleted);
        transport.Authorize("authorizationStateClosed");
        await logout.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AuthorizationStep.Closed, service.Status.Step);
    }

    [Fact]
    public async Task CanceledSentLogOutReportsUnconfirmedOutcomeAndPreventsRepeat()
    {
        var transport = new FakeTransport();
        await using var service = CreateService(transport);
        await StartReadyAsync(service, transport);
        using var cancellation = new CancellationTokenSource();
        var logout = service.LogOutAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAsync<TelegramLogoutUnconfirmedException>(() => logout);
        await Assert.ThrowsAsync<TelegramLogoutUnconfirmedException>(() => service.LogOutAsync());
        Assert.Single(transport.RequestTypes, type => type == "logOut");
    }

    [Fact]
    public async Task DemoListsAndMutatesOnlyFictionalState()
    {
        await using var demo = new DemoTelegramService();
        await demo.StartAsync(0, "");
        Assert.True(demo.IsDemo);
        Assert.Equal(AuthorizationStep.Ready, demo.Status.Step);
        Assert.Equal("12025550110", (await demo.GetAccountAsync()).PhoneNumber);
        await demo.TerminateOtherSessionsAsync();
        Assert.True(Assert.Single(await demo.GetSessionsAsync()).IsCurrent);
        await demo.SetPasswordAsync("", "mock", "hint", "mock@example.test");
        Assert.True((await demo.ConfirmRecoveryEmailAsync("012345")).HasRecoveryEmail);
    }

    private static TelegramService CreateService(FakeTransport transport) =>
        new(() => transport, _ => ValueTask.FromResult(new TelegramSessionStorage(TestDatabase, new byte[32])));

    private static async Task StartReadyAsync(TelegramService service, FakeTransport transport)
    {
        await service.StartAsync(42, "test-hash");
        transport.Authorize("authorizationStateReady");
    }

    private sealed class FakeTransport : ITdJsonTransport
    {
        private readonly Dictionary<string, Queue<string>> _responses = new();
        public List<JsonElement> Requests { get; } = [];
        public string[] RequestTypes => Requests.Select(request => request.GetProperty("@type").GetString()!).ToArray();

        public event Action<JsonElement>? Updated;
        public event Action? Faulted { add { } remove { } }

        public void Respond(string requestType, string response)
        {
            if (!_responses.TryGetValue(requestType, out var queue))
            {
                queue = new Queue<string>();
                _responses.Add(requestType, queue);
            }

            queue.Enqueue(response);
        }

        public void Authorize(string type, string? extra = null)
        {
            var fields = extra is null ? "" : $",{extra}";
            Updated?.Invoke(Parse($$"""{"@type":"updateAuthorizationState","authorization_state":{"@type":"{{type}}"{{fields}}} }"""));
        }

        public Task<JsonElement> SendAsync(Dictionary<string, object?> request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(JsonSerializer.SerializeToElement(request, typeof(Dictionary<string, object?>),
                TdJsonSerializerContext.Default));
            var type = (string)request["@type"]!;
            if (type == "getAuthorizationState")
                return Task.FromResult(Parse("""{"@type":"authorizationStateWaitTdlibParameters"}"""));

            var response = _responses.TryGetValue(type, out var queue) ? queue.Dequeue() : """{"@type":"ok"}""";
            return Task.FromResult(Parse(response));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static JsonElement Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}
