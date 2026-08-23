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

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// The result of matching a Jellyfin item to its Radarr/Sonarr counterpart,
/// per <c>ARR-INTEGRATION-PROPOSAL.md</c> section 3. <see cref="Matched"/>
/// is false, with <see cref="MatchMethod"/> "unmatched", when no match could
/// be made -- a clean, expected terminal state (e.g. unidentified content),
/// never a forced best-effort guess.
/// </summary>
/// <param name="Matched">Whether a match was found.</param>
/// <param name="MatchMethod">"provider_id", "path_suffix", or "unmatched".</param>
/// <param name="ArrItemId">Radarr's movieId, or Sonarr's seriesId. Null if unmatched.</param>
/// <param name="ArrFileId">The moviefile/episodefile ID. Null if unmatched, or matched but the arr item has no file of its own on record.</param>
/// <param name="ArrEpisodeId">Sonarr's own episode ID (distinct from <paramref name="ArrItemId"/>'s seriesId and <paramref name="ArrFileId"/>'s episodeFileId) -- needed for history lookup and search. Always null for a movie match.</param>
public record ArrMatchResult(bool Matched, string MatchMethod, int? ArrItemId, int? ArrFileId, int? ArrEpisodeId = null)
{
    /// <summary>Gets a result representing "no match could be made".</summary>
    public static ArrMatchResult Unmatched { get; } = new(false, "unmatched", null, null);
}
