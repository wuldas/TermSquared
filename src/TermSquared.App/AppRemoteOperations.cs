using System.Collections.Concurrent;
using TermSquared.Core;
using TermSquared.Protocols.Ssh;
using TermSquared.Security;

namespace TermSquared.App;

internal sealed class AppRemoteOperations(
    IReadOnlyList<ConnectionProfile> profiles,
    ISecretStore secrets,
    IKnownHostStore knownHosts) : IRemoteOperations, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, ConnectionProfile> _profiles = profiles.ToDictionary(
        static profile => profile.Id.ToString("D"),
        StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SessionState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<SshSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ConnectionProfile>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.Values.ToArray());
    }

    public Task<SessionState> GetStatusAsync(string connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_states.GetValueOrDefault(connectionId, SessionState.Disconnected));
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string connectionId, string path, CancellationToken cancellationToken) =>
        await (await GetSessionAsync(connectionId, cancellationToken).ConfigureAwait(false))
            .ListAsync(path, cancellationToken).ConfigureAwait(false);

    public async Task<RemoteEntry?> StatAsync(string connectionId, string path, CancellationToken cancellationToken) =>
        await (await GetSessionAsync(connectionId, cancellationToken).ConfigureAwait(false))
            .StatAsync(path, cancellationToken).ConfigureAwait(false);

    public async Task<string> ResolveReadPathAsync(
        string connectionId,
        string path,
        IReadOnlyList<string> allowedRoots,
        CancellationToken cancellationToken) =>
        await (await GetSessionAsync(connectionId, cancellationToken).ConfigureAwait(false))
            .ResolveReadPathAsync(path, allowedRoots, cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> ReadFileAsync(string connectionId, string path, int maximumBytes, CancellationToken cancellationToken) =>
        await (await GetSessionAsync(connectionId, cancellationToken).ConfigureAwait(false))
            .ReadAsync(path, maximumBytes, cancellationToken).ConfigureAwait(false);

    public async Task<string> ExecuteSshAsync(string connectionId, string command, CancellationToken cancellationToken)
    {
        var result = await (await GetSessionAsync(connectionId, cancellationToken).ConfigureAwait(false))
            .ExecAsync(command, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    public Task ExecuteWriteAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Remote writes require the desktop approval executor."));

    private Task<SshSession> GetSessionAsync(string connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_profiles.TryGetValue(connectionId, out var profile))
            throw new KeyNotFoundException("The connection is not available.");
        return _sessions.GetOrAdd(
            connectionId,
            _ => new Lazy<Task<SshSession>>(() => ConnectAsync(connectionId, profile), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private async Task<SshSession> ConnectAsync(string connectionId, ConnectionProfile profile)
    {
        _states[connectionId] = SessionState.Connecting;
        SshSession? session = null;
        try
        {
            session = await SshSession.CreateFromProfileAsync(
                profile,
                secrets,
                knownHosts,
                static (check, _) => Task.FromResult(
                    check.Status == HostKeyStatus.Trusted ? HostKeyDecision.TrustOnce : HostKeyDecision.Reject)).ConfigureAwait(false);
            await session.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            _states[connectionId] = SessionState.Connected;
            return session;
        }
        catch
        {
            _states[connectionId] = SessionState.Failed;
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            _sessions.TryRemove(connectionId, out _);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.IsValueCreated) continue;
            try
            {
                await (await session.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _sessions.Clear();
    }
}
