# Feature Proposal: Forward Bad Files to Sonarr/Radarr for Replacement

**Status:** Proposal — not started. This document is a complete design; no code
in this repo has been changed to implement it. Written 2026-08-23.

**Author's framing:** the scanner today does exactly one job — tell you a file
is corrupt. It does not close the loop. An admin has to notice a Failed row in
the dashboard, go figure out which Radarr/Sonarr item it maps to, delete the
file by hand, and trigger a new search. This proposal closes that loop: when
the scanner finds a bad file, the plugin optionally does that cleanup itself.

---

## 1. Goals and non-goals

**Goals:**
- When a scan finds a file that fails validation (corrupt header, decode
  error), the plugin can automatically (or on manual click) tell Radarr/Sonarr
  "this file is bad, get rid of it and find a replacement."
- Prefer Radarr/Sonarr's own built-in "this release was bad" mechanism over
  reinventing blocklist logic, so behavior matches what an admin clicking
  "Mark as Failed" in the Radarr/Sonarr UI would do.
- Make this safe by default: opt-in, dry-run-capable, rate-limited, and fully
  visible — never a black box that silently deletes files.
- Give this feature its own dedicated page (§8) rather than adding to the
  existing dashboard, which is already dense with unrelated functionality
  (health stats, scan-scope controls, database backup/maintenance) — a
  focused "what's broken and what do I do about it" view instead of one
  more thing competing for space on an already-busy screen.
- Support the same multi-server pattern this user's actual Radarr/Sonarr
  deployment already uses (separate "Disney"/"Family" root folders and
  profiles — see `k8s-proxmox/docs/ARR-SUITE-DEPLOYMENT-PLAN.md`), so
  forwarding routes to the right server, not just a single hardcoded one.

**Non-goals (explicitly out of scope for this proposal):**
- Music/audio libraries (Lidarr) — no Lidarr instance exists in this user's
  deployment; Radarr (movies) and Sonarr (TV) only.
- Automatically *deleting the Jellyfin library item itself* — only the
  underlying file gets removed (via Radarr/Sonarr, which then re-imports a
  replacement into the same Jellyfin item once the new download lands).
- A general-purpose "notifications" framework for arbitrary third-party
  webhooks. This is a narrow, Radarr/Sonarr-specific integration.
- Handling content Radarr/Sonarr don't manage at all (e.g. home videos,
  manually-curated collections with no arr-tracked file) — these get a clean
  "Unmatched, not managed by Sonarr/Radarr" outcome, not an error.

---

## 2. Prior art: what Seerr and JellyGlance actually do

The user asked to look at Seerr and JellyGlance, both already deployed in
this homelab, for integration patterns worth borrowing. Researched directly
against their real source (Seerr: `github.com/seerr-team/seerr`,
`server/api/servarr/{base,radarr,sonarr}.ts`; JellyGlance:
`github.com/Nerdy-Technician/JellyGlance`, `apps/api/tasks/*.js` +
`apps/api/classes/integration-store.js`). Neither one does what this proposal
describes — worth being explicit about that rather than overselling the "prior
art" framing — but both have real, specific patterns worth adopting or
avoiding.

### 2.1 Seerr — what to borrow

- **Shared base API client class.** `ServarrBase<T>` extends a generic
  `ExternalAPI`; `RadarrAPI`/`SonarrAPI` both extend it. Auth is a single
  `apikey` header set once in the base class. This proposal's `ArrClientBase`
  (§6.2) follows the same shape.
- **Contextual error wrapping.** Every call site wraps its HTTP call in
  try/catch and re-throws with `"[${apiName}] Failed to <op>: ${e.message}"`,
  preserving the original as `.cause`. Directly worth copying — makes failures
  legible in logs instead of a bare `HttpRequestException`.
- **Multi-server support is a first-class concept.** Seerr's settings model
  supports multiple Radarr/Sonarr "servers" of the same type (its documented
  use case: separate 4K vs. standard-quality servers, `isDefault`/`is4k`
  flags), which is exactly the shape needed here for this user's real
  "Disney/Family" server split.

### 2.2 Seerr — what to explicitly avoid

- **No timeout configured anywhere in the client**, and **no retry/resync
  logic at all.** Two open, unresolved Seerr issues confirm the real-world
  consequence: [#1994](https://github.com/seerr-team/seerr/issues/1994) (no
  auto-re-request job) and
  [#2891](https://github.com/seerr-team/seerr/issues/2891) (pending requests
  never resync once a service comes back online) — a request made while
  Radarr/Sonarr is down is simply lost. This proposal uses a durable SQLite
  outbox table instead of a fire-and-forget call specifically to avoid this
  failure mode (§6.4).
- **Seerr's "Issues" feature (user reports a problem with existing media) is
  disconnected from its own Radarr/Sonarr client** — reporting an issue never
  triggers a delete/re-search. So there's no existing "detect bad, fix it"
  precedent to lean on here at all; this is genuinely new ground for this
  ecosystem, not a gap-fill of something half-built elsewhere.

### 2.3 JellyGlance — what to borrow

- **Encrypted-at-rest secrets with an edit-safe UI convention.** JellyGlance
  encrypts API keys with AES-256-GCM (`jgenc:v1:` prefix) before writing to
  Postgres, and — the genuinely reusable detail — **an empty secret field on
  a settings-page save preserves the existing stored value** rather than
  wiping it. This matters because a settings form for N Radarr/Sonarr servers
  will always show existing API keys masked/blank; without this rule, an
  innocuous re-save of unrelated settings would silently blank every stored
  API key. §7 adopts this rule directly.

### 2.4 JellyGlance — what's not applicable

- Its own *arr calls are **100% read-only** (`GET /movie/{id}`, `GET
  /calendar`, etc., inline in task files, no shared client class) — it never
  deletes a file or triggers a search. There's no action-taking logic to
  study here.
- Its axios instance has no timeout or retry logic either (same gap as
  Seerr, independently) — another confirmation that "durable outbox, real
  timeout" isn't optional polish for this proposal, it's the one thing
  neither piece of prior art got right.
- Could not confirm its actual settings-form UI component within research
  budget — not cited as a UI pattern source for that reason.

---

## 3. The hard part: matching a Jellyfin item to a Radarr/Sonarr item

This plugin scans **Jellyfin** `BaseItem`s. Radarr/Sonarr have no concept of a
Jellyfin item ID — they need to be told "which movie/episode" some other way.
This is the crux of the whole feature; get it wrong and either nothing
matches (annoying) or the wrong file gets deleted (bad). Two strategies, used
in order:

### 3.1 Primary: metadata provider IDs (recommended default)

Jellyfin's `BaseItem.ProviderIds` (`Dictionary<string,string>`) carries
`Tmdb`/`Imdb` for movies and `Tvdb`/`Imdb` for series, populated by Jellyfin's
own metadata agents independent of file path. This is **far more robust than
path matching** because it doesn't care about mount-point differences between
containers.

- **Movie:** `item.ProviderIds["Tmdb"]` → Radarr `GET /api/v3/movie` (fetch
  all, filter client-side on `tmdbId`) → get `id` (Radarr's internal movieId).
- **Episode:** an `Episode` `BaseItem` doesn't carry its own provider IDs —
  its **parent series** does. Resolve via
  `_libraryManager.GetItemById(episode.SeriesId)` → that `Series` item's
  `ProviderIds["Tvdb"]` → Sonarr `GET /api/v3/series` (filter client-side on
  `tvdbId`) → get Sonarr's `id` (seriesId) → `GET
  /api/v3/episode?seriesId={id}` → filter on `seasonNumber ==
  episode.ParentIndexNumber && episodeNumber == episode.IndexNumber` → get
  `episodeFileId` from the matched record.

**Verified live against this user's actual Radarr/Sonarr** (2026-08-23):
`GET /api/v3/movie/{id}` embeds `tmdbId`/`imdbId` directly and — when the
movie has a file — an embedded `movieFile` object with the real on-disk
`path`. `GET /api/v3/series/{id}` similarly exposes `tvdbId`/`imdbId`. No
guessing here; this is the actual response shape on the live instances.

### 3.2 Fallback: path matching (when provider IDs are missing/unmatched)

Some content is unidentified in Jellyfin (never matched to a metadata
provider) — provider-ID matching can't help there. Fall back to file path,
with two real complications documented from this cluster's own build history
(`k8s-proxmox/docs/ARR-SUITE-DEPLOYMENT-PLAN.md`, "Storage plan" section):

1. **Mount-point prefixes can legitimately differ** between the Jellyfin
   container and the Radarr/Sonarr containers, even though they're the same
   underlying CephFS. An exact absolute-path string match will false-negative
   in that case.
2. Radarr/Sonarr's own `moviefile.path`/`episodefile.path` fields are already
   confirmed (§3.1) to be the real absolute on-disk path as *they* see it —
   the plugin needs the equivalent on the Jellyfin side, which is simply
   `item.Path` (already stored in `ScanRecord.FilePath`).

**Recommended matching rule, in order:**
1. Exact match on the two paths' shared **suffix** (e.g. last 2-3 path
   segments: `"<Series or Movie folder>/<filename>"`), not the full absolute
   path. This is naturally robust to differing mount prefixes without
   requiring any admin configuration, and matches how this cluster's actual
   directory layout is structured (one folder per title).
2. If that's ambiguous (e.g. two different shows happen to have
   identically-named episode files — rare but possible with scene-release
   naming), fall back to an **admin-configured path-prefix translation
   table** in settings (`JellyfinPrefix` → `ArrPrefix` pairs), mirroring
   Radarr/Sonarr's own "Remote Path Mappings" feature that this same cluster
   already relies on elsewhere in the *arr stack.
3. If still ambiguous or no match at all: mark `Unmatched`, take no action,
   surface it plainly in the dashboard. **Never guess and delete the wrong
   file** — an unmatched result is a completely acceptable, expected outcome
   for content Radarr/Sonarr simply doesn't manage (home videos, manually
   curated content, etc.), not a bug to suppress.

---

## 4. The remediation flow (once a target file is identified)

This is the part I want to be precise about, because the naive version is
subtly wrong. Radarr/Sonarr expose two related-but-different actions:

- `POST /api/v3/history/failed/{historyId}` (`Sonarr`: same path, series
  history) — internally `FailedDownloadService.MarkAsFailed(historyId,
  skipRedownload)`. **Confirmed live** (2026-08-23, via the 404 error's own
  stack trace against both real Radarr and Sonarr instances — this endpoint
  genuinely exists and is wired to exactly that service method). This is the
  same action the Radarr/Sonarr web UI's "Mark as Failed" button on a history
  row calls. It blocklists that specific release (so the next search won't
  re-grab the identical bad copy) and, unless `skipRedownload=true`,
  immediately triggers a new search.
- `DELETE /api/v3/moviefile/{id}` / `DELETE /api/v3/episodefile/{id}` —
  **confirmed live**, removes the file from disk and from Radarr/Sonarr's own
  database, making that movie/episode "missing" again.

**The subtlety:** `history/failed` is designed for the case where a download
is still in-flight or just landed — it's normally clicked *before* import
completes. It is **not** documented to also delete an already-imported file.
If this plugin only calls `history/failed` on a file that finished importing
days ago, Radarr/Sonarr may still believe the movie/episode already "has a
file" and not treat a fresh grab as urgently as it should.

**Correct order, therefore:**
0. **Pre-flight availability check, before touching anything** — added
   2026-08-23 after the user asked a pointed question this doc originally
   glossed over: "what if the replacement is hard to find, or Radarr/Sonarr
   just re-grabs the exact same bad copy?" `GET /api/v3/release?movieId={id}`
   (Sonarr: `?episodeId={id}`) runs a real interactive search and returns
   every candidate release *without grabbing anything*, each tagged
   `rejected: true/false` plus a `rejections` array explaining why.
   **Verified live** against this user's actual Radarr (2026-08-23): a real
   test query returned 31 candidates for a real movie, with genuine
   rejection reasons already computed (e.g. `"Existing file meets cutoff"`,
   a size-limit rejection). Filter to `rejected == false`, excluding the
   specific release about to be blocklisted in step 2. **If zero viable
   candidates remain, stop here — do not delete anything.** Mark the item
   `action_taken = 'no_replacement_available'`, leave the corrupt file in
   place (a bad copy the admin can decide about is strictly better than an
   item silently missing with nothing to search for), and surface it
   plainly on the Issues page (§8) for a manual decision rather than
   guessing on the admin's behalf.
1. `DELETE /api/v3/moviefile/{id}` (or `episodefile`) — only once step 0
   confirms a real replacement is findable. This makes Radarr/Sonarr
   consider the item missing again, unconditionally. **Confirmed this is
   currently a genuine, permanent removal on this user's real Radarr/Sonarr,
   not a soft-delete** — checked `GET /api/v3/config/mediamanagement` live
   on both instances 2026-08-23; `recycleBin` is unconfigured (empty) on
   both. See §4.2 for the fix.
2. **Then** look up the most recent `grabbed` history event for that
   movie/episode (`GET /api/v3/history/movie?movieId={id}` or the Sonarr
   equivalent, filter `eventType == "grabbed"`, take the newest) and call
   `POST /api/v3/history/failed/{historyId}` for it — this blocklists the
   specific bad release (so even if the pre-flight check in step 0 saw it as
   one of several viable candidates, it can never be re-grabbed) and fires
   the redownload search.
3. **Fallback when no recent grab history exists** (manually-added files,
   very old imports with history since pruned): after the delete in step 1,
   just call `POST /api/v3/command` with `MoviesSearch`/`EpisodeSearch`
   directly — same pattern already used extensively elsewhere in this
   cluster's arr-suite automation. Step 0's pre-flight check already ruled
   out the "nothing else exists" case, so this fallback only ever fires when
   real alternatives are known to exist; the remaining risk is just "which
   specific one gets grabbed," not "will anything be grabbed at all."
4. Record the outcome (which of steps 0-3 fired, success/failure, any error)
   — this is what the Issues page's Arr Action column reads from (§8).

"Recent" in step 2 should be bounded — don't blocklist a release from a grab
that happened years ago and has since been legitimately superseded by an
upgrade; a config option (`HistoryLookbackDays`, default 30) bounds this.

### 4.2 Why delete is functionally necessary, not just a design preference

Worth being explicit about the reasoning, since "just blocklist and let
Radarr/Sonarr's normal upgrade search handle it, without ever deleting
anything" is a real alternative someone could reasonably propose, and it's
worth explaining directly why it doesn't reliably work rather than asserting
delete-first as an unexamined default.

Radarr/Sonarr's import-time decision for a *new* grab, when the movie/episode
already has a file on record, is an "is this actually an upgrade?" quality/
score comparison — **confirmed live** in the same `GET /api/v3/release`
response used for the pre-flight check above, which already returns real
rejection reasons like `"Existing file meets cutoff"`. Radarr/Sonarr have
**no concept of "corrupt"** in that comparison at all — only quality and
score. A same-quality replacement for a corrupt file can get **rejected at
import time as "not an upgrade,"** leaving the corrupt file in place and
silently discarding the good replacement Radarr/Sonarr just spent time and
indexer/download-client resources fetching. Deleting the file first removes
this failure mode entirely: an empty slot always imports whatever lands,
regardless of whether it would have scored as an "upgrade" over the (now
gone) corrupt copy.

### 4.3 Recommended prerequisite: enable Radarr/Sonarr's Recycle Bin

Directly addresses the user's "can we retain the existing known-bad media to
revert to it?" question, without reopening the filesystem-permission
boundary §4.4 (below) establishes. Radarr/Sonarr already ship exactly this
feature — `Settings → Media Management → Recycle Bin` — a path where deleted
files get *moved* instead of erased, auto-purged after a configurable
retention window (`recycleBinCleanupDays`, currently `7` on both this user's
real instances, itself changeable in that same settings screen). Since
`DELETE /api/v3/moviefile/{id}` is the exact same delete path this feature
already intercepts, **turning on Recycle Bin on both Radarr and Sonarr gets
"retain the known-bad file until we're sure the replacement worked" for
free, using a mechanism Radarr/Sonarr already own and already have
permissions for** — no new plugin capability, no filesystem access on the
Jellyfin side, nothing to build.

**This is documented here as a strongly recommended prerequisite, not built
into the plugin's own remediation flow** — checking whether it's actually
configured is cheap, though (`GET /api/v3/config/mediamanagement`, same call
used above), so the Issues page (§8) or settings page (§7) can show a live
warning banner when `EnableArrForwarding` is on but Recycle Bin is off on a
configured server, prompting the admin to either enable it or acknowledge
the risk explicitly. Exact placement (settings page vs. Issues page) is an
implementation-time call, not decided here.

### 4.4 The plugin never touches the filesystem for this feature

**Raised directly by the user and worth stating as an explicit guarantee,
not an assumption:** everything in §4 is an outbound HTTP API call *to*
Radarr/Sonarr — `DELETE /api/v3/moviefile/{id}` is a request the plugin sends
over the network, not a filesystem operation the plugin performs itself. The
actual file deletion happens inside the Radarr/Sonarr process, using
permissions *they* already have on their own CephFS mount (the
idmap-punch/`cephfs-rw` group pattern documented in
`k8s-proxmox/docs/CEPHFS-LXC-PERMISSIONS.md` and already relied on
throughout this user's `ct:505`/`ct:506` deployment).

Concretely: **the Jellyfin container/process this plugin runs inside needs
zero additional filesystem permissions for this feature** — no write access,
no delete access, nothing beyond what it already has today for read-only
scanning. The only new capability the plugin needs is outbound network access
to the configured Radarr/Sonarr URLs (already true today, since Jellyfin and
Radarr/Sonarr already share a LAN in this deployment) plus the API keys
entered in settings (§7). This is a meaningfully smaller trust/permission
footprint than it might first sound like, and worth calling out plainly in
the eventual settings-page UI copy too, not just this doc.

---

## 5. Data model

New SQLite table, separate from `scan_results` (existing table, untouched) so
this feature's data can be purged/reset independently and doesn't grow the
hot path of every scan write:

```sql
CREATE TABLE IF NOT EXISTS arr_remediation (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id         TEXT NOT NULL,           -- Jellyfin item GUID (matches scan_results.item_id)
    scan_record_id  INTEGER,                 -- FK to the scan_results row that triggered this, if any
    file_path       TEXT NOT NULL,
    arr_app         TEXT NOT NULL,           -- 'radarr' | 'sonarr'
    arr_server_name TEXT,                    -- which configured server (multi-server support, see PluginConfiguration.ArrServers)
    match_method    TEXT NOT NULL,           -- 'provider_id' | 'path_suffix' | 'path_prefix_map' | 'unmatched'
    arr_item_id     INTEGER,                 -- Radarr movieId / Sonarr seriesId, null if unmatched
    arr_file_id     INTEGER,                 -- moviefile/episodefile id, null if unmatched
    action_taken    TEXT,                    -- 'deleted_and_blocklisted' | 'deleted_and_searched' | 'no_replacement_available' | 'unmatched' | 'skipped_cap' | 'skipped_cooldown' | 'skipped_cycle_limit' | 'dry_run'
    status          TEXT NOT NULL,           -- 'pending' | 'success' | 'failed' | 'skipped' | 'blocked'
    error_message   TEXT,
    requested_at    TEXT NOT NULL,           -- ISO 8601, matches existing timestamp convention
    completed_at    TEXT,
    retry_count     INTEGER NOT NULL DEFAULT 0,
    cycle_number    INTEGER NOT NULL DEFAULT 1  -- 1 = first time this item_id has ever been forwarded; see §5.1
);

CREATE INDEX IF NOT EXISTS idx_arr_remediation_item_id ON arr_remediation(item_id);
CREATE INDEX IF NOT EXISTS idx_arr_remediation_status ON arr_remediation(status);
```

Matches this codebase's existing conventions exactly: `long`/`INTEGER`
primary keys, ISO-8601 string timestamps (not native SQLite datetime), a
short-string "enum as text" pattern rather than a foreign lookup table (same
choice `ScanRecord.ScanPhase`/`ScanStatus` make as plain ints with a
documented mapping — using text here instead of int since these enums are
purely internal/dashboard-facing, never round-tripped through a public API
response the way `ScanStatus` is).

`Data/Models/ArrRemediationRecord.cs` (C# entity, mirrors `ScanRecord.cs`'s
style exactly):

```csharp
namespace Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;

/// <summary>
/// Database entity representing one attempt to forward a bad file to
/// Radarr/Sonarr for deletion + replacement.
/// </summary>
public class ArrRemediationRecord
{
    /// <summary>Gets or sets the auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the Jellyfin item GUID.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating scan_results row ID, if any.</summary>
    public long? ScanRecordId { get; set; }

    /// <summary>Gets or sets the full file path at the time this was queued.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets which app this targets ("radarr" or "sonarr").</summary>
    public string ArrApp { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured server name this routed to, if matched.</summary>
    public string? ArrServerName { get; set; }

    /// <summary>Gets or sets how the Jellyfin item was matched to an arr item.</summary>
    public string MatchMethod { get; set; } = string.Empty;

    /// <summary>Gets or sets Radarr's movieId or Sonarr's seriesId, if matched.</summary>
    public int? ArrItemId { get; set; }

    /// <summary>Gets or sets the moviefile/episodefile ID, if matched.</summary>
    public int? ArrFileId { get; set; }

    /// <summary>Gets or sets which remediation action was actually taken.</summary>
    public string? ActionTaken { get; set; }

    /// <summary>Gets or sets the outcome status ("pending", "success", "failed", "skipped").</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the error message, if the action failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets when this remediation was queued (ISO 8601).</summary>
    public string RequestedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets when this remediation finished, successfully or not (ISO 8601).</summary>
    public string? CompletedAt { get; set; }

    /// <summary>Gets or sets how many times this has been retried after a transient failure.</summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets which forwarding cycle this is for <see cref="ItemId"/> --
    /// 1 the first time this item is ever forwarded, incrementing each
    /// subsequent time the same item fails again after a prior remediation
    /// attempt. Drives the trip-wire in §5.1.
    /// </summary>
    public int CycleNumber { get; set; } = 1;
}
```

### 5.1 Trip-wire: stop auto-cycling after N attempts, flag for manual review

Added at the user's explicit request during design review (2026-08-23): if
the *same* Jellyfin item gets forwarded, "fixed," and then fails again
repeatedly, that's a strong signal the problem isn't a bad release at all
(e.g. a codec this environment's ffmpeg genuinely can't decode, or a
persistently flaky source) — auto-cycling forever just wastes Radarr/Sonarr
grabs and indexer queries without ever actually fixing anything.

`CycleNumber` (§5's table/entity) is computed when a new remediation is
enqueued: `1 + COUNT(*) FROM arr_remediation WHERE item_id = ? AND status =
'success'` (i.e. how many times a remediation for this exact item has
already completed successfully before). A new `MaxRemediationCycles`
setting (§7, default 3) is checked before the worker processes a row: if
`CycleNumber > MaxRemediationCycles`, the row is marked `status = 'blocked'`,
`action_taken = 'skipped_cycle_limit'`, and **no further automatic attempts
are made for that item ever again** until an admin manually clears/overrides
it (a "Reset cycle count" action on the item's dashboard detail modal,
alongside the existing manual "Send to Radarr/Sonarr" button from §8).

**Alert surface** (the user asked for this not to be silent): Phase 2 ships
a dashboard-only signal — a persistent banner (reusing the exact
`active-scan-banner` visual pattern already shipped in the dashboard for
"scan in progress," styled instead as an amber "Needs Manual Review: N
item(s) blocked after repeated failures" banner, always visible whenever the
blocked count is nonzero, not just while a scan is running) plus a "Blocked"
value in the new Arr Action column (§8) so a blocked item is easy to filter
to directly. **Deferred to Phase 4 as a nice-to-have, not committed for v1**:
pushing an actual Jellyfin admin notification (Dashboard → Notifications)
instead of/in addition to the dashboard banner — Jellyfin does have a native
admin-notification mechanism plugins can push into, but this plugin has no
existing code touching it, so the exact interface needs verification at
implementation time rather than being speced here without evidence.

`IDatabaseManager` gains matching methods (`EnqueueRemediationAsync`,
`GetPendingRemediationsAsync`, `UpdateRemediationAsync`,
`GetRemediationForItemAsync`, `CountRemediationsSinceAsync` — backs the
daily-cap check in §9 — and `CountSuccessfulRemediationsForItemAsync` — backs
`CycleNumber`/the trip-wire in §5.1), implemented in `SqliteDatabaseManager`
alongside the existing `scan_results` methods, same file (it already owns the
schema-init/migration logic for the plugin's one SQLite database — this new
table's `CREATE TABLE IF NOT EXISTS` belongs in that same init path, not a
separate database file).

---

## 6. New components

### 6.1 Directory layout

```
Jellyfin.Plugin.MediaIntegrityScanner/
├── ArrIntegration/
│   ├── IArrClient.cs
│   ├── ArrClientBase.cs
│   ├── RadarrClient.cs
│   ├── SonarrClient.cs
│   ├── Models/
│   │   ├── RadarrMovie.cs
│   │   ├── RadarrMovieFile.cs
│   │   ├── RadarrHistoryRecord.cs
│   │   ├── SonarrSeries.cs
│   │   ├── SonarrEpisode.cs
│   │   ├── SonarrEpisodeFile.cs
│   │   └── SonarrHistoryRecord.cs
│   ├── ArrItemMatcher.cs
│   ├── IArrRemediationService.cs
│   ├── ArrRemediationService.cs      -- orchestrates one remediation attempt end-to-end
│   └── ArrRemediationWorker.cs       -- IHostedService, drains the SQLite queue
└── Web/
    └── integrity_issues.html         -- new dedicated page, see §8
```

### 6.2 `ArrClientBase` — shared HTTP plumbing

Borrows Seerr's shared-base-class shape and contextual error wrapping;
explicitly fixes the "no timeout" gap found in both Seerr and JellyGlance.
Uses `IHttpClientFactory` (the correct Jellyfin-plugin-DI pattern — avoids
per-call `new HttpClient()` socket exhaustion, which neither piece of prior
art bothered with either):

```csharp
namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

public abstract class ArrClientBase
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    protected readonly ILogger Logger;

    protected ArrClientBase(IHttpClientFactory httpClientFactory, string baseUrl, string apiKey, ILogger logger)
    {
        _http = httpClientFactory.CreateClient(nameof(ArrClientBase));
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v3/");
        _http.Timeout = TimeSpan.FromSeconds(15); // Seerr/JellyGlance both leave this unbounded -- don't repeat that
        _apiKey = apiKey;
        Logger = logger;
    }

    protected async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("X-Api-Key", _apiKey);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Seerr's exact pattern: contextual message, original preserved as .cause equivalent.
            throw new ArrClientException($"[{GetType().Name}] GET {path} failed: {ex.Message}", ex);
        }
    }

    // PostAsync<TReq,TResp>, DeleteAsync, etc. -- same shape, omitted for brevity.
}
```

### 6.3 `ArrItemMatcher` — implements §3's matching strategy

```csharp
public interface IArrItemMatcher
{
    Task<ArrMatchResult> MatchMovieAsync(BaseItem movie, RadarrClient client, CancellationToken ct);
    Task<ArrMatchResult> MatchEpisodeAsync(Episode episode, ILibraryManager library, SonarrClient client, CancellationToken ct);
}

public record ArrMatchResult(bool Matched, string MatchMethod, int? ArrItemId, int? ArrFileId);
```

Implementation follows §3.1 then §3.2 in order, returning `Matched = false,
MatchMethod = "unmatched"` if nothing works — the caller (§6.4) treats that
as a clean, expected terminal state, not an exception.

### 6.4 `ArrRemediationService` + `ArrRemediationWorker`

This is the piece that directly addresses the Seerr durability gap (§2.2).
**Nothing calls Radarr/Sonarr synchronously from the scan path.**
`ScanEngine.ScanItemAsync`'s existing failure branch (right where
`LogScanFailed` already fires today) only *enqueues* a row via
`IDatabaseManager.EnqueueRemediationAsync` — cheap, local, can't fail due to
Radarr/Sonarr being down. A separate `IHostedService`
(`ArrRemediationWorker`, registered the same way `LibraryMonitor` already is
in `PluginServiceRegistrator`) polls for `status = 'pending'` rows on a timer
and does the actual work, with its own retry/backoff independent of the scan
loop. If Radarr/Sonarr is unreachable, the row just stays `pending` and gets
retried next poll — the exact resync-on-recovery behavior Seerr's open issues
say it's missing.

```csharp
public class ArrRemediationWorker : IHostedService, IDisposable
{
    private Timer? _timer;

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new Timer(async _ => await ProcessQueueAsync().ConfigureAwait(false),
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    private async Task ProcessQueueAsync()
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableArrForwarding != true) return;

        var todayCount = await _db.CountRemediationsSinceAsync(DateTime.UtcNow.Date).ConfigureAwait(false);
        var pending = await _db.GetPendingRemediationsAsync().ConfigureAwait(false);

        foreach (var record in pending)
        {
            if (todayCount >= config.MaxAutoRemediationsPerDay)
            {
                await _db.UpdateRemediationAsync(record.Id, status: "skipped", actionTaken: "skipped_cap").ConfigureAwait(false);
                continue;
            }

            await _remediationService.ProcessAsync(record, config.ArrForwardingDryRun).ConfigureAwait(false);
            todayCount++;
        }
    }

    // StopAsync/Dispose: standard timer cleanup, omitted.
}
```

---

## 7. Settings

New `PluginConfiguration` properties (same style as existing ones — every
one gets an XML-doc explaining *why*, not just what):

```csharp
/// <summary>
/// Gets or sets a value indicating whether failed scans are forwarded to
/// Radarr/Sonarr for deletion + re-download. Off by default -- this deletes
/// real files on another system; an admin has to explicitly opt in.
/// </summary>
public bool EnableArrForwarding { get; set; }

/// <summary>
/// Gets or sets a value indicating whether forwarding only logs what it
/// would do, without actually calling Radarr/Sonarr's delete/blocklist
/// endpoints. True by default even when EnableArrForwarding is turned on,
/// so a first-time enable doesn't immediately start deleting files -- an
/// admin has to explicitly turn this off too, a deliberate two-step opt-in.
/// </summary>
public bool ArrForwardingDryRun { get; set; } = true;

/// <summary>
/// Gets or sets which scan outcomes trigger forwarding. Fail-only by
/// default -- Error means the *scan itself* broke (ffprobe crashed, a read
/// error), which is a weaker signal that the media is actually bad than a
/// completed scan that affirmatively found corruption.
/// </summary>
public ArrForwardTrigger ArrForwardOnStatus { get; set; } = ArrForwardTrigger.FailOnly;

/// <summary>
/// Gets or sets the maximum number of automatic remediation actions
/// (delete + blocklist/search) per rolling day, across all configured
/// servers. Protects against a scanner bug, a bad ffmpeg build, or a
/// misconfigured hardware-decode path flagging a large swath of the
/// library as corrupt all at once and mass-blocklisting real, good
/// releases as a result. Anything beyond this cap is skipped, not queued
/// past midnight -- surfaces in the dashboard as "skipped (daily cap)" for
/// manual review instead. A plain settings-page number, not a rebuild/
/// restart-gated value -- meant to be bumped up temporarily during an
/// initial library-wide review (a fresh install scanning years of existing
/// media will legitimately find more than 10 bad files on day one) and
/// brought back down afterward.
/// </summary>
public int MaxAutoRemediationsPerDay { get; set; } = 10;

/// <summary>
/// Gets or sets how many hours must pass before the same Jellyfin item can
/// be forwarded again, even if it fails a later scan too. Prevents a
/// persistently-failing item (e.g. a codec this environment's ffmpeg build
/// genuinely can't decode, not a corrupt file) from repeatedly cycling
/// through delete+redownload forever. Distinct from
/// <see cref="MaxRemediationCycles"/>, which is a hard stop after N total
/// attempts regardless of timing -- this setting only spaces successive
/// attempts out, it doesn't cap how many can eventually happen.
/// </summary>
public int RemediationCooldownHours { get; set; } = 168; // 1 week

/// <summary>
/// Gets or sets the maximum number of times the same Jellyfin item can be
/// automatically forwarded before it's permanently blocked from further
/// auto-forwarding and flagged in the dashboard for manual review (see
/// §5.1 of ARR-INTEGRATION-PROPOSAL.md). Repeated failures on the exact
/// same item after being "fixed" each time is a strong signal the real
/// problem isn't a bad release -- more likely an unsupported codec or a
/// persistently bad source -- and auto-cycling past that point just wastes
/// Radarr/Sonarr grabs without ever actually fixing anything. An admin can
/// manually reset an item's cycle count from its dashboard detail view to
/// let auto-forwarding try again.
/// </summary>
public int MaxRemediationCycles { get; set; } = 3;

/// <summary>
/// Gets or sets how many days back to look for a "grabbed" history record
/// when deciding what to blocklist alongside a deletion. A grab older than
/// this is assumed already-superseded and not worth blocklisting -- the
/// plugin just deletes the file and triggers a plain search instead.
/// </summary>
public int HistoryLookbackDays { get; set; } = 30;

/// <summary>
/// Gets or sets the configured Radarr servers. Supports more than one to
/// match deployments with separate servers per library (e.g. a
/// "Family"/Disney server alongside the main one) -- each entry's
/// LibraryPathPrefixes decides which scanned files route to it.
/// </summary>
public List<ArrServerConfig> RadarrServers { get; set; } = new();

/// <summary>Gets or sets the configured Sonarr servers. See <see cref="RadarrServers"/>.</summary>
public List<ArrServerConfig> SonarrServers { get; set; } = new();

public enum ArrForwardTrigger { FailOnly, FailAndError }
```

`ArrServerConfig` (a plain settings sub-object, matching how a hypothetical
multi-value setting would be modeled here — this plugin has no existing
example of a *list*-valued config property, so this introduces that pattern
for the first time; flagged explicitly as new ground, not an existing
convention being followed):

```csharp
public class ArrServerConfig
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public List<string> LibraryPathPrefixes { get; set; } = new();
}
```

**Secret handling, borrowing JellyGlance's rule directly (§2.3):** the
settings-page save handler must treat an empty/unchanged `ApiKey` field as
"keep the existing value," not "blank it out" — the settings API response
should mask stored keys (e.g. `"••••••••"`) rather than round-tripping the
real value to the browser, and the save endpoint should skip overwriting
`ApiKey` when the incoming value is exactly that mask. This repo's plugin
config today has no secrets at all (no existing pattern to follow for this)
-- this is the first feature that needs it.

**Settings page UI** (`integrity_settings.html`, following its existing
form-field conventions): a new "Radarr/Sonarr Integration" section — enable
toggle, dry-run toggle, trigger-status radio (Fail only / Fail + Error),
numeric inputs for the cap/cooldown/lookback/cycle-limit settings (each with
a field hint matching this file's existing `.field-hint` convention — e.g.
the daily-cap hint should explicitly mention it's safe to raise temporarily
for an initial backlog review, per its own doc comment above), and a
repeatable "Add Server" mini-form (Name, App type dropdown, URL, API Key, a
textarea for path-prefix pairs) appended to a list, mirroring how
Radarr/Sonarr's own UI lets you add multiple download clients.

---

## 8. A dedicated "Media Issues" page, not more columns on the main dashboard

**Revised 2026-08-23 at the user's explicit request.** The original version
of this section bolted an "Arr Action" column, a banner, and two buttons onto
the existing `integrity_dashboard.html` — which is already a dense page
(health stats, scan-scope controls, the full results table with its own
filters/pagination/export, database backup, database maintenance, all on one
screen). Adding a whole second workflow (matching, remediation status, bulk
retry, cycle-limit recovery) on top of that would make an already-crowded
page worse, not better. Instead: **a new, focused, top-level page whose only
job is showing and acting on media with problems.**

### 8.1 Page registration

Follows the exact existing pattern in `Plugin.cs` (§ confirmed by reading the
real file — two pages today, `integrity_dashboard.html` and
`integrity_settings.html`, each a `PluginPageInfo` pointing at an embedded
resource). A third entry, given its own main-menu presence since the whole
point is making this easy to find, not one click deeper than the main
dashboard:

```csharp
public IEnumerable<PluginPageInfo> GetPages()
{
    return new[]
    {
        new PluginPageInfo
        {
            Name = "Media Integrity Scanner",
            EmbeddedResourcePath = GetType().Namespace + ".Web.integrity_dashboard.html",
            EnableInMainMenu = true,
            MenuIcon = "fact_check"
        },
        new PluginPageInfo
        {
            Name = "Media Issues",
            EmbeddedResourcePath = GetType().Namespace + ".Web.integrity_issues.html",
            EnableInMainMenu = true,
            MenuIcon = "healing"
        },
        new PluginPageInfo
        {
            Name = "Media Integrity Scanner Settings",
            EmbeddedResourcePath = GetType().Namespace + ".Web.integrity_settings.html"
        }
    };
}
```

`.csproj`'s embedded-resource glob already picks up everything under `Web/`
(confirmed — that's how the existing two pages get bundled today), so
`integrity_issues.html` needs no separate build-file change.

### 8.2 Cross-navigation

- Main dashboard (`integrity_dashboard.html`) gets a small, persistent nav
  item next to the existing "Settings »" link — `Media Issues (N) »`, where
  `N` is `FailedFiles + ErroredFiles` from the existing `/MediaIntegrity/Status`
  response (both fields already exist today, no API change needed for the
  count itself). Hidden entirely (no badge, plain text link) when `N` is 0.
  This is the *only* footprint this feature has on the main dashboard —
  everything else lives on the new page.
- The new page gets a matching `« Dashboard` / `Settings »` link pair at the
  top, same visual treatment as the existing pages' own nav links, so moving
  between all three feels like one plugin, not three unrelated tools.

**A static visual mockup of this page was built and reviewed 2026-08-23**
(styled to match Jellyfin's real admin dark theme and this plugin's existing
color/component conventions) — every state described below (Blocked banner,
each Arr Action chip variant, an Unmatched row, bulk-select) is represented
in it with realistic sample content, not just described in prose here.

### 8.3 Page content

Scoped tightly to "media with problems," nothing else:

- **Compact stat row** (4 cards, not the main dashboard's 6): Failed,
  Errored, Unmatched (arr-forwarding couldn't identify a target), Blocked
  (hit the §5.1 cycle limit, needs manual review).
- **"Needs Manual Review" banner** (moved here from the old design, and now
  this page is its natural, permanent home) — visible whenever the Blocked
  count is nonzero, same visual treatment as the existing `active-scan-banner`
  pattern, amber instead of blue.
- **Filter row**: Status (Fail / Error / both — no Pass/Pending here, this
  page only ever shows problems), Phase, and Arr Action status (Pending /
  Sent-searching / Unmatched / No replacement found / Blocked / Failed /
  Dry-run / Not forwarded).
  Deliberately *not* the full scope row (library/name/season) from the main
  dashboard — those control what gets *scanned*, which isn't this page's job;
  this page always shows every current problem regardless of scan scope.
- **Table**: checkbox column (for bulk actions below) · File · Status
  (Fail/Error) · Phase · Last Scanned · Error (truncated, click to expand
  the same detail modal pattern the main dashboard already has) · **Arr
  Match** (matched movie/series title + which server, or "Unmatched") ·
  **Arr Action** (current state + inline per-row buttons: `Send`, or `Reset
  Cycle` + `Send` when Blocked).
- **Bulk action bar** (appears once ≥1 row is checked): "Send N Selected to
  Radarr/Sonarr" — genuinely useful on a dedicated page in a way it wasn't
  worth building into the crowded main dashboard; matches the bulk-action
  pattern this cluster's own Radarr/Sonarr automation already uses
  extensively (`DELETE /api/v3/queue/bulk`) for exactly this kind of "act on
  many flagged items at once" workflow.

### 8.4 New API surface backing this page

- `GET /MediaIntegrity/Issues?status=&phase=&arrAction=&page=&pageSize=` —
  **new, dedicated endpoint**, not a reuse/extension of the existing
  `GET /MediaIntegrity/Results`. Deliberately kept separate: this query needs
  to join `scan_results` (status IN Fail/Error) against each item's *latest*
  `arr_remediation` row, which `Results` has no reason to know about for its
  own (much broader) purpose. Backed by a new `IDatabaseManager` method,
  `GetIssuesAsync(...)`, implemented as a single SQL query with a correlated
  subquery for "latest arr_remediation row per item_id" rather than N+1
  lookups.
- `POST /MediaIntegrity/ArrRemediation/{itemId}` — manual single-item
  trigger (already speced in the original §8, unchanged).
- `POST /MediaIntegrity/ArrRemediation/Bulk` — new, body `{ "itemIds":
  ["...", "..."] }`, enqueues one remediation row per ID, same
  bypass-daily-cap-but-not-cycle-limit semantics as the single-item version.
- `POST /MediaIntegrity/ArrRemediation/{itemId}/ResetCycle` — new, clears
  `CycleNumber` history for that item so it's eligible for auto-forwarding
  (or a fresh manual send) again.

### 8.5 A deliberate, named tradeoff: no shared JS module

Both existing pages (`integrity_dashboard.html`, `integrity_settings.html`)
are self-contained files with their own inline `<script>` — neither
references an external `.js` file, and nothing in this codebase today loads
a shared script across pages. `integrity_issues.html` will duplicate a small
amount of boilerplate (`apiFetch`, pagination rendering, the detail-modal
pattern) rather than introduce a new shared-script-loading mechanism this
plugin has never used. Worth naming explicitly as a conscious choice, not an
oversight: the duplicated surface is small (tens of lines, not hundreds), and
matches the existing pattern rather than fighting it. Revisit only if a
*fourth* page ever needs the same boilerplate — three own copies of ~50 lines
is fine; a real shared-module case would need the actual duplication to hurt
first.

---

## 9. Safety guardrails — summary

All of these are already threaded through §5-§8 above; collected here as a
single checklist since this is the part most worth getting right before ever
flipping the default:

- [ ] Off by default (`EnableArrForwarding = false`).
- [ ] Dry-run by default even once enabled (`ArrForwardingDryRun = true`) —
      two explicit opt-in steps, not one.
- [ ] Fail-only triggers by default, Error is opt-in.
- [ ] Pre-flight availability check (§4 step 0) — never delete a file
      without first confirming a real replacement is findable; a title
      with zero viable candidates keeps its bad-but-present copy instead
      of ending up with nothing at all.
- [ ] Recycle Bin recommended as a prerequisite on every configured
      Radarr/Sonarr server (§4.3), with a live settings/Issues-page warning
      when it's off but forwarding is on — makes the delete step recoverable
      using a mechanism Radarr/Sonarr already own, no new plugin capability.
- [ ] Daily cap (`MaxAutoRemediationsPerDay`, default 10) — bounds the blast
      radius of a scanner false-positive.
- [ ] Per-item cooldown (`RemediationCooldownHours`, default 1 week) — no
      repeat-cycling on a persistently-failing item.
- [ ] Hard trip-wire (`MaxRemediationCycles`, default 3) — after N total
      automatic attempts on the same item, stop entirely and surface a
      dashboard alert for manual review, rather than cycling forever
      (§5.1; the cooldown above only spaces attempts out in time, this is
      the separate hard ceiling on total attempts).
- [ ] Durable outbox (SQLite queue + background worker), not a synchronous
      call from the scan path — a Radarr/Sonarr outage can't crash or stall
      scanning, and pending work survives a Jellyfin restart.
- [ ] Every action fully logged and visible in the dashboard, including
      unmatched/skipped/dry-run outcomes, not just successes.
- [ ] Manual per-item trigger available independent of the automatic path,
      for admin override.
- [ ] Never guess on ambiguous matches — unmatched is a clean terminal
      state, not a forced best-effort delete.

---

## 10. Testing strategy

Matches this repo's existing conventions (Moq-based unit tests, `dotnet
test` gating CI, plus the Docker-based integration suite for real
end-to-end verification):

- **Unit tests**, mocking `HttpMessageHandler` (standard .NET pattern for
  testing code built on `HttpClient`/`IHttpClientFactory`) to verify
  `ArrClientBase`'s request shape, error wrapping, and timeout behavior
  without a real server.
- **`ArrItemMatcher` unit tests** covering: provider-ID match (movie and
  episode), path-suffix fallback match, path-prefix-map fallback, and the
  unmatched case — each as an isolated, deterministic test against a mocked
  `RadarrClient`/`SonarrClient`.
- **`ArrRemediationService` unit tests** covering the full delete → history
  lookup → blocklist sequence from §4, including the "no recent history,
  fall back to plain search" branch, and the daily-cap/cooldown skip logic.
- **Integration test extension**: `tests/docker-compose.integration.yml`
  could add a lightweight Radarr container (there's precedent for spinning
  up real service containers for this suite already) seeded with one known
  movie, to prove the real HTTP round-trip against genuine Radarr responses
  — not required for v1, flagged as a nice-to-have once the unit-test layer
  is solid.
- **Real-world dry-run verification** (not automated, a manual step before
  ever disabling dry-run for real): enable forwarding with dry-run on
  against this user's actual `jellyfin-test`/Radarr/Sonarr instances, force
  one scan to fail against a disposable test file (e.g. re-run the existing
  `bad-*.mp4` corruption-matrix files from `tests/generate-test-media.sh`
  through the plugin against a throwaway Radarr-tracked movie), and confirm
  the dashboard shows exactly the expected "would delete + blocklist"
  outcome with the correct matched movie/file IDs before ever flipping dry
  run off.

---

## 11. Rollout phasing

Smallest safe slice first, each phase independently mergeable/releasable
(matching this repo's existing PR-per-slice convention). Revised 2026-08-23
per the user's explicit choice to merge Radarr and Sonarr into Phase 1
rather than stage Sonarr later:

1. **Phase 1 — Radarr + Sonarr together, manual trigger only, on the new
   dedicated page.** No auto-forward, no background worker yet. Both apps'
   matching (§3.1, movies and episodes), the delete-then-blocklist sequence
   (§4), the data model including `CycleNumber` (§5), the new
   `integrity_issues.html` page itself with its read-only table + manual
   single/bulk "Send" actions (§8.1-§8.4, minus the parts of §8.3/§8.4 that
   only make sense once auto-forwarding exists — no Blocked state or "Needs
   Manual Review" banner yet, since nothing can hit the cycle limit without
   automation running). Proves the core mechanism and the new page's
   plumbing work for both movie and TV content before any automation risk
   is introduced. Bigger first slice than a Radarr-only start would have
   been (episode matching is genuinely more complex than movie matching,
   and a whole new page is more than a dashboard tweak), accepted
   deliberately per the user's explicit choice on both counts.
2. **Phase 2 — Auto-forward with every guardrail from §9 live.** Adds
   `ArrRemediationWorker`, the daily cap, per-item cooldown, the
   `MaxRemediationCycles` trip-wire, and — now meaningful for the first time
   — the Blocked state, the "Reset Cycle" action, and the "Needs Manual
   Review" banner on the Phase-1 page (§5.1, §8.3), plus the dry-run toggle.
   Covers both apps at once since Phase 1 already built both.
3. **Phase 3 — Multi-server support + path-prefix-mapping settings UI.**
   Needed for this user's real Disney/Family-server split. Confirmed staged
   here rather than folded into Phase 1/2 -- path-prefix routing is pure
   settings/routing complexity, independent of whether the core
   single-server mechanism already works.
4. **Phase 4 — Polish.** Error-status opt-in, CSV export for the Issues
   page, richer Arr Action filtering, and investigating a real Jellyfin
   native-notification push for the §5.1 trip-wire alert (currently
   dashboard-banner-only, see §5.1's note on why that's deferred rather
   than speced now).

---

## 12. Decisions made during design review (2026-08-23)

§12 originally posed five open questions before Phase 1 could start. All
five were answered directly by the user; recorded here for anyone reading
this doc later, with a pointer to where each decision is actually reflected
in the design above.

1. **Delete-then-blocklist ordering (§4): confirmed.** The user also raised
   a real question about filesystem permissions -- addressed head-on in
   §4.4: the plugin never touches the filesystem itself, every action is
   an HTTP call to Radarr/Sonarr, who perform the actual delete with
   permissions they already have. No new filesystem access is needed on the
   Jellyfin side for this feature at all. **Follow-up 2026-08-23**: the user
   then asked whether delete is reversible and what happens if no
   replacement can be found -- neither was addressed in the original design.
   Added §4.2 (why delete is functionally necessary, verified live via
   Radarr's real import-rejection logic), §4.3 (Recycle Bin as the
   recommended, zero-new-permissions way to make the delete recoverable),
   and a new pre-flight availability check (§4 step 0, verified live via a
   real `GET /api/v3/release` call) that refuses to delete at all when no
   viable replacement exists.
2. **Daily cap: 10/day confirmed as the default**, with the explicit
   requirement that it stay a normal, freely-adjustable settings-page value
   (not a rebuild-gated constant) so it can be raised temporarily during an
   initial full-library review. Reflected in `MaxAutoRemediationsPerDay`'s
   updated doc comment in §7.
3. **Cooldown: 1 week confirmed as the default, and configurable.** The user
   also asked for a hard trip-wire independent of the cooldown -- "not fire
   after cycling more than 3 or 4 times without an alert for manual review."
   This is the new `MaxRemediationCycles` setting and the whole of new §5.1
   (cycle tracking, the `Blocked` state, the "Needs Manual Review" banner,
   and the manual "Reset cycle count" recovery action -- both now living on
   the dedicated Issues page from decision 6 below, not the main dashboard).
4. **Phase 1 scope: Radarr + Sonarr together**, not Radarr-only as
   originally proposed. Reflected in the revised §11 above.
5. **Multi-server routing: path-prefix-based, staged as its own later
   phase** (now Phase 3 after the Phase 1/2 merge) -- confirmed as
   originally proposed, no changes needed to §3.2/§7's design.
6. **A dedicated "Media Issues" page, not more UI on the existing dashboard**
   (raised in a follow-up message, not the original five questions, but
   recorded here for the same reason). The user's own framing: the existing
   dashboard is "super overloaded with features" already, and cramming a
   second workflow (matching status, remediation actions, bulk retry,
   cycle-limit recovery) onto it would make it worse rather than better.
   This replaced the entire original §8 -- see the current §8 for the full
   redesign (new top-level page, new `GetIssuesAsync`/bulk/reset-cycle API
   endpoints, the main dashboard's footprint reduced to a single small
   `Media Issues (N) »` link).
