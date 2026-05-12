using CaptivePortalAutoLogin.Models;

namespace CaptivePortalAutoLogin.Core.Tests;

public sealed class PortalConfigTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        var cfg = new PortalConfig();

        Assert.Equal(10, cfg.PollIntervalSeconds);
        Assert.Equal(5, cfg.MaxRetries);
        Assert.False(cfg.EnableWifiScanning);
        Assert.Equal(3, cfg.ProbeEndpoints.Count);
        Assert.Contains(cfg.ProbeEndpoints, p => p.Url.Contains("msftconnecttest", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("username", cfg.UsernameFieldHints, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("password", cfg.PasswordFieldHints, StringComparer.OrdinalIgnoreCase);
    }
}
