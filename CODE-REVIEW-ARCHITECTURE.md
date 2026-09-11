# Architectural Code Review — Open Items

> **Review Date:** September 11, 2026
> **Scope:** Architecture and structure of `Jellyfin.Plugin.MediaIntegrityScanner/` (source, Web pages, DI wiring, config strategy, release pipeline, test architecture) as of `v0.4.3`.
> **Relationship to [CODE_REVIEW.md](CODE_REVIEW.md):** That file is the completed historical record of 21 resolved review sections (point bugs, correctness, coverage). This file is the *open* list — structural debt to resolve in future sessions. Nothing here duplicates an item already resolved there or shipped in v0.4.0–v0.4.3 (auth-header migration, `GetCount()` perf fix, CSV formula injection, `_cts` race, `MaxConcurrentScans` clamp, Arr circuit breaker, bounded scan queue, `ArrClientBase` tests, etc.).
> **How to use:** Each finding is a checkbox. Tick it (and note the commit/PR) when resolved. Each item is self-contained — no session context needed to pick it up.

---

## Executive Summary

The codebase is healthy at the unit level — 441 passing tests across both target frameworks, zero build warnings, a clean DI graph, and consistently good inline documentation. The debt is structural, accumulated feature-by-feature: two god files (`SqliteDatabaseManager.cs` at 1,454 lines, `MediaIntegrityController.cs` at 969), a 29-call-site static-singleton config pattern that already caused one production-class bug and forces test serialization, ~200 lines of copy-pasted JavaScript across three admin pages that already caused a shipped 14-site bug, and a config-validation strategy that lives entirely in client-side JS. None of these block current work; all of them make the *next* feature more expensive than the last.

Priorities: the two **High** items are the ones with a demonstrated (not hypothetical) bug history. The Mediums are pre-emptive. The Lows are hygiene.

---

## High

### H1. Shared JS helpers are copy-pasted across the three Web pages — with a proven drift record

- [x] **(Partial, 2026-09-11)** `apiFetch` in all three pages now deep-merges headers (`Object.assign({}, options)` + a separate `Object.assign` for `headers`) instead of a single shallow merge, so the default auth header always applies regardless of what a call site's own `headers` object contains. This removed all 11 of the 14 duplicated `Authorization` string literals that existed purely to work around the shallow-merge bug -- each file now has exactly one `Authorization` string, inside its own `apiFetch`. Verified live against ct:591 via Playwright: every button that POSTs with its own `headers` (Backup Now, Cancel Scan, Run Header Scan, Ffmpeg Refresh, Database Maintenance) still authenticates correctly.
- [ ] **Remaining:** the *three* copies of `apiFetch`/`getApiUrl`/`showError`/`escapeHtml`/`formatDate`/`hideError` themselves are still separate per-file -- the full recommendation below (one shared embedded script resource) is not done. The immediate drift risk (a 14-site fix) is gone; the maintenance-cost risk (any helper's *logic* changing needs 3 edits) remains.

**Evidence:** `integrity_dashboard.html` (1,120 lines), `integrity_settings.html` (662), `integrity_issues.html` (555) each define their own private copies of the same helpers: `apiFetch` (3 copies), `getApiUrl` (3), `showError` (3), `escapeHtml` (2), `formatDate` (2), `hideError` (2), plus per-page `formatBytes`/`escapeAttr`. Because the pages' `apiFetch` uses `Object.assign` (shallow merge), the auth header is additionally repeated at every call site that passes its own `headers` — 14 occurrences across the 3 files (8/4/2).

**Why it matters (demonstrated, not speculative):** The Jellyfin 12.0 `X-Emby-Token` → `Authorization: MediaBrowser Token` migration required the *same one-line fix applied 14 times across 3 files*. It initially shipped incomplete precisely because of this structure — the dashboard was fixed and the settings/issues pages drifted until a full Playwright pass caught them. Every future change to auth, error handling, or API-URL construction repeats this risk exactly.

**Recommended resolution (medium effort):** Jellyfin plugin pages support multiple `PluginPageInfo` entries, and embedded resources can be served as scripts: add a `Web/integrity_shared.js` embedded resource, register it as an additional page (or serve it via the existing `configurationpage?name=...` mechanism with a `.js` resource — the Intro Skipper and several jellyfin-plugin-template derivatives do this), and have each page load it with a single `<script src>` tag pointing at the plugin resource URL. Alternative if script-resource serving proves awkward on both 10.11 and 12.0: a build-time include step in `release.yml` that splices a shared fragment into each page before packaging (keeps runtime identical, moves dedup to CI). Either way, the acceptance test is: the auth header string exists in exactly one place. Also fix `apiFetch` to deep-merge headers so call sites stop re-declaring the auth header at all — that alone removes 11 of the 14 duplications and is a small, independent first step.

### H2. No server-side configuration validation — all clamping lives in settings-page JS

- [x] **Resolved (2026-09-11).** Added `Plugin.Sanitize(PluginConfiguration)` -- clamps all seven numeric settings to their documented ranges (matching the settings page's own HTML min/max), restores unparseable quiet-hours strings and blank manifest URLs to their defaults, and drops/trims Arr server entries with no URL or API key. Called from both `Plugin.UpdateConfiguration` (the settings-page save path, and Jellyfin's generic REST config endpoint) and the `Plugin` constructor (a hand-edited XML file loaded at startup gets the same treatment, without rewriting the file). 36 new unit tests (`PluginConfigurationSanitizeTests.cs`), including the exact `MaxConcurrentScans=0` incident as a named regression case. `PluginTests.cs`'s `CreatePlugin()` helper updated to stub `IApplicationPaths.PluginConfigurationsPath` and a throwing `IXmlSerializer`, since the constructor now genuinely reads `Configuration` (previously untouched in that test's lightweight mock setup).

**Evidence:** The settings page saves via Jellyfin's generic `ApiClient.updatePluginConfiguration()` (`integrity_settings.html:610`), which deserializes straight into `PluginConfiguration` with no plugin-side checks. All seven numeric settings (`MaxConcurrentScans`, `DelayBetweenFilesMs`, `MaxReadRateMbPerSec`, `HistoryLookbackDays`, `MaxAutoRemediationsPerDay`, `RemediationCooldownHours`, `MaxRemediationCycles`) plus the Arr server URLs/API keys are validated only by HTML `min`/`max` attributes and `parseInt(...) || fallback` in page JS. The config XML on disk and Jellyfin's REST config endpoint bypass all of it.

**Why it matters (demonstrated):** The `MaxConcurrentScans` incident (fixed in v0.4.3) showed the exact failure mode — a `0` on disk produced `SemaphoreSlim(0,0)` throwing out of a DI constructor at server startup. That specific site is now clamped, but the same class of bug exists for every other setting: a negative `DelayBetweenFilesMs` reaches `Task.Delay` (throws `ArgumentOutOfRangeException` mid-scan), a negative `HistoryLookbackDays` inverts the blocklist cutoff window, a malformed Arr `Url` throws `UriFormatException` inside `ArrClientBase`'s constructor at remediation time. Defensive `Math.Max` calls scattered at use sites (the current pattern) must be remembered at every *new* use site forever.

**Recommended resolution (small effort):** `BasePlugin<T>.UpdateConfiguration` is `virtual` (confirmed against Jellyfin source, `BasePluginOfT.cs:163`). Override it once in `Plugin.cs` with a `Sanitize(PluginConfiguration)` method that clamps every numeric to its documented range, trims/validates URLs (`Uri.TryCreate`), and drops empty Arr server entries — then call the same `Sanitize` from the `Plugin` constructor so a hand-edited XML loaded at startup gets the same treatment. One method, both entry points covered, and the scattered per-site `Math.Max` guards become redundant (keep them; they're cheap belt-and-braces). Add a unit test per property (theory with out-of-range inline data).

---

## Medium

### M1. `SqliteDatabaseManager` is a 1,454-line god class behind a 20-method god interface

- [ ] **Split `IDatabaseManager`/`SqliteDatabaseManager` by responsibility.**

**Evidence:** `Data/SqliteDatabaseManager.cs` is 1,454 lines; `IDatabaseManager` declares 20 methods spanning four unrelated jobs: (1) scan-result persistence (`SaveResultAsync`, `IsCurrentAsync`, `MarkPendingAsync`, `GetStatisticsAsync`, `GetResultsAsync`, `GetAllResultsAsync`, `GetItemDetailAsync`, `PurgeItemAsync`, `ReconcileAsync`), (2) Arr-remediation persistence (8 methods: `RecordRemediationAsync` through `UpdateRemediationAsync`, plus `GetIssuesAsync`/`GetAllIssuesAsync`), (3) backup/restore (`BackupAsync`, `ListBackupsAsync`, `RestoreAsync`), (4) maintenance (`InitializeAsync`, `GetMaintenanceInfoAsync`, `RunMaintenanceAsync`). Every consumer mocks the whole 20-method interface to use 2–3 of them; `SqliteDatabaseManagerTests.cs` is 1,042 lines with a separate 427-line `ArrRemediationDatabaseTests.cs` already implicitly acknowledging the seam.

**Why it matters:** Each new feature (the Arr integration alone added 8 methods) lands in the same file/interface, and merge conflicts, review noise, and mock setup all scale with total size rather than the change's size. The `_writeLock`, `_connectionString`, and schema-migration logic are genuinely shared state — but the *query* methods above them are independent.

**Recommended resolution (medium-large effort, mechanical):** Split into `IScanResultStore`, `IArrRemediationStore`, `IDatabaseBackupService`, `IDatabaseMaintenanceService`, implemented as partial classes of the existing singleton *or* as four thin classes sharing an injected `SqliteConnectionProvider` (owns connection string, write lock, and `InitializeAsync`). Register each interface forwarding to the same singleton (the registrator already uses this forwarding pattern for `IDatabaseManager` → `SqliteDatabaseManager`). Keep `IDatabaseManager : IScanResultStore, IArrRemediationStore, ...` temporarily as a shim so all call sites and tests migrate incrementally rather than in one commit. The partial-class variant is the low-risk first step (pure file split, no behavior change, no test churn) and is worth doing even if the interface split waits.

### M2. `MediaIntegrityController` mixes 25 endpoints and 8 DTO classes in one 969-line file

- [ ] **Move the DTOs out; consider splitting the controller by feature area.**

**Evidence:** `Api/MediaIntegrityController.cs` (969 lines) contains 8 public DTO classes inline (`DiagnosticsResponse`, `ScanStatusResponse`, `ScanRequest`, `InstallUpdateRequest`, `RestoreBackupRequest`, `ArrRemediationBulkRequest`, `FfmpegRefreshResult`, `PagedResultResponse`, lines 779–969) below endpoints spanning five feature areas: scan control, results/export, issues/remediation, database backup/maintenance, and update-checking.

**Why it matters:** One-class-per-file is the codebase's own convention everywhere else (`Data/Models/` exists for exactly this); the inline DTOs are the exception. The single controller also means every feature's endpoints share one constructor with eight injected dependencies — each new endpoint grows the blast radius of every controller test's setup.

**Recommended resolution (small for DTOs, medium for split):** First (small, zero risk): move the 8 DTOs to `Api/Models/` — pure file move. Second (optional, medium): split into `ScanController`, `DatabaseController`, `IssuesController`, `UpdateController`, all sharing the `[Route("MediaIntegrity")]` prefix via route templates so the public API doesn't change. The split is worth it only when the next feature area lands; the DTO move is worth it now.

### M3. `TriggerScan`'s fire-and-forget `Task.Run` is untracked — no way to observe or await it

- [ ] **Route the manual-scan trigger through a tracked mechanism instead of a bare `Task.Run`.**

**Evidence:** `MediaIntegrityController.cs:556` — the `POST /Scan` endpoint dispatches `_ = Task.Run(async () => ...)` with `CancellationToken.None` and returns `202 Accepted`. `LibraryMonitor.OnItemRemoved` (`LibraryMonitor.cs:284`) has the same pattern for purges, and `ArrRemediationWorker.OnTick` (`:91`) discards its task.

**Why it matters:** These are the last three untracked fire-and-forget sites (the item-added/updated paths were moved to a bounded channel in v0.4.3). The controller one is the most consequential: the spawned scan is invisible to server shutdown (Jellyfin can stop while a full-library scan is mid-write, with only SQLite's own journal protecting the DB), and there's no handle for tests or diagnostics to await. The worker's `OnTick` discard is defensible (the reentrancy guard makes overlap impossible) but means an unhandled exception in `ProcessQueueAsync`'s outer body would be silently swallowed by the unobserved task.

**Recommended resolution (small-medium):** For the controller: capture the task in a field on `ScanEngine` (e.g. `Task? CurrentScanTask`) or route library-scan requests through the same channel pattern `LibraryMonitor` now uses — the engine already tracks `IsScanning`; tracking the `Task` alongside it is a natural extension and gives `StopAsync`/`Dispose` something to await with a timeout. For `OnItemRemoved`: a purge is quick — either await it inline in a `Try`-wrapped handler or push it through the existing channel with a `Purge` request kind. For `OnTick`: wrap `ProcessQueueAsync` in a try/catch-log at the `OnTick` boundary (two lines).

### M4. `ScanEngine` depends on concrete `FfmpegWrapper` and `SharedBandwidthLimiter`, and self-constructs the latter

- [ ] **Introduce `IFfmpegWrapper`; register `SharedBandwidthLimiter` in DI instead of `new`-ing it in the constructor.**

**Evidence:** `ScanEngine.cs:77–92` — constructor takes concrete `FfmpegWrapper` (tests cope by making `ProbeAsync`/`DecodeAsync` `virtual` purely so Moq can subclass — `FfmpegWrapper.cs:121,158` — and an `internal` event-raiser exists only for Moq, `:78`) and `SharedBandwidthLimiter? bandwidthLimiter = null` with `?? new SharedBandwidthLimiter()` fallback. `PluginServiceRegistrator` never registers `SharedBandwidthLimiter`, so production always takes the self-constructed path — a hidden dependency the DI container can't see or replace.

**Why it matters:** The virtual-for-Moq pattern couples the production class's shape to the test framework (each new public method must remember to be `virtual` or tests silently exercise the real ffmpeg path — a class of bug that has no compile-time guard). The optional-parameter DI fallback means anyone reading the registrator gets an incomplete picture of the object graph, and a second consumer of the bandwidth limiter (plausible: the Arr worker throttling downloads) would silently get a *different* budget instance, defeating the "shared" in its name.

**Recommended resolution (small):** Extract `IFfmpegWrapper` (5 members), register it, drop the `virtual`s and the internal Moq helper. Add `serviceCollection.AddSingleton<SharedBandwidthLimiter>()` and make the `ScanEngine` parameter required. Both are mechanical; the test diffs are find-and-replace.

### M5. `RemediateMovieAsync` / `RemediateEpisodeAsync` are parallel 56-line near-duplicates

- [ ] **Collapse the movie/episode remediation flows over a small adapter abstraction.**

**Evidence:** `ArrRemediationService.cs:237–292` and `:294–350` — 56 and 57 lines, structurally identical (select server → create client → match → pre-flight release search → delete → blocklist-or-search → record), differing only in the client type, match call, and ID plumbing. The file already proves the pattern works: `BlocklistOrSearchAsync` (`:361`) is generic over the Radarr/Sonarr history shape via delegates.

**Why it matters:** Every change to the remediation flow (the v0.4.3 circuit breaker, any future retry policy, the daily-cap interaction) must be made — and reviewed, and tested — twice. The two blocks have already required synchronized edits in this repo's history (dry-run handling, the pre-flight availability check).

**Recommended resolution (medium):** Extend the delegate-bundle approach already used by `BlocklistOrSearchAsync` one level up: a private `RemediateCoreAsync<TClient>` taking `(matchFunc, deleteFileFunc, historyFuncs, searchFunc, appName)`, or a tiny `IArrAppAdapter` with two implementations. Not urgent, but do it *before* the next remediation-flow feature rather than after.

---

## Low

### L1. `Plugin.Instance` static config access — 29 call sites across 12 classes

- [ ] **(Optional) Introduce an injected config accessor; at minimum, don't grow the pattern.**

**Evidence:** 29 `Plugin.Instance` references across `ScanEngine` (6), `UpdateChecker` (4), `LibraryMonitor` (4), `ArrRemediationService` (3), `FfmpegResolver` (3), the four scheduled tasks, both workers, and the controller. `tests/TestPluginContext.cs` exists solely to cope (reflection-sets the static), and 15 of 34 test files are forced into one serialized xUnit collection (`[Collection("PluginInstance")]`) because they share that mutable static — a real, measurable test-throughput cost that grows with every config-reading class.

**Why this is Low, not High — the honest tradeoff:** This *is* the idiomatic Jellyfin plugin pattern (`BasePlugin<T>` is designed around the static `Instance`, and reading `Instance.Configuration` at call time is what gives every class live config after a settings save, with no change-notification plumbing). A full `IConfigProvider` migration would touch 12 classes and all their tests for a mostly-aesthetic win, and *fresh-read-at-call-time semantics must be preserved* — naively injecting a snapshot at construction would introduce stale-config bugs worse than the disease. The serialized test collection is the one genuine cost, and it's tolerable at the current suite size (~5s total).

**Recommended resolution if/when it's done (large):** `IPluginConfigAccessor` with a `Current` property that reads the static internally (one-line production implementation, trivially fakeable per-test without reflection or collection serialization). Migrate class-by-class opportunistically when a file is already being edited. Do not do it as a big-bang refactor.

### L2. API error-shape and `[ProducesResponseType]` coverage is *mostly* consistent — two small gaps

- [ ] **Give the four bare `NotFound()` returns a `{ message }` body; audit endpoints that can throw for missing 4xx/5xx annotations.**

**Evidence:** Every 4xx path in the controller returns `new { message = "..." }` *except* four bare `NotFound()`s (lines 284, 393, 399, 454) whose bodies are empty — page JS that reads `err.message` after a 404 gets nothing to show. Annotation-wise, endpoints are consistently `[ProducesResponseType(200)]` (+404/409/400 where applicable), but e.g. `TriggerScan` can throw `FormatException` from `Guid.Parse(request.ItemId)` inside its fire-and-forget block (logged, never surfaced — related to M3), and the export endpoints declare only 200 despite hitting the DB.

**Why it matters:** Minor UX/documentation polish; the shapes are already 90% uniform, which is why this is Low rather than Medium.

**Recommended resolution (small):** Mechanical pass: bodies on the `NotFound`s, `Guid.TryParse` before dispatch in `TriggerScan` (return 400 synchronously instead of logging asynchronously), annotations to match.

### L3. `manifest-unstable.json` grows unboundedly — 64 dev-version entries and counting

- [ ] **Cap retained dev-channel manifest entries in `scripts/update-manifest.py`.**

**Evidence:** `manifest-unstable.json` is at 64 version entries (525 lines) vs. the stable manifest's 12; `update-manifest.py:160–166` prepends and dedups but never prunes. Two dev releases land per real release (the tag build + the manifest-commit race pattern visible in git history), so this roughly doubles every few release cycles.

**Why it matters:** Every Jellyfin server with the dev repo configured downloads and parses the full file on each catalog refresh, and each entry references a GitHub release asset that must stay alive for the entry to be honest. Old dev builds have no downgrade value (that's what the stable manifest's retained entries are for, per RELEASE.md).

**Recommended resolution (small):** In `update-manifest.py`, after inserting, truncate the unstable manifest's `versions` list to the newest N (10–15) *per targetAbi* — the per-ABI grouping matters since one tag now produces two entries. Stable manifest stays untouched (downgrades are a documented feature there).

### L4. Test-suite structural friction worth recording (no action required yet)

- [ ] **When the `PluginInstance` collection becomes the slowest part of the suite, revisit L1; when a fourth shell script joins `tests/`, document the integration-test entry points in a `tests/README.md`.**

**Evidence:** 15/34 test files serialized in one collection (see L1). Integration testing spans three bash scripts (`generate-test-media.sh`, `setup-jellyfin.sh`, `run-integration-tests.sh`) plus a Playwright tree, wired together by `integration-test.yml`/`playwright-e2e.yml` — the relationships are currently documented only in workflow YAML and AGENTS.md prose. Unit coverage is strong and proportional (9,086 test lines vs 8,480 source); the two structural notes above are the only friction points, and neither has bitten yet.

---

## Considered and Acceptable

These were assessed deliberately and are fine as-is — documented so future reviews don't re-litigate them:

1. **`Plugin.Instance` fresh-read-at-call-time semantics** (as distinct from L1's testability cost): reading config at each use is *correct* for this plugin — settings saves take effect immediately without restart or change-notification plumbing. Any refactor must preserve this; the pattern itself is not a bug.
2. **`IDatabaseManager` registered via forwarding** (`AddSingleton<SqliteDatabaseManager>()` + `AddSingleton<IDatabaseManager>(sp => ...)`): correct pattern, single instance, both resolutions identical. Not a smell — it's the template M1's split should follow.
3. **`ArrRemediationWorker.StopAsync` not awaiting an in-flight pass:** the pass is short (bounded by the daily cap and, since v0.4.3, the circuit breaker), every row it touches is durably `pending` in SQLite, and a restart resumes cleanly. Draining on shutdown would add complexity for no data-integrity gain.
4. **`LibraryMonitor.StopAsync` cancelling consumers without draining:** explicitly documented in-code as matching the prior fire-and-forget semantics; scheduled scans are the durable backstop. Correct tradeoff for a media scanner.
5. **`BlocklistOrSearchAsync`'s 9-parameter delegate signature:** ugly at a glance, but it's the *right* shape for genericizing over two wire formats without inventing interface hierarchies for Radarr/Sonarr model classes — and it's private. If M5 is done, fold it into the same adapter; until then, leave it.
6. **`SqliteDatabaseManager`'s single `_writeLock` serializing all writes:** assessed in the v0.4.3 review pass — correct for SQLite under WAL with this write volume; not a scalability concern at plugin scale.
7. **Response DTOs using classes (not records) with mutable setters:** consistent with System.Text.Json serialization needs and the codebase's existing style; churning them to records is style-only.
8. **The release workflows' race-retry `git push` loop** (`release.yml:97`, `release-dev.yml:113`): the manifest-commit race between the tag build and dev build is real (observed twice in session history as benign non-fast-forward rejects), and the retry loop is the correct, already-shipped mitigation. No further action.
