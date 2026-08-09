using Microsoft.AspNetCore.Http;
using TermSquared.Mcp.HttpHost;

namespace TermSquared.Mcp.Tests;

public sealed class HttpTokenMiddlewareTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ValidBearerTokenAuthenticatesRequest()
    {
        var invoked = false;
        using var middleware = CreateMiddleware(context =>
        {
            invoked = context.User.Identity?.IsAuthenticated == true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {Token}";

        await middleware.InvokeAsync(context);

        Assert.True(invoked);
    }

    [Fact]
    public async Task InvalidBearerTokenIsRejected()
    {
        using var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer invalid";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate);
    }

    [Fact]
    public async Task UnlistedOriginIsRejected()
    {
        using var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers.Origin = "https://example.invalid";
        context.Request.Headers.Authorization = $"Bearer {Token}";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static McpRequestSecurityMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, new HttpHostSettings(Token, 5088, 1_048_576, 2, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
}
