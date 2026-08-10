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
    private IReadOnlyDictionary<string, ConnectionProfile> _profiles = profiles.ToDictionary(
        static profile => profile.Id.ToString("D"),
        StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SessionState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<SshSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private long _catalogRevision;

    public Task<IReadOnlyList<ConnectionProfile>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = Volatile.Read(ref _profiles);
        return Task.FromResult<IReadOnlyList<ConnectionProfile>>(profiles.Values.ToArray());
    }

    public async Task ReplaceProfilesAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken)
    {
        var replacement = profiles.ToDictionary(
            static profile => profile.Id.ToString("D"),
            StringComparer.OrdinalIgnoreCase);
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Lazy<Task<SshSession>>[] sessions;
        try
        {
            Volatile.Write(ref _profiles, replacement);
            Interlocked.Increment(ref _catalogRevision);
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _states.Clear();
        }
        finally
        {
            _catalogGate.Release();
        }
        await DisposeSessionsAsync(sessions).ConfigureAwait(false);
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

    private async Task<SshSession> GetSessionAsync(string connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Lazy<Task<SshSession>> session;
        long revision;
        try
        {
            var profiles = Volatile.Read(ref _profiles);
            if (!profiles.TryGetValue(connectionId, out var profile))
                throw new KeyNotFoundException("The connection is not available.");
            revision = _catalogRevision;
            session = _sessions.GetOrAdd(
                connectionId,
                _ => new Lazy<Task<SshSession>>(
                    () => ConnectAsync(connectionId, profile, revision),
                    LazyThreadSafetyMode.ExecutionAndPublication));
        }
        finally
        {
            _catalogGate.Release();
        }
        try
        {
            var connected = await session.Value.ConfigureAwait(false);
            return revision == Volatile.Read(ref _catalogRevision)
                ? connected
                : throw new InvalidOperationException("The connection profile changed while the session was opening.");
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<SshSession>>>>)_sessions)
                .Remove(new KeyValuePair<string, Lazy<Task<SshSession>>>(connectionId, session));
            throw;
        }
    }

    private async Task<SshSession> ConnectAsync(string connectionId, ConnectionProfile profile, long revision)
    {
        SetState(connectionId, SessionState.Connecting, revision);
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
            SetState(connectionId, SessionState.Connected, revision);
            return session;
        }
        catch
        {
            SetState(connectionId, SessionState.Failed, revision);
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void SetState(string connectionId, SessionState state, long revision)
    {
        if (revision == Volatile.Read(ref _catalogRevision)) _states[connectionId] = state;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSessionsAsync(_sessions.Values.ToArray()).ConfigureAwait(false);
        _sessions.Clear();
        _catalogGate.Dispose();
    }

    private static async Task DisposeSessionsAsync(IEnumerable<Lazy<Task<SshSession>>> sessions)
    {
        foreach (var session in sessions)
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
    }
}
