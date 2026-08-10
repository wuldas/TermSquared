using System.Security.Cryptography;
using System.Text;
using TermSquared.Core;

namespace TermSquared.Security;

public interface ISecretStore
{
    Task<SecretReference> StoreAsync(string purpose, ReadOnlyMemory<char> secret, CancellationToken cancellationToken);
    Task<string?> RetrieveAsync(SecretReference reference, CancellationToken cancellationToken);
    Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken);
}

public sealed class InMemorySecretStore : ISecretStore, IDisposable
{
    private readonly Dictionary<string, char[]> _secrets = new(StringComparer.Ordinal);

    public Task<SecretReference> StoreAsync(string purpose, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        _secrets[id] = secret.ToArray();
        return Task.FromResult(new SecretReference("memory", id));
    }

    public Task<string?> RetrieveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(reference.Store, "memory", StringComparison.Ordinal))
            throw new ArgumentException("The secret reference belongs to a different store.", nameof(reference));
        return Task.FromResult(_secrets.TryGetValue(reference.Id, out var value) ? new string(value) : null);
    }

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(reference.Store, "memory", StringComparison.Ordinal))
            throw new ArgumentException("The secret reference belongs to a different store.", nameof(reference));
        if (_secrets.Remove(reference.Id, out var value)) Array.Clear(value);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var value in _secrets.Values) Array.Clear(value);
        _secrets.Clear();
    }
}

public sealed class DpapiFileSecretStore(string rootDirectory) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TermSquared.SecretStore.v1");

    public async Task<SecretReference> StoreAsync(string purpose, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI storage requires Windows.");
        Directory.CreateDirectory(rootDirectory);
        var id = Guid.NewGuid().ToString("N");
        var characters = secret.ToArray();
        var plaintext = Encoding.UTF8.GetBytes(characters);
        try
        {
            var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(GetPath(id), protectedBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(characters);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        return new SecretReference("dpapi-file", id);
    }

    public async Task<string?> RetrieveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI storage requires Windows.");
        ValidateReference(reference);
        var path = GetPath(reference.Id);
        if (!File.Exists(path)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        File.Delete(GetPath(reference.Id));
        return Task.CompletedTask;
    }

    private string GetPath(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid secret reference.", nameof(id));
        return Path.Combine(rootDirectory, id + ".secret");
    }

    private static void ValidateReference(SecretReference reference)
    {
        if (!string.Equals(reference.Store, "dpapi-file", StringComparison.Ordinal))
            throw new ArgumentException("The secret reference belongs to a different store.", nameof(reference));
    }
}

public sealed class RoutingSecretStore(ISecretStore defaultStore, IReadOnlyDictionary<string, ISecretStore> stores) : ISecretStore
{
    public async Task<SecretReference> StoreAsync(
        string purpose,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken)
    {
        var reference = await defaultStore.StoreAsync(purpose, secret, cancellationToken).ConfigureAwait(false);
        if (!stores.ContainsKey(reference.Store))
            throw new InvalidOperationException("The default secret store returned an unregistered reference.");
        return reference;
    }

    public Task<string?> RetrieveAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Resolve(reference).RetrieveAsync(reference, cancellationToken);

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Resolve(reference).DeleteAsync(reference, cancellationToken);

    private ISecretStore Resolve(SecretReference reference) =>
        stores.TryGetValue(reference.Store, out var store)
            ? store
            : throw new ArgumentException("Unknown secret store.", nameof(reference));
}
