using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using TermSquared.Core;

namespace TermSquared.Mcp;

public sealed record NamedPipeBrokerOptions
{
    public const string DefaultPipeName = "TermSquared.DesktopBroker";

    public string PipeName { get; init; } = DefaultPipeName;
    public int MaximumMessageBytes { get; init; } = 1_048_576;
    public int MaximumConcurrentConnections { get; init; } = 8;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PipeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumMessageBytes, 256);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumMessageBytes, 16 * 1_048_576);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumConcurrentConnections, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumConcurrentConnections, 64);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(OperationTimeout, TimeSpan.Zero);
    }
}

public sealed class BrokerUnavailableException : InvalidOperationException
{
    public BrokerUnavailableException()
        : base("The TermSquared desktop broker is unavailable.")
    {
    }
}

public sealed class BrokerMessageTooLargeException : InvalidOperationException
{
    public BrokerMessageTooLargeException()
        : base("The broker message exceeds the configured size limit.")
    {
    }
}

public sealed class NamedPipeRemoteOperations : IRemoteOperations
{
    private readonly NamedPipeBrokerOptions _options;

    public NamedPipeRemoteOperations(NamedPipeBrokerOptions options)
    {
        options.Validate();
        _options = options;
    }

    public Task<IReadOnlyList<ConnectionProfile>> ListConnectionsAsync(CancellationToken cancellationToken) =>
        InvokeAsync<object?, IReadOnlyList<ConnectionProfile>>("connections.list", null, cancellationToken);

    public Task<SessionState> GetStatusAsync(string connectionId, CancellationToken cancellationToken) =>
        InvokeAsync<ConnectionParameters, SessionState>("connections.status", new(connectionId), cancellationToken);

    public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string connectionId, string path, CancellationToken cancellationToken) =>
        InvokeAsync<PathParameters, IReadOnlyList<RemoteEntry>>("sftp.list", new(connectionId, path), cancellationToken);

    public Task<RemoteEntry?> StatAsync(string connectionId, string path, CancellationToken cancellationToken) =>
        InvokeAsync<PathParameters, RemoteEntry?>("sftp.stat", new(connectionId, path), cancellationToken);

    public Task<string> ResolveReadPathAsync(
        string connectionId,
        string path,
        IReadOnlyList<string> allowedRoots,
        CancellationToken cancellationToken) =>
        InvokeAsync<ResolvePathParameters, string>("sftp.resolve", new(connectionId, path, allowedRoots), cancellationToken);

    public Task<byte[]> ReadFileAsync(string connectionId, string path, int maximumBytes, CancellationToken cancellationToken) =>
        InvokeAsync<ReadFileParameters, byte[]>("sftp.read", new(connectionId, path, maximumBytes), cancellationToken);

    public Task<string> ExecuteSshAsync(string connectionId, string command, CancellationToken cancellationToken) =>
        Task.FromException<string>(new UnauthorizedAccessException("High-risk broker operations are disabled."));

    public Task ExecuteWriteAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        Task.FromException(new UnauthorizedAccessException("High-risk broker operations are disabled."));

    private async Task<TResult> InvokeAsync<TParameters, TResult>(string method, TParameters parameters, CancellationToken cancellationToken)
    {
        var request = new BrokerRequest(
            Guid.NewGuid().ToString("N"),
            method,
            JsonSerializer.SerializeToElement(parameters, BrokerJson.Options));

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCancellation.CancelAfter(_options.ConnectTimeout);
            await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);

            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationCancellation.CancelAfter(_options.OperationTimeout);
            await BrokerFraming.WriteAsync(pipe, request, _options.MaximumMessageBytes, operationCancellation.Token).ConfigureAwait(false);
            var response = await BrokerFraming.ReadAsync<BrokerResponse>(pipe, _options.MaximumMessageBytes, operationCancellation.Token).ConfigureAwait(false);
            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                throw new BrokerUnavailableException();
            if (response.Error is not null)
                throw response.Error.Code == "broker_unavailable"
                    ? new BrokerUnavailableException()
                    : new InvalidOperationException(response.Error.Message);
            if (response.Result is null)
                return default!;
            return response.Result.Value.Deserialize<TResult>(BrokerJson.Options)!;
        }
        catch (BrokerMessageTooLargeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BrokerUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            throw new BrokerUnavailableException();
        }
    }

    private sealed record ConnectionParameters(string ConnectionId);
    private sealed record PathParameters(string ConnectionId, string Path);
    private sealed record ReadFileParameters(string ConnectionId, string Path, int MaximumBytes);
    private sealed record ResolvePathParameters(string ConnectionId, string Path, IReadOnlyList<string> AllowedRoots);
}

public sealed class NamedPipeBrokerServer : IAsyncDisposable
{
    private readonly IRemoteOperations _operations;
    private readonly NamedPipeBrokerOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _connections;
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly object _startLock = new();
    private Task? _serverTask;

    public NamedPipeBrokerServer(IRemoteOperations operations, NamedPipeBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(operations);
        options.Validate();
        _operations = operations;
        _options = options;
        _connections = new SemaphoreSlim(options.MaximumConcurrentConnections, options.MaximumConcurrentConnections);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_startLock)
        {
            if (_serverTask is not null)
                throw new InvalidOperationException("The broker server has already been started.");
            _serverTask = RunAsync(_stopping.Token);
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        await Task.WhenAll(_handlers.Values).ConfigureAwait(false);

        _connections.Dispose();
        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _options.PipeName,
                    PipeDirection.InOut,
                    _options.MaximumConcurrentConnections,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var handler = HandleConnectionAsync(pipe, cancellationToken);
                _handlers.TryAdd(handler.Id, handler);
                _ = handler.ContinueWith(
                    static (completed, state) => ((ConcurrentDictionary<int, Task>)state!).TryRemove(completed.Id, out _),
                    _handlers,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                pipe = null;
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    _connections.Release();
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverCancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
                requestCancellation.CancelAfter(_options.OperationTimeout);
                var request = await BrokerFraming.ReadAsync<BrokerRequest>(
                    pipe,
                    _options.MaximumMessageBytes,
                    requestCancellation.Token).ConfigureAwait(false);
                BrokerResponse response;
                try
                {
                    var result = await DispatchAsync(request, requestCancellation.Token).ConfigureAwait(false);
                    response = new BrokerResponse(request.Id, result, null);
                }
                catch (OperationCanceledException) when (!serverCancellationToken.IsCancellationRequested)
                {
                    response = BrokerResponse.Failure(request.Id, "timeout", "The broker operation timed out.");
                }
                catch (BrokerRequestException exception)
                {
                    response = BrokerResponse.Failure(request.Id, "invalid_request", exception.Message);
                }
                catch (Exception)
                {
                    response = BrokerResponse.Failure(request.Id, "operation_failed", "The remote operation failed.");
                }

                await BrokerFraming.WriteAsync(pipe, response, _options.MaximumMessageBytes, serverCancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or BrokerMessageTooLargeException or OperationCanceledException)
            {
                // Close malformed, oversized, or stalled connections without exposing transport details.
            }
            finally
            {
                _connections.Release();
            }
        }
    }

    private async Task<JsonElement?> DispatchAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            "connections.list" => await ListConnectionsAsync(cancellationToken).ConfigureAwait(false),
            "connections.status" => await GetStatusAsync(request, cancellationToken).ConfigureAwait(false),
            "sftp.list" => await ListDirectoryAsync(request, cancellationToken).ConfigureAwait(false),
            "sftp.stat" => await StatAsync(request, cancellationToken).ConfigureAwait(false),
            "sftp.resolve" => await ResolvePathAsync(request, cancellationToken).ConfigureAwait(false),
            "sftp.read" => await ReadFileAsync(request, cancellationToken).ConfigureAwait(false),
            "ssh.exec" or "remote.write" => throw new BrokerRequestException("High-risk broker operations are disabled."),
            _ => throw new BrokerRequestException("The broker method is not supported.")
        };
    }

    private async Task<JsonElement?> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var profiles = await _operations.ListConnectionsAsync(cancellationToken).ConfigureAwait(false);
        var sanitized = profiles
            .Where(static profile => profile.PublishedScope != PublishedMcpScope.None && profile.PublishedRoots is { Count: > 0 })
            .Select(static profile => profile with
        {
            Host = "",
            Port = 0,
            Username = null,
            Authentication = AuthenticationKind.None,
            Secret = null
        });
        return JsonSerializer.SerializeToElement(sanitized, BrokerJson.Options);
    }

    private async Task<JsonElement?> ListDirectoryAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var parameters = Parameters<PathParameters>(request);
        var path = await DemandPublishedPathAsync(parameters.ConnectionId, parameters.Path, cancellationToken).ConfigureAwait(false);
        var result = await _operations.ListDirectoryAsync(parameters.ConnectionId, path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, BrokerJson.Options);
    }

    private async Task<JsonElement?> StatAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var parameters = Parameters<PathParameters>(request);
        var path = await DemandPublishedPathAsync(parameters.ConnectionId, parameters.Path, cancellationToken).ConfigureAwait(false);
        var result = await _operations.StatAsync(parameters.ConnectionId, path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, BrokerJson.Options);
    }

    private async Task<JsonElement?> ReadFileAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var parameters = Parameters<ReadFileParameters>(request);
        var path = await DemandPublishedPathAsync(parameters.ConnectionId, parameters.Path, cancellationToken).ConfigureAwait(false);
        var result = await _operations.ReadFileAsync(parameters.ConnectionId, path, parameters.MaximumBytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, BrokerJson.Options);
    }

    private async Task<JsonElement?> GetStatusAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var parameters = Parameters<ConnectionParameters>(request);
        _ = await GetPublishedProfileAsync(parameters.ConnectionId, cancellationToken).ConfigureAwait(false);
        var result = await _operations.GetStatusAsync(parameters.ConnectionId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, BrokerJson.Options);
    }

    private async Task<JsonElement?> ResolvePathAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        var parameters = Parameters<ResolvePathParameters>(request);
        var path = await DemandPublishedPathAsync(parameters.ConnectionId, parameters.Path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(path, BrokerJson.Options);
    }

    private async Task<string> DemandPublishedPathAsync(string connectionId, string path, CancellationToken cancellationToken)
    {
        var profile = await GetPublishedProfileAsync(connectionId, cancellationToken).ConfigureAwait(false);
        return await _operations.ResolveReadPathAsync(
            connectionId,
            path,
            profile.PublishedRoots ?? [],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectionProfile> GetPublishedProfileAsync(string connectionId, CancellationToken cancellationToken)
    {
        var profiles = await _operations.ListConnectionsAsync(cancellationToken).ConfigureAwait(false);
        return profiles.FirstOrDefault(profile =>
                   profile.PublishedScope != PublishedMcpScope.None &&
                   profile.PublishedRoots is { Count: > 0 } &&
                   string.Equals(profile.Id.ToString("D"), connectionId, StringComparison.OrdinalIgnoreCase))
               ?? throw new BrokerRequestException("The connection is not published to MCP.");
    }

    private static T Parameters<T>(BrokerRequest request) =>
        request.Parameters.Deserialize<T>(BrokerJson.Options)
        ?? throw new BrokerRequestException("The broker request parameters are invalid.");

    private sealed record ConnectionParameters(string ConnectionId);
    private sealed record PathParameters(string ConnectionId, string Path);
    private sealed record ReadFileParameters(string ConnectionId, string Path, int MaximumBytes);
    private sealed record ResolvePathParameters(string ConnectionId, string Path, IReadOnlyList<string> AllowedRoots);
}

internal static class BrokerFraming
{
    public static async Task WriteAsync<T>(Stream stream, T message, int maximumMessageBytes, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, BrokerJson.Options);
        if (payload.Length > maximumMessageBytes)
            throw new BrokerMessageTooLargeException();

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, int maximumMessageBytes, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > maximumMessageBytes)
            throw new BrokerMessageTooLargeException();

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, BrokerJson.Options)
            ?? throw new JsonException("The broker message was empty.");
    }
}

internal static class BrokerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

internal sealed record BrokerRequest(string Id, string Method, JsonElement Parameters);
internal sealed record BrokerResponse(string Id, JsonElement? Result, BrokerRpcError? Error)
{
    public static BrokerResponse Failure(string id, string code, string message) => new(id, null, new(code, message));
}

internal sealed record BrokerRpcError(string Code, string Message);
internal sealed class BrokerRequestException(string message) : InvalidOperationException(message);
