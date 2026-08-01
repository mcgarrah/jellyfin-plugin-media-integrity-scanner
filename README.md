# Jellyfin Media Integrity Scanner

A [Jellyfin](https://jellyfin.org/) plugin that validates media file integrity using FFmpeg. Detects corrupt, truncated, and damaged files in your library without impacting playback performance.

## Status

✅ **Functional** — Two-phase scanning, the REST API, the admin dashboard, the in-app settings page, and library event hooks are all implemented and covered by 112 unit tests plus a Docker-based integration test suite. See [CODE_REVIEW.md](CODE_REVIEW.md) for the detailed change history.

## Features

- **Two-phase scanning** — Fast header/metadata checks via `ffprobe`, with opt-in deep byte-stream decode via `ffmpeg`
- **Production-safe throttling** — Configurable read-rate cap, inter-file delays, automatic pause during active playback, and an optional quiet-hours window
- **Persistent state** — SQLite database tracks scan history so rescans are incremental
- **Event-driven** — Hooks into Jellyfin library events to scan new files and purge records on delete
- **Admin dashboard** — HTML dashboard showing library health (total/passed/failed/errored/pending) at a glance
- **In-app settings page** — Every setting below is editable from **Dashboard → Plugins → Media Integrity Scanner → Settings**, no config-file editing required
- **REST API** — Query scan results (with status and per-library filtering), trigger scans, and check status programmatically
- **Cross-platform** — Runs on Linux, Windows, and macOS wherever Jellyfin and FFmpeg are available

## Architecture

```
┌─────────────────────────────────────────────────┐
│                 Jellyfin Server                   │
├─────────────────────────────────────────────────┤
│  ┌───────────────────────────────────────────┐  │
│  │     Media Integrity Scanner Plugin         │  │
│  ├───────────────────────────────────────────┤  │
│  │  Library Event Monitor                     │  │
│  │    ├── OnItemAdded → Queue for scan       │  │
│  │    └── OnItemRemoved → Purge records      │  │
│  ├───────────────────────────────────────────┤  │
│  │  Scan Engine (Bounded, Thread-Safe)        │  │
│  │    ├── Phase 1: Header/metadata check     │  │
│  │    ├── Phase 2: Full stream decode        │  │
│  │    └── Read-rate / quiet-hours throttle   │  │
│  ├───────────────────────────────────────────┤  │
│  │  SQLite Cache + REST API + Dashboard      │  │
│  └───────────────────────────────────────────┘  │
└─────────────────────────────────────────────────┘
         │                           │
         ▼                           ▼
  ┌─────────────┐            ┌──────────────┐
  │   FFmpeg    │            │  Media Files  │
  │  (decode)   │            │  (read-only)  │
  └─────────────┘            └──────────────┘
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full event-flow diagrams — every Jellyfin event, scheduled task, and API call that can trigger a scan, the gate pipeline each scan runs through, and two worked scenarios.

## Requirements

- Jellyfin 10.11+
- .NET 9 Runtime (included with Jellyfin 10.11+)
- FFmpeg (typically bundled with Jellyfin as `jellyfin-ffmpeg`)

## Installation

Via custom plugin repository:

1. **Dashboard → Plugins → Repositories → Add**
2. **Name:** `mcgarrah-plugins`
3. **URL:** `https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest.json`
4. **Save** → **Catalog** → Install **Media Integrity Scanner**
5. **Restart Jellyfin**

See [INSTALL.md](INSTALL.md) for manual installation methods, Proxmox LXC notes, uninstall steps, and troubleshooting.

## Building from Source

Prerequisites:
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

```bash
git clone https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner.git
cd jellyfin-plugin-media-integrity-scanner

dotnet restore
dotnet build --configuration Release
dotnet publish Jellyfin.Plugin.MediaIntegrityScanner/Jellyfin.Plugin.MediaIntegrityScanner.csproj --configuration Release --output ./artifacts
```

Copy the contents of `./artifacts` to your Jellyfin plugins directory:
- **Linux:** `/var/lib/jellyfin/plugins/MediaIntegrityScanner/`
- **Windows:** `%PROGRAMDATA%\Jellyfin\Server\plugins\MediaIntegrityScanner\`
- **Docker:** `/config/plugins/MediaIntegrityScanner/`

Restart Jellyfin after installation.

## Configuration

Configure via **Dashboard → Plugins → Media Integrity Scanner**, then the **Settings »** link (or directly at **Dashboard → Plugins → Media Integrity Scanner Settings**):

| Setting | Default | Description |
|---------|---------|-------------|
| MaxConcurrentScans | 1 | Number of files scanned simultaneously |
| DelayBetweenFilesMs | 5000 | Pause (ms) between scanning each file |
| MaxReadRateMbPerSec | 10 | Average I/O rate cap for scanning, in MB/s |
| PauseDuringPlayback | true | Stop scanning when users are streaming |
| EnableDeepScan | false | Enable Phase 2 full byte-stream decode |
| UseQuietHoursOnly | false | Restrict scanning to the quiet-hours window below |
| QuietHoursStart | 02:00 | Beginning of the scan window (HH:mm) |
| QuietHoursEnd | 06:00 | End of the scan window (HH:mm) |
| FfmpegPathOverride | *(none)* | Explicit path to the `ffmpeg` binary, if auto-detection picks the wrong one |
| FfprobePathOverride | *(none)* | Explicit path to the `ffprobe` binary, if auto-detection picks the wrong one |
| ScanOnItemAdded | true | Auto-scan newly imported files |
| PurgeOnItemRemoved | true | Delete scan records when the corresponding library item is removed |

## Project Structure

```
jellyfin-plugin-media-integrity-scanner/
├── Jellyfin.Plugin.MediaIntegrityScanner/
│   ├── Plugin.cs                        # Plugin entry point
│   ├── PluginConfiguration.cs           # Settings model
│   ├── PluginServiceRegistrator.cs      # DI registration
│   ├── AssemblyInfo.cs                  # InternalsVisibleTo (test assembly)
│   ├── Scanner/
│   │   ├── IScanEngine.cs               # Scanner interface
│   │   ├── ScanEngine.cs                # Bounded scan orchestrator
│   │   ├── ScanThrottle.cs              # Quiet-hours + read-rate pacing (pure logic)
│   │   ├── FfmpegWrapper.cs             # FFmpeg process management
│   │   ├── FfmpegResolver.cs            # Cross-platform binary finder
│   │   └── ScanResult.cs                # Result model
│   ├── Data/
│   │   ├── IDatabaseManager.cs          # Database interface
│   │   ├── SqliteDatabaseManager.cs     # SQLite implementation
│   │   └── Models/
│   │       └── ScanRecord.cs            # DB entity
│   ├── ScheduledTasks/
│   │   ├── HeaderScanTask.cs            # Phase 1 scheduled task
│   │   └── DeepScanTask.cs              # Phase 2 scheduled task
│   ├── EventHandlers/
│   │   └── LibraryMonitor.cs            # Library event hooks
│   ├── Api/
│   │   └── MediaIntegrityController.cs  # REST API
│   └── Web/
│       ├── integrity_dashboard.html     # Admin dashboard
│       └── integrity_settings.html      # Settings page
├── tests/
│   ├── Jellyfin.Plugin.MediaIntegrityScanner.Tests/  # xUnit unit tests (112 tests)
│   ├── docker-compose.integration.yml   # Integration test Jellyfin instance
│   └── run-integration-tests.sh         # Integration test runner
├── scripts/
│   └── update-manifest.py               # Bumps manifest.json on tagged release
├── Jellyfin.Plugin.MediaIntegrityScanner.csproj
├── Jellyfin.Plugin.MediaIntegrityScanner.sln
├── Directory.Build.props
├── manifest.json
├── .github/workflows/
│   ├── build.yml                        # Build + unit tests on every push/PR
│   ├── integration-test.yml             # Docker-based integration test
│   └── release.yml                      # Tagged release + manifest.json automation
├── .editorconfig
├── .gitignore
├── LICENSE
└── README.md
```

## Development

### Build Environment

CI runs on GitHub-hosted Ubuntu runners. A dedicated Proxmox LXC container (Debian, .NET 9 SDK, `jellyfin-ffmpeg`, and a test Jellyfin instance) is also available for local build/integration verification.

### Running Tests

```bash
dotnet test
```

112 unit tests cover the scan engine, database layer, REST API, config throttling logic, and FFmpeg process handling — see [CODE_REVIEW.md](CODE_REVIEW.md) for what's covered and the deliberate scope boundaries (e.g., actual ffmpeg/ffprobe argument behavior is left to the integration suite below).

### Local Development Workflow

1. Build the plugin: `dotnet publish Jellyfin.Plugin.MediaIntegrityScanner/Jellyfin.Plugin.MediaIntegrityScanner.csproj -c Debug -o ./publish`
2. Copy to Jellyfin plugins directory
3. Restart Jellyfin
4. Check **Dashboard → Plugins** for the plugin
5. View logs: `journalctl -u jellyfin -f | grep MediaIntegrity`

### Integration Testing with Docker

A Docker-based integration test setup validates that the plugin loads correctly in a real Jellyfin instance. This mirrors what runs in CI via GitHub Actions.

**Prerequisites:** Docker, docker compose, ffmpeg (for generating test media)

```bash
# 1. Build the plugin
dotnet publish Jellyfin.Plugin.MediaIntegrityScanner/Jellyfin.Plugin.MediaIntegrityScanner.csproj --configuration Release --output ./publish

# 2. Start Jellyfin with the plugin loaded
docker compose -f tests/docker-compose.integration.yml up -d

# 3. Run the integration test suite
./tests/run-integration-tests.sh

# 4. Tear down when done
docker compose -f tests/docker-compose.integration.yml down -v
```

The test script will:
- Copy the built plugin DLLs into the Jellyfin config directory
- Generate a small test video if one doesn't exist
- Wait for Jellyfin to become healthy
- Complete the startup wizard via API
- Authenticate and verify the plugin is loaded (by GUID)
- Check the plugin configuration endpoint
- Verify FFmpeg is available inside the container
- Create a test media library and confirm items are discovered

The same workflow runs automatically in CI on every push to `main`/`dev` and on pull requests (see `.github/workflows/integration-test.yml`).

## Blog Series

This project is documented in a series of articles at [mcgarrah.github.io](https://mcgarrah.github.io):

1. [Introduction & Problem Statement](/jellyfin-media-integrity-scanner-introduction/)
2. [Architecture & Design Decisions](/jellyfin-media-integrity-architecture-design/)
3. [Building the Scanner Core](/jellyfin-media-integrity-scanner-core/)
4. [The Dashboard & API](/jellyfin-media-integrity-dashboard-api/)
5. [Deployment & Operations](/jellyfin-media-integrity-deployment-operations/)

## Contributing

Contributions welcome. Please open an issue to discuss before submitting PRs.

## License

Copyright © 2026 Michael McGarrah &lt;mcgarrah@gmail.com&gt;

This project is licensed under the [GNU General Public License v2.0 or later](LICENSE) — the same license as [Jellyfin server](https://github.com/jellyfin/jellyfin), chosen to keep the door open for potential inclusion as a core Jellyfin plugin.

## Acknowledgments

- [Jellyfin](https://jellyfin.org/) — The free software media system
- [jellyfin-plugin-template](https://github.com/jellyfin/jellyfin-plugin-template) — Plugin scaffolding reference
- [jellyfin-plugin-media-analyzer](https://github.com/endrl/jellyfin-plugin-media-analyzer) — Inspiration for media analysis within Jellyfin
- [FFmpeg](https://ffmpeg.org/) — The multimedia framework powering the integrity checks
