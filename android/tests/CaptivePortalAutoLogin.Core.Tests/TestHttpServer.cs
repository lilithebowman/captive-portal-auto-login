using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CaptivePortalAutoLogin.Core.Tests;

internal sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoopTask;
    private readonly Func<HttpRequest, HttpResponse> _handler;

    public Uri BaseUri { get; }
    public HttpRequest? LastRequest { get; private set; }

    public TestHttpServer(Func<HttpRequest, HttpResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var _ = client.ConfigureAwait(false);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(requestLine))
            return;

        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
            return;

        var method = parts[0];
        var path = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl))
            int.TryParse(cl, out contentLength);

        var body = string.Empty;
        if (contentLength > 0)
        {
            var buffer = new char[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = await reader.ReadAsync(buffer, read, contentLength - read);
                if (n == 0) break;
                read += n;
            }
            body = new string(buffer, 0, read);
        }

        LastRequest = new HttpRequest(method, path, headers, body);
        var response = _handler(LastRequest);

        var responseBuilder = new StringBuilder();
        responseBuilder.Append($"HTTP/1.1 {response.StatusCode} {ReasonPhrase(response.StatusCode)}\r\n");

        var contentBytes = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
        response.Headers["Content-Length"] = contentBytes.Length.ToString();
        response.Headers["Connection"] = "close";

        foreach (var header in response.Headers)
            responseBuilder.Append($"{header.Key}: {header.Value}\r\n");

        responseBuilder.Append("\r\n");

        var headBytes = Encoding.ASCII.GetBytes(responseBuilder.ToString());
        await stream.WriteAsync(headBytes, ct);
        await stream.WriteAsync(contentBytes, ct);
        await stream.FlushAsync(ct);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        302 => "Found",
        404 => "Not Found",
        _ => "OK"
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoopTask;
        }
        catch
        {
            // Ignore shutdown noise from disposed listener.
        }
        _cts.Dispose();
    }
}

internal sealed record HttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

internal sealed class HttpResponse
{
    public int StatusCode { get; init; } = 200;
    public string Body { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Content-Type"] = "text/plain; charset=utf-8"
    };

    public static HttpResponse Ok(string body) => new() { StatusCode = 200, Body = body };
    public static HttpResponse Redirect(string location)
    {
        var r = new HttpResponse { StatusCode = 302 };
        r.Headers["Location"] = location;
        return r;
    }
}
