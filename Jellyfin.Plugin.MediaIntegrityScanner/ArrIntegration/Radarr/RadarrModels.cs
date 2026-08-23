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

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;

/// <summary>
/// A Radarr movie (<c>GET /api/v3/movie</c> / <c>/movie/{id}</c>). Only the
/// fields this plugin actually uses -- Radarr's real response has many more.
/// Field shapes (including the embedded <see cref="MovieFile"/> when
/// <see cref="HasFile"/> is true) confirmed live against a real instance,
/// see <c>ARR-INTEGRATION-PROPOSAL.md</c> section 3.1.
/// </summary>
public class RadarrMovie
{
    /// <summary>Gets or sets Radarr's internal movie ID.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the movie title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDb ID -- the primary match key (see section 3.1).</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb ID.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets a value indicating whether this movie currently has an imported file.</summary>
    public bool HasFile { get; set; }

    /// <summary>Gets or sets the ID of the current movie file, if <see cref="HasFile"/> is true.</summary>
    public int MovieFileId { get; set; }

    /// <summary>Gets or sets the embedded current file's details, if <see cref="HasFile"/> is true.</summary>
    public RadarrMovieFile? MovieFile { get; set; }
}

/// <summary>
/// A Radarr movie file (<c>GET /api/v3/moviefile/{id}</c>, or embedded in
/// <see cref="RadarrMovie.MovieFile"/>).
/// </summary>
public class RadarrMovieFile
{
    /// <summary>Gets or sets Radarr's internal movie file ID.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the owning movie's ID.</summary>
    public int MovieId { get; set; }

    /// <summary>Gets or sets the absolute on-disk path, as Radarr sees it.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the path relative to the movie's folder.</summary>
    public string RelativePath { get; set; } = string.Empty;
}

/// <summary>
/// A Radarr history event (<c>GET /api/v3/history?movieId={id}</c>). Only
/// <c>eventType == "grabbed"</c> events matter for remediation -- confirmed
/// live that <c>eventType</c> is a plain string, not a numeric code.
/// </summary>
public class RadarrHistoryRecord
{
    /// <summary>Gets or sets the history record's own ID -- the value <c>POST /history/failed/{id}</c> expects.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the movie this history event belongs to.</summary>
    public int MovieId { get; set; }

    /// <summary>Gets or sets the event type (e.g. "grabbed", "downloadFolderImported").</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets when this event occurred (ISO 8601).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets the release title, for logging/display.</summary>
    public string? SourceTitle { get; set; }
}

/// <summary>
/// A candidate release from Radarr's interactive search
/// (<c>GET /api/v3/release?movieId={id}</c>) -- the pre-flight availability
/// check in <c>ARR-INTEGRATION-PROPOSAL.md</c> section 4 step 0. Confirmed
/// live: Radarr computes <see cref="Rejected"/>/<see cref="Rejections"/>
/// itself, real rejection reasons like "Existing file meets cutoff".
/// </summary>
public class RadarrReleaseCandidate
{
    /// <summary>Gets or sets the release title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets which indexer this came from.</summary>
    public string? Indexer { get; set; }

    /// <summary>Gets or sets a value indicating whether Radarr would reject this release if grabbed.</summary>
    public bool Rejected { get; set; }

    /// <summary>Gets or sets the reasons this release would be rejected, if <see cref="Rejected"/> is true.</summary>
    public string[] Rejections { get; set; } = System.Array.Empty<string>();
}
