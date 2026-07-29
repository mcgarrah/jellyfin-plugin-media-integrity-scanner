# Jellyfin Media Integrity Scanner

A [Jellyfin](https://jellyfin.org/) plugin that validates media file integrity using FFmpeg. Detects corrupt, truncated, and damaged files in your library without impacting playback performance.

## Status

🚧 **Early Development** — This plugin is not yet functional. The project structure and architecture are being established. A dedicated .NET 8 build environment (Proxmox LXC) is being provisioned for development and CI.

## Features (Planned)

- **Two-phase scanning** — Fast header/metadata checks via `ffprobe`, with opt-in deep byte-stream decode via `ffmpeg`
- **Production-safe throttling** — Configurable I/O limits, inter-file delays, and automatic pause during active playback
- **Persistent state** — SQLite database tracks scan history so rescans are incremental
- **Event-driven** — Hooks into Jellyfin library events to scan new files and clean up on delete
- **Admin dashboard** — HTML dashboard showing library health at a glance
- **REST API** — Query scan results, trigger scans, and check status programmatically
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
│  │    ├── OnItemUpdated → Re-queue           │  │
│  │    └── OnItemRemoved → Purge cache        │  │
│  ├───────────────────────────────────────────┤  │
│  │  Scan Engine (Bounded, Thread-Safe)        │  │
│  │    ├── Phase 1: Header/metadata check     │  │
│  │    ├── Phase 2: Full stream decode        │  │
│  │    └── I/O Throttle (configurable)        │  │
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

## Requirements

- Jellyfin 10.11+
- .NET 9 Runtime (included with Jellyfin 10.11+)
- FFmpeg (typically bundled with Jellyfin as `jellyfin-ffmpeg`)

## Installation

> ⚠️ Not yet available — the plugin is in early development.

Once released, installation will be via custom plugin repository:

1. **Dashboard → Plugins → Repositories → Add**
2. **Name:** `mcgarrah-plugins`
3. **URL:** `https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest.json`
4. **Save** → **Catalog** → Install **Media Integrity Scanner**
5. **Restart Jellyfin**

## Building from Source

Prerequisites:
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

```bash
git clone https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner.git
cd jellyfin-plugin-media-integrity-scanner

dotnet restore
dotnet build --configuration Release
dotnet publish --configuration Release --output ./artifacts
```

Copy the contents of `./artifacts` to your Jellyfin plugins directory:
- **Linux:** `/var/lib/jellyfin/plugins/MediaIntegrityScanner/`
- **Windows:** `%PROGRAMDATA%\Jellyfin\Server\plugins\MediaIntegrityScanner\`
- **Docker:** `/config/plugins/MediaIntegrityScanner/`

Restart Jellyfin after installation.

## Configuration

After installation, configure via **Dashboard → Plugins → Media Integrity Scanner**:

| Setting | Default | Description |
|---------|---------|-------------|
| Max Concurrent Scans | 1 | Number of files scanned simultaneously |
| Delay Between Files | 5000ms | Pause between scanning each file |
| Max Read Rate | 10 MB/s | I/O bandwidth limit for scanning |
| Pause During Playback | true | Stop scanning when users are streaming |
| Enable Deep Scan | false | Enable Phase 2 full byte-stream decode |
| Quiet Hours Only | false | Restrict scanning to off-peak hours |
| Quiet Hours Start | 02:00 | Beginning of scan window |
| Quiet Hours End | 06:00 | End of scan window |
| Scan on Item Added | true | Auto-scan newly imported files |

## Project Structure

```
jellyfin-plugin-media-integrity-scanner/
├── Jellyfin.Plugin.MediaIntegrityScanner/
│   ├── Plugin.cs                        # Plugin entry point
│   ├── PluginConfiguration.cs           # Settings model
│   ├── Scanner/
│   │   ├── IScanEngine.cs               # Scanner interface
│   │   ├── ScanEngine.cs                # Bounded scan orchestrator
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
│       └── integrity_dashboard.html     # Admin UI
├── Jellyfin.Plugin.MediaIntegrityScanner.csproj
├── Jellyfin.Plugin.MediaIntegrityScanner.sln
├── Directory.Build.props
├── manifest.json
├── build.yaml
├── .editorconfig
├── .gitignore
├── LICENSE
└── README.md
```

## Development

### Build Environment

The development and CI build environment runs in a Proxmox LXC container with:
- .NET 9 SDK
- `jellyfin-ffmpeg` for integration testing
- GitHub Actions self-hosted runner (planned)

Until the dedicated build LXC is provisioned, builds run locally or via GitHub-hosted runners.

### Running Tests

```bash
dotnet test
```

### Local Development Workflow

1. Build the plugin: `dotnet publish -c Debug -o ./publish`
2. Copy to Jellyfin plugins directory
3. Restart Jellyfin
4. Check **Dashboard → Plugins** for the plugin
5. View logs: `journalctl -u jellyfin -f | grep MediaIntegrity`

## Blog Series

This project is documented in a series of articles at [mcgarrah.github.io](https://mcgarrah.github.io):

1. [Introduction & Problem Statement](/jellyfin-media-integrity-scanner-introduction/)
2. [Architecture & Design Decisions](/jellyfin-media-integrity-architecture-design/)
3. [Building the Scanner Core](/jellyfin-media-integrity-scanner-core/)
4. [The Dashboard & API](/jellyfin-media-integrity-dashboard-api/)
5. [Deployment & Operations](/jellyfin-media-integrity-deployment-operations/)

## Contributing

Contributions welcome once the initial architecture stabilizes. Please open an issue to discuss before submitting PRs.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).

## Acknowledgments

- [Jellyfin](https://jellyfin.org/) — The free software media system
- [jellyfin-plugin-template](https://github.com/jellyfin/jellyfin-plugin-template) — Plugin scaffolding reference
- [jellyfin-plugin-media-analyzer](https://github.com/endrl/jellyfin-plugin-media-analyzer) — Inspiration for media analysis within Jellyfin
- [FFmpeg](https://ffmpeg.org/) — The multimedia framework powering the integrity checks
