using CaptivePortalAutoLogin.Models;
using Xunit;

namespace CaptivePortalAutoLogin.Core.Tests;

public sealed class CaptivePortalDetectorTests
{
	[Fact]
	public async Task CheckAsync_ReturnsConnected_WhenProbeContentMatches()
	{
		await using var server = new TestHttpServer(req =>
			req.Path.StartsWith("/success", StringComparison.OrdinalIgnoreCase)
				? HttpResponse.Ok("success")
				: HttpResponse.Ok("other"));

		var cfg = new PortalConfig
		{
			ProbeEndpoints =
			[
				new ProbeEndpoint
				{
					Url = new Uri(server.BaseUri, "/success").ToString(),
					ExpectedContent = "success"
				}
			]
		};

		var detector = new CaptivePortalDetector(cfg);
		var result = await detector.CheckAsync();

		Assert.False(result.IsPortalDetected);
		Assert.True(result.IsConnectivityConfirmed);
		Assert.Null(result.LoginPageUrl);
	}

	[Fact]
	public async Task CheckAsync_DetectsRedirect_AsPortal()
	{
		await using var server = new TestHttpServer(_ => HttpResponse.Redirect("/login"));

		var cfg = new PortalConfig
		{
			ProbeEndpoints =
			[
				new ProbeEndpoint
				{
					Url = server.BaseUri.ToString(),
					ExpectedContent = "success"
				}
			]
		};

		var detector = new CaptivePortalDetector(cfg);
		var result = await detector.CheckAsync();

		Assert.True(result.IsPortalDetected);
		Assert.False(result.IsConnectivityConfirmed);
		Assert.Equal("/login", result.LoginPageUrl);
	}

	[Fact]
	public async Task CheckAsync_DetectsContentMismatch_AsPortal()
	{
		await using var server = new TestHttpServer(_ => HttpResponse.Ok("unexpected portal html"));

		var cfg = new PortalConfig
		{
			ProbeEndpoints =
			[
				new ProbeEndpoint
				{
					Url = server.BaseUri.ToString(),
					ExpectedContent = "success"
				}
			]
		};

		var detector = new CaptivePortalDetector(cfg);
		var result = await detector.CheckAsync();

		Assert.True(result.IsPortalDetected);
		Assert.False(result.IsConnectivityConfirmed);
		Assert.Equal(server.BaseUri.ToString(), result.LoginPageUrl);
	}
}
