using System.Security.Cryptography;
using System.Text.Json;

namespace TermSquared.Security;

public enum HostKeyStatus
{
    Trusted,
    Unknown,
    Changed
}

public enum HostKeyDecision
{
    Reject,
    TrustOnce,
    TrustAndStore
}

public sealed record KnownHost(string Host, int Port, string Algorithm, string Sha256Fingerprint, DateTimeOffset TrustedAt);

public sealed record HostKeyCheck(HostKeyStatus Status, KnownHost? Existing, KnownHost Presented);

public interface IKnownHostStore
{
    Task<HostKeyCheck> CheckAsync(string host, int port, string algorithm, ReadOnlyMemory<byte> hostKey, CancellationToken cancellationToken);
    Task TrustAsync(KnownHost host, CancellationToken cancellationToken);
}

public sealed class JsonKnownHostStore(string filePath) : IKnownHostStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<HostKeyCheck> CheckAsync(string host, int port, string algorithm, ReadOnlyMemory<byte> hostKey, CancellationToken cancellationToken)
    {
        var presented = new KnownHost(host, port, algorithm, Fingerprint(hostKey.Span), DateTimeOffset.UtcNow);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hosts = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var existing = hosts.FirstOrDefault(item =>
                item.Port == port && string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return new HostKeyCheck(HostKeyStatus.Unknown, null, presented);
            return new HostKeyCheck(
                string.Equals(existing.Sha256Fingerprint, presented.Sha256Fingerprint, StringComparison.Ordinal)
                    ? HostKeyStatus.Trusted
                    : HostKeyStatus.Changed,
                existing,
                presented);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TrustAsync(KnownHost host, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hosts = await LoadAsync(cancellationToken).ConfigureAwait(false);
            hosts.RemoveAll(item => item.Port == host.Port && string.Equals(item.Host, host.Host, StringComparison.OrdinalIgnoreCase));
            hosts.Add(host);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, hosts, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string Fingerprint(ReadOnlySpan<byte> hostKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    private async Task<List<KnownHost>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return [];
        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<List<KnownHost>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public void Dispose() => _gate.Dispose();
}
