# Agent Context — Jellyfin Media Integrity Scanner Plugin

## Development Environment

### Shell & Platform
- **Host OS:** Windows 11 — but you are NOT running in Windows or PowerShell
- **Execution environment:** WSL2 (Ubuntu 24.04 LTS) — this is a Linux environment
- **Shell:** zsh inside WSL2 (bash also available)
- **IDE:** VS Code / Kiro connecting via WSL2 Remote extension
- **.NET SDK:** Not installed in WSL — builds run via GitHub Actions CI or the LXC build environment
- **Git:** Configured in WSL2, pushes to GitHub via HTTPS with `gh` CLI credential helper

### Important: Shell Execution
- **You are in Linux (Ubuntu 24.04).** Use Linux commands, paths, and tooling.
- Do NOT use PowerShell, `cmd.exe`, or Windows-style paths in commands.
- The IDE may present paths as `\\wsl$\Ubuntu-24.04\home\mcgarrah\github\...` — these are Windows UNC paths for the IDE's file access. Shell commands use native Linux paths: `/home/mcgarrah/github/...`
- Do NOT prefix commands with `bash -c` — the shell is already bash/zsh.
- `dotnet` is not available in WSL — do not attempt local builds here.
- Use `gh` CLI for GitHub API access (runs, PRs, issues).
- Set `GH_PAGER=cat` when using `gh` commands to prevent interactive pager issues.

### Build Environment: Proxmox LXC (Debian 13)
- A dedicated Proxmox LXC container (Debian 13 / Trixie) serves as the .NET 9 build and integration test environment.
- This container has `dotnet` SDK 9.0, `jellyfin-ffmpeg`, and a test Jellyfin instance.
- SSH access from WSL to the LXC is available for remote builds when needed.
- GitHub Actions CI (Ubuntu runners) remains the primary build verification path.

### Git Configuration
- Remote: `https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner.git`
- Default branch: `main`
- Auth: `gh auth git-credential` (GitHub CLI)
- Always use `git mv` for tracked file renames/moves
- Commit messages: conventional commits (`feat:`, `fix:`, `docs:`, `chore:`)

## Agentic Development Process

The toolchain for this project: **Claude Code** (primary, this session) and
likely **Codex** from the LXC, both tied into GitHub automation. **Kiro** (via
the IDE) is used to spot-check work but `kiro-cli` doesn't work, so Kiro stays
outside the automated loop — treat its input as a manual review pass, not
something CI or an agent workflow can invoke. **Amazon Q** and **Gemini** were
evaluated (dotfiles for both exist on this host) but aren't part of the active
toolchain.

The practices below were consolidated from two sibling projects —
[`light-bringer`](/opt/light-bringer) (a Godot game, ~572+ GUT tests, heavy
autonomous-agent-driven balance testing) and [`nutrition_api`](/opt/nutrition_api)
(a FastAPI service) — where the same agentic workflow has been run longer and
at higher volume. Treat this as a living section: fold in new lessons as this
project's own agentic history grows, the same way `CODE_REVIEW.md`'s
pass-by-pass narrative already does for bugs.

### What actually transfers here

- **Branch off `main`, never commit straight to it; PR title under 70 chars;
  PR body states a test plan.** Already this project's convention — both
  sibling repos independently converged on the same rule, which is a good sign
  it's not arbitrary.
- **A living numbered-pass review document is worth the upkeep.** `light-bringer`'s
  `docs/reference/ARCHITECTURE_REVIEW_2026-07.md` splits findings into **"Real
  bugs"** (wrong behavior today) vs. **"Architecture drift / future traps"**
  (not wrong yet, but two sources of truth that will diverge) — a sharper cut
  than `CODE_REVIEW.md`'s current single "Known Remaining Issues" bucket.
  Consider that split next time this doc gets a pass.
- **A known-failures ledger, not just a passing/failing count.** `light-bringer`'s
  `docs/reference/KNOWN_TEST_FAILURES.md` records root cause + fix + any
  follow-up hardening for every failure ever seen, including environment-specific
  ones ("Proxmox shared filesystem bind-mount adds ~100ms per write vs. local
  SSD — widen the timing threshold, don't chase a phantom regression"). This
  project already does this instinctively inside `CODE_REVIEW.md` (the
  ffmpeg-on-PATH CI check, the Docker-container-reuse false failure, the
  auto-scan-on-add race) — keep doing it, and keep the environment-specific
  reasoning explicit rather than just bumping a timeout with no comment.
- **A pre-flight/smoke check before the real test suite, for anything with a
  "compiles but doesn't actually load" failure mode.** `light-bringer`'s Phase 41
  (`./bin/check-project-loads.sh`) exists because "tests pass but the editor
  crashes" was a real, recurring failure class GUT alone didn't catch. This
  project's rough equivalent is `dotnet build` before `dotnet test` — already
  the habit here; the transferable idea is naming *why* a cheap check runs
  first, not just that it does.
- **Two-tier dependency scanning: PR-blocking + a separate scheduled sweep.**
  `nutrition_api`'s `dependency-audit.yml` runs `pip-audit`/`npm audit` weekly
  against whatever is on `main`, specifically because a CVE disclosed against
  an already-merged, already-pinned dependency needs no code change to appear
  — nothing would ever trigger the PR-blocking scan to notice it. **This
  project has no dependency vulnerability scanning at all, PR-blocking or
  scheduled** — worth adding as a `dotnet list package --vulnerable` (or
  equivalent NuGet audit) step, mirroring that split.
- **Shell/tool-use friction avoidance.** `light-bringer`'s `CLAUDE.md` calls
  out: never prefix a command with `cd <project-root>; …` when the working
  directory is already there — a leading `cd` plus a compound/redirection can
  trip permission-prompt heuristics for no benefit. Prefer separate tool calls
  over chained `a; b; c` one-liners, and read a file directly rather than
  piping a large output through `grep`. This project's own system prompt
  already carries an equivalent warning; treat it as confirmed, not
  hypothetical — two independent projects hit the same friction.
- **A meta-instruction to reduce wrong-assumption rework.** `light-bringer`'s
  `CLAUDE.md` opens with: *"Before you answer, tell me what you need to know
  to answer well, and point out any assumptions you'd otherwise make."* Worth
  invoking explicitly on ambiguous asks in this project too, rather than
  guessing and redoing work.
- **`claude/<slug>` branch naming for agent-originated work**, distinct from
  the human's own `feature/`-style branches, appears in `nutrition_api`'s
  history (e.g. `claude/exclusion-counts`, `claude/usda-fdc-0.2.0`). This
  project currently names every branch by topic (`fix/`, `test/`, `docs/`,
  `refactor/`) regardless of who originated it, which is fine as long as it
  stays consistent — flagging the alternative in case multi-agent attribution
  (Claude vs. Codex vs. human) becomes useful to distinguish at a glance once
  Codex is actually wired in.

### GitHub automation — available, not yet wired up here

`light-bringer` runs two Claude Code GitHub Actions this project doesn't have:

```yaml
# .github/workflows/claude.yml — responds to @claude mentions in issues/PR comments/reviews
on:
  issue_comment: { types: [created] }
  pull_request_review_comment: { types: [created] }
  issues: { types: [opened, assigned] }
  pull_request_review: { types: [submitted] }
# uses: anthropics/claude-code-action@v1, gated on the comment/body containing "@claude"
```

```yaml
# .github/workflows/claude-code-review.yml — auto-reviews every PR on open/sync
on:
  pull_request: { types: [opened, synchronize, ready_for_review, reopened] }
# uses: anthropics/claude-code-action@v1 with plugins: 'code-review@claude-code-plugins'
```

Both need a `CLAUDE_CODE_OAUTH_TOKEN` repo secret, which is the user's call to
provision, not something to add unilaterally. If/when Codex joins the GitHub
automation for this repo (per the toolchain note above), these two workflows
are the concrete, already-proven pattern to adapt rather than designing one
from scratch — ask before adding either file for real.

### What does *not* transfer

- `light-bringer`'s `docs/core/AUTONOMOUS_AGENT_SYSTEM.md` and Godot MCP
  workflows are about an **in-game AI agent** used for automated playtesting
  and a live editor-inspection bridge — a different kind of "agent" than the
  Claude/Codex/Kiro dev-tooling this section is about. The transferable idea
  (reproducible, seeded, watchdog-bounded automated runs generating objective
  metrics) doesn't map onto a media-integrity scanner; skip re-deriving it here.
- `nutrition_api`'s NOTES.md pattern of enumerating **sibling repos under the
  same author's control** (with an explicit "fix upstream, don't work around
  it here" rule) doesn't apply — this plugin's only sibling relationship is
  the `mcgarrah.github.io` blog repo, which is documentation, not a library
  dependency. Worth revisiting this note only if this plugin ever gains a
  dependency on another `mcgarrah`-owned package.

## Project Overview

- **Plugin name:** Media Integrity Scanner
- **GUID:** `c8f4a3b2-1d5e-4f6a-9b7c-2e8d0f1a3b5c` (never change this)
- **Target:** Jellyfin 10.11+ / .NET 9
- **License:** GPL-2.0-or-later (matches Jellyfin server)
- **Namespace:** `Jellyfin.Plugin.MediaIntegrityScanner`
- **NuGet packages:** `Jellyfin.Controller` 10.11.*, `Jellyfin.Model` 10.11.*

## Jellyfin 10.11 API Notes

### Removed/Changed APIs
- **`IServerEntryPoint`** — Removed in 10.11. Use `IHostedService` from `Microsoft.Extensions.Hosting` instead. Register with `serviceCollection.AddHostedService<T>()`.
- **`MediaType`** — Now in `MediaBrowser.Model.Entities` as a static class with string constants. Must add `using MediaBrowser.Model.Entities;` to use `MediaType.Video` and `MediaType.Audio`.
- **`TaskTriggerInfo.TriggerDaily`** — Constant name changed in 10.11. Use the string `"DailyTrigger"` for the `Type` property.
- **`TaskTriggerInfo.TriggerWeekly`** — Use the string `"WeeklyTrigger"` for the `Type` property.
- **`IPluginServiceRegistrator`** — This is the correct DI registration interface for 10.11 plugins. Located in `MediaBrowser.Controller.Plugins`.

### Correct Patterns for 10.11
```csharp
// Hosted service (replaces IServerEntryPoint)
using Microsoft.Extensions.Hosting;
public class LibraryMonitor : IHostedService, IDisposable { ... }

// DI registration
serviceCollection.AddHostedService<LibraryMonitor>();

// MediaType usage
using MediaBrowser.Model.Entities;
var query = new InternalItemsQuery
{
    MediaTypes = new[] { MediaType.Video, MediaType.Audio },
    IsVirtualItem = false
};

// Scheduled task triggers
new TaskTriggerInfo { Type = "DailyTrigger", TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
new TaskTriggerInfo { Type = "WeeklyTrigger", DayOfWeek = DayOfWeek.Sunday, TimeOfDayTicks = TimeSpan.FromHours(1).Ticks }
```

### Key References
- Plugin template: https://github.com/jellyfin/jellyfin-plugin-template
- Jellyfin Controller NuGet: https://www.nuget.org/packages/Jellyfin.Controller
- Jellyfin source (for API verification): https://github.com/jellyfin/jellyfin

## Architecture

```
Jellyfin.Plugin.MediaIntegrityScanner/
├── Plugin.cs                          # Entry point, IHasWebPages
├── PluginConfiguration.cs             # User settings model
├── PluginServiceRegistrator.cs        # DI registration (IPluginServiceRegistrator)
├── Scanner/
│   ├── IScanEngine.cs                 # Scanner interface
│   ├── ScanEngine.cs                  # Bounded concurrent scanner
│   ├── FfmpegWrapper.cs              # FFmpeg/ffprobe process execution
│   ├── FfmpegResolver.cs            # Cross-platform binary resolution
│   └── ScanResult.cs                 # Result model + enums (ScanPhase, ScanStatus)
├── Data/
│   ├── IDatabaseManager.cs           # Database interface
│   ├── SqliteDatabaseManager.cs      # SQLite implementation (WAL mode)
│   └── Models/
│       └── ScanRecord.cs             # Database entity
├── ScheduledTasks/
│   ├── HeaderScanTask.cs             # Phase 1 daily scan (ffprobe)
│   └── DeepScanTask.cs              # Phase 2 weekly scan (ffmpeg full decode)
├── EventHandlers/
│   └── LibraryMonitor.cs            # IHostedService, ItemAdded/ItemRemoved hooks
├── Api/
│   └── MediaIntegrityController.cs   # REST API + request/response models
└── Web/
    └── integrity_dashboard.html      # Admin UI (embedded resource)
```

## CI/CD

### GitHub Actions Workflows
- **build.yml** — Builds on push to `main`/`dev` and PRs. Uses .NET 9 SDK on ubuntu-latest.
- **release.yml** — Triggers on `v*` tags. Builds, packages zip, creates GitHub Release.
- **integration-test.yml** — Spins up Jellyfin Docker container, installs plugin DLL, validates loading.

### Build Verification
Since `dotnet` is not installed locally, all build verification happens via CI:
1. Push changes to GitHub
2. Check workflow status: `GH_PAGER=cat gh run list --limit 3`
3. View errors: `GH_PAGER=cat gh run view <run-id> --log-failed 2>&1 | grep "error CS"`

## Code Standards

### File Headers
All `.cs` files include the GPL-2.0-or-later copyright header:
```csharp
// Jellyfin Media Integrity Scanner - validates media file integrity using FFmpeg
// Copyright (C) 2026  Michael McGarrah <mcgarrah@gmail.com>
//
// This program is free software; you can redistribute it and/or modify
// ...
```

### C# Conventions
- XML doc comments on all public members
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- `ConfigureAwait(false)` on all awaited calls (library code)
- Use `ArgumentList` (not string interpolation) for process arguments to prevent injection
- Parameterized queries for all SQLite operations

### Analysis
- `<AnalysisLevel>latest-recommended</AnalysisLevel>` in Directory.Build.props
- Documentation XML generation enabled
- Treat build output as guidance — fix warnings where practical

## Related Blog Series

The plugin is documented in a 5-part series at mcgarrah.github.io:
1. Introduction & Problem Statement
2. Architecture & Design Decisions
3. Building the Scanner Core
4. The Dashboard & API
5. Deployment & Operations

All articles are in `_drafts/2026-07-29-jellyfin-media-integrity-*.md` in the blog repo.

## Current Status

- Functional: two-phase scanning, REST API, admin dashboard, in-app settings page, and library event hooks are all implemented and merged to `main`.
- 141 unit tests + a Docker-based integration test suite (`tests/run-integration-tests.sh`, mirrored in `.github/workflows/integration-test.yml`), both green.
- See `CODE_REVIEW.md` (kept untracked/local, not committed) for the full pass-by-pass history and current known-remaining-issues list.

## Authorial Context

Michael McGarrah writes as a Senior Director / IT Architect / Principal Engineer. The plugin and blog content should reflect:
- Strategic framing connecting homelab work to enterprise patterns
- Architectural perspective with trade-off analysis
- Principal engineer depth with precision and mastery
- The homelab as a technology evaluation lab, not a toy
