# Captive Portal Auto Login

A .NET 8 console app that detects captive-portal interception and automatically submits the portal form.

It is designed for networks like hotel, cafe, airport, and guest Wi-Fi where normal internet requests are redirected to a login or acceptance page.

## What it does

- Probes known connectivity-check endpoints.
- Detects captive portals by:
  - Redirect status codes (3xx), or
  - Unexpected probe response content.
- Fetches and parses the login page form.
- Attempts login by:
  - Credential mode (configured username/password), or
  - Click-through mode (Accept/Connect/Continue style buttons).
- Repeats on an interval until internet access is restored or max retries are exceeded.

## Tech stack

- .NET 8 (`net8.0`)
- `HttpClient` for network probes and form submission
- `HtmlAgilityPack` for HTML form parsing
- `Microsoft.Extensions.Configuration` for JSON + environment variable config
- `Microsoft.Extensions.Hosting` + Windows Service hosting integration

## Project structure

- [Program.cs](Program.cs): app startup, config loading, and polling loop.
- [CaptivePortalDetector.cs](CaptivePortalDetector.cs): probe logic and interception detection.
- [PortalLoginHandler.cs](PortalLoginHandler.cs): form discovery, field mapping, and submission.
- [Models/PortalConfig.cs](Models/PortalConfig.cs): configuration models and defaults.
- [appsettings.json](appsettings.json): runtime configuration.

## Requirements

- .NET SDK 8.0+
- Network where captive portal behavior can be tested

## Quick start

1. Restore and build:

```bash
dotnet restore
dotnet build
```

1. Configure credentials in one of these ways:

- Preferred: environment variables
- Optional: `PortalConfig` in [appsettings.json](appsettings.json)

1. Run:

```bash
dotnet run
```

The app keeps running until Ctrl+C.

## Windows service installation (auto-start at boot)

This app can run as a native Windows Service and start automatically when Windows boots.

1. Publish for Windows:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

1. Open an elevated PowerShell or Command Prompt.

1. Install the service from the published EXE:

```powershell
.\bin\Release\net8.0\win-x64\publish\CaptivePortalAutoLogin.exe --install-service --start
```

Optional service name:

```powershell
.\CaptivePortalAutoLogin.exe --install-service --service-name MyPortalAutoLogin --start
```

Uninstall:

```powershell
.\CaptivePortalAutoLogin.exe --uninstall-service
```

Notes:

- Service install/uninstall requires administrator privileges.
- Install from the EXE output, not `dotnet run`.
- Credentials can still be supplied via environment variables (system-level variables are recommended for services).

## Command line usage

```text
CaptivePortalAutoLogin.exe
CaptivePortalAutoLogin.exe --install-service [--service-name Name] [--start]
CaptivePortalAutoLogin.exe --uninstall-service [--service-name Name]
CaptivePortalAutoLogin.exe --help
```

## Configuration

All settings are under `PortalConfig` in [appsettings.json](appsettings.json).

| Key | Type | Default | Purpose |
| --- | --- | --- | --- |
| `Username` | string | `""` | Username/email for login forms |
| `Password` | string | `""` | Password for login forms |
| `PollIntervalSeconds` | int | `10` | Delay between portal checks |
| `MaxRetries` | int | `5` | Max login attempts before exit |
| `OverrideLoginUrl` | string | `""` | Force a specific login URL |
| `ProbeEndpoints` | array | built-in list | Connectivity probes |
| `UsernameFieldHints` | array | common usernames | Field-name matching hints |
| `PasswordFieldHints` | array | common passwords | Field-name matching hints |
| `EnableWifiScanning` | bool | `false` | Scan for open APs and join them when offline |
| `BlockedSsids` | array | `[]` | SSIDs to never join (permanent deny-list) |

### Environment variable overrides

The app loads environment variables after JSON, so they override matching config keys.

- `PORTALCONFIG__USERNAME`
- `PORTALCONFIG__PASSWORD`

Per-run overrides (highest precedence in this app):

- `PORTAL_USERNAME`
- `PORTAL_PASSWORD`

PowerShell example:

```powershell
$env:PORTAL_USERNAME = "user@example.com"
$env:PORTAL_PASSWORD = "super-secret"
dotnet run
```

Bash example:

```bash
export PORTAL_USERNAME="user@example.com"
export PORTAL_PASSWORD="super-secret"
dotnet run
```

## Wi-Fi scanning

When `EnableWifiScanning` is `true`, the app automatically searches for open (unsecured) Wi-Fi access points and attempts to join them whenever no internet connectivity is detected.

### Flow

1. Probe endpoints all fail (no network) → scan for open APs.
2. For each open AP (ordered by signal strength):
   - Connect to it.
   - Probe for internet connectivity.
   - If internet is reachable → done.
   - If a captive portal is detected → attempt login.
   - If login succeeds → done.
   - Otherwise → mark AP as **do not join** for this session and try the next one.
3. Blocked SSIDs are tracked in memory for the session. Pre-configure permanent entries in `BlockedSsids`.

### Platform requirements

| Platform | Tool required |
| --- | --- |
| Windows | WLAN AutoConfig service running; administrator not required for scanning/connecting |
| Linux | `nmcli` (NetworkManager CLI) |

### Enable via appsettings.json

```json
"PortalConfig": {
  "EnableWifiScanning": true,
  "BlockedSsids": ["HotspotToAvoid", "BadCoffeeShopWifi"]
}
```

### Enable via environment variable

```powershell
$env:PORTALCONFIG__ENABLEWIFISCANNING = "true"
```

> **Note:** Blocked SSIDs reset when the app restarts unless they are listed in `BlockedSsids` in the config.

## How detection works

For each configured probe URL:

1. Send a GET request without auto-redirect.
2. If response is redirect (3xx), treat as captive portal and use redirect target.
3. If response is `200` but body does not contain expected text, treat as captive portal.
4. If body matches expected text, treat as normal internet connectivity.

## How login works

1. Fetch login page with redirect-following enabled.
2. Parse all `<form>` elements.
3. Score candidate forms.
4. Build fields dictionary including hidden fields/tokens.
5. Fill known username/password fields from config.
6. Fill unknown fields with random placeholder values.
7. Submit via GET or POST based on form method.
8. Check response body for common error phrases.

When no credentials are configured, the handler prefers click-through forms and can fall back to placeholder credentials.

## Exit codes

- `0`: clean shutdown (Ctrl+C)
- `1`: command/setup failure (for example, service installation errors)
- `2`: max retries exceeded

## Notes and limitations

- Captive portals vary a lot; some pages require JavaScript flows not covered by plain form submit.
- SSL interception, MFA, OTP, or SSO-style flows may require custom handling.
- This tool uses heuristics and may need tuning of field hints and probe endpoints per network.

## Security guidance

- Prefer environment variables over committing credentials in [appsettings.json](appsettings.json).
- Do not store real credentials in source control.
- Consider using OS secret storage or CI/CD secret injection for automated environments.

## Troubleshooting

- Increase log visibility by reviewing console output from detector and login handler.
- Add or adjust `ProbeEndpoints` when default endpoints are blocked by your network.
- Set `OverrideLoginUrl` when redirect targets are unstable.
- Tune `UsernameFieldHints` and `PasswordFieldHints` for portal-specific field names.

## Development

Run formatting/build checks:

```bash
dotnet build
```

No tests are currently included. Adding integration tests with mocked portal pages is recommended for future hardening.
