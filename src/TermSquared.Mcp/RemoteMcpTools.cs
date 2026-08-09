using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using TermSquared.Core;
using TermSquared.Security;

namespace TermSquared.Mcp;

[McpServerToolType]
public sealed class RemoteMcpTools(
    IRemoteOperations operations,
    IPublishedConnectionRegistry publishedConnections,
    IOperationAuthorizer authorizer,
    McpCallerIdentity callerIdentity,
    OperationDeduplicator operationDeduplicator)
{
    [McpServerTool(Name = "connections.list"), Description("Lists configured remote connections without credentials.")]
    public async Task<IReadOnlyList<PublishedConnectionDto>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        var connections = await publishedConnections.ListAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<PublishedConnectionDto>(connections.Count);
        foreach (var connection in connections)
        {
            var status = await operations.GetStatusAsync(connection.ConnectionId, cancellationToken).ConfigureAwait(false);
            results.Add(ToDto(connection, status));
        }
        return results;
    }

    [McpServerTool(Name = "connections.status"), Description("Gets the current state of a connection.")]
    public async Task<PublishedConnectionDto> GetConnectionStatusAsync(string connectionId, CancellationToken cancellationToken)
    {
        var connection = await DemandPublishedAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var status = await operations.GetStatusAsync(connection.ConnectionId, cancellationToken).ConfigureAwait(false);
        return ToDto(connection, status);
    }

    [McpServerTool(Name = "sftp.list"), Description("Lists a remote directory.")]
    public async Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string connectionId, string path, CancellationToken cancellationToken)
    {
        var (connection, canonicalPath) = await DemandReadablePathAsync(connectionId, path, cancellationToken).ConfigureAwait(false);
        return await operations.ListDirectoryAsync(connection.ConnectionId, canonicalPath, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "sftp.stat"), Description("Gets metadata for one remote path.")]
    public async Task<RemoteEntry?> StatAsync(string connectionId, string path, CancellationToken cancellationToken)
    {
        var (connection, canonicalPath) = await DemandReadablePathAsync(connectionId, path, cancellationToken).ConfigureAwait(false);
        return await operations.StatAsync(connection.ConnectionId, canonicalPath, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "sftp.read"), Description("Reads a bounded UTF-8 remote file.")]
    public async Task<string> ReadAsync(string connectionId, string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var (connection, canonicalPath) = await DemandReadablePathAsync(connectionId, path, cancellationToken).ConfigureAwait(false);
        var bytes = await operations.ReadFileAsync(
            connection.ConnectionId,
            canonicalPath,
            Math.Clamp(maximumBytes, 1, 1_048_576),
            cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    [McpServerTool(Name = "ssh.exec"), Description("Executes an SSH command after an explicit one-time approval grant.")]
    public async Task<string> ExecAsync(string operationId, string connectionId, string command, CancellationToken cancellationToken)
    {
        var connection = await DemandHighRiskAccessAsync(connectionId, ConnectionCapabilities.Terminal, cancellationToken).ConfigureAwait(false);
        var argumentsHash = ComputeArgumentsHash("ssh.exec", connection.ConnectionId, [new("command", command)]);
        await DemandApprovalAsync(operationId, "ssh.exec", connection.ConnectionId, argumentsHash, cancellationToken).ConfigureAwait(false);
        return await operations.ExecuteSshAsync(connection.ConnectionId, command, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "remote.write"), Description("Performs an approved remote write operation.")]
    public async Task WriteAsync(string operationId, string connectionId, string action, string path, CancellationToken cancellationToken)
    {
        var connection = await DemandHighRiskAccessAsync(connectionId, ConnectionCapabilities.FileBrowser, cancellationToken).ConfigureAwait(false);
        var canonicalPath = DemandContainedPath(connection, path);
        var argumentsHash = ComputeArgumentsHash(
            "remote.write",
            connection.ConnectionId,
            [new("action", action), new("path", canonicalPath)]);
        await DemandApprovalAsync(operationId, "remote.write", connection.ConnectionId, argumentsHash, cancellationToken).ConfigureAwait(false);
        await operations.ExecuteWriteAsync(
            new RemoteOperationRequest(operationId, connection.ConnectionId, action, new Dictionary<string, object?> { ["path"] = canonicalPath }, true),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DemandApprovalAsync(
        string operationId,
        string tool,
        string connectionId,
        string argumentsHash,
        CancellationToken cancellationToken)
    {
        await authorizer.AuthorizeAsync(
            new OperationAuthorizationRequest(operationId, callerIdentity.Actor, tool, connectionId, argumentsHash),
            cancellationToken).ConfigureAwait(false);

        switch (operationDeduplicator.TryBegin(operationId, argumentsHash, DateTimeOffset.UtcNow))
        {
            case OperationDeduplicationResult.Started:
                return;
            case OperationDeduplicationResult.Duplicate:
                throw new InvalidOperationException("The operation identifier has already been used for these arguments.");
            case OperationDeduplicationResult.Conflict:
                throw new InvalidOperationException("The operation identifier conflicts with different arguments.");
            default:
                throw new InvalidOperationException("Unknown operation deduplication result.");
        }
    }

    private async Task<PublishedConnection> DemandPublishedAsync(string connectionId, CancellationToken cancellationToken) =>
        await publishedConnections.FindAsync(connectionId, cancellationToken).ConfigureAwait(false) ??
        throw new UnauthorizedAccessException("The connection is not published to MCP with explicit readable roots.");

    private async Task<(PublishedConnection Connection, string CanonicalPath)> DemandReadablePathAsync(
        string connectionId,
        string path,
        CancellationToken cancellationToken)
    {
        var connection = await DemandPublishedAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if ((connection.Capabilities & ConnectionCapabilities.FileBrowser) == 0)
            throw new UnauthorizedAccessException("The published connection does not expose file access.");
        var containedPath = DemandContainedPath(connection, path);
        var resolvedPath = await operations.ResolveReadPathAsync(
            connection.ConnectionId,
            containedPath,
            connection.ReadableRoots,
            cancellationToken).ConfigureAwait(false);
        return (connection, resolvedPath);
    }

    private async Task<PublishedConnection> DemandHighRiskAccessAsync(
        string connectionId,
        ConnectionCapabilities capability,
        CancellationToken cancellationToken)
    {
        var connection = await DemandPublishedAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var allowed = capability switch
        {
            ConnectionCapabilities.Terminal => connection.Scope is PublishedMcpScope.ApprovedCommands or PublishedMcpScope.FullSession,
            ConnectionCapabilities.FileBrowser => connection.Scope is PublishedMcpScope.ApprovedWrites or PublishedMcpScope.FullSession,
            _ => false
        };
        if (!allowed)
            throw new UnauthorizedAccessException("The connection does not publish this high-risk capability.");
        if ((connection.Capabilities & capability) == 0)
            throw new UnauthorizedAccessException("The published connection does not expose the requested capability.");
        return connection;
    }

    private static string DemandContainedPath(PublishedConnection connection, string path)
    {
        if (!PathRootPolicy.TryCanonicalizePosixPath(path, out var canonicalPath) ||
            !connection.ReadableRoots.Any(root => PathRootPolicy.ContainsRemotePath(root, canonicalPath)))
            throw new UnauthorizedAccessException("The remote path is outside the connection's published roots.");
        return canonicalPath;
    }

    private static string ComputeArgumentsHash(
        string tool,
        string connectionId,
        IEnumerable<KeyValuePair<string, string?>> arguments) =>
        CanonicalArgumentsHash.Compute(
            arguments.Concat([new("connectionId", connectionId), new("tool", tool)]));

    private static PublishedConnectionDto ToDto(PublishedConnection connection, SessionState status) =>
        new(connection.ConnectionId, connection.Alias, connection.Protocol, connection.Capabilities, status);
}
