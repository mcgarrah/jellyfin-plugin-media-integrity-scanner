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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Api;

/// <summary>
/// REST API controller for media integrity scan operations.
/// </summary>
[ApiController]
[Route("MediaIntegrity")]
[Authorize(Policy = "RequiresElevation")]
public class MediaIntegrityController : ControllerBase
{
    private readonly SqliteDatabaseManager _db;
    private readonly IScanEngine _scanner;
    private readonly ILibraryManager _library;
    private readonly ILogger<MediaIntegrityController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIntegrityController"/> class.
    /// </summary>
    /// <param name="db">Database manager.</param>
    /// <param name="scanner">Scan engine.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="logger">Logger instance.</param>
    public MediaIntegrityController(
        SqliteDatabaseManager db,
        IScanEngine scanner,
        ILibraryManager library,
        ILogger<MediaIntegrityController> logger)
    {
        _db = db;
        _scanner = scanner;
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// Get overall scan status and statistics.
    /// </summary>
    /// <returns>Scan status response.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ScanStatusResponse>> GetStatus()
    {
        var stats = await _db.GetStatisticsAsync().ConfigureAwait(false);
        return Ok(new ScanStatusResponse
        {
            IsScanning = _scanner.IsScanning,
            TotalFiles = stats.ScannedFiles + stats.PendingFiles,
            ScannedFiles = stats.ScannedFiles,
            PassedFiles = stats.PassedFiles,
            FailedFiles = stats.FailedFiles,
            PendingFiles = stats.PendingFiles,
            LastScanTimestamp = stats.LastScanTimestamp,
            HealthPercentage = stats.ScannedFiles > 0
                ? Math.Round((double)stats.PassedFiles / stats.ScannedFiles * 100, 1)
                : 0
        });
    }

    /// <summary>
    /// Get scan results with filtering and pagination.
    /// </summary>
    /// <param name="status">Optional status filter (0=pending, 1=pass, 2=fail, 3=error).</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Results per page.</param>
    /// <param name="libraryId">Optional library ID filter.</param>
    /// <returns>Paginated scan results.</returns>
    [HttpGet("Results")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultResponse>> GetResults(
        [FromQuery] int? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? libraryId = null)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1 || pageSize > 200)
        {
            pageSize = 50;
        }

        var results = await _db.GetResultsAsync(status, page, pageSize, libraryId)
            .ConfigureAwait(false);

        return Ok(new PagedResultResponse
        {
            Items = results.Items,
            TotalCount = results.TotalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>
    /// Get details for a specific item's scan history.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>Item scan detail.</returns>
    [HttpGet("Results/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetItemDetail(string itemId)
    {
        var detail = await _db.GetItemDetailAsync(itemId).ConfigureAwait(false);
        if (detail == null)
        {
            return NotFound();
        }

        return Ok(detail);
    }

    /// <summary>
    /// Trigger a manual scan for a specific item or library.
    /// </summary>
    /// <param name="request">Scan request parameters.</param>
    /// <returns>Accepted if scan was started.</returns>
    [HttpPost("Scan")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult TriggerScan([FromBody] ScanRequest request)
    {
        if (_scanner.IsScanning)
        {
            return Conflict(new { message = "A scan is already in progress." });
        }

        var phase = request.DeepScan ? ScanPhase.FullDecode : ScanPhase.Header;

        // Fire-and-forget with error logging
        _ = Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(request.ItemId))
                {
                    var item = _library.GetItemById(Guid.Parse(request.ItemId));
                    if (item != null)
                    {
                        await _scanner.ScanItemAsync(item, phase, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    await _scanner.ScanLibraryAsync(request.LibraryId, phase, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during manually triggered scan");
            }
        });

        return Accepted();
    }

    /// <summary>
    /// Cancel the currently running scan.
    /// </summary>
    /// <returns>Ok if cancellation was requested.</returns>
    [HttpPost("Cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CancelScan()
    {
        _scanner.Cancel();
        return Ok(new { message = "Scan cancellation requested." });
    }
}

/// <summary>
/// Response model for scan status.
/// </summary>
public class ScanStatusResponse
{
    /// <summary>Gets or sets a value indicating whether a scan is in progress.</summary>
    public bool IsScanning { get; set; }

    /// <summary>Gets or sets the total number of tracked files.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Gets or sets the number of files that have been scanned.</summary>
    public int ScannedFiles { get; set; }

    /// <summary>Gets or sets the number of files that passed.</summary>
    public int PassedFiles { get; set; }

    /// <summary>Gets or sets the number of files that failed.</summary>
    public int FailedFiles { get; set; }

    /// <summary>Gets or sets the number of files pending scan.</summary>
    public int PendingFiles { get; set; }

    /// <summary>Gets or sets the timestamp of the last scan.</summary>
    public string? LastScanTimestamp { get; set; }

    /// <summary>Gets or sets the library health percentage.</summary>
    public double HealthPercentage { get; set; }
}

/// <summary>
/// Request model for triggering a scan.
/// </summary>
public class ScanRequest
{
    /// <summary>Gets or sets an optional item ID to scan a specific file.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets an optional library ID to scope the scan.</summary>
    public string? LibraryId { get; set; }

    /// <summary>Gets or sets a value indicating whether to run a deep (Phase 2) scan.</summary>
    public bool DeepScan { get; set; }
}

/// <summary>
/// Paginated response model for scan results.
/// </summary>
public class PagedResultResponse
{
    /// <summary>Gets or sets the list of scan result items.</summary>
    public System.Collections.Generic.List<Data.Models.ScanRecord> Items { get; set; } = new();

    /// <summary>Gets or sets the total count of matching records.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the current page number.</summary>
    public int Page { get; set; }

    /// <summary>Gets or sets the page size.</summary>
    public int PageSize { get; set; }
}
