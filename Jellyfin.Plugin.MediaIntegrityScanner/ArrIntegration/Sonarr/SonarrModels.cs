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

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;

/// <summary>
/// A Sonarr series (<c>GET /api/v3/series</c> / <c>/series/{id}</c>). Only
/// the fields this plugin actually uses. Field shapes confirmed live
/// against a real instance, see <c>ARR-INTEGRATION-PROPOSAL.md</c> section
/// 3.1.
/// </summary>
public class SonarrSeries
{
    /// <summary>Gets or sets Sonarr's internal series ID.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the series title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVDb ID -- the primary match key (see section 3.1).</summary>
    public int TvdbId { get; set; }

    /// <summary>Gets or sets the IMDb ID.</summary>
    public string? ImdbId { get; set; }
}

/// <summary>
/// A Sonarr episode (<c>GET /api/v3/episode?seriesId={id}</c>). Matched to a
/// Jellyfin <c>Episode</c> item by season/episode number (section 3.1).
/// </summary>
public class SonarrEpisode
{
    /// <summary>Gets or sets Sonarr's internal episode ID.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the owning series' ID.</summary>
    public int SeriesId { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number within the season.</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>Gets or sets a value indicating whether this episode currently has an imported file.</summary>
    public bool HasFile { get; set; }

    /// <summary>Gets or sets the ID of the current episode file, if <see cref="HasFile"/> is true.</summary>
    public int EpisodeFileId { get; set; }
}

/// <summary>
/// A Sonarr episode file (<c>GET /api/v3/episodefile/{id}</c> or
/// <c>?seriesId={id}</c>). Confirmed live to <em>not</em> embed a matching
/// episode-ID list directly -- <see cref="SonarrEpisode.EpisodeFileId"/> is
/// the correct direction to join these, not the reverse.
/// </summary>
public class SonarrEpisodeFile
{
    /// <summary>Gets or sets Sonarr's internal episode file ID.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the owning series' ID.</summary>
    public int SeriesId { get; set; }

    /// <summary>Gets or sets the absolute on-disk path, as Sonarr sees it.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// A Sonarr history event (<c>GET /api/v3/history/series?seriesId={id}</c>,
/// series-scoped -- unlike Radarr's per-movie endpoint, this returns every
/// episode's history for the whole series, so callers filter client-side by
/// <see cref="EpisodeId"/>). **Verified live** 2026-08-23 against this
/// user's real Sonarr: a real query for series 134 returned 3,448 records
/// with exactly this field shape (<c>id</c>, <c>episodeId</c>,
/// <c>eventType</c>, <c>sourceTitle</c>).
/// </summary>
public class SonarrHistoryRecord
{
    /// <summary>Gets or sets the history record's own ID -- the value <c>POST /history/failed/{id}</c> expects.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the episode this history event belongs to.</summary>
    public int EpisodeId { get; set; }

    /// <summary>Gets or sets the event type (e.g. "grabbed", "downloadFolderImported").</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets when this event occurred (ISO 8601).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets the release title, for logging/display.</summary>
    public string? SourceTitle { get; set; }
}

/// <summary>
/// A candidate release from Sonarr's interactive search
/// (<c>GET /api/v3/release?episodeId={id}</c>) -- mirrors
/// <c>RadarrReleaseCandidate</c>. <b>Note:</b> the exact query parameter
/// name (<c>episodeId</c>) follows Radarr's confirmed <c>movieId</c>
/// pattern by structural analogy (same Servarr framework, identical
/// delete/history-failed shapes already confirmed live for both apps) but
/// was not independently verified live the way Radarr's was -- verify this
/// specific parameter name during Phase 1 end-to-end testing before relying
/// on it in production.
/// </summary>
public class SonarrReleaseCandidate
{
    /// <summary>Gets or sets the release title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets which indexer this came from.</summary>
    public string? Indexer { get; set; }

    /// <summary>Gets or sets a value indicating whether Sonarr would reject this release if grabbed.</summary>
    public bool Rejected { get; set; }

    /// <summary>Gets or sets the reasons this release would be rejected, if <see cref="Rejected"/> is true.</summary>
    public string[] Rejections { get; set; } = System.Array.Empty<string>();
}
