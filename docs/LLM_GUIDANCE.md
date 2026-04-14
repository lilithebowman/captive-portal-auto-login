# LLM Guidance and Prompt Pack

This file gives practical prompts and review checklists for working on this repository with Copilot or other LLMs.

## Repository context for prompts

Paste this context into your first prompt when needed:

"This repository is a .NET 8 console app that detects captive portals using connectivity probes, then auto-submits portal forms using HtmlAgilityPack. Core files: Program.cs, CaptivePortalDetector.cs, PortalLoginHandler.cs, Models/PortalConfig.cs, appsettings.json. Preserve retry/cancellation behavior and avoid logging secrets."

## High-signal prompt templates

### 1) Bug fix prompt

"Investigate and fix a bug where portal detection loops indefinitely on successful internet. Show root cause, minimal code diff, and why behavior is correct. Validate with dotnet build."

### 2) Heuristic improvement prompt

"Improve form field matching for uncommon username/password names while keeping current defaults backward compatible. Update config model and docs only if needed."

### 3) Safety hardening prompt

"Audit logs and network handling for accidental credential leakage. Apply only minimal changes. Include before/after examples of sanitized logging."

### 4) Refactor prompt

"Refactor CaptivePortalDetector for readability without changing behavior. Keep public types and return semantics identical."

### 5) Documentation prompt

"Update README sections impacted by this change: configuration keys, runtime behavior, and troubleshooting. Keep docs concise and concrete."

## Code review prompt

"Review this diff with a production mindset. Prioritize: behavioral regressions, false-positive portal detection risk, credential safety, retry/cancellation correctness, and config compatibility. Provide findings ordered by severity with file/line references."

## Definition of done (LLM tasks)

- Build passes with `dotnet build`.
- No new secret exposure in logs or docs.
- Config changes include defaults and documentation.
- Runtime loop behavior remains deterministic.
- Diff remains focused and avoids unrelated rewrites.

## Common pitfalls to avoid

- Treating any network error as guaranteed captive portal.
- Overfitting heuristics to one portal vendor.
- Breaking no-credentials click-through mode.
- Dropping hidden form fields required by portal backends.
- Editing docs without matching implementation changes.

## Suggested workflow

1. Ask the LLM for a short plan.
2. Request a minimal patch.
3. Run `dotnet build`.
4. Request a self-review against the definition-of-done list.
5. Update README and guidance docs only where behavior changed.
