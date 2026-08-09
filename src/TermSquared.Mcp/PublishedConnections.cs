using TermSquared.Core;
using TermSquared.Security;

namespace TermSquared.Mcp;

public sealed record PublishedConnectionAccess(Guid ConnectionId, IReadOnlyList<string> ReadableRoots);

public sealed record PublishedConnection(
    string ConnectionId,
    string Alias,
    ConnectionProtocol Protocol,
    ConnectionCapabilities Capabilities,
    PublishedMcpScope Scope,
    IReadOnlyList<string> ReadableRoots);

public sealed record PublishedConnectionDto(
    string ConnectionId,
    string Alias,
    ConnectionProtocol Protocol,
    ConnectionCapabilities Capabilities,
    SessionState Status);

public interface IPublishedConnectionRegistry
{
    Task<IReadOnlyList<PublishedConnection>> ListAsync(CancellationToken cancellationToken);
    Task<PublishedConnection?> FindAsync(string connectionId, CancellationToken cancellationToken);
}

public sealed class PublishedConnectionRegistry : IPublishedConnectionRegistry
{
    private readonly IRemoteOperations _operations;
    private readonly Dictionary<Guid, string[]> _readableRoots;

    public PublishedConnectionRegistry(IRemoteOperations operations, IEnumerable<PublishedConnectionAccess> configuredAccess)
    {
        _operations = operations;
        _readableRoots = configuredAccess.ToDictionary(
            static access => access.ConnectionId,
            static access => NormalizeRoots(access.ReadableRoots));
    }

    public async Task<IReadOnlyList<PublishedConnection>> ListAsync(CancellationToken cancellationToken)
    {
        var profiles = await _operations.ListConnectionsAsync(cancellationToken).ConfigureAwait(false);
        return profiles
            .Where(profile => profile.PublishedScope != PublishedMcpScope.None && ResolveRoots(profile).Length > 0)
            .Select(profile => new PublishedConnection(
                profile.Id.ToString("D"),
                profile.Name,
                profile.Protocol,
                profile.Capabilities,
                profile.PublishedScope,
                ResolveRoots(profile)))
            .ToArray();
    }

    public async Task<PublishedConnection?> FindAsync(string connectionId, CancellationToken cancellationToken)
    {
        var published = await ListAsync(cancellationToken).ConfigureAwait(false);
        return published.FirstOrDefault(connection =>
            string.Equals(connection.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] NormalizeRoots(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return [];

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!PathRootPolicy.TryCanonicalizePosixPath(root, out var canonicalRoot))
                throw new ArgumentException("Published readable roots must be absolute POSIX paths.", nameof(roots));
            normalized.Add(canonicalRoot);
        }
        return normalized.ToArray();
    }

    private string[] ResolveRoots(ConnectionProfile profile)
    {
        if (_readableRoots.TryGetValue(profile.Id, out var configured)) return configured;
        return NormalizeRoots(profile.PublishedRoots ?? []);
    }
}

public sealed record McpCallerIdentity(string Actor);
