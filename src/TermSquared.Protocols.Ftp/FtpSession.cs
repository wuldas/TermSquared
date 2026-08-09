using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using TermSquared.Core;
using TermSquared.Security;

namespace TermSquared.Protocols.Ftp;

public sealed record FtpConnectionOptions
{
    public FtpConnectionOptions(string host, int port, string username, ConnectionProtocol protocol, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (protocol is not (ConnectionProtocol.Ftp or ConnectionProtocol.Ftps))
            throw new ArgumentOutOfRangeException(nameof(protocol), "Only FTP and explicit FTPS are supported.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Host = host;
        Port = port;
        Username = username;
        Protocol = protocol;
        Timeout = timeout;
    }

    public string Host { get; }
    public int Port { get; }
    public string Username { get; }
    public ConnectionProtocol Protocol { get; }
    public TimeSpan Timeout { get; }

    public static FtpConnectionOptions FromProfile(ConnectionProfile profile, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Protocol is not (ConnectionProtocol.Ftp or ConnectionProtocol.Ftps) ||
            profile.Authentication != AuthenticationKind.Password ||
            string.IsNullOrWhiteSpace(profile.Username) || profile.Secret is null)
            throw new ArgumentException("The connection profile is not a complete FTP password profile.", nameof(profile));
        return new FtpConnectionOptions(profile.Host, profile.Port, profile.Username, profile.Protocol,
            timeout ?? TimeSpan.FromSeconds(30));
    }
}

public sealed record FtpCertificateCheck(
    X509Certificate Certificate,
    X509Chain? Chain,
    SslPolicyErrors PolicyErrors);

public delegate bool FtpCertificateDecisionCallback(FtpCertificateCheck check);

public sealed class FtpSession : IAsyncDisposable
{
    private readonly AsyncFtpClient _client;
    private bool _disposed;

    private FtpSession(AsyncFtpClient client) => _client = client;

    public bool IsConnected => _client.IsConnected;

    public static FtpSession CreatePassword(
        FtpConnectionOptions options,
        string password,
        FtpCertificateDecisionCallback? certificateDecision = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var config = CreateConfig(options);
        var client = new AsyncFtpClient(options.Host, options.Username, password, options.Port, config);
        if (options.Protocol == ConnectionProtocol.Ftps)
        {
            client.ValidateCertificate += (_, eventArgs) =>
            {
                var check = new FtpCertificateCheck(eventArgs.Certificate, eventArgs.Chain, eventArgs.PolicyErrors);
                eventArgs.Accept = certificateDecision?.Invoke(check) ?? DefaultCertificateDecision(check);
            };
        }
        return new FtpSession(client);
    }

    public static FtpConfig CreateConfig(FtpConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var timeout = checked((int)Math.Min(options.Timeout.TotalMilliseconds, int.MaxValue));
        return new FtpConfig
        {
            EncryptionMode = options.Protocol == ConnectionProtocol.Ftps
                ? FtpEncryptionMode.Explicit
                : FtpEncryptionMode.None,
            DataConnectionEncryption = options.Protocol == ConnectionProtocol.Ftps,
            ConnectTimeout = timeout,
            ReadTimeout = timeout,
            DataConnectionConnectTimeout = timeout,
            DataConnectionReadTimeout = timeout
        };
    }

    public static async Task<FtpSession> CreateFromProfileAsync(
        ConnectionProfile profile,
        ISecretStore secretStore,
        FtpCertificateDecisionCallback? certificateDecision = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        var options = FtpConnectionOptions.FromProfile(profile, timeout);
        var password = await secretStore.RetrieveAsync(profile.Secret!, cancellationToken).ConfigureAwait(false);
        if (password is null) throw new InvalidOperationException("The referenced FTP credential is unavailable.");
        return CreatePassword(options, password, certificateDecision);
    }

    public static bool DefaultCertificateDecision(FtpCertificateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.PolicyErrors == SslPolicyErrors.None;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.Connect(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_client.IsConnected) await _client.Disconnect(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var items = await _client.GetListing(path, cancellationToken).ConfigureAwait(false);
        return items.Select(Map).ToArray();
    }

    public async Task<RemoteEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var item = await _client.GetObjectInfo(path, dateModified: true, cancellationToken).ConfigureAwait(false);
            return item is null ? null : Map(item);
        }
        catch (InvalidOperationException)
        {
            var parent = GetParentPath(path);
            var item = (await _client.GetListing(parent, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => string.Equals(candidate.FullName, path, StringComparison.Ordinal));
            return item is null ? null : Map(item);
        }
    }

    public async Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentNullException.ThrowIfNull(destination);
        if (!await _client.DownloadStream(destination, remotePath, token: cancellationToken).ConfigureAwait(false))
            throw new IOException("The FTP download did not complete.");
    }

    public async Task UploadAsync(Stream source, string remotePath, bool overwrite, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var result = await _client.UploadStream(source, remotePath,
            overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip,
            createRemoteDir: false, token: cancellationToken).ConfigureAwait(false);
        if (result == FtpStatus.Failed) throw new IOException("The FTP upload did not complete.");
        if (!overwrite && result == FtpStatus.Skipped) throw new IOException("The remote destination already exists.");
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _client.CreateDirectory(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        await _client.Rename(oldPath, newPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (isDirectory)
            await _client.DeleteDirectory(path, cancellationToken).ConfigureAwait(false);
        else
            await _client.DeleteFile(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_client.IsConnected) await _client.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _client.Dispose();
        }
    }

    private static RemoteEntry Map(FtpListItem item) => new(
        item.Name,
        item.FullName,
        item.Type switch
        {
            FtpObjectType.File => RemoteEntryKind.File,
            FtpObjectType.Directory => RemoteEntryKind.Directory,
            FtpObjectType.Link => RemoteEntryKind.SymbolicLink,
            _ => RemoteEntryKind.Other
        },
        item.Size,
        item.Modified == DateTime.MinValue ? DateTimeOffset.MinValue : new DateTimeOffset(item.Modified),
        item.LinkTarget);

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
