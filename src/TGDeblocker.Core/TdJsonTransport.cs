using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TGDeblocker.Core;

internal interface ITdJsonTransport : IAsyncDisposable
{
    event Action<JsonElement>? Updated;
    event Action? Faulted;

    Task<JsonElement> SendAsync(Dictionary<string, object?> request, CancellationToken ct);
}

/// <summary>
/// A platform adapter for one native TDLib JSON client. Send and Receive can run on different threads.
/// The transport serializes calls to Receive and stops the receiver before disposing the client.
/// </summary>
public interface ITdJsonClient : IDisposable
{
    void Send(string json);
    string? Receive(double timeoutSeconds);
}

[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal sealed partial class TdJsonSerializerContext : JsonSerializerContext;

internal sealed class TdJsonTransport : ITdJsonTransport
{
    private readonly ITdJsonClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiver;
    private Task? _disposeTask;
    private bool _faulted;

    public TdJsonTransport(ITdJsonClient client, TimeSpan? requestTimeout = null)
    {
        _client = client;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(45);
        _receiver = Task.Factory.StartNew(Receive, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public event Action<JsonElement>? Updated;
    public event Action? Faulted;

    public async Task<JsonElement> SendAsync(Dictionary<string, object?> request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var message = new Dictionary<string, object?>(request) { ["@extra"] = id };
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_faulted || _closed.Task.IsCompleted)
                throw Unavailable();

            ct.ThrowIfCancellationRequested();
            if (!_pending.TryAdd(id, response))
                throw new InvalidOperationException("Повторный идентификатор запроса TDLib.");

            try
            {
                _client.Send(JsonSerializer.Serialize(message, typeof(Dictionary<string, object?>), TdJsonSerializerContext.Default));
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }
        }

        try
        {
            return await response.Task.WaitAsync(_requestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Telegram не ответил вовремя. Перед повтором проверьте состояние операции.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void Receive()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var json = _client.Receive(0.25);
                if (json is null)
                    continue;

                using var document = JsonDocument.Parse(json);
                var message = document.RootElement;
                if (message.TryGetProperty("@extra", out var extra))
                {
                    if (extra.ValueKind == JsonValueKind.String && extra.GetString() is { } id &&
                        _pending.TryGetValue(id, out var response))
                    {
                        if (message.GetProperty("@type").GetString() == "error")
                        {
                            response.TrySetException(TelegramException.FromTdLib(
                                message.GetProperty("code").GetInt32(), message.GetProperty("message").GetString() ?? ""));
                        }
                        else
                        {
                            response.TrySetResult(message.Clone());
                        }
                    }

                    continue;
                }

                var closed = message.GetProperty("@type").GetString() == "updateAuthorizationState" &&
                    message.GetProperty("authorization_state").GetProperty("@type").GetString() == "authorizationStateClosed";
                Updated?.Invoke(message.Clone());
                if (closed)
                {
                    _closed.TrySetResult();
                    break;
                }
            }
        }
        catch (Exception)
        {
            lock (_gate)
                _faulted = true;

            _closed.TrySetResult();
            Faulted?.Invoke();
        }
        finally
        {
            foreach (var (id, response) in _pending)
            {
                if (_pending.TryRemove(id, out _))
                    response.TrySetException(Unavailable());
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (!_closed.Task.IsCompleted)
            {
                _client.Send("{\"@type\":\"close\"}");
                await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            // Stop the receiver before destroying an unresponsive client.
        }
        finally
        {
            _stop.Cancel();
            await _receiver.ConfigureAwait(false);
            _client.Dispose();
            _stop.Dispose();
        }
    }

    private static TelegramException Unavailable() => new(0, "Соединение с TDLib закрыто.");
}

internal sealed class NativeTdJsonClient : ITdJsonClient
{
    private const string Library = "tdjson";
    private readonly IntPtr _client;

    public NativeTdJsonClient()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("TDLib в этом приложении поддерживает Windows x64.");

        try
        {
            td_json_client_execute(IntPtr.Zero, "{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");
            _client = td_json_client_create();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new TelegramException(0, "Не удалось загрузить TDLib. Проверьте сборку Windows x64 и наличие Microsoft Visual C++ Redistributable.");
        }

        if (_client == IntPtr.Zero)
            throw new TelegramException(0, "Не удалось создать клиент TDLib.");
    }

    public void Send(string json) => td_json_client_send(_client, json);

    public string? Receive(double timeoutSeconds) =>
        Marshal.PtrToStringUTF8(td_json_client_receive(_client, timeoutSeconds));

    public void Dispose() => td_json_client_destroy(_client);

    // The per-client JSON ABI in the pinned TDLib version allows isolated clients and deterministic cleanup.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr td_json_client_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr td_json_client_execute(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void td_json_client_send(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr td_json_client_receive(IntPtr client, double timeout);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void td_json_client_destroy(IntPtr client);
}
