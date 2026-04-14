# Copilot Instructions for This Repository

These instructions help LLMs and coding agents make accurate, safe changes for this project.

## Project summary

- This is a .NET 8 console app for detecting captive portals and auto-submitting login forms.
- Core flow:
  1. detect portal interception,
  2. locate a login form,
  3. submit credentials or click-through action,
  4. repeat until connectivity is restored.

## Source of truth

Before changing behavior, review these files first:

- [Program.cs](../Program.cs)
- [CaptivePortalDetector.cs](../CaptivePortalDetector.cs)
- [PortalLoginHandler.cs](../PortalLoginHandler.cs)
- [Models/PortalConfig.cs](../Models/PortalConfig.cs)
- [appsettings.json](../appsettings.json)

## Change principles

- Keep changes small and focused.
- Preserve existing retry and cancellation semantics.
- Do not silently weaken security defaults.
- Prefer configuration-driven behavior over hard-coded portal-specific logic.
- Maintain compatibility with `net8.0`.

## Configuration rules

- Keep `PortalConfig` backward compatible when adding new settings.
- Add sensible defaults in [Models/PortalConfig.cs](../Models/PortalConfig.cs).
- Mirror new config keys in [appsettings.json](../appsettings.json).
- Respect environment variable overrides.

## Networking and portal logic

- Detector should avoid false positives where possible.
- Login handler should preserve hidden fields and submit method (GET/POST).
- New heuristics should be additive and not break existing field hint matching.
- Avoid assumptions that every portal supports JavaScript-free form login.

## Logging and diagnostics

- Keep logs concise and actionable.
- Prefix logs with `[Main]`, `[Detector]`, or `[Login]` style tags for consistency.
- Avoid logging secrets (passwords, tokens, full credential payloads).

## Validation checklist for LLM-generated changes

1. Build succeeds with `dotnet build`.
2. No credentials are printed in logs.
3. Retry loop and cancellation still work.
4. Config defaults remain valid with empty settings.
5. New behavior is reflected in [README.md](../README.md) when relevant.

## Good prompt patterns for this repo

- "Add a new probe endpoint config with default and docs update."
- "Improve login form scoring without changing public config shape."
- "Add structured logging around failure reasons but never expose secrets."
- "Refactor URL resolution in login handler with equivalent behavior and tests."
