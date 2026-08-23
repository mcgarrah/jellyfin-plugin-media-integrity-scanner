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
/// A single row on the "Media Issues" page: a failing <see cref="ScanRecord"/>
/// joined with its most recent <see cref="ArrRemediationRecord"/>, if any has
/// ever been attempted for this item. Backs <c>GET /MediaIntegrity/Issues</c>
/// (see <c>ARR-INTEGRATION-PROPOSAL.md</c> section 8.4) -- deliberately a
/// separate read model from <see cref="ScanRecord"/> rather than adding
/// remediation columns to it, since most callers of the plain scan-results
/// query have no reason to pay for this join.
/// </summary>
public class IssueRecord
{
    /// <summary>Gets or sets the Jellyfin item GUID.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the full file path.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the scan phase (1 = header, 2 = full decode).</summary>
    public int ScanPhase { get; set; }

    /// <summary>Gets or sets the scan status (2 = fail, 3 = error -- this view never includes pending/pass).</summary>
    public int ScanStatus { get; set; }

    /// <summary>Gets or sets the scan timestamp (ISO 8601).</summary>
    public string ScanTimestamp { get; set; } = string.Empty;

    /// <summary>Gets or sets the ffmpeg/ffprobe stderr output on failure.</summary>
    public string? ErrorOutput { get; set; }

    /// <summary>Gets or sets the most recent remediation attempt for this item, if any has ever been made.</summary>
    public ArrRemediationRecord? Remediation { get; set; }
}
