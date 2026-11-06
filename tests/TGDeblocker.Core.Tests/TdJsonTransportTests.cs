using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TGDeblocker.Core;

namespace TGDeblocker.Core.Tests;

public sealed class TdJsonTransportTests
{
    [Fact]
    public async Task ConcurrentRequestsReceiveTheirOwnResponsesAndFloodWaitIsSurfacedWithoutRetry()
    {
        var native = new FakeNativeClient();
        await using var transport = new TdJsonTransport(native, TimeSpan.FromSeconds(2));
        var first = transport.SendAsync(Request("first"), CancellationToken.None);
        var secondRequest = Request("second");
        secondRequest["code"] = new Dictionary<string, object?> { ["@type"] = "emailAddressAuthenticationCode", ["code"] = "000000" };
        var second = transport.SendAsync(secondRequest, CancellationToken.None);

        Assert.Equal(2, native.Sent.Count);
        var firstId = Extra(native.Sent[0]);
        var secondId = Extra(native.Sent[1]);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("000000", native.Sent[1].GetProperty("code").GetProperty("code").GetString());
        native.Inject($$"""{"@type":"ok","result":"second","@extra":"{{secondId}}"}""");
        native.Inject($$"""{"@type":"error","code":429,"message":"Too Many Requests: retry after 27 sensitive input","@extra":"{{firstId}}"}""");

        Assert.Equal("second", (await second).GetProperty("result").GetString());
        var error = await Assert.ThrowsAsync<TelegramException>(() => first);
        Assert.Equal(429, error.Code);
        Assert.Equal(TimeSpan.FromSeconds(27), error.RetryAfter);
        Assert.DoesNotContain("sensitive input", error.ToString());
        Assert.Equal(2, native.Sent.Count);
    }

    [Fact]
    public async Task CancellationAndTimeoutDropLateResponsesAndDisposeClosesClient()
    {
        var native = new FakeNativeClient();
        var transport = new TdJsonTransport(native, TimeSpan.FromMilliseconds(400));
        await using (transport)
        {
            using var cancellation = new CancellationTokenSource();
            var canceled = transport.SendAsync(Request("canceled"), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            var canceledId = Extra(native.Sent[0]);

            var timedOut = transport.SendAsync(Request("timeout"), CancellationToken.None);
            await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
            var timedOutId = Extra(native.Sent[1]);

            var working = transport.SendAsync(Request("working"), CancellationToken.None);
            var workingId = Extra(native.Sent[2]);
            native.Inject($$"""{"@type":"ok","result":"canceled","@extra":"{{canceledId}}"}""");
            native.Inject($$"""{"@type":"ok","result":"expired","@extra":"{{timedOutId}}"}""");
            native.Inject($$"""{"@type":"ok","result":"working","@extra":"{{workingId}}"}""");
            Assert.Equal("working", (await working).GetProperty("result").GetString());
        }

        Assert.Equal("close", native.Sent[^1].GetProperty("@type").GetString());
        Assert.True(native.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendAsync(Request("closed"), CancellationToken.None));
    }

    [Fact]
    public async Task WindowsNativePackageSupportsTheOfflineJsonAbi()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        await using var transport = new TdJsonTransport(new NativeTdJsonClient(), TimeSpan.FromSeconds(5));
        var state = await transport.SendAsync(Request("getAuthorizationState"), CancellationToken.None);
        Assert.Equal("authorizationStateWaitTdlibParameters", state.GetProperty("@type").GetString());
    }

    [Fact]
    public async Task PlatformServiceUsesInjectedEncryptedStorageNativeClientAndMetadata()
    {
        var native = new FakeNativeClient();
        native.OnSend = request =>
        {
            var id = Extra(request);
            var responseType = request.GetProperty("@type").GetString() == "getAuthorizationState"
                ? "authorizationStateWaitTdlibParameters" : "ok";
            native.Inject($$"""{"@type":"{{responseType}}","@extra":"{{id}}"}""");
        };

        var database = Path.Combine(Path.GetTempPath(), "tdlib-platform-test");
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        var storage = new TaskCompletionSource<TelegramSessionStorage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new TelegramService(() => native, ct => new(storage.Task),
            new("Mobile Test", "fr-FR", "1.2.3", "Test OS 5"));

        var started = service.StartAsync(42, "test-hash");
        Assert.Empty(native.Sent);
        storage.SetResult(new TelegramSessionStorage(database, key));
        await started;

        var request = native.Sent[1];
        Assert.Equal("setTdlibParameters", request.GetProperty("@type").GetString());
        Assert.Equal(database, request.GetProperty("database_directory").GetString());
        Assert.Equal(Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
            request.GetProperty("database_encryption_key").GetString());
        Assert.Equal("Mobile Test", request.GetProperty("device_model").GetString());
        Assert.Equal("fr-FR", request.GetProperty("system_language_code").GetString());
        Assert.Equal("1.2.3", request.GetProperty("application_version").GetString());
        Assert.Equal("Test OS 5", request.GetProperty("system_version").GetString());
        Assert.All(key, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CanceledPlatformStorageOpenErasesReturnedKeyBeforeNativeClientCreation()
    {
        var database = Path.Combine(Path.GetTempPath(), "tdlib-platform-test");
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        var storage = new TaskCompletionSource<TelegramSessionStorage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeCreated = false;
        await using var service = new TelegramService(() =>
        {
            nativeCreated = true;
            return new FakeNativeClient();
        }, ct => new(storage.Task), new("Mobile Test", "en", "1"));

        using var cancellation = new CancellationTokenSource();
        var started = service.StartAsync(42, "test-hash", cancellation.Token);
        cancellation.Cancel();
        storage.SetResult(new TelegramSessionStorage(database, key));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => started);
        Assert.False(nativeCreated);
        Assert.All(key, value => Assert.Equal(0, value));
    }

    [Fact]
    public void WindowsStorageReopensSameKeyProtectedForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("tgdeblocker-test-");
        try
        {
            var initial = SessionStorage.Open(root.FullName);
            var reopened = SessionStorage.Open(root.FullName);
            try
            {
                Assert.Equal(32, initial.Key.Length);
                Assert.Equal(initial.Key, reopened.Key);
                var blob = File.ReadAllBytes(Path.Combine(root.FullName, "database-key.dpapi"));
                Assert.False(blob.AsSpan().SequenceEqual(initial.Key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(initial.Key);
                CryptographicOperations.ZeroMemory(reopened.Key);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static Dictionary<string, object?> Request(string type) => new() { ["@type"] = type };

    private static string Extra(JsonElement request) => request.GetProperty("@extra").GetString()!;

    private sealed class FakeNativeClient : ITdJsonClient
    {
        private readonly BlockingCollection<string> _received = new();
        public List<JsonElement> Sent { get; } = [];
        public bool Disposed { get; private set; }
        public Action<JsonElement>? OnSend { get; set; }

        public void Inject(string json) => _received.Add(json);

        public void Send(string json)
        {
            using var document = JsonDocument.Parse(json);
            var request = document.RootElement.Clone();
            Sent.Add(request);
            if (request.GetProperty("@type").GetString() == "close")
                Inject("""{"@type":"updateAuthorizationState","authorization_state":{"@type":"authorizationStateClosed"}}""");
            else
                OnSend?.Invoke(request);
        }

        public string? Receive(double timeoutSeconds) =>
            _received.TryTake(out var item, TimeSpan.FromSeconds(timeoutSeconds)) ? item : null;

        public void Dispose()
        {
            Disposed = true;
            _received.Dispose();
        }
    }
}
