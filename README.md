# Jellyfin Media Integrity Scanner

A [Jellyfin](https://jellyfin.org/) plugin that validates media file integrity using FFmpeg. Detects corrupt, truncated, and damaged files in your library without impacting playback performance.

## Status

✅ **Functional** — Two-phase scanning, the REST API, the admin dashboard, the in-app settings page, update checking, and library event hooks are all implemented and covered by 154 unit tests, a Docker-based integration test suite (including a good/bad video corruption matrix), and a Playwright suite that drives the admin dashboard and settings pages through a real browser session. See [CODE_REVIEW.md](CODE_REVIEW.md) for the detailed change history.

## Features

- **Two-phase scanning** — Fast header/metadata checks via `ffprobe`, with opt-in deep byte-stream decode via `ffmpeg`
- **Production-safe throttling** — Configurable read-rate cap, inter-file delays, automatic pause during active playback, and an optional quiet-hours window
- **Persistent state** — SQLite database tracks scan history so rescans are incremental
- **Event-driven** — Hooks into Jellyfin library events to scan new files and purge records on delete
- **Admin dashboard** — HTML dashboard showing library health (total/passed/failed/errored/pending) at a glance
- **In-app settings page** — Every setting below is editable from **Dashboard → Plugins → Media Integrity Scanner → Settings**, no config-file editing required
- **REST API** — Query scan results (with status and per-library filtering), trigger scans, and check status programmatically
- **Update checking** — Detects newer stable or development-channel releases via Jellyfin's own plugin installation mechanism, with a one-click install from the dashboard
- **Cross-platform** — A single release package works on Linux (including musl-based containers/NAS), Windows, and macOS, on both x64 and ARM — wherever Jellyfin and FFmpeg are available

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

**Why the build output includes a `meta.json` and a `runtimes/` folder for every real Jellyfin server platform:** Jellyfin's plugin loader recursively loads every `.dll` it finds under a plugin's folder as a managed assembly by default, which breaks on the native SQLite binaries this plugin depends on (see [CODE_REVIEW.md](CODE_REVIEW.md#sixteenth-pass-a-real-packaging-bug-found-only-by-actually-installing-a-release) for the full story). Shipping a `meta.json` with an explicit `assemblies` whitelist tells Jellyfin to load only the real managed DLLs, so the native binaries for every platform (Windows/Linux/macOS, x64/ARM) can safely ship in one package — Jellyfin's own `.NET` runtime resolves the correct one for whatever platform it's actually running on. Android/iOS/browser-wasm native binaries are trimmed post-build since Jellyfin's server never runs on those.

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
| UpdateChannel | Stable | Which release channel to check for updates against (`Stable` or `Development`) |
| StableManifestUrl | *(this repo's `manifest.json`)* | Used to classify a discovered version as stable — see [Checking for Updates](#checking-for-updates) |
| DevManifestUrl | *(this repo's `manifest-unstable.json`)* | Used to classify a discovered version as a development build |

## Checking for Updates

The dashboard shows the currently running version and, when a newer one is available for your configured channel, an "Update Available" banner with a one-click **Update Now** button. This works by calling Jellyfin's own plugin installation API — the same mechanism Dashboard > Plugins > Catalog uses — rather than reimplementing download/install logic.

**One-time setup required**: Jellyfin can only discover plugin versions from repositories you've registered yourself under **Dashboard → Plugins → Repositories**. Add whichever channel(s) you want:

| Channel | Repository URL |
|---------|----------------|
| Stable | `https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest.json` |
| Development | `https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest-unstable.json` |

Then set **Update Channel** on the settings page to `Stable` or `Development`. Development builds are cut automatically from the tip of `main` on every push (see `release-dev.yml`) and are not guaranteed stable.

Installing an update stages the new version on disk; Jellyfin needs a restart to actually load it (the dashboard's banner tells you this after a successful install).

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
│   │   ├── DeepScanTask.cs              # Phase 2 scheduled task
│   │   └── CheckForUpdatesTask.cs       # Daily update-status refresh
│   ├── EventHandlers/
│   │   └── LibraryMonitor.cs            # Library event hooks
│   ├── Updates/
│   │   ├── IUpdateChecker.cs            # Update-checker interface
│   │   ├── UpdateChecker.cs             # Wraps Jellyfin's IInstallationManager
│   │   ├── UpdateChannel.cs             # Stable/Development enum
│   │   └── UpdateStatus.cs              # Update-status response model
│   ├── Api/
│   │   └── MediaIntegrityController.cs  # REST API
│   ├── Web/
│   │   ├── integrity_dashboard.html     # Admin dashboard
│   │   └── integrity_settings.html      # Settings page
│   └── meta.json                        # Bundled plugin manifest (assemblies whitelist -- see below)
├── tests/
│   ├── Jellyfin.Plugin.MediaIntegrityScanner.Tests/  # xUnit unit tests (154 tests)
│   ├── docker-compose.integration.yml   # Integration test Jellyfin instance
│   ├── generate-test-media.sh           # Good/bad video corruption matrix generator
│   ├── setup-jellyfin.sh                # Shared Jellyfin bring-up (sourced by the below + Playwright)
│   ├── run-integration-tests.sh         # Integration test runner (curl-based)
│   └── playwright/                      # Real-browser E2E suite (dashboard + settings pages)
├── scripts/
│   └── update-manifest.py               # Bumps a manifest.json on a tagged/dev release
├── Jellyfin.Plugin.MediaIntegrityScanner.csproj
├── Jellyfin.Plugin.MediaIntegrityScanner.sln
├── Directory.Build.props
├── manifest.json                        # Stable-channel repository manifest
├── manifest-unstable.json               # Development-channel repository manifest
├── .github/workflows/
│   ├── build.yml                        # Build + unit tests on every push/PR
│   ├── integration-test.yml             # Docker-based integration test
│   ├── playwright-e2e.yml               # Real-browser E2E suite (separate, non-blocking)
│   ├── release.yml                      # Tagged release + manifest.json automation
│   └── release-dev.yml                  # Dev-channel pre-release on every push to main
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

154 unit tests cover the scan engine, database layer, REST API, config throttling logic, update-checker channel classification, and FFmpeg process handling — see [CODE_REVIEW.md](CODE_REVIEW.md) for what's covered and the deliberate scope boundaries (e.g., actual ffmpeg/ffprobe argument behavior is left to the integration suite below).

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

The test script sources `tests/setup-jellyfin.sh`, which:
- Copies the built plugin DLLs into the Jellyfin config directory
- Generates the good/bad test-media matrix via `tests/generate-test-media.sh` if it doesn't exist yet (two valid files, five corrupted in distinct, verified-for-real ways — see that script's header comment for the full pass/fail table)
- Waits for Jellyfin to become healthy
- Completes the startup wizard via API
- Authenticates and creates a test media library, waiting for all 7 items to be discovered

`run-integration-tests.sh` then runs its own curl-based assertions on top: plugin-loaded/config-endpoint checks, a settings round-trip, both web pages being served, a full scan-and-verify flow against the known corruption matrix, item-detail lookups, an item-scoped deep scan (proving the two-phase Header/FullDecode split actually catches different things), and the cancel endpoint.

**Important ordering note:** Jellyfin loads plugins once, early in its own startup, and copying the plugin DLL into the bind-mounted config directory *after* the container has already started is a no-op until the container is restarted. `setup-jellyfin.sh`'s own file-copy step runs after step 2's `docker compose up` above, which can race Jellyfin's own startup — if `run-integration-tests.sh` fails with the plugin GUID missing or every `/MediaIntegrity/*` route 404ing, run `docker restart jellyfin-integration-test` once and re-run the script. CI sidesteps this entirely by copying the DLL in before the container's first start (see `.github/workflows/integration-test.yml`/`playwright-e2e.yml`).

The same workflow runs automatically in CI on every push to `main`/`dev` and on pull requests (see `.github/workflows/integration-test.yml`).

### End-to-End Testing with Playwright

A [Playwright](https://playwright.dev/) suite (`tests/playwright/`) drives the admin dashboard and settings pages through a real Chromium session — logging in via the actual web login form, triggering a real scan, and asserting the UI reflects it — rather than curl+grep, which never executes a page's own JavaScript or its real `ApiClient`-backed session.

```bash
# 1-2. Same as above: build the plugin, bring up Jellyfin with tests/setup-jellyfin.sh
dotnet publish Jellyfin.Plugin.MediaIntegrityScanner/Jellyfin.Plugin.MediaIntegrityScanner.csproj --configuration Release --output ./publish
docker compose -f tests/docker-compose.integration.yml up -d
bash tests/setup-jellyfin.sh

# 3. Install and run the suite
cd tests/playwright
npm ci
npx playwright install --with-deps chromium
npx playwright test
```

This runs in its own `playwright-e2e.yml` CI workflow, deliberately kept separate from `build.yml`/`integration-test.yml`: a real-browser suite is slower and more prone to environmental flakiness than a curl-based check, so a Playwright failure doesn't block those other checks.

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
