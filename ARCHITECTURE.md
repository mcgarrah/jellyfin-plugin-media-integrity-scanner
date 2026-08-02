# Architecture: Event Flow and Scan Pipeline

This document maps every path that can start, gate, or stop a scan, and works
through two concrete scenarios where those paths interact. It's meant to
answer "what does the plugin actually do when X happens" without reading the
whole codebase.

Six triggers reach into the same `ScanEngine`: two are Jellyfin's own library
events, two are the plugin's scheduled tasks, and two come from the dashboard
or settings page hitting the REST API directly. Everything converges on the
same per-file gate pipeline — nothing gets special treatment once it's queued.

## Where a Scan Begins

```mermaid
%%{init: {"theme": "base", "themeVariables": {
  "primaryColor": "#17221f",
  "primaryTextColor": "#e7ede9",
  "primaryBorderColor": "#3e6e67",
  "lineColor": "#7c93a3",
  "secondaryColor": "#1d2b27",
  "tertiaryColor": "#101a18",
  "fontFamily": "monospace",
  "fontSize": "14px"
}}}%%
flowchart TD
    IA["ItemAdded<br/>library event"]:::ev
    IR["ItemRemoved<br/>library event"]:::ev
    HS["HeaderScanTask<br/>daily, 03:00"]:::ev
    DS["DeepScanTask<br/>Sunday, 01:00"]:::ev
    API["POST /MediaIntegrity/Scan<br/>dashboard or API"]:::ev
    CAN["POST /MediaIntegrity/Cancel<br/>dashboard or API"]:::ev

    IA -->|"ScanOnItemAdded"| SIA1["ScanItemAsync<br/>Header · this file"]:::act
    IR -->|"PurgeOnItemRemoved"| PURGE["PurgeItemAsync"]:::act

    HS --> LOOP1{"IsCurrentAsync<br/>at Header?"}:::gate
    DS -->|"only if EnableDeepScan"| LOOP2{"IsCurrentAsync<br/>at FullDecode?"}:::gate

    LOOP1 -->|"current"| SKIP1["file untouched"]:::stop
    LOOP1 -->|"stale"| SIA1
    LOOP2 -->|"current"| SKIP2["file untouched"]:::stop
    LOOP2 -->|"stale"| SIA2["ScanItemAsync<br/>FullDecode · this file"]:::act

    API --> BUSY{"IsScanning?"}:::gate
    BUSY -->|"yes &rarr; 409"| REJECT["request refused,<br/>nothing changes"]:::stop
    BUSY -->|"no &rarr; 202"| SCOPE{"itemId given?"}:::gate
    SCOPE -->|"yes — skips the<br/>currency check"| SIA3["ScanItemAsync<br/>forced phase · one file"]:::act
    SCOPE -->|"no"| SLA["ScanLibraryAsync<br/>checks IsCurrentAsync per item"]:::act

    CAN -.->|"cancellation token"| SIA1
    CAN -.-> SIA2
    CAN -.-> SIA3
    CAN -.-> SLA

    SIA1 --> GATES(["gate pipeline — below"]):::pipe
    SIA2 --> GATES
    SIA3 --> GATES
    SLA --> GATES

    classDef ev fill:#1d2b27,stroke:#3e6e67,color:#e7ede9
    classDef gate fill:#2a2013,stroke:#e3a857,color:#f4d9a8
    classDef act fill:#16332c,stroke:#5fa88f,color:#cfefe2
    classDef stop fill:#331c1a,stroke:#d96c5d,color:#f3c8c2
    classDef pipe fill:#101a18,stroke:#e3a857,color:#e3a857,stroke-width:2px
```

The one asymmetry worth remembering: an `itemId`-scoped API scan skips the
currency check entirely — it always runs. That's the only way to force a
re-scan of a file the scheduled tasks would otherwise consider "already
handled." See the second scenario below.

## Inside One File's Scan

However a file got here, `ScanItemAsync` runs the same five gates in the same
order before ffmpeg ever touches the file. Each gate is independently
configurable via `PluginConfiguration`, and three of them are polling loops
the scan can sit inside for a while before proceeding.

```mermaid
%%{init: {"theme": "base", "themeVariables": {
  "primaryColor": "#17221f",
  "primaryTextColor": "#e7ede9",
  "primaryBorderColor": "#3e6e67",
  "lineColor": "#7c93a3",
  "secondaryColor": "#1d2b27",
  "tertiaryColor": "#101a18",
  "fontFamily": "monospace",
  "fontSize": "14px"
}}}%%
flowchart TD
    START(["ScanItemAsync called<br/>from any trigger"]):::pipe
    SEM["wait for a free slot<br/>MaxConcurrentScans (1)"]:::act
    QH{"UseQuietHoursOnly (false)<br/>and outside window?"}:::gate
    WAITQH["poll every 5 min<br/>until inside 02:00–06:00"]:::wait
    PP{"PauseDuringPlayback (true)<br/>and any session playing?"}:::gate
    WAITPP["poll every 30 sec<br/>until playback ends"]:::wait
    DELAY["fixed pause<br/>DelayBetweenFilesMs (5000)"]:::act
    PHASE{"which phase?"}:::gate
    HEADER["ffprobe<br/>Header check"]:::exec
    DECODE["ffmpeg null-decode<br/>FullDecode check"]:::exec
    THROTTLE["paced delay<br/>MaxReadRateMbPerSec (10)"]:::act
    SAVE[("SaveResultAsync<br/>scan_results")]:::db
    ERRSAVE[("SaveResultAsync<br/>Status: Error")]:::db
    DONE(["slot released,<br/>IsScanning re-evaluated"]):::pipe
    CANCELLED(["OperationCanceledException<br/>propagates, nothing saved"]):::stop

    START --> SEM --> QH
    QH -->|"yes"| WAITQH --> QH
    QH -->|"no"| PP
    PP -->|"yes"| WAITPP --> PP
    PP -->|"no"| DELAY --> PHASE
    PHASE -->|"Header"| HEADER
    PHASE -->|"FullDecode"| DECODE
    HEADER --> THROTTLE
    DECODE --> THROTTLE
    HEADER -.->|"exception,<br/>e.g. ffmpeg missing"| ERRSAVE
    DECODE -.->|"exception"| ERRSAVE
    THROTTLE --> SAVE --> DONE
    ERRSAVE --> DONE

    WAITQH -.->|"Cancel() called"| CANCELLED
    WAITPP -.->|"Cancel() called"| CANCELLED
    SEM -.->|"Cancel() called"| CANCELLED

    classDef gate fill:#2a2013,stroke:#e3a857,color:#f4d9a8
    classDef wait fill:#2a2013,stroke:#e3a857,color:#f4d9a8,stroke-dasharray: 3 3
    classDef act fill:#16332c,stroke:#5fa88f,color:#cfefe2
    classDef exec fill:#16332c,stroke:#5fa88f,color:#cfefe2,stroke-width:2px
    classDef db fill:#17221f,stroke:#7c93a3,color:#e7ede9
    classDef pipe fill:#101a18,stroke:#e3a857,color:#e3a857,stroke-width:2px
    classDef stop fill:#331c1a,stroke:#d96c5d,color:#f3c8c2
```

## Scenario: New Arrival Mid-Stream

A new episode finishes copying into a watched folder while someone's already
three episodes deep into the same show. `ScanOnItemAdded` fires the moment
Jellyfin notices the file — the scan doesn't wait for Jellyfin's own library
scan to finish, but it does wait for the living room to stop.

```mermaid
%%{init: {"theme": "base", "themeVariables": {
  "actorBkg": "#17221f",
  "actorBorder": "#3e6e67",
  "actorTextColor": "#e7ede9",
  "actorLineColor": "#3e6e67",
  "signalColor": "#a8b8bd",
  "signalTextColor": "#e7ede9",
  "labelBoxBkgColor": "#2a2013",
  "labelBoxBorderColor": "#e3a857",
  "labelTextColor": "#f4d9a8",
  "loopTextColor": "#e7ede9",
  "noteBkgColor": "#1d2b27",
  "noteBorderColor": "#3e6e67",
  "noteTextColor": "#e7ede9",
  "activationBorderColor": "#5fa88f",
  "activationBkgColor": "#16332c",
  "sequenceNumberColor": "#101a18",
  "fontFamily": "monospace",
  "fontSize": "13px"
}}}%%
sequenceDiagram
    participant JF as Jellyfin Core
    participant LM as LibraryMonitor
    participant SE as ScanEngine
    participant SM as SessionManager
    participant DB as scan_results

    JF->>LM: ItemAdded (new episode copied in)
    activate LM
    LM->>LM: ScanOnItemAdded == true?
    LM->>SE: ScanItemAsync(item, Header)
    deactivate LM
    activate SE
    SE->>SE: acquire concurrency slot
    SE->>SM: any session with NowPlayingItem?
    SM-->>SE: yes, someone is watching
    loop every 30 seconds
        SE->>SM: still playing?
        SM-->>SE: yes
    end
    Note over SE: playback ends
    SM-->>SE: no
    SE->>SE: apply DelayBetweenFilesMs
    SE->>SE: ffprobe the file
    SE->>DB: SaveResultAsync(Pass, Header)
    deactivate SE
    Note over DB: the dashboard's next<br/>GET /Status reflects the new file
```

## Scenario: The Sunday Deep Scan

Every file in the library gets a quick Header check on arrival, but a full
byte-stream decode only happens if `EnableDeepScan` is on — and only for
files the scheduled task doesn't think it already has covered. This is the
exact logic that shipped broken for a while: the "already covered" check
didn't distinguish a Header pass from a FullDecode pass, so a deep scan could
silently never run against a file that had merely passed its arrival check
(fixed in [PR #17](https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/pull/17)).

```mermaid
%%{init: {"theme": "base", "themeVariables": {
  "actorBkg": "#17221f",
  "actorBorder": "#3e6e67",
  "actorTextColor": "#e7ede9",
  "actorLineColor": "#3e6e67",
  "signalColor": "#a8b8bd",
  "signalTextColor": "#e7ede9",
  "labelBoxBkgColor": "#2a2013",
  "labelBoxBorderColor": "#e3a857",
  "labelTextColor": "#f4d9a8",
  "loopTextColor": "#e7ede9",
  "noteBkgColor": "#1d2b27",
  "noteBorderColor": "#3e6e67",
  "noteTextColor": "#e7ede9",
  "activationBorderColor": "#5fa88f",
  "activationBkgColor": "#16332c",
  "sequenceNumberColor": "#101a18",
  "fontFamily": "monospace",
  "fontSize": "13px"
}}}%%
sequenceDiagram
    participant SCH as Task Scheduler
    participant DST as DeepScanTask
    participant DB as scan_results
    participant SE as ScanEngine
    participant AD as Admin (dashboard)

    SCH->>DST: Sunday 01:00 trigger
    activate DST
    DST->>DST: EnableDeepScan == true?
    DST->>DB: IsCurrentAsync(item, minPhase=FullDecode)
    Note over DB: file already has a passing<br/>Header (phase 1) record —<br/>phase 1 is less than FullDecode (phase 2)
    DB-->>DST: false, not current at this phase
    DST->>SE: ScanItemAsync(item, FullDecode)
    SE->>DB: SaveResultAsync(Pass, FullDecode)
    deactivate DST

    Note over DB,SE: before the phase-aware fix, that check<br/>returned true for ANY passing record —<br/>this file would have been skipped forever

    AD->>SE: POST /MediaIntegrity/Scan<br/>itemId + deepScan: true
    activate SE
    Note over SE: the itemId-scoped path calls<br/>ScanItemAsync directly —<br/>no currency check at all
    SE->>DB: SaveResultAsync(Pass, FullDecode)
    deactivate SE
```

Why this matters: if `EnableDeepScan` doesn't seem to touch files that
already loaded in cleanly, this was why. Fixed now — the currency check
compares scan phase, not just pass/fail.

## Settings Reference

Every gate and trigger above is backed by a real `PluginConfiguration` field,
editable from **Dashboard → Plugins → Media Integrity Scanner → Settings**.

| Setting | Default | What it actually does |
|---|---|---|
| `MaxConcurrentScans` | `1` | Size of the semaphore every file scan waits on — the hard ceiling on simultaneous ffmpeg processes, for both manual and scheduled scans. |
| `DelayBetweenFilesMs` | `5000` | Fixed pause immediately before each file's ffprobe/ffmpeg call, after all other gates have cleared. |
| `PauseDuringPlayback` | `true` | Checks every active Jellyfin session for a `NowPlayingItem`; if any exist, the scan polls every 30 seconds until they don't. |
| `UseQuietHoursOnly` | `false` | Restricts scanning to the window below; outside it, the scan polls every 5 minutes. |
| `QuietHoursStart` / `QuietHoursEnd` | `02:00` / `06:00` | The window checked above, can span midnight. |
| `MaxReadRateMbPerSec` | `10` | Applied *after* each file completes — a delay sized to the file's bytes and how long the scan actually took, to keep sustained I/O bounded. |
| `EnableDeepScan` | `false` | Master switch for the whole weekly FullDecode task — off means `DeepScanTask` reports 100% progress and exits immediately. |
| `ScanOnItemAdded` | `true` | Whether `LibraryMonitor` reacts to Jellyfin's `ItemAdded` event with an automatic Header scan. |
| `PurgeOnItemRemoved` | `true` | Whether `ItemRemoved` deletes that item's scan history rather than leaving it orphaned. |
| `FfmpegPathOverride` / `FfprobePathOverride` | *(auto-detected)* | Skips `FfmpegResolver`'s autodetection in favor of an explicit binary path. |

## Source Reference

- `Api/MediaIntegrityController.cs` — REST endpoints (`Status`, `Results`, `Results/{itemId}`, `Scan`, `Cancel`)
- `Scanner/ScanEngine.cs` — the gate pipeline and `ScanLibraryAsync`/`ScanItemAsync`
- `EventHandlers/LibraryMonitor.cs` — `ItemAdded`/`ItemRemoved` handling
- `ScheduledTasks/HeaderScanTask.cs`, `ScheduledTasks/DeepScanTask.cs` — the two scheduled sweeps
- `Data/SqliteDatabaseManager.cs` — `IsCurrentAsync` and the `scan_results` schema
