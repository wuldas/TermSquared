using System.Diagnostics;
using TermSquared.Protocols.Rdp;

namespace TermSquared.Protocols.Tests;

public sealed class RdpTests
{
    [Fact]
    public void BuildsRdpFileWithoutPassword()
    {
        var content = RdpFileBuilder.Build(new RdpConnectionOptions(
            "rdp.example.test", 3390, "DOMAIN\\alice", 1600, 900));

        Assert.Contains("full address:s:rdp.example.test:3390", content, StringComparison.Ordinal);
        Assert.Contains("username:s:DOMAIN\\alice", content, StringComparison.Ordinal);
        Assert.Contains("desktopwidth:i:1600", content, StringComparison.Ordinal);
        Assert.Contains("desktopheight:i:900", content, StringComparison.Ordinal);
        Assert.Contains("screen mode id:i:1", content, StringComparison.Ordinal);
        Assert.Contains("prompt for credentials:i:1", content, StringComparison.Ordinal);
        Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("51:b", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreatesShellFreeMstscStartInfoWithSingleFileArgument()
    {
        var path = Path.Combine(Path.GetTempPath(), "folder with spaces", "test.rdp");

        var startInfo = RdpLauncher.CreateStartInfo(path);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "mstsc.exe"), startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Single(startInfo.ArgumentList);
        Assert.Equal(Path.GetFullPath(path), startInfo.ArgumentList[0]);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void FormatsIpv6AddressWithSeparatePort()
    {
        var content = RdpFileBuilder.Build(new RdpConnectionOptions("2001:db8::1", 3389));

        Assert.Contains("full address:s:[2001:db8::1]:3389", content, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherWritesUnderConfiguredTempDirectoryAndCleansUpWithoutStartingMstsc()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "TermSquared.Tests", Guid.NewGuid().ToString("N"));
        ProcessStartInfo? captured = null;
        var launcher = new RdpLauncher(tempDirectory, startProcess: startInfo =>
        {
            captured = startInfo;
            return new Process();
        });
        try
        {
            using var process = launcher.Launch(new RdpConnectionOptions("rdp.example.test"));
            Assert.NotNull(captured);
            var path = Assert.Single(captured.ArgumentList);
            Assert.StartsWith(Path.GetFullPath(tempDirectory) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path));

            launcher.Dispose();

            Assert.False(File.Exists(path));
        }
        finally
        {
            launcher.Dispose();
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
