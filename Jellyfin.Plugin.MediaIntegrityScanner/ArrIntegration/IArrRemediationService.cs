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

using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Orchestrates one full remediation attempt for a bad file: matching it to
/// Radarr/Sonarr, the pre-flight availability check, delete-then-blocklist
/// (or the plain-search fallback), and recording the outcome. Implements the
/// flow from <c>ARR-INTEGRATION-PROPOSAL.md</c> section 4.
///
/// Phase 1 only supports manual, one-at-a-time triggering (see
/// <c>MediaIntegrityController.TriggerArrRemediation</c>) -- there is no
/// background worker or automatic forwarding yet (that's Phase 2).
/// </summary>
public interface IArrRemediationService
{
    /// <summary>
    /// Runs the full remediation flow for a single Jellyfin item (a movie or
    /// TV episode -- other item types return an immediate "unmatched"
    /// result, since only those two are Radarr/Sonarr-managed).
    /// </summary>
    /// <param name="item">The Jellyfin item to remediate.</param>
    /// <param name="scanRecordId">The originating scan_results row ID, if this was triggered from a specific failed scan result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed remediation record, already persisted.</returns>
    Task<ArrRemediationRecord> RemediateAsync(BaseItem item, long? scanRecordId, CancellationToken cancellationToken);

    /// <summary>
    /// Phase 2 automatic-forwarding entry point. Called from
    /// <c>ScanEngine</c> right after a Fail/Error scan result is persisted --
    /// cheap and purely local (a single SQLite insert), so it can never fail
    /// due to Radarr/Sonarr being unreachable. Enqueues a <c>pending</c>
    /// remediation row for <c>ArrRemediationWorker</c> to actually process
    /// later, subject to <see cref="PluginConfiguration.EnableArrForwarding"/>,
    /// <see cref="PluginConfiguration.ArrForwardOnStatus"/>, the per-item
    /// cooldown (<see cref="PluginConfiguration.RemediationCooldownHours"/>),
    /// and not already having a pending row for this item. Does nothing (returns
    /// <c>null</c>) for any item type other than a movie or TV episode.
    /// </summary>
    /// <param name="item">The Jellyfin item that just failed/errored a scan.</param>
    /// <param name="scanStatus">The raw <c>scan_status</c> value (2 = Fail, 3 = Error).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly enqueued pending record, or <c>null</c> if not eligible.</returns>
    Task<ArrRemediationRecord?> EnqueueIfEligibleAsync(BaseItem item, int scanStatus, CancellationToken cancellationToken);

    /// <summary>
    /// Processes one <c>pending</c> remediation row enqueued by
    /// <see cref="EnqueueIfEligibleAsync"/> -- called by
    /// <c>ArrRemediationWorker</c>, never directly from the API. Checks the
    /// <see cref="PluginConfiguration.MaxRemediationCycles"/> trip-wire first
    /// (marking the row <c>blocked</c> instead of processing it if exceeded),
    /// then resolves the item, matches it, and either performs the real
    /// delete-then-blocklist/search flow or -- if
    /// <see cref="PluginConfiguration.ArrForwardingDryRun"/> is on -- matches
    /// and reports what it would have done without calling any
    /// mutating Radarr/Sonarr endpoint.
    /// </summary>
    /// <param name="pending">The pending row to process, as returned by <c>IDatabaseManager.GetPendingRemediationsAsync</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The same record, updated to its terminal state and already persisted.</returns>
    Task<ArrRemediationRecord> ProcessPendingAsync(ArrRemediationRecord pending, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the cycle-limit trip-wire for an item that's currently
    /// <c>blocked</c> (<c>ARR-INTEGRATION-PROPOSAL.md</c> section 5.1's
    /// "Reset cycle count" recovery action) by inserting a fresh
    /// <c>skipped</c>/<c>cycle_reset</c> row with <c>CycleNumber</c> reset to 1 --
    /// history is never deleted or overwritten, only superseded, matching
    /// how every other remediation outcome is recorded. The next scan
    /// failure for this item is then free to enqueue and process normally
    /// again.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new reset-marker record, or <c>null</c> if the item was never blocked.</returns>
    Task<ArrRemediationRecord?> ResetCycleAsync(string itemId, CancellationToken cancellationToken);
}
