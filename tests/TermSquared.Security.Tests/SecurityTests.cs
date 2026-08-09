using TermSquared.Security;

namespace TermSquared.Security.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void RootContainmentRejectsSiblingPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.True(PathRootPolicy.Contains(root, Path.Combine(root, "folder", "file.txt")));
        Assert.False(PathRootPolicy.Contains(root, root + "-other"));
        Assert.False(PathRootPolicy.Contains(root, Path.Combine(root, "..", "escape.txt")));
    }

    [Fact]
    public void RemoteRootContainmentUsesPosixSegments()
    {
        Assert.True(PathRootPolicy.ContainsRemotePath("/srv/data", "/srv/data/team/file.txt"));
        Assert.True(PathRootPolicy.ContainsRemotePath("/srv/data", "/srv/data/team/../file.txt"));
        Assert.False(PathRootPolicy.ContainsRemotePath("/srv/data", "/srv/database/file.txt"));
        Assert.False(PathRootPolicy.ContainsRemotePath("/srv/data", "/srv/data/../../etc/passwd"));
    }

    [Fact]
    public async Task ApprovalGrantIsSingleUseAndBoundToCanonicalArguments()
    {
        var broker = new ApprovalBroker();
        var expected = new OperationAuthorizationRequest("op-1", "test-actor", "remote.write", "connection-1", "HASH-1");
        var grant = broker.Issue(CreateApproval(expected));

        Assert.Equal(expected.Actor, grant.Actor);
        Assert.Equal(expected.Tool, grant.Tool);
        Assert.Equal(expected.ConnectionId, grant.ConnectionId);
        Assert.Equal(expected.ArgumentsHash, grant.ArgumentsHash);
        await AssertGrantMismatchAsync(broker, expected, expected with { Actor = "other-actor" });
        await AssertGrantMismatchAsync(broker, expected, expected with { Tool = "ssh.exec" });
        await AssertGrantMismatchAsync(broker, expected, expected with { ConnectionId = "connection-2" });
        await AssertGrantMismatchAsync(broker, expected, expected with { ArgumentsHash = "HASH-2" });

        broker.Issue(CreateApproval(expected));
        await broker.AuthorizeAsync(expected, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await broker.AuthorizeAsync(expected, default));
        Assert.Contains(expected, broker.PendingRequests);
    }

    [Fact]
    public void OperationIdsAreDeduplicatedAndArgumentConflictsAreExplicit()
    {
        var deduplicator = new OperationDeduplicator(TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(OperationDeduplicationResult.Started, deduplicator.TryBegin("same", "HASH-1", now));
        Assert.Equal(OperationDeduplicationResult.Duplicate, deduplicator.TryBegin("same", "HASH-1", now.AddSeconds(1)));
        Assert.Equal(OperationDeduplicationResult.Conflict, deduplicator.TryBegin("same", "HASH-2", now.AddSeconds(2)));
        Assert.Equal(OperationDeduplicationResult.Started, deduplicator.TryBegin("same", "HASH-2", now.AddMinutes(6)));
    }

    [Fact]
    public void ConcurrentOperationIdCanStartOnlyOnce()
    {
        var deduplicator = new OperationDeduplicator(TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;
        var results = new OperationDeduplicationResult[64];

        Parallel.For(0, results.Length, index =>
            results[index] = deduplicator.TryBegin("same", "HASH", now));

        Assert.Equal(1, results.Count(static result => result == OperationDeduplicationResult.Started));
        Assert.Equal(results.Length - 1, results.Count(static result => result == OperationDeduplicationResult.Duplicate));
    }

    [Fact]
    public void CanonicalArgumentHashIgnoresDictionaryOrder()
    {
        var first = CanonicalArgumentsHash.Compute([new("path", "/srv/data"), new("action", "delete")]);
        var second = CanonicalArgumentsHash.Compute([new("action", "delete"), new("path", "/srv/data")]);
        Assert.Equal(first, second);
    }

    private static ApprovalRequest CreateApproval(OperationAuthorizationRequest request) =>
        new(
            request.OperationId,
            request.Actor,
            request.Tool,
            request.ConnectionId,
            request.ArgumentsHash,
            DateTimeOffset.UtcNow.AddMinutes(1));

    private static async Task AssertGrantMismatchAsync(
        ApprovalBroker broker,
        OperationAuthorizationRequest approved,
        OperationAuthorizationRequest attempted)
    {
        broker.Issue(CreateApproval(approved));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await broker.AuthorizeAsync(attempted, default));
    }

    [Fact]
    public async Task UnknownAndChangedHostKeysAreNotAutomaticallyTrusted()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "known-hosts.json");
        using var store = new JsonKnownHostStore(path);
        var unknown = await store.CheckAsync("host", 22, "ssh-ed25519", new byte[] { 1, 2, 3 }, default);
        Assert.Equal(HostKeyStatus.Unknown, unknown.Status);
        await store.TrustAsync(unknown.Presented, default);
        var trusted = await store.CheckAsync("host", 22, "ssh-ed25519", new byte[] { 1, 2, 3 }, default);
        var changed = await store.CheckAsync("host", 22, "ssh-ed25519", new byte[] { 4, 5, 6 }, default);
        Assert.Equal(HostKeyStatus.Trusted, trusted.Status);
        Assert.Equal(HostKeyStatus.Changed, changed.Status);
    }
}
