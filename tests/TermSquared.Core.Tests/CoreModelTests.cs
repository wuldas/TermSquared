using TermSquared.Core;

namespace TermSquared.Core.Tests;

public sealed class CoreModelTests
{
    [Fact]
    public void SshProfileDoesNotExposeSecretValue()
    {
        var profile = ConnectionProfile.CreateSsh("demo", "host", 22, "user", new SecretReference("memory", "opaque"));
        Assert.Equal(ConnectionCapabilities.Terminal | ConnectionCapabilities.FileBrowser, profile.Capabilities);
        Assert.Equal("memory:opaque", profile.Secret?.ToString());
    }

    [Fact]
    public void StableErrorDoesNotReturnExceptionMessage()
    {
        var error = RemoteError.FromException(new InvalidOperationException("password=secret"));
        Assert.Equal(RemoteErrorCode.TransportFailure, error.Code);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }
}
