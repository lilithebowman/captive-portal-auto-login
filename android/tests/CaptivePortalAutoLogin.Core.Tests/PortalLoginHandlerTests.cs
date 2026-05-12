using CaptivePortalAutoLogin.Models;

namespace CaptivePortalAutoLogin.Core.Tests;

public sealed class PortalLoginHandlerTests
{
    [Fact]
    public async Task LoginAsync_SubmitsCredentialsAndHiddenFields()
    {
        HttpRequest? submitRequest = null;

        await using var server = new TestHttpServer(req =>
        {
            if (req.Path.StartsWith("/", StringComparison.OrdinalIgnoreCase) && req.Method == "GET")
            {
                return HttpResponse.Ok("""
                    <html><body>
                      <form method='post' action='/submit'>
                        <input type='hidden' name='token' value='abc123' />
                        <input type='text' name='username' />
                        <input type='password' name='password' />
                        <button type='submit' name='submit' value='Login'>Login</button>
                      </form>
                    </body></html>
                    """);
            }

            if (req.Path.StartsWith("/submit", StringComparison.OrdinalIgnoreCase))
            {
                submitRequest = req;
                return HttpResponse.Ok("welcome");
            }

            return new HttpResponse { StatusCode = 404, Body = "not found" };
        });

        var cfg = new PortalConfig
        {
            Username = "user@example.com",
            Password = "s3cret"
        };

        var handler = new PortalLoginHandler(cfg);
        var ok = await handler.LoginAsync(server.BaseUri.ToString());

        Assert.True(ok);
        Assert.NotNull(submitRequest);
        Assert.Equal("POST", submitRequest!.Method);
        Assert.Contains("username=user%40example.com", submitRequest.Body, StringComparison.Ordinal);
        Assert.Contains("password=s3cret", submitRequest.Body, StringComparison.Ordinal);
        Assert.Contains("token=abc123", submitRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginAsync_ClickThroughMode_UsesButtonAndSucceeds()
    {
        HttpRequest? submitRequest = null;

        await using var server = new TestHttpServer(req =>
        {
            if (req.Path.StartsWith("/", StringComparison.OrdinalIgnoreCase) && req.Method == "GET")
            {
                return HttpResponse.Ok("""
                    <html><body>
                      <form method='post' action='/accept'>
                        <input type='hidden' name='csrf' value='xyz' />
                        <button type='submit' name='accept' value='Connect'>Accept Terms</button>
                      </form>
                    </body></html>
                    """);
            }

            if (req.Path.StartsWith("/accept", StringComparison.OrdinalIgnoreCase))
            {
                submitRequest = req;
                return HttpResponse.Ok("connected");
            }

            return new HttpResponse { StatusCode = 404, Body = "not found" };
        });

        var cfg = new PortalConfig(); // no credentials => click-through mode
        var handler = new PortalLoginHandler(cfg);

        var ok = await handler.LoginAsync(server.BaseUri.ToString());

        Assert.True(ok);
        Assert.NotNull(submitRequest);
        Assert.Equal("POST", submitRequest!.Method);
        Assert.Contains("accept=Connect", submitRequest.Body, StringComparison.Ordinal);
        Assert.Contains("csrf=xyz", submitRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginAsync_ReturnsFalse_WhenResponseContainsErrorPhrase()
    {
        await using var server = new TestHttpServer(req =>
        {
            if (req.Method == "GET")
            {
                return HttpResponse.Ok("""
                    <html><body>
                      <form method='post' action='/submit'>
                        <input type='text' name='username' />
                        <input type='password' name='password' />
                      </form>
                    </body></html>
                    """);
            }

            return HttpResponse.Ok("invalid credentials");
        });

        var cfg = new PortalConfig
        {
            Username = "u",
            Password = "p"
        };

        var handler = new PortalLoginHandler(cfg);
        var ok = await handler.LoginAsync(server.BaseUri.ToString());

        Assert.False(ok);
    }
}
