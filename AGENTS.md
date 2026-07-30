# Agent Context — Jellyfin Media Integrity Scanner Plugin

## Development Environment

### Shell & Platform
- **OS:** Windows 11 with WSL2 (Ubuntu 24.04)
- **Shell:** zsh inside WSL2
- **IDE:** VS Code / Kiro connecting to WSL2 Remote
- **.NET SDK:** Not installed locally in WSL — builds run via GitHub Actions CI only
- **Git:** Configured in WSL2, pushes to GitHub via HTTPS with `gh` CLI credential helper

### Important: Shell Execution
- The workspace is accessed via WSL2 paths (`\\wsl$\Ubuntu-24.04\home\mcgarrah\github\...`)
- Shell commands must run in a Linux context (bash/zsh), not PowerShell
- `dotnet` is not available locally — do not attempt local builds
- Use `gh` CLI for GitHub API access (runs, PRs, issues)
- Set `GH_PAGER=cat` when using `gh` commands to prevent interactive pager issues

### Git Configuration
- Remote: `https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner.git`
- Default branch: `main`
- Auth: `gh auth git-credential` (GitHub CLI)
- Always use `git mv` for tracked file renames/moves
- Commit messages: conventional commits (`feat:`, `fix:`, `docs:`, `chore:`)

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

- v0.1.0 scaffold is released on GitHub
- Core implementations (Scanner, Database, API, Tasks, Events) are in `main`
- **Known CI issue:** `MediaType` and `TaskTriggerInfo` constants need updating for 10.11 API (see API Notes above)
- Integration tests pass (plugin loads in Jellyfin Docker container)
- No unit test project yet — planned for post-core-implementation

## Authorial Context

Michael McGarrah writes as a Senior Director / IT Architect / Principal Engineer. The plugin and blog content should reflect:
- Strategic framing connecting homelab work to enterprise patterns
- Architectural perspective with trade-off analysis
- Principal engineer depth with precision and mastery
- The homelab as a technology evaluation lab, not a toy
