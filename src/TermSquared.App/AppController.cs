using System.Net.Sockets;
using System.Text;
using Square.Controls;
using Square.Events;
using Square.Extensions.Terminal;
using Square.Hosting;
using Square.Runtime;
using TermSquared.Core;
using TermSquared.Protocols.Rdp;
using TermSquared.Protocols.Ssh;
using TermSquared.Protocols.Vnc;
using TermSquared.Mcp;
using TermSquared.Security;

namespace TermSquared.App;

internal sealed class AppController : IDisposable
{
    private readonly ImportedConfiguration _configuration;
    private readonly string _configPath;
    private readonly JsonKnownHostStore _knownHosts;
    private readonly AppRemoteOperations _remoteOperations;
    private readonly NamedPipeBrokerServer _broker;
    private readonly RdpLauncher _rdpLauncher = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sessionGate = new();
    private AppWindow? _window;
    private TerminalView? _terminal;
    private Text? _sessionStatus;
    private Text? _details;
    private Text? _fileResults;
    private Text? _activeTabText;
    private Input? _sendInput;
    private Button? _trustOnceButton;
    private Button? _trustStoreButton;
    private Input? _rdpHost;
    private Input? _rdpUsername;
    private Input? _rdpPort;
    private SshSession? _session;
    private SshShellSession? _shell;
    private Task? _shellReader;
    private ConnectionProfile? _selectedProfile;
    private HostKeyCheck? _pendingHostKey;
    private bool _disposed;

    public AppController(ImportedConfiguration configuration, string configPath)
    {
        _configuration = configuration;
        _configPath = configPath;
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermSquared");
        _knownHosts = new JsonKnownHostStore(Path.Combine(dataRoot, "known-hosts.json"));
        _remoteOperations = new AppRemoteOperations(configuration.Profiles, configuration.VolatileSecrets, _knownHosts);
        _broker = new NamedPipeBrokerServer(_remoteOperations, new NamedPipeBrokerOptions());
    }

    public void Attach(AppWindow window)
    {
        _window = window;
        window.Closed += Dispose;
        _broker.StartAsync(_lifetime.Token).GetAwaiter().GetResult();
    }

    public View BuildWorkspace()
    {
        var root = Panel("#171717", "column", "100%", "100%");
        root.Style.Set("gap", "0");
        root.Children.Add(BuildMenuBar());

        var body = Panel("#171717", "row", "100%", "auto");
        body.Style.Set("flex", "1");
        body.Style.Set("min-height", "0");
        body.Style.Set("gap", "0");

        var left = Panel("#1b1b1b", "column", "260px", "100%");
        left.Style.Set("min-width", "220px");
        left.Style.Set("border-right", "1px solid #343434");
        left.Style.Set("gap", "0");
        left.Children.Add(PanelHeader("■  资源管理器", "⚙   ×"));
        left.Children.Add(Caption("筛选", "#676767", "32px", "10px 14px"));
        var connectionList = new ScrollViewer();
        connectionList.Style.Set("flex", "1");
        connectionList.Style.Set("min-height", "0");
        connectionList.Style.Set("background", "#1b1b1b");
        if (_configuration.Profiles.Count == 0)
            connectionList.Children.Add(Caption("没有导入连接", "#8c96a8", "36px", "8px 16px"));
        foreach (var profile in _configuration.Profiles.Take(40))
            connectionList.Children.Add(ResourceButton(profile));
        left.Children.Add(connectionList);
        left.Children.Add(Caption("MCP Broker  ● 运行中", "#86efac", "34px", "8px 14px"));

        var center = Panel("#1d1d1d", "column", "auto", "100%");
        center.Style.Set("flex", "1");
        center.Style.Set("min-width", "0");
        center.Style.Set("gap", "0");
        center.Children.Add(BuildSessionTabs());
        center.Children.Add(BuildTerminalToolbar());
        _terminal = new TerminalView(100, 30, 5000);
        _terminal.Style.Set("flex", "1");
        _terminal.Style.Set("width", "100%");
        _terminal.Style.Set("min-height", "300px");
        _terminal.Style.Set("font-family", "Cascadia Mono, Consolas, monospace");
        _terminal.Style.Set("font-size", "15px");
        _terminal.Style.Set("border", "0");
        _terminal.Feed("\x1b[1;36mTermSquared Windows x64 MVP\x1b[0m\r\n");
        _terminal.Feed("Select an SSH alias to connect. Unknown host keys are never accepted automatically.\r\n\r\n");
        _terminal.Input += (_, input) => _ = WriteTerminalInputAsync(input.Data);
        center.Children.Add(_terminal);
        center.Children.Add(BuildBottomPanel());

        var right = Panel("#1b1b1b", "column", "310px", "100%");
        right.Style.Set("min-width", "270px");
        right.Style.Set("border-left", "1px solid #343434");
        right.Style.Set("gap", "0");
        right.Children.Add(PanelHeader("■  文件管理器", "⚙   ×"));
        var pathBar = Panel("#1b1b1b", "row", "100%", "38px");
        pathBar.Style.Set("padding", "4px 8px");
        pathBar.Style.Set("align-items", "center");
        pathBar.Children.Add(Caption("/", "#e5e7eb", "30px", "5px 8px"));
        pathBar.Children.Add(ActionButton("↑", () => _ = ListRootAsync(), compact: true));
        pathBar.Children.Add(ActionButton("↻", () => _ = ListRootAsync(), compact: true));
        right.Children.Add(pathBar);
        right.Children.Add(Caption("名称", "#9ca3af", "32px", "7px 12px"));
        var files = new ScrollViewer();
        files.Style.Set("flex", "1");
        files.Style.Set("min-height", "0");
        _details = Caption("Select a connection.", "#c6d0df");
        _fileResults = Caption("连接 SSH 后刷新远程目录。", "#8c96a8", "auto", "8px 12px");
        files.Children.Add(_fileResults);
        right.Children.Add(files);
        right.Children.Add(Caption("会话与安全", "#9ca3af", "32px", "7px 12px"));
        right.Children.Add(_details);
        _trustOnceButton = ActionButton("Trust once & connect", () => _ = ConnectPendingAsync(HostKeyDecision.TrustOnce));
        _trustStoreButton = ActionButton("Trust and store", () => _ = ConnectPendingAsync(HostKeyDecision.TrustAndStore));
        SetTrustButtons(false);
        right.Children.Add(_trustOnceButton);
        right.Children.Add(_trustStoreButton);

        var tools = Panel("#1b1b1b", "column", "100%", "auto");
        tools.Style.Set("padding", "8px 10px");
        tools.Children.Add(Caption("远程工具", "#9ca3af"));
        tools.Children.Add(ActionButton("VNC 10.10.0.4", () => _ = ProbeVncAsync()));
        tools.Children.Add(Caption("FTP / FTPS Provider 可用", "#fbbf24"));
        _rdpHost = RdpInput("Host", "");
        _rdpUsername = RdpInput("User", "");
        _rdpPort = RdpInput("Port", "3389", "number");
        tools.Children.Add(_rdpHost);
        tools.Children.Add(_rdpUsername);
        tools.Children.Add(_rdpPort);
        tools.Children.Add(ActionButton("启动 Windows RDP", LaunchRdp));
        right.Children.Add(tools);

        body.Children.Add(left);
        body.Children.Add(center);
        body.Children.Add(right);
        root.Children.Add(body);
        root.Children.Add(BuildStatusBar());
        return root;
    }

    private static MenuBar BuildMenuBar()
    {
        var menuBar = new MenuBar();
        menuBar.Style.Set("height", "42px");
        menuBar.Style.Set("background", "#181818");
        menuBar.Style.Set("border-bottom", "1px solid #343434");
        foreach (var title in new[] { "会话", "编辑", "搜索", "选择", "转到", "查看", "模式", "工具", "窗口", "帮助" })
        {
            var item = new MenuItem { TextContent = title };
            item.Style.Set("color", "#d1d5db");
            item.Children.Add(new Menu
            {
                Children =
                {
                    new MenuItem { TextContent = title == "会话" ? "新建连接" : $"{title}选项" },
                    new MenuSeparator(),
                    new MenuItem { TextContent = "TermSquared" }
                }
            });
            menuBar.Children.Add(item);
        }
        return menuBar;
    }

    private View BuildSessionTabs()
    {
        var tabs = Panel("#202020", "row", "100%", "42px");
        tabs.Style.Set("border-bottom", "1px solid #343434");
        tabs.Style.Set("gap", "1px");
        var index = 1;
        foreach (var profile in _configuration.Profiles.Take(5))
        {
            var label = $"■  {index}. {profile.Name}   ×";
            tabs.Children.Add(TabButton(label, () => _ = SelectAndConnectAsync(profile, HostKeyDecision.Reject)));
            index++;
        }
        _activeTabText = Caption("未连接", "#9ca3af", "40px", "10px 14px");
        tabs.Children.Add(_activeTabText);
        return tabs;
    }

    private View BuildTerminalToolbar()
    {
        var toolbar = Panel("#202020", "row", "100%", "42px");
        toolbar.Style.Set("padding", "5px 8px");
        toolbar.Style.Set("align-items", "center");
        toolbar.Style.Set("border-bottom", "1px solid #343434");
        toolbar.Children.Add(ActionButton("＋", () => { }, compact: true));
        toolbar.Children.Add(ActionButton("▶", () =>
        {
            if (_selectedProfile is not null) _ = ConnectAsync(_selectedProfile, HostKeyDecision.Reject);
        }, compact: true));
        toolbar.Children.Add(ActionButton("■", () => _ = DisconnectAsync(), compact: true));
        toolbar.Children.Add(Caption("ssh   ›   个人   ›   当前会话", "#d1d5db", "32px", "7px 12px"));
        toolbar.Children.Add(ActionButton("刷新 SFTP", () => _ = ListRootAsync(), compact: true));
        return toolbar;
    }

    private View BuildBottomPanel()
    {
        var bottom = Panel("#1a1a1a", "column", "100%", "190px");
        bottom.Style.Set("border-top", "1px solid #343434");
        bottom.Style.Set("gap", "0");
        var tabs = Panel("#202020", "row", "100%", "36px");
        tabs.Children.Add(TabButton("■ 发送", () => { }));
        tabs.Children.Add(TabButton("■ Shell", () => { }));
        tabs.Children.Add(TabButton("■ 传输", () => { }));
        bottom.Children.Add(tabs);
        var sendToolbar = Panel("#1a1a1a", "row", "100%", "42px");
        sendToolbar.Style.Set("padding", "5px 10px");
        sendToolbar.Style.Set("align-items", "center");
        _sendInput = RdpInput("输入要发送到当前会话的文本", "");
        _sendInput.Style.Set("flex", "1");
        sendToolbar.Children.Add(_sendInput);
        sendToolbar.Children.Add(ActionButton("发送", SendText, compact: true));
        sendToolbar.Children.Add(Caption("文本  |  计数 1  |  间隔 1.00s  |  当前会话", "#b6beca"));
        bottom.Children.Add(sendToolbar);
        bottom.Children.Add(Caption("发送内容不会写入日志；密码请使用连接配置。", "#6b7280", "auto", "10px 14px"));
        return bottom;
    }

    private View BuildStatusBar()
    {
        var status = Panel("#161616", "row", "100%", "34px");
        status.Style.Set("border-top", "1px solid #343434");
        status.Style.Set("padding", "7px 14px");
        status.Style.Set("align-items", "center");
        _sessionStatus = Caption("就绪", "#c7ccd4");
        _sessionStatus.Style.Set("flex", "1");
        status.Children.Add(_sessionStatus);
        status.Children.Add(Caption("远程模式   UTF-8   cmd   MCP ●   🔒 安全", "#aab2bf"));
        return status;
    }

    private static View PanelHeader(string title, string actions)
    {
        var header = Panel("#202020", "row", "100%", "40px");
        header.Style.Set("padding", "9px 12px");
        header.Style.Set("border-bottom", "1px solid #343434");
        var label = Caption(title, "#d1d5db");
        label.Style.Set("flex", "1");
        header.Children.Add(label);
        header.Children.Add(Caption(actions, "#9ca3af"));
        return header;
    }

    private Button ResourceButton(ConnectionProfile profile)
    {
        var color = profile.Name.GetHashCode(StringComparison.Ordinal) % 2 == 0 ? "#38bdf8" : "#fb7185";
        var button = ActionButton($"■  {profile.Name}", () => _ = SelectAndConnectAsync(profile, HostKeyDecision.Reject));
        button.Style.Set("height", "38px");
        button.Style.Set("text-align", "left");
        button.Style.Set("background", "#1b1b1b");
        button.Style.Set("color", color);
        button.Style.Set("border", "0");
        return button;
    }

    private static Button TabButton(string text, Action action)
    {
        var button = ActionButton(text, action);
        button.Style.Set("width", "auto");
        button.Style.Set("height", "40px");
        button.Style.Set("background", "#292929");
        button.Style.Set("border", "0");
        button.Style.Set("border-right", "1px solid #373737");
        return button;
    }

    private void SendText()
    {
        var text = _sendInput?.Value;
        if (string.IsNullOrEmpty(text)) return;
        _ = WriteTerminalInputAsync(text + "\r");
        _sendInput!.Value = "";
    }

    private async Task SelectAndConnectAsync(ConnectionProfile profile, HostKeyDecision decision)
    {
        _selectedProfile = profile;
        if (_activeTabText is not null) _activeTabText.TextContent = $"当前: {profile.Name}";
        if (_rdpHost is not null) _rdpHost.Value = profile.Host;
        if (_rdpUsername is not null) _rdpUsername.Value = profile.Username ?? "";
        _pendingHostKey = null;
        SetTrustButtons(false);
        await ConnectAsync(profile, decision).ConfigureAwait(false);
    }

    private Task ConnectPendingAsync(HostKeyDecision decision)
    {
        var profile = _selectedProfile;
        return profile is null ? Task.CompletedTask : ConnectAsync(profile, decision);
    }

    private async Task ConnectAsync(ConnectionProfile profile, HostKeyDecision requestedDecision)
    {
        await DisconnectAsync().ConfigureAwait(false);
        await SetStatusAsync($"Connecting to {profile.Name}...", "#fbbf24").ConfigureAwait(false);
        HostKeyCheck? observed = null;
        SshSession? session = null;
        try
        {
            session = await SshSession.CreateFromProfileAsync(
                profile,
                _configuration.VolatileSecrets,
                _knownHosts,
                (check, _) =>
                {
                    observed = check;
                    var decision = check.Status switch
                    {
                        HostKeyStatus.Trusted => HostKeyDecision.TrustOnce,
                        HostKeyStatus.Unknown when _pendingHostKey?.Presented.Sha256Fingerprint == check.Presented.Sha256Fingerprint => requestedDecision,
                        _ => HostKeyDecision.Reject
                    };
                    return Task.FromResult(decision);
                },
                cancellationToken: _lifetime.Token).ConfigureAwait(false);
            await session.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
            var shell = session.CreateShellSession(
                (uint)(_terminal?.Columns ?? 100),
                (uint)(_terminal?.Rows ?? 30));
            lock (_sessionGate)
            {
                _session = session;
                _shell = shell;
                session = null;
            }
            _pendingHostKey = null;
            await InvokeUiAsync(() =>
            {
                SetTrustButtons(false);
                _terminal?.Feed($"\r\n\x1b[32mConnected to {profile.Name}\x1b[0m\r\n");
                SetText(_sessionStatus, $"已连接  |  {profile.Name}  |  {_terminal?.Columns}x{_terminal?.Rows}", "#86efac");
                SetText(_details, $"{profile.Username}@{profile.Host}:{profile.Port}\nHost key: {observed?.Presented.Algorithm}\n{observed?.Presented.Sha256Fingerprint}", "#c6d0df");
            }).ConfigureAwait(false);
            _shellReader = ReadShellAsync(shell, _lifetime.Token);
        }
        catch (Exception exception) when (exception.GetType().Name == "SshAuthenticationException")
        {
            await SetStatusAsync($"{profile.Name}: authentication failed.", "#fda4af").ConfigureAwait(false);
        }
        catch (Exception exception) when (observed?.Status == HostKeyStatus.Unknown)
        {
            _pendingHostKey = observed;
            await InvokeUiAsync(() =>
            {
                SetTrustButtons(true);
                SetText(_sessionStatus, "Host key approval required", "#fbbf24");
                SetText(_details, $"Unknown host key for {profile.Name}\n{observed.Presented.Algorithm}\n{observed.Presented.Sha256Fingerprint}\nReview before trusting.", "#fbbf24");
            }).ConfigureAwait(false);
            _ = exception;
        }
        catch (Exception) when (observed?.Status == HostKeyStatus.Changed)
        {
            await SetStatusAsync($"{profile.Name}: host key changed. Connection rejected.", "#f87171").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetStatusAsync($"{profile.Name}: {SafeError(exception)}", "#fda4af").ConfigureAwait(false);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadShellAsync(SshShellSession shell, CancellationToken cancellationToken)
    {
        var bytes = new byte[16 * 1024];
        var chars = new char[16 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await shell.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (charCount == 0) continue;
                var batch = new string(chars, 0, charCount);
                await InvokeUiAsync(() => _terminal?.Feed(batch)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetStatusAsync($"Shell disconnected: {SafeError(exception)}", "#fda4af").ConfigureAwait(false);
        }
    }

    private async Task WriteTerminalInputAsync(string data)
    {
        SshShellSession? shell;
        lock (_sessionGate) shell = _shell;
        if (shell is null) return;
        try
        {
            await shell.WriteAsync(Encoding.UTF8.GetBytes(data), _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SetStatusAsync($"Terminal write failed: {SafeError(exception)}", "#fda4af").ConfigureAwait(false);
        }
    }

    private async Task ListRootAsync()
    {
        SshSession? session;
        lock (_sessionGate) session = _session;
        if (session is null)
        {
            await InvokeUiAsync(() => SetText(_fileResults, "Connect SSH first.", "#fbbf24")).ConfigureAwait(false);
            return;
        }
        try
        {
            var entries = await session.ListAsync("/", _lifetime.Token).ConfigureAwait(false);
            var summary = string.Join('\n', entries.Take(14).Select(static entry => $"{(entry.Kind == RemoteEntryKind.Directory ? "d" : "-")} {entry.Name}"));
            await InvokeUiAsync(() => SetText(_fileResults, summary.Length == 0 ? "Directory is empty." : summary, "#c6d0df")).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeUiAsync(() => SetText(_fileResults, SafeError(exception), "#fda4af")).ConfigureAwait(false);
        }
    }

    private async Task ProbeVncAsync()
    {
        await SetStatusAsync("Connecting VNC 10.10.0.4:5900...", "#fbbf24").ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("10.10.0.4", 5900, timeout.Token).ConfigureAwait(false);
            await using var client = new RfbClient(tcp.GetStream(), leaveOpen: true);
            var info = await client.HandshakeAsync(timeout.Token).ConfigureAwait(false);
            await client.SetEncodingsAsync([0], timeout.Token).ConfigureAwait(false);
            await SetStatusAsync($"VNC ready: {info.Name} {info.Width}x{info.Height}, Raw encoding", "#86efac").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SetStatusAsync($"VNC failed: {SafeError(exception)}", "#fda4af").ConfigureAwait(false);
        }
    }

    private void LaunchRdp()
    {
        var host = _rdpHost?.Value.Trim();
        var username = _rdpUsername?.Value.Trim();
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(_rdpPort?.Value, out var port) || port is < 1 or > ushort.MaxValue)
        {
            _ = SetStatusAsync("RDP requires a host and a valid port.", "#fda4af");
            return;
        }
        try
        {
            using var process = _rdpLauncher.Launch(new RdpConnectionOptions(host, port,
                string.IsNullOrWhiteSpace(username) ? null : username));
            _ = SetStatusAsync($"RDP launched for {host}:{port}. Windows will prompt for credentials.", "#86efac");
        }
        catch (Exception exception)
        {
            _ = SetStatusAsync($"RDP failed: {SafeError(exception)}", "#fda4af");
        }
    }

    private async Task DisconnectAsync()
    {
        SshShellSession? shell;
        SshSession? session;
        lock (_sessionGate)
        {
            shell = _shell;
            session = _session;
            _shell = null;
            _session = null;
        }
        if (shell is not null) await shell.DisposeAsync().ConfigureAwait(false);
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        if (_window is not null && !_window.IsClosed)
            await InvokeUiAsync(() => SetText(_sessionStatus, "就绪  |  UTF-8  |  Software renderer", "#8c96a8")).ConfigureAwait(false);
    }

    private Task SetStatusAsync(string status, string color) =>
        InvokeUiAsync(() =>
        {
            SetText(_sessionStatus, status, color);
            SetText(_details, status, color);
        });

    private Task InvokeUiAsync(Action action)
    {
        var window = _window;
        return window is null || window.IsClosed ? Task.CompletedTask : window.Dispatcher.InvokeAsync(action);
    }

    private void SetTrustButtons(bool enabled)
    {
        if (_trustOnceButton is not null) _trustOnceButton.IsEnabled = enabled;
        if (_trustStoreButton is not null) _trustStoreButton.IsEnabled = enabled;
    }

    private static void SetText(Text? text, string value, string color)
    {
        if (text is null) return;
        text.TextContent = value;
        text.Style.Set("color", color);
    }

    private static string SafeError(Exception exception) => exception switch
    {
        OperationCanceledException => "operation cancelled",
        SocketException => "network connection failed",
        _ when exception.GetType().Name == "SshAuthenticationException" => "authentication failed",
        _ => exception.GetType().Name
    };

    private static Button ActionButton(string text, Action action, bool compact = false)
    {
        var button = new Button(text);
        button.Style.Set("width", compact ? "auto" : "100%");
        button.Style.Set("height", compact ? "30px" : "36px");
        button.Style.Set("padding", compact ? "3px 10px" : "6px 12px");
        button.Style.Set("background", "#263244");
        button.Style.Set("color", "#e7edf7");
        button.Style.Set("border", "1px solid #3b4a60");
        button.AddEventListener(StandardEvents.Click, action);
        return button;
    }

    private static Input RdpInput(string placeholder, string value, string type = "text")
    {
        var input = new Input { Placeholder = placeholder, Value = value, Type = type };
        input.Style.Set("width", "100%");
        input.Style.Set("background", "#10141b");
        input.Style.Set("color", "#e7edf7");
        input.Style.Set("border", "1px solid #3b4a60");
        return input;
    }

    private static View Panel(string background, string direction, string width, string height)
    {
        var panel = new View();
        panel.Style.Set("display", "flex");
        panel.Style.Set("flex-direction", direction);
        panel.Style.Set("width", width);
        panel.Style.Set("height", height);
        panel.Style.Set("background", background);
        panel.Style.Set("gap", "12px");
        return panel;
    }

    private static Text Heading(string value, float size, string color)
    {
        var text = new Text(value) { FontSize = size };
        text.Style.Set("color", color);
        text.Style.Set("font-weight", "700");
        return text;
    }

    private static Text Caption(string value, string color, string height = "auto", string padding = "0")
    {
        var text = new Text(value) { FontSize = 13 };
        text.Style.Set("color", color);
        text.Style.Set("height", height);
        text.Style.Set("padding", padding);
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
            _broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _remoteOperations.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
        }
        _rdpLauncher.Dispose();
        _knownHosts.Dispose();
        _lifetime.Dispose();
    }
}
