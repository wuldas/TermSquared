using Square.Extensions.Terminal;
using Square.Extensions.CodeEditor;
using Square.DevTools;
using Square.Graphics;
using Square.Hosting;

namespace TermSquared.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        TerminalRegistration.RegisterDefaults();
        CodeEditorRegistration.RegisterDefaults();
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "ssh-config.json");
        using var imported = LoadConfiguration(configPath);
        using var controller = new AppController(imported, configPath);
        var window = new AppWindow("TermSquared", 1440, 860)
        {
            Background = Color.FromRgb(15, 18, 24),
            RenderingMode = RenderMode.Auto
        };
        controller.Attach(window);
        window.LoadGlobalCssText(AppTheme.Css);
        window.Load(controller.BuildWorkspace());
        if (string.Equals(Environment.GetEnvironmentVariable("TERMSQUARED_DEVTOOLS"), "1", StringComparison.Ordinal))
        {
            var devToolsToken = Environment.GetEnvironmentVariable("TERMSQUARED_DEVTOOLS_TOKEN");
            if (string.IsNullOrWhiteSpace(devToolsToken) || devToolsToken.Length < 32)
                throw new InvalidOperationException("TERMSQUARED_DEVTOOLS_TOKEN must contain at least 32 characters when DevTools is enabled.");
            var devTools = window.UseDevToolsServer(new DevToolsOptions
            {
                Port = 0,
                AccessToken = devToolsToken,
                AllowInputInjection = true,
                AllowInspector = true,
                IncludeTextContent = true
            });
            Console.Error.WriteLine($"Square DevTools: {devTools.BaseAddress}; use the token supplied by the launching process.");
        }
        new DesktopApplication(window).Run();
    }

    private static ImportedConfiguration LoadConfiguration(string path)
    {
        try
        {
            return ConnectionConfigImporter.LoadAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Failed to import SSH aliases: {exception.GetType().Name}");
            return new ImportedConfiguration([], new TermSquared.Security.InMemorySecretStore());
        }
    }
}
