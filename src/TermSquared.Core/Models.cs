namespace TermSquared.Core;

public enum ConnectionProtocol
{
    Ssh,
    Vnc,
    McpStdio,
    McpHttp,
    Ftp,
    Ftps,
    Rdp
}

public enum AuthenticationKind
{
    None,
    Password,
    PrivateKey,
    Agent
}

[Flags]
public enum ConnectionCapabilities
{
    None = 0,
    Terminal = 1,
    FileBrowser = 2,
    RemoteDesktop = 4,
    PortForwarding = 8,
    McpClient = 16,
    McpServer = 32
}

public enum SessionState
{
    Created,
    Connecting,
    Authenticating,
    Connected,
    Disconnecting,
    Disconnected,
    Failed,
    Disposed
}

public enum PublishedMcpScope
{
    None = 0,
    ReadOnly = 1,
    ApprovedWrites = 2,
    ApprovedCommands = 3,
    FullSession = 4
}

public sealed record SecretReference(string Store, string Id)
{
    public override string ToString() => $"{Store}:{Id}";
}

public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    ConnectionProtocol Protocol,
    string Host,
    int Port,
    string? Username,
    AuthenticationKind Authentication,
    SecretReference? Secret,
    ConnectionCapabilities Capabilities,
    PublishedMcpScope PublishedScope = PublishedMcpScope.None,
    IReadOnlyList<string>? PublishedRoots = null)
{
    public static ConnectionProfile CreateSsh(string name, string host, int port, string username, SecretReference secret) =>
        new(Guid.NewGuid(), name, ConnectionProtocol.Ssh, host, port, username, AuthenticationKind.Password, secret,
            ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser);
}

public enum RemoteEntryKind
{
    File,
    Directory,
    SymbolicLink,
    Other
}

public sealed record RemoteEntry(
    string Name,
    string FullPath,
    RemoteEntryKind Kind,
    long Length,
    DateTimeOffset LastWriteTime,
    string? LinkTarget = null);

public enum RemoteErrorCode
{
    Unknown,
    InvalidRequest,
    NotConnected,
    AuthenticationFailed,
    HostKeyUnknown,
    HostKeyChanged,
    AccessDenied,
    NotFound,
    Conflict,
    Timeout,
    Cancelled,
    TransportFailure,
    ApprovalRequired,
    DuplicateOperation,
    BrokerUnavailable
}

public sealed record RemoteError(RemoteErrorCode Code, string Message, bool Retryable = false, string? Detail = null)
{
    public static RemoteError FromException(Exception exception) => exception switch
    {
        OperationCanceledException => new(RemoteErrorCode.Cancelled, "The operation was cancelled."),
        TimeoutException => new(RemoteErrorCode.Timeout, "The operation timed out.", true),
        _ => new(RemoteErrorCode.TransportFailure, "The remote operation failed.", true)
    };
}

public sealed record OperationResult<T>(T? Value, RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record RemoteOperationRequest(
    string OperationId,
    string ConnectionId,
    string Name,
    IReadOnlyDictionary<string, object?> Arguments,
    bool RequiresApproval);

public interface IRemoteOperations
{
    Task<IReadOnlyList<ConnectionProfile>> ListConnectionsAsync(CancellationToken cancellationToken);
    Task<SessionState> GetStatusAsync(string connectionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string connectionId, string path, CancellationToken cancellationToken);
    Task<RemoteEntry?> StatAsync(string connectionId, string path, CancellationToken cancellationToken);
    Task<string> ResolveReadPathAsync(string connectionId, string path, IReadOnlyList<string> allowedRoots, CancellationToken cancellationToken);
    Task<byte[]> ReadFileAsync(string connectionId, string path, int maximumBytes, CancellationToken cancellationToken);
    Task<string> ExecuteSshAsync(string connectionId, string command, CancellationToken cancellationToken);
    Task ExecuteWriteAsync(RemoteOperationRequest request, CancellationToken cancellationToken);
}
