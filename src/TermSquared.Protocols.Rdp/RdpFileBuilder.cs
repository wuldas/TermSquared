using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TermSquared.Protocols.Rdp;

public sealed record RdpConnectionOptions
{
    public RdpConnectionOptions(
        string host,
        int port = 3389,
        string? username = null,
        int desktopWidth = 1280,
        int desktopHeight = 720,
        bool fullScreen = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host.Contains('\r') || host.Contains('\n'))
            throw new ArgumentException("RDP host must be a single line.", nameof(host));
        if (host.Contains(':') && !(IPAddress.TryParse(host.Trim('[', ']'), out var address) &&
                                  address.AddressFamily == AddressFamily.InterNetworkV6))
            throw new ArgumentException("Specify the RDP port separately from the host.", nameof(host));
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        if (username?.Contains('\r') == true || username?.Contains('\n') == true)
            throw new ArgumentException("RDP username must be a single line.", nameof(username));
        ArgumentOutOfRangeException.ThrowIfLessThan(desktopWidth, 200);
        ArgumentOutOfRangeException.ThrowIfLessThan(desktopHeight, 200);
        Host = host;
        Port = port;
        Username = username;
        DesktopWidth = desktopWidth;
        DesktopHeight = desktopHeight;
        FullScreen = fullScreen;
    }

    public string Host { get; }
    public int Port { get; }
    public string? Username { get; }
    public int DesktopWidth { get; }
    public int DesktopHeight { get; }
    public bool FullScreen { get; }
}

public static class RdpFileBuilder
{
    public static string Build(RdpConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = IsIpv6(options.Host) ? $"[{options.Host.Trim('[', ']')}]" : options.Host;
        var address = $"{host}:{options.Port.ToString(CultureInfo.InvariantCulture)}";
        var builder = new StringBuilder()
            .Append("full address:s:").AppendLine(address)
            .Append("desktopwidth:i:").AppendLine(options.DesktopWidth.ToString(CultureInfo.InvariantCulture))
            .Append("desktopheight:i:").AppendLine(options.DesktopHeight.ToString(CultureInfo.InvariantCulture))
            .Append("screen mode id:i:").AppendLine(options.FullScreen ? "2" : "1")
            .AppendLine("prompt for credentials:i:1");
        if (!string.IsNullOrWhiteSpace(options.Username)) builder.Append("username:s:").AppendLine(options.Username);
        return builder.ToString();
    }

    private static bool IsIpv6(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
}

public sealed class RdpLauncher : IDisposable
{
    private readonly string _tempDirectory;
    private readonly bool _deleteFileOnDispose;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private string? _rdpFilePath;
    private bool _disposed;

    public RdpLauncher(
        string? tempDirectory = null,
        bool deleteFileOnDispose = true,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        _tempDirectory = tempDirectory ?? Path.GetTempPath();
        _deleteFileOnDispose = deleteFileOnDispose;
        _startProcess = startProcess ?? Process.Start;
    }

    public Process Launch(RdpConnectionOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, $"TermSquared-{Guid.NewGuid():N}.rdp");
        File.WriteAllText(path, RdpFileBuilder.Build(options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var process = _startProcess(CreateStartInfo(path));
            if (process is null) throw new InvalidOperationException("Windows Remote Desktop did not start.");
            DeletePreviousFile();
            _rdpFilePath = path;
            return process;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public static ProcessStartInfo CreateStartInfo(string rdpFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rdpFilePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "mstsc.exe"),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.GetFullPath(rdpFilePath));
        return startInfo;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeletePreviousFile();
    }

    private void DeletePreviousFile()
    {
        if (_deleteFileOnDispose && _rdpFilePath is not null) TryDelete(_rdpFilePath);
        _rdpFilePath = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
