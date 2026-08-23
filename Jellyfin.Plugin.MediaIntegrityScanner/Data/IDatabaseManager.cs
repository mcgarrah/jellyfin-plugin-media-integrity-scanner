// Jellyfin Media Integrity Scanner - validates media file integrity using FFmpeg
// Copyright (C) 2026  Michael McGarrah <mcgarrah@gmail.com>
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along
// with this program; if not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Data;

/// <summary>
/// Interface for the scan results database manager.
/// </summary>
public interface IDatabaseManager
{
    /// <summary>
    /// Saves or updates a scan result record.
    /// </summary>
    /// <param name="record">The scan record to persist.</param>
    /// <returns>A task representing the async operation.</returns>
    Task SaveResultAsync(ScanRecord record);

    /// <summary>
    /// Checks if an item already has a passing scan result at or above the given
    /// phase, for the file's current contents (file hasn't changed since that scan).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="filePath">The current file path.</param>
    /// <param name="minPhase">The minimum <see cref="Scanner.ScanPhase"/> (as an int) required for the existing result to count.</param>
    /// <returns>True if an existing scan result at or above <paramref name="minPhase"/> is still valid.</returns>
    Task<bool> IsCurrentAsync(string itemId, string filePath, int minPhase);

    /// <summary>
    /// Marks a batch of items <see cref="Scanner.ScanStatus.Pending"/> for a given phase --
    /// queued but not yet scanned. Called once, upfront, before a library scan actually starts
    /// processing items, so the results table (and the "Pending Only" filter) reflect the real,
    /// currently in-flight queue immediately rather than only ever showing scans that have
    /// already completed. Upserts the same way <see cref="SaveResultAsync"/> does: an item that
    /// already has a stale Fail/Error record for this phase gets overwritten with Pending, since
    /// a rescan is about to supersede it anyway. Callers are expected to only pass items
    /// <see cref="IsCurrentAsync"/> has already said need scanning -- this does not check that itself.
    /// </summary>
    /// <param name="items">The items to mark pending, as (item ID, file path) pairs.</param>
    /// <param name="phase">The <see cref="Scanner.ScanPhase"/> (as an int) being queued.</param>
    /// <returns>A task representing the async operation.</returns>
    Task MarkPendingAsync(IReadOnlyList<(string ItemId, string FilePath)> items, int phase);

    /// <summary>
    /// Gets a summary of scan statistics.
    /// </summary>
    /// <returns>Scan statistics.</returns>
    Task<ScanStatistics> GetStatisticsAsync();

    /// <summary>
    /// Removes all scan records for a given item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to purge.</param>
    /// <returns>A task representing the async operation.</returns>
    Task PurgeItemAsync(string itemId);

    /// <summary>
    /// Purges scan records for any item not present in <paramref name="currentItemIds"/> --
    /// the periodic counterpart to <see cref="PurgeItemAsync"/>'s event-driven purge,
    /// for catching removals the <c>ItemRemoved</c> event missed (plugin offline at
    /// removal time, purge-on-remove was off, an unverified Jellyfin removal path
    /// that doesn't fire the event). As a safety guard against wiping the entire
    /// table from a caller passing an empty set by mistake (e.g. a transient
    /// library-query failure), an empty <paramref name="currentItemIds"/> is treated
    /// as "don't reconcile" rather than "nothing is current" -- callers should not
    /// call this at all if the library query itself failed.
    /// </summary>
    /// <param name="currentItemIds">Every item ID currently present in the library.</param>
    /// <returns>The number of scan-history rows purged (0 if <paramref name="currentItemIds"/> was empty).</returns>
    Task<int> ReconcileAsync(IReadOnlyCollection<string> currentItemIds);

    /// <summary>
    /// Initializes the database schema.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    Task InitializeAsync();

    /// <summary>
    /// Creates a consistent point-in-time snapshot of the database (via SQLite's
    /// <c>VACUUM INTO</c>, safe to run against a live WAL-mode database without
    /// stopping the scanner) into a timestamped file under a <c>backups</c>
    /// subdirectory next to the live database.
    /// </summary>
    /// <returns>The file name (not full path) of the backup that was created.</returns>
    Task<string> BackupAsync();

    /// <summary>
    /// Lists available backups, newest first.
    /// </summary>
    /// <returns>Metadata for every backup file found.</returns>
    Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync();

    /// <summary>
    /// Replaces the live database with the contents of a previously-created backup.
    /// The caller is responsible for ensuring no scan is in progress before calling this.
    /// </summary>
    /// <param name="backupFileName">The backup file name, as returned by <see cref="ListBackupsAsync"/> -- not a full path.</param>
    /// <returns>A task representing the async operation.</returns>
    Task RestoreAsync(string backupFileName);

    /// <summary>
    /// Records one complete Radarr/Sonarr remediation attempt. Phase 1 has no
    /// background worker (see <c>ARR-INTEGRATION-PROPOSAL.md</c>), so a
    /// remediation is always recorded as a single, already-finished row --
    /// there is no separate "enqueue, then update later" step yet.
    /// </summary>
    /// <param name="record">The remediation attempt to record. <see cref="ArrRemediationRecord.Id"/> is ignored on input.</param>
    /// <returns>The new row's auto-generated ID.</returns>
    Task<long> RecordRemediationAsync(ArrRemediationRecord record);

    /// <summary>
    /// Gets the most recent remediation attempt for a given Jellyfin item, or
    /// <c>null</c> if it has never been forwarded.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>The latest <see cref="ArrRemediationRecord"/>, or <c>null</c>.</returns>
    Task<ArrRemediationRecord?> GetLatestRemediationForItemAsync(string itemId);

    /// <summary>
    /// Counts how many past remediation attempts for this item completed
    /// successfully since the most recent "cycle_reset" marker row (see
    /// <c>IArrRemediationService.ResetCycleAsync</c>), or since the
    /// beginning if it's never been reset -- used to compute the next
    /// attempt's <see cref="ArrRemediationRecord.CycleNumber"/>. Not
    /// enforced against any limit until Phase 2's <c>MaxRemediationCycles</c>
    /// trip-wire.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>The number of prior successful remediations since the last reset.</returns>
    Task<int> CountSuccessfulRemediationsForItemAsync(string itemId);

    /// <summary>
    /// Gets a paginated list of every current Fail/Error scan result, each
    /// joined with its most recent remediation attempt if one exists. Backs
    /// the "Media Issues" page (<c>GET /MediaIntegrity/Issues</c>).
    /// </summary>
    /// <param name="status">Optional scan-status filter (2 = fail, 3 = error). Null means both.</param>
    /// <param name="phase">Optional scan-phase filter (1 = header, 2 = full decode).</param>
    /// <param name="arrAction">
    /// Optional Arr Action bucket filter (<c>not_sent</c>, <c>pending</c>, <c>sent</c>,
    /// <c>unmatched</c>, <c>no_replacement</c>, <c>blocked</c>, <c>failed</c>, <c>dry_run</c> --
    /// matching <c>integrity_issues.html</c>'s <c>arrActionBucket()</c> exactly). Null/unrecognized means no filter.
    /// </param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Results per page.</param>
    /// <returns>Paginated issue rows.</returns>
    Task<PagedIssueResults> GetIssuesAsync(int? status, int? phase, string? arrAction, int page, int pageSize);

    /// <summary>
    /// Gets every remediation row still in the <c>pending</c> state --
    /// enqueued by a failed/errored scan (Phase 2 automatic forwarding) but
    /// not yet processed by <c>ArrRemediationWorker</c>. Oldest first, so a
    /// backlog drains in the order items actually broke.
    /// </summary>
    /// <returns>Pending remediation rows.</returns>
    Task<IReadOnlyList<ArrRemediationRecord>> GetPendingRemediationsAsync();

    /// <summary>
    /// Gets whether <paramref name="itemId"/> already has a remediation row
    /// in the <c>pending</c> state, so a repeatedly-failing item doesn't
    /// enqueue a new row on every scan while an earlier one is still
    /// waiting to be processed.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>True if a pending remediation already exists for this item.</returns>
    Task<bool> HasPendingRemediationAsync(string itemId);

    /// <summary>
    /// Gets the most recent remediation attempt for a given Jellyfin item
    /// that has actually finished (any status other than <c>pending</c>),
    /// or <c>null</c> if none has. Unlike <see cref="GetLatestRemediationForItemAsync"/>
    /// (which can return a still-<c>pending</c> row), this is what the
    /// per-item cooldown check (<see cref="PluginConfiguration.RemediationCooldownHours"/>)
    /// needs -- it must look past a just-enqueued pending row to the prior
    /// completed attempt's timestamp.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>The most recent completed <see cref="ArrRemediationRecord"/>, or <c>null</c>.</returns>
    Task<ArrRemediationRecord?> GetLastCompletedRemediationForItemAsync(string itemId);

    /// <summary>
    /// Counts real automatic remediation actions (<c>status</c> of
    /// <c>success</c> or <c>failed</c> -- an actual Radarr/Sonarr call was
    /// attempted, not merely enqueued/skipped/blocked) completed at or
    /// after <paramref name="sinceUtc"/>. Backs the daily-cap check in
    /// <see cref="PluginConfiguration.MaxAutoRemediationsPerDay"/>.
    /// </summary>
    /// <param name="sinceUtc">The UTC instant to count from (inclusive).</param>
    /// <returns>The number of qualifying remediation attempts.</returns>
    Task<int> CountAutoRemediationsSinceAsync(DateTime sinceUtc);

    /// <summary>
    /// Updates an existing remediation row's outcome fields in place
    /// (<c>ArrServerName</c>, <c>MatchMethod</c>, <c>ArrItemId</c>,
    /// <c>ArrFileId</c>, <c>ActionTaken</c>, <c>Status</c>, <c>ErrorMessage</c>,
    /// <c>CompletedAt</c>) -- the Phase 2 counterpart to <see cref="RecordRemediationAsync"/>'s
    /// insert-only Phase 1 behavior, used to transition a <c>pending</c> row
    /// enqueued by a failed scan into its terminal state once
    /// <c>ArrRemediationWorker</c> actually processes it. <c>ItemId</c>,
    /// <c>ScanRecordId</c>, <c>FilePath</c>, <c>ArrApp</c>, <c>RequestedAt</c>,
    /// and <c>CycleNumber</c> are identifying/immutable and never touched by
    /// this update.
    /// </summary>
    /// <param name="record">The record to persist, identified by <see cref="ArrRemediationRecord.Id"/>.</param>
    /// <returns>A task representing the async operation.</returns>
    Task UpdateRemediationAsync(ArrRemediationRecord record);
}

/// <summary>
/// Paginated response for <see cref="IDatabaseManager.GetIssuesAsync"/>.
/// </summary>
public class PagedIssueResults
{
    /// <summary>Gets or sets the list of issue rows for this page.</summary>
    public List<IssueRecord> Items { get; set; } = new();

    /// <summary>Gets or sets the total count of matching Fail/Error scan results.</summary>
    public int TotalCount { get; set; }
}

/// <summary>
/// Metadata describing one on-disk database backup.
/// </summary>
public class DatabaseBackupInfo
{
    /// <summary>
    /// Gets or sets the backup file's name (not full path).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the backup file's size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the backup's creation timestamp, ISO 8601 UTC.
    /// </summary>
    public string CreatedUtc { get; set; } = string.Empty;
}

/// <summary>
/// Summary statistics for scan results. Reflects only items that have at least
/// one *completed* scan record — a <see cref="Scanner.ScanStatus.Pending"/> row
/// (queued via <see cref="IDatabaseManager.MarkPendingAsync"/> but not yet actually
/// scanned) never counts toward any field here, so a large in-flight queue doesn't
/// make these numbers look more complete than they really are. The database also has
/// no notion of a library's total item count; callers with access to
/// <c>ILibraryManager</c> should derive that from the real library count.
/// Pass/fail/error counts use each item's most authoritative (highest scan
/// phase) *completed* result, even if it was scanned in both phases. <see cref="ScannedFiles"/>
/// and <see cref="DeepScannedFiles"/> are phase-specific, since a header scan and
/// a deep scan are independent passes over the library and a file can be
/// "current" for one while still pending the other.
/// </summary>
public class ScanStatistics
{
    /// <summary>
    /// Gets or sets the number of distinct items that have at least a Header-phase
    /// scan result (a FullDecode result also satisfies this, since it implies the
    /// header was read successfully too).
    /// </summary>
    public int ScannedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of distinct items that have a FullDecode-phase
    /// (deep) scan result specifically.
    /// </summary>
    public int DeepScannedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose most recent scan passed.
    /// </summary>
    public int PassedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose most recent scan failed.
    /// </summary>
    public int FailedFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose most recent scan ended in an error.
    /// </summary>
    public int ErroredFiles { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last completed scan.
    /// </summary>
    public string? LastScanTimestamp { get; set; }
}

/// <summary>
/// Point-in-time size/health information for the plugin's own SQLite database.
/// </summary>
public class DatabaseMaintenanceInfo
{
    /// <summary>
    /// Gets or sets the total on-disk size in bytes, including the main
    /// database file and its WAL/SHM sidecar files -- WAL mode keeps data in
    /// those between checkpoints, so the main file alone can understate the
    /// database's real footprint on disk.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets SQLite's own logical size (<c>page_count * page_size</c>),
    /// the size a successful <c>VACUUM</c> would shrink the main file to.
    /// </summary>
    public long LogicalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the estimated space a <c>VACUUM</c> would reclaim
    /// (<c>freelist_count * page_size</c>) -- pages SQLite has already freed
    /// internally (e.g. from deletes) but not yet returned to the OS.
    /// </summary>
    public long ReclaimableBytes { get; set; }
}

/// <summary>
/// Result of a manually or automatically triggered database maintenance pass:
/// an integrity check, followed by a <c>VACUUM</c> if that check passes.
/// </summary>
public class DatabaseMaintenanceResult
{
    /// <summary>
    /// Gets or sets a value indicating whether <c>PRAGMA integrity_check</c>
    /// reported the database as healthy.
    /// </summary>
    public bool IntegrityCheckOk { get; set; }

    /// <summary>
    /// Gets or sets the raw message from <c>PRAGMA integrity_check</c> --
    /// <c>"ok"</c> when healthy, otherwise one line per corruption issue found.
    /// </summary>
    public string? IntegrityCheckMessage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>VACUUM</c> actually ran.
    /// Skipped when the integrity check fails, to avoid rewriting a database
    /// already known to be corrupt.
    /// </summary>
    public bool VacuumRan { get; set; }

    /// <summary>
    /// Gets or sets the on-disk size in bytes before this maintenance pass.
    /// </summary>
    public long SizeBeforeBytes { get; set; }

    /// <summary>
    /// Gets or sets the on-disk size in bytes after this maintenance pass.
    /// Equal to <see cref="SizeBeforeBytes"/> when <see cref="VacuumRan"/> is <c>false</c>.
    /// </summary>
    public long SizeAfterBytes { get; set; }

    /// <summary>
    /// Gets or sets how long the maintenance pass took, in milliseconds.
    /// </summary>
    public long DurationMs { get; set; }
}
