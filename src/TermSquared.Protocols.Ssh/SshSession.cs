using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using TermSquared.Core;
using TermSquared.Security;

namespace TermSquared.Protocols.Ssh;

public sealed record SshConnectionOptions(
    string Host,
    int Port,
    string Username,
    TimeSpan Timeout,
    TimeSpan KeepAliveInterval);

public sealed record SshExecResult(int ExitStatus, string StandardOutput, string StandardError);

public delegate Task<HostKeyDecision> HostKeyDecisionCallback(HostKeyCheck check, CancellationToken cancellationToken);

public sealed class SshSession : IAsyncDisposable
{
    private const int MaximumDirectoryEntries = 1000;
    private const int MaximumDirectoryTextCharacters = 262_144;
    private readonly SshClient _ssh;
    private readonly SftpClient _sftp;
    private readonly IKnownHostStore _knownHosts;
    private readonly HostKeyDecisionCallback _hostKeyDecision;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _timeout;
    private bool _disposed;

    private SshSession(
        SshClient ssh,
        SftpClient sftp,
        IKnownHostStore knownHosts,
        HostKeyDecisionCallback hostKeyDecision,
        TimeSpan timeout)
    {
        _ssh = ssh;
        _sftp = sftp;
        _knownHosts = knownHosts;
        _hostKeyDecision = hostKeyDecision;
        _timeout = timeout;
        _ssh.HostKeyReceived += OnHostKeyReceived;
        _sftp.HostKeyReceived += OnHostKeyReceived;
    }

    public bool IsConnected => _ssh.IsConnected && _sftp.IsConnected;

    public static SshSession CreatePassword(
        SshConnectionOptions options,
        string password,
        IKnownHostStore knownHosts,
        HostKeyDecisionCallback? hostKeyDecision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        hostKeyDecision ??= static (check, _) => Task.FromResult(
            check.Status == HostKeyStatus.Trusted ? HostKeyDecision.TrustOnce : HostKeyDecision.Reject);
        var sshInfo = CreateConnectionInfo(options, password);
        var sftpInfo = CreateConnectionInfo(options, password);
        var ssh = new SshClient(sshInfo) { KeepAliveInterval = options.KeepAliveInterval };
        var sftp = new SftpClient(sftpInfo) { KeepAliveInterval = options.KeepAliveInterval };
        return new SshSession(ssh, sftp, knownHosts, hostKeyDecision, options.Timeout);
    }

    public static async Task<SshSession> CreateFromProfileAsync(
        ConnectionProfile profile,
        ISecretStore secretStore,
        IKnownHostStore knownHosts,
        HostKeyDecisionCallback? hostKeyDecision = null,
        TimeSpan? timeout = null,
        TimeSpan? keepAliveInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (profile.Protocol != ConnectionProtocol.Ssh || profile.Authentication != AuthenticationKind.Password ||
            string.IsNullOrWhiteSpace(profile.Username) || profile.Secret is null)
            throw new ArgumentException("The connection profile is not a complete SSH password profile.", nameof(profile));
        var password = await secretStore.RetrieveAsync(profile.Secret, cancellationToken).ConfigureAwait(false);
        if (password is null) throw new InvalidOperationException("The referenced SSH credential is unavailable.");
        return CreatePassword(
            new SshConnectionOptions(
                profile.Host,
                profile.Port,
                profile.Username,
                timeout ?? TimeSpan.FromSeconds(30),
                keepAliveInterval ?? TimeSpan.FromSeconds(15)),
            password,
            knownHosts,
            hostKeyDecision);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        try
        {
            await _ssh.ConnectAsync(linked.Token).ConfigureAwait(false);
            await _sftp.ConnectAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            if (_sftp.IsConnected) _sftp.Disconnect();
            if (_ssh.IsConnected) _ssh.Disconnect();
            throw;
        }
    }

    public async Task<SshExecResult> ExecAsync(string command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using var linked = CreateTimeoutToken(cancellationToken);
        using var sshCommand = _ssh.CreateCommand(command, Encoding.UTF8);
        await sshCommand.ExecuteAsync(linked.Token).ConfigureAwait(false);
        return new SshExecResult(sshCommand.ExitStatus ?? -1, sshCommand.Result, sshCommand.Error);
    }

    public SshShellSession CreateShellSession(uint columns = 80, uint rows = 24, uint width = 0, uint height = 0)
    {
        ThrowIfDisposed();
        var stream = _ssh.CreateShellStream("xterm-256color", columns, rows, width, height, 64 * 1024);
        return new SshShellSession(stream, _lifetime.Token);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        var entries = new List<RemoteEntry>();
        var textCharacters = 0;
        await foreach (var item in _sftp.ListDirectoryAsync(path, linked.Token).ConfigureAwait(false))
        {
            if (item.Name is "." or "..") continue;
            textCharacters = checked(textCharacters + item.Name.Length + item.FullName.Length);
            if (entries.Count >= MaximumDirectoryEntries || textCharacters > MaximumDirectoryTextCharacters)
                throw new InvalidDataException("The remote directory exceeds the configured listing limit.");
            entries.Add(Map(item));
        }
        return entries;
    }

    public async Task<string> ResolveReadPathAsync(
        string path,
        IReadOnlyList<string> allowedRoots,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!PathRootPolicy.TryCanonicalizePosixPath(path, out var canonicalPath))
            throw new UnauthorizedAccessException("The remote path is invalid.");
        using var linked = CreateTimeoutToken(cancellationToken);

        foreach (var root in allowedRoots)
        {
            if (!PathRootPolicy.ContainsRemotePath(root, canonicalPath)) continue;
            var rootPath = PathRootPolicy.TryCanonicalizePosixPath(root, out var canonicalRoot)
                ? canonicalRoot
                : throw new UnauthorizedAccessException("The published root is invalid.");
            await DemandNoSymbolicLinksAsync(rootPath, canonicalPath, linked.Token).ConfigureAwait(false);
            return canonicalPath;
        }

        throw new UnauthorizedAccessException("The remote path is outside the published roots.");
    }

    public async Task<RemoteEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        try
        {
            var attributes = await _sftp.GetAttributesAsync(path, linked.Token).ConfigureAwait(false);
            return new RemoteEntry(Path.GetFileName(path), path, GetKind(attributes), attributes.Size, attributes.LastWriteTimeUtc);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task<byte[]> ReadAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var linked = CreateTimeoutToken(cancellationToken);
        await using var input = await _sftp.OpenAsync(path, FileMode.Open, FileAccess.Read, linked.Token).ConfigureAwait(false);
        if (input.Length > maximumBytes) throw new InvalidDataException("The remote file exceeds the configured read limit.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, linked.Token).ConfigureAwait(false);
        return bytes;
    }

    public async Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        await _sftp.DownloadFileAsync(remotePath, destination, linked.Token).ConfigureAwait(false);
    }

    public async Task UploadAsync(Stream source, string remotePath, bool overwrite, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        if (!overwrite && await _sftp.ExistsAsync(remotePath, linked.Token).ConfigureAwait(false))
            throw new IOException("The remote destination already exists.");
        await _sftp.UploadFileAsync(source, remotePath, linked.Token).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        await _sftp.CreateDirectoryAsync(path, linked.Token).ConfigureAwait(false);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        await _sftp.RenameFileAsync(oldPath, newPath, linked.Token).ConfigureAwait(false);
    }

    public async Task RemoveFileAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        await _sftp.DeleteFileAsync(path, linked.Token).ConfigureAwait(false);
    }

    public async Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CreateTimeoutToken(cancellationToken);
        await _sftp.DeleteDirectoryAsync(path, linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _ssh.HostKeyReceived -= OnHostKeyReceived;
        _sftp.HostKeyReceived -= OnHostKeyReceived;
        if (_sftp.IsConnected) _sftp.Disconnect();
        if (_ssh.IsConnected) _ssh.Disconnect();
        _sftp.Dispose();
        _ssh.Dispose();
        _lifetime.Dispose();
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs eventArgs)
    {
        try
        {
            var info = sender switch
            {
                SshClient => _ssh.ConnectionInfo,
                SftpClient => _sftp.ConnectionInfo,
                _ => throw new InvalidOperationException("Unexpected SSH.NET sender.")
            };
            var check = _knownHosts.CheckAsync(info.Host, info.Port, eventArgs.HostKeyName, eventArgs.HostKey, _lifetime.Token)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            var decision = _hostKeyDecision(check, _lifetime.Token).ConfigureAwait(false).GetAwaiter().GetResult();
            if (decision == HostKeyDecision.TrustAndStore)
                _knownHosts.TrustAsync(check.Presented, _lifetime.Token).ConfigureAwait(false).GetAwaiter().GetResult();
            eventArgs.CanTrust = decision is HostKeyDecision.TrustOnce or HostKeyDecision.TrustAndStore;
        }
        catch
        {
            eventArgs.CanTrust = false;
        }
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(_timeout);
        return linked;
    }

    private static ConnectionInfo CreateConnectionInfo(SshConnectionOptions options, string password) =>
        new(options.Host, options.Port, options.Username, new PasswordAuthenticationMethod(options.Username, password))
        {
            Timeout = options.Timeout
        };

    private static RemoteEntry Map(Renci.SshNet.Sftp.ISftpFile item) =>
        new(item.Name, item.FullName, GetKind(item.Attributes), item.Attributes.Size, item.Attributes.LastWriteTimeUtc,
            item.IsSymbolicLink ? item.FullName : null);

    private static RemoteEntryKind GetKind(Renci.SshNet.Sftp.SftpFileAttributes attributes) => attributes switch
    {
        { IsDirectory: true } => RemoteEntryKind.Directory,
        { IsSymbolicLink: true } => RemoteEntryKind.SymbolicLink,
        { IsRegularFile: true } => RemoteEntryKind.File,
        _ => RemoteEntryKind.Other
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private async Task DemandNoSymbolicLinksAsync(string root, string path, CancellationToken cancellationToken)
    {
        var current = "/";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current == "/" ? "/" + segment : current + "/" + segment;
            if (!PathRootPolicy.ContainsRemotePath(root, current) && !PathRootPolicy.ContainsRemotePath(current, root))
                continue;
            var item = await _sftp.GetAsync(current, cancellationToken).ConfigureAwait(false);
            if (item.IsSymbolicLink)
                throw new UnauthorizedAccessException("Symbolic links are not allowed in published MCP paths.");
        }
    }
}

public sealed class SshShellSession : IAsyncDisposable
{
    private readonly ShellStream _stream;
    private readonly CancellationTokenSource _lifetime;

    internal SshShellSession(ShellStream stream, CancellationToken sessionCancellation)
    {
        _stream = stream;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
    }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return ReadCoreAsync(buffer, linked);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _stream.WriteAsync(input, linked.Token).ConfigureAwait(false);
        await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
    }

    public void Resize(uint columns, uint rows, uint width = 0, uint height = 0) =>
        _stream.ChangeWindowSize(columns, rows, width, height);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _stream.Dispose();
        _lifetime.Dispose();
    }

    private async Task<int> ReadCoreAsync(Memory<byte> buffer, CancellationTokenSource linked)
    {
        using (linked)
            return await _stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
    }
}
