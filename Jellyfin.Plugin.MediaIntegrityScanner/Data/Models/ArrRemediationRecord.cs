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

namespace Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;

/// <summary>
/// Database entity representing one attempt to forward a bad file to
/// Radarr/Sonarr for deletion + replacement. See
/// <c>ARR-INTEGRATION-PROPOSAL.md</c> section 5 for the full design.
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

    /// <summary>Gets or sets the outcome status ("pending", "success", "failed", "skipped", "blocked").</summary>
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
    /// subsequent time the same item is forwarded again after a prior
    /// successful remediation. Not enforced/gated in Phase 1 (no automatic
    /// forwarding or cycle-limit trip-wire exists yet), but computed and
    /// stored from the start so Phase 2 doesn't need a backfill migration.
    /// </summary>
    public int CycleNumber { get; set; } = 1;
}
