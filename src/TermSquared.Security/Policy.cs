using System.Collections.Concurrent;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TermSquared.Security;

public static class PathRootPolicy
{
    public static bool Contains(string root, string candidate, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (string.Equals(normalizedRoot, Path.TrimEndingDirectorySeparator(normalizedCandidate), comparison)) return true;
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    public static bool ContainsRemotePath(string root, string candidate)
    {
        if (!TryCanonicalizePosixPath(root, out var normalizedRoot) ||
            !TryCanonicalizePosixPath(candidate, out var normalizedCandidate)) return false;
        if (normalizedRoot == "/") return true;
        return normalizedCandidate == normalizedRoot ||
               normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
    }

    public static bool TryCanonicalizePosixPath(string path, out string canonicalPath)
    {
        canonicalPath = "";
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/') return false;
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return false;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (segment.Contains('\0', StringComparison.Ordinal)) return false;
            segments.Add(segment);
        }
        canonicalPath = "/" + string.Join('/', segments);
        return true;
    }
}

public static class CanonicalArgumentsHash
{
    public static string Compute(IEnumerable<KeyValuePair<string, string?>> arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var argument in arguments.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                if (argument.Value is null)
                    writer.WriteNull(argument.Key);
                else
                    writer.WriteString(argument.Key, argument.Value);
            }
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }
}

public sealed record OperationAuthorizationRequest(
    string OperationId,
    string Actor,
    string Tool,
    string ConnectionId,
    string ArgumentsHash);

public sealed record ApprovalRequest(
    string OperationId,
    string Actor,
    string Tool,
    string ConnectionId,
    string ArgumentsHash,
    DateTimeOffset ExpiresAt);

public sealed record ApprovalGrant(
    string OperationId,
    string Actor,
    string Tool,
    string ConnectionId,
    string ArgumentsHash,
    DateTimeOffset ExpiresAt);

public interface IOperationAuthorizer
{
    ValueTask AuthorizeAsync(OperationAuthorizationRequest request, CancellationToken cancellationToken);
}

public sealed class ApprovalBroker : IOperationAuthorizer
{
    private readonly ConcurrentDictionary<string, ApprovalGrant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OperationAuthorizationRequest> _pending = new(StringComparer.Ordinal);

    public IReadOnlyCollection<OperationAuthorizationRequest> PendingRequests => _pending.Values.ToArray();

    public ApprovalGrant Issue(ApprovalRequest request)
    {
        if (request.ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Approval request is already expired.", nameof(request));
        var grant = new ApprovalGrant(
            request.OperationId,
            request.Actor,
            request.Tool,
            request.ConnectionId,
            request.ArgumentsHash,
            request.ExpiresAt);
        _grants[request.OperationId] = grant;
        _pending.TryRemove(request.OperationId, out _);
        return grant;
    }

    public ValueTask AuthorizeAsync(OperationAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_grants.TryRemove(request.OperationId, out var grant))
        {
            _pending[request.OperationId] = request;
            throw new UnauthorizedAccessException("The operation is pending explicit approval and cannot run without an approval service.");
        }

        if (grant.ExpiresAt <= DateTimeOffset.UtcNow ||
            !string.Equals(grant.Actor, request.Actor, StringComparison.Ordinal) ||
            !string.Equals(grant.Tool, request.Tool, StringComparison.Ordinal) ||
            !string.Equals(grant.ConnectionId, request.ConnectionId, StringComparison.Ordinal) ||
            !CryptographicEquals(grant.ArgumentsHash, request.ArgumentsHash))
            throw new UnauthorizedAccessException("The approval grant does not match this operation.");

        return ValueTask.CompletedTask;
    }

    private static bool CryptographicEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
}

public sealed class UnavailableOperationAuthorizer : IOperationAuthorizer
{
    public ValueTask AuthorizeAsync(OperationAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new UnauthorizedAccessException("Operation approval is unavailable; the high-risk operation was denied.");
    }
}

public enum OperationDeduplicationResult
{
    Started,
    Duplicate,
    Conflict
}

public sealed class OperationDeduplicator
{
    private sealed record Entry(string ArgumentsHash, DateTimeOffset StartedAt);

    private readonly ConcurrentDictionary<string, Entry> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _retention;

    public OperationDeduplicator(TimeSpan retention) => _retention = retention;

    public OperationDeduplicationResult TryBegin(string operationId, string argumentsHash, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var item in _seen)
                if (now - item.Value.StartedAt >= _retention)
                    _seen.TryRemove(new KeyValuePair<string, Entry>(item.Key, item.Value));

            if (_seen.TryAdd(operationId, new Entry(argumentsHash, now)))
                return OperationDeduplicationResult.Started;

            return _seen.TryGetValue(operationId, out var existing) &&
                   string.Equals(existing.ArgumentsHash, argumentsHash, StringComparison.Ordinal)
                ? OperationDeduplicationResult.Duplicate
                : OperationDeduplicationResult.Conflict;
        }
    }
}

public enum AuditOutcome
{
    Allowed,
    Denied,
    Failed,
    Cancelled
}

public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string OperationId,
    string Actor,
    string Action,
    string Target,
    AuditOutcome Outcome,
    string? ErrorCode = null);

public interface IAuditSink
{
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
