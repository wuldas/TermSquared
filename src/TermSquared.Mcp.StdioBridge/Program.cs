using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using TermSquared.Core;
using TermSquared.Mcp;
using TermSquared.Security;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(new OperationDeduplicator(TimeSpan.FromHours(24)));
builder.Services.AddSingleton(new NamedPipeBrokerOptions
{
    PipeName = Environment.GetEnvironmentVariable("TERMSQUARED_BROKER_PIPE")
        ?? NamedPipeBrokerOptions.DefaultPipeName
});
builder.Services.AddSingleton<IRemoteOperations, NamedPipeRemoteOperations>();
builder.Services.AddSingleton<IPublishedConnectionRegistry, PublishedConnectionRegistry>();
builder.Services.AddSingleton<IOperationAuthorizer, UnavailableOperationAuthorizer>();
builder.Services.AddSingleton(new McpCallerIdentity("mcp-stdio-client"));
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RemoteMcpTools>();
await builder.Build().RunAsync().ConfigureAwait(false);
