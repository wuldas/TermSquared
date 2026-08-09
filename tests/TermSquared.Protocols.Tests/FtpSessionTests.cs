using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using TermSquared.Core;
using TermSquared.Protocols.Ftp;

namespace TermSquared.Protocols.Tests;

public sealed class FtpSessionTests
{
    [Theory]
    [InlineData(ConnectionProtocol.Ftp, 21)]
    [InlineData(ConnectionProtocol.Ftps, 21)]
    public void MapsPasswordProfileWithoutConnecting(ConnectionProtocol protocol, int port)
    {
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "files", protocol, "ftp.example.test", port, "alice",
            AuthenticationKind.Password, new SecretReference("memory", "opaque"), ConnectionCapabilities.FileBrowser);

        var options = FtpConnectionOptions.FromProfile(profile, TimeSpan.FromSeconds(12));

        Assert.Equal(profile.Host, options.Host);
        Assert.Equal(profile.Port, options.Port);
        Assert.Equal(profile.Username, options.Username);
        Assert.Equal(protocol, options.Protocol);
        Assert.Equal(TimeSpan.FromSeconds(12), options.Timeout);
    }

    [Theory]
    [InlineData(ConnectionProtocol.Ftp, FtpEncryptionMode.None, false)]
    [InlineData(ConnectionProtocol.Ftps, FtpEncryptionMode.Explicit, true)]
    public void MapsProtocolToFluentFtpConfiguration(
        ConnectionProtocol protocol,
        FtpEncryptionMode encryptionMode,
        bool dataConnectionEncryption)
    {
        var options = new FtpConnectionOptions("ftp.example.test", 21, "alice", protocol, TimeSpan.FromSeconds(12));

        var config = FtpSession.CreateConfig(options);

        Assert.Equal(encryptionMode, config.EncryptionMode);
        Assert.Equal(dataConnectionEncryption, config.DataConnectionEncryption);
        Assert.Equal(12_000, config.ConnectTimeout);
        Assert.Equal(12_000, config.DataConnectionReadTimeout);
    }

    [Fact]
    public void DefaultCertificateDecisionRejectsPolicyErrors()
    {
        using var certificate = CreateCertificate();
        var check = new FtpCertificateCheck(certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.False(FtpSession.DefaultCertificateDecision(check));
        Assert.True(FtpSession.DefaultCertificateDecision(check with { PolicyErrors = SslPolicyErrors.None }));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ftp.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
