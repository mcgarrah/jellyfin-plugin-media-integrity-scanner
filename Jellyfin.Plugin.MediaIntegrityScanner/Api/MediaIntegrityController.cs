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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
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
public partial class MediaIntegrityController : ControllerBase
{
    private readonly SqliteDatabaseManager _db;
    private readonly IScanEngine _scanner;
    private readonly ILibraryManager _library;
    private readonly IUpdateChecker _updateChecker;
    private readonly FfmpegWrapper _ffmpeg;
    private readonly IServerApplicationHost _appHost;
    private readonly IArrRemediationService _arrRemediation;
    private readonly ILogger<MediaIntegrityController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaIntegrityController"/> class.
    /// </summary>
    /// <param name="db">Database manager.</param>
    /// <param name="scanner">Scan engine.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="updateChecker">Plugin update checker.</param>
    /// <param name="ffmpeg">FFmpeg wrapper, for manual path re-resolution.</param>
    /// <param name="appHost">Server application host, for the running Jellyfin server version.</param>
    /// <param name="arrRemediation">Forwards bad files to Radarr/Sonarr for deletion + replacement (see ARR-INTEGRATION-PROPOSAL.md).</param>
    /// <param name="logger">Logger instance.</param>
    public MediaIntegrityController(
        SqliteDatabaseManager db,
        IScanEngine scanner,
        ILibraryManager library,
        IUpdateChecker updateChecker,
        FfmpegWrapper ffmpeg,
        IServerApplicationHost appHost,
        IArrRemediationService arrRemediation,
        ILogger<MediaIntegrityController> logger)
    {
        _db = db;
        _scanner = scanner;
        _library = library;
        _updateChecker = updateChecker;
        _ffmpeg = ffmpeg;
        _appHost = appHost;
        _arrRemediation = arrRemediation;
        _logger = logger;
    }

    /// <summary>
    /// Gets a non-sensitive diagnostic snapshot for filing a bug report --
    /// environment/version info and aggregate scan counts only, never file
    /// paths, library names, or error text, so it's safe to prefill into a
    /// public GitHub issue without the reporter needing to redact anything.
    /// When <see cref="FfmpegWrapper.IsUsingCustomOverride"/> is set, the
    /// resolved ffmpeg/ffprobe paths are withheld too, since those come
    /// directly from an admin-entered path that could reveal local directory
    /// structure -- only the fact that an override is configured is reported.
    /// </summary>
    /// <returns>Diagnostics response.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticsResponse>> GetDiagnostics()
    {
        var stats = await _db.GetStatisticsAsync().ConfigureAwait(false);
        var config = Plugin.Instance?.Configuration;
        var usingOverride = _ffmpeg.IsUsingCustomOverride;

        return Ok(new DiagnosticsResponse
        {
            PluginVersion = Plugin.Instance?.Version?.ToString() ?? "unknown",
            UpdateChannel = config?.UpdateChannel.ToString() ?? "unknown",
            JellyfinServerVersion = _appHost.ApplicationVersionString,
            OperatingSystem = RuntimeInformation.OSDescription,
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            UsingCustomFfmpegOverride = usingOverride,
            FfmpegPath = usingOverride ? "(custom override configured)" : _ffmpeg.FfmpegPath,
            FfprobePath = usingOverride ? "(custom override configured)" : _ffmpeg.FfprobePath,
            HardwareAccelerationType = config?.HardwareAccelerationType.ToString() ?? "unknown",
            MaxConcurrentScans = config?.MaxConcurrentScans ?? 0,
            TotalFiles = stats.ScannedFiles,
            PassedFiles = stats.PassedFiles,
            FailedFiles = stats.FailedFiles,
            ErroredFiles = stats.ErroredFiles,
            HealthPercentage = stats.ScannedFiles > 0
                ? Math.Round((double)stats.PassedFiles / stats.ScannedFiles * 100, 1)
                : 0
        });
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
        var totalFiles = GetLibraryMediaItems(null, null, null).Count;
        var pendingHeaderFiles = Math.Max(0, totalFiles - stats.ScannedFiles);
        var pendingDeepFiles = Math.Max(0, totalFiles - stats.DeepScannedFiles);

        return Ok(new ScanStatusResponse
        {
            IsScanning = _scanner.IsScanning,
            CurrentPhase = _scanner.CurrentPhase,
            CurrentLibraryId = _scanner.CurrentLibraryId,
            CurrentNameFilter = _scanner.CurrentNameFilter,
            CurrentSeasons = _scanner.CurrentSeasons,
            TotalFiles = totalFiles,
            ScannedFiles = stats.ScannedFiles,
            PassedFiles = stats.PassedFiles,
            FailedFiles = stats.FailedFiles,
            ErroredFiles = stats.ErroredFiles,
            PendingHeaderFiles = pendingHeaderFiles,
            PendingDeepFiles = pendingDeepFiles,
            LastScanTimestamp = stats.LastScanTimestamp,
            HealthPercentage = stats.ScannedFiles > 0
                ? Math.Round((double)stats.PassedFiles / stats.ScannedFiles * 100, 1)
                : 0
        });
    }

    /// <summary>
    /// Gets real media items in the library (or a specific library, when
    /// <paramref name="parentId"/> is supplied), matching the same query
    /// shape used by <c>ScanEngine</c> and the scheduled tasks, with the same
    /// name/season scope filter applied on top (see <see cref="ScanScopeFilter"/>)
    /// so "what the Results table shows" always matches "what a scan with this
    /// scope would actually touch".
    /// </summary>
    private IReadOnlyList<BaseItem> GetLibraryMediaItems(Guid? parentId, string? nameFilter, int[]? seasons)
    {
        var query = new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video, MediaType.Audio },
            IsVirtualItem = false,
            // See the matching comment in ScanEngine.ScanLibraryAsync -- without
            // this, a library-scoped query (parentId set) returns only that
            // library's direct children (Series, not episodes) instead of
            // descending the full hierarchy.
            Recursive = true
        };

        if (parentId.HasValue)
        {
            query.ParentId = parentId.Value;
        }

        return ScanScopeFilter.Apply(_library.GetItemList(query), nameFilter, seasons);
    }

    /// <summary>
    /// Get scan results with filtering and pagination.
    /// </summary>
    /// <param name="status">Optional status filter (0=pending, 1=pass, 2=fail, 3=error).</param>
    /// <param name="phase">Optional scan phase filter (1=header, 2=full decode).</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Results per page.</param>
    /// <param name="libraryId">Optional library ID filter.</param>
    /// <param name="nameFilter">Optional case-insensitive name filter -- see <see cref="ScanRequest.NameFilter"/>.</param>
    /// <param name="seasons">Optional season filter -- see <see cref="ScanRequest.Seasons"/>.</param>
    /// <returns>Paginated scan results.</returns>
    [HttpGet("Results")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultResponse>> GetResults(
        [FromQuery] int? status = null,
        [FromQuery] int? phase = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? libraryId = null,
        [FromQuery] string? nameFilter = null,
        [FromQuery] int[]? seasons = null)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1 || pageSize > 200)
        {
            pageSize = 50;
        }

        IReadOnlyCollection<string>? itemIds = null;
        if (!string.IsNullOrEmpty(libraryId) || !string.IsNullOrWhiteSpace(nameFilter) || (seasons?.Length ?? 0) > 0)
        {
            Guid? parentId = null;
            if (!string.IsNullOrEmpty(libraryId))
            {
                if (!Guid.TryParse(libraryId, out var parsed))
                {
                    return Ok(new PagedResultResponse { Items = new List<Data.Models.ScanRecord>(), TotalCount = 0, Page = page, PageSize = pageSize });
                }

                parentId = parsed;
            }

            itemIds = GetLibraryMediaItems(parentId, nameFilter, seasons).Select(item => item.Id.ToString()).ToArray();
        }

        var results = await _db.GetResultsAsync(status, phase, page, pageSize, itemIds)
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
    /// Get a paginated list of every current Fail/Error scan result, each
    /// joined with its most recent Radarr/Sonarr remediation attempt if one
    /// exists -- backs the dedicated "Media Issues" page (see
    /// ARR-INTEGRATION-PROPOSAL.md section 8), deliberately not the
    /// general-purpose <see cref="GetResults"/> endpoint above.
    /// </summary>
    /// <param name="status">Optional scan-status filter (2=fail, 3=error). Null means both.</param>
    /// <param name="phase">Optional scan-phase filter (1=header, 2=full decode).</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Results per page.</param>
    /// <returns>Paginated issue rows.</returns>
    [HttpGet("Issues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedIssueResults>> GetIssues(
        [FromQuery] int? status = null,
        [FromQuery] int? phase = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1 || pageSize > 200)
        {
            pageSize = 50;
        }

        var results = await _db.GetIssuesAsync(status, phase, page, pageSize).ConfigureAwait(false);
        return Ok(results);
    }

    /// <summary>
    /// Manually forwards a single bad file to Radarr/Sonarr for deletion +
    /// replacement, bypassing whatever automatic-forwarding gating exists
    /// (Phase 1 has none yet -- see ARR-INTEGRATION-PROPOSAL.md section 8.4).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to remediate.</param>
    /// <returns>The completed remediation record.</returns>
    [HttpPost("ArrRemediation/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Data.Models.ArrRemediationRecord>> TriggerArrRemediation(string itemId)
    {
        if (!Guid.TryParse(itemId, out var guid))
        {
            return NotFound();
        }

        var item = _library.GetItemById(guid);
        if (item is null)
        {
            return NotFound();
        }

        var record = await _arrRemediation.RemediateAsync(item, scanRecordId: null, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(record);
    }

    /// <summary>
    /// Manually forwards several bad files to Radarr/Sonarr at once -- the
    /// "Send N Selected" bulk action on the Media Issues page (section 8.3).
    /// Each item is remediated independently; one item's failure doesn't
    /// stop the rest.
    /// </summary>
    /// <param name="request">The Jellyfin item IDs to remediate.</param>
    /// <returns>The completed remediation record for each item that was found.</returns>
    [HttpPost("ArrRemediation/Bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<Data.Models.ArrRemediationRecord>>> TriggerArrRemediationBulk([FromBody] ArrRemediationBulkRequest request)
    {
        var results = new List<Data.Models.ArrRemediationRecord>();
        foreach (var itemId in request.ItemIds)
        {
            if (!Guid.TryParse(itemId, out var guid))
            {
                continue;
            }

            var item = _library.GetItemById(guid);
            if (item is null)
            {
                continue;
            }

            results.Add(await _arrRemediation.RemediateAsync(item, scanRecordId: null, HttpContext.RequestAborted).ConfigureAwait(false));
        }

        return Ok(results);
    }

    /// <summary>
    /// Clears the Phase 2 cycle-limit trip-wire for an item that's currently
    /// <c>Blocked</c> (<c>ARR-INTEGRATION-PROPOSAL.md</c> section 5.1's
    /// "Reset cycle count" recovery action), letting the next scan failure
    /// enqueue and auto-forward it again normally.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID to reset.</param>
    /// <returns>The new reset-marker record, or 404 if the item isn't currently blocked.</returns>
    [HttpPost("ArrRemediation/{itemId}/ResetCycle")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Data.Models.ArrRemediationRecord>> ResetArrRemediationCycle(string itemId)
    {
        var record = await _arrRemediation.ResetCycleAsync(itemId, HttpContext.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound();
        }

        return Ok(record);
    }

    /// <summary>
    /// Export every result matching the given status filter (not just one page)
    /// as a downloadable CSV or TSV file.
    /// </summary>
    /// <param name="status">Optional status filter, matching <see cref="GetResults"/>'s values.</param>
    /// <param name="phase">Optional scan phase filter, matching <see cref="GetResults"/>'s values.</param>
    /// <param name="format">"csv" (default) or "tsv".</param>
    /// <returns>A downloadable file.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ExportResults([FromQuery] int? status = null, [FromQuery] int? phase = null, [FromQuery] string format = "csv")
    {
        var isTsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var delimiter = isTsv ? "\t" : ",";
        var records = await _db.GetAllResultsAsync(status, phase).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append(string.Join(delimiter, "FilePath", "Status", "Phase", "Timestamp", "DurationMs", "DecodeMode", "HardwareAccelType", "Error")).Append("\r\n");
        foreach (var record in records)
        {
            sb.Append(string.Join(
                delimiter,
                FormatField(record.FilePath, isTsv),
                ((ScanStatus)record.ScanStatus).ToString(),
                ((ScanPhase)record.ScanPhase).ToString(),
                record.ScanTimestamp,
                record.ScanDurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ((DecodeMode)record.DecodeMode).ToString(),
                record.HardwareAccelType ?? string.Empty,
                FormatField(record.ErrorOutput ?? string.Empty, isTsv)));
            sb.Append("\r\n");
        }

        var extension = isTsv ? "tsv" : "csv";
        var contentType = isTsv ? "text/tab-separated-values" : "text/csv";
        var fileName = $"media-integrity-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}";
        return File(Encoding.UTF8.GetBytes(sb.ToString()), contentType, fileName);
    }

    /// <summary>
    /// Formats a field for CSV/TSV output. CSV uses RFC 4180 quoting for fields
    /// containing the delimiter, a quote, or a newline. TSV has no standard
    /// quoting convention most consumers honor, so embedded tabs/newlines are
    /// flattened to spaces instead.
    /// </summary>
    private static string FormatField(string value, bool isTsv)
    {
        if (isTsv)
        {
            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
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
                    await _scanner.ScanLibraryAsync(
                        request.LibraryId,
                        phase,
                        CancellationToken.None,
                        nameFilter: request.NameFilter,
                        seasons: request.Seasons)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogManualScanError(ex);
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

    /// <summary>
    /// List available database backups, newest first.
    /// </summary>
    /// <returns>Backup metadata.</returns>
    [HttpGet("Database/Backups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DatabaseBackupInfo>>> GetBackups()
    {
        var backups = await _db.ListBackupsAsync().ConfigureAwait(false);
        return Ok(backups);
    }

    /// <summary>
    /// Create a new database backup -- primarily useful before destructive
    /// testing (e.g. clearing scan history) so results can be restored and
    /// compared later without re-scanning the whole library.
    /// </summary>
    /// <returns>The created backup's file name.</returns>
    [HttpPost("Database/Backup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> CreateBackup()
    {
        if (_scanner.IsScanning)
        {
            return Conflict(new { message = "Cannot back up while a scan is in progress." });
        }

        var fileName = await _db.BackupAsync().ConfigureAwait(false);
        return Ok(new { fileName });
    }

    /// <summary>
    /// Restore the database from a previously-created backup, replacing all
    /// current scan history.
    /// </summary>
    /// <param name="request">Which backup file to restore.</param>
    /// <returns>Ok if the restore completed.</returns>
    [HttpPost("Database/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RestoreBackup([FromBody] RestoreBackupRequest request)
    {
        if (_scanner.IsScanning)
        {
            return Conflict(new { message = "Cannot restore while a scan is in progress." });
        }

        try
        {
            await _db.RestoreAsync(request.FileName).ConfigureAwait(false);
            return Ok(new { message = "Database restored." });
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            LogRestoreError(ex);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get current size/health information for the plugin's own SQLite database.
    /// </summary>
    /// <returns>Database maintenance info.</returns>
    [HttpGet("Database/Info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DatabaseMaintenanceInfo>> GetDatabaseInfo()
    {
        var info = await _db.GetMaintenanceInfoAsync().ConfigureAwait(false);
        return Ok(info);
    }

    /// <summary>
    /// Runs an integrity check, then (if it passes) a VACUUM, against the
    /// plugin's own SQLite database.
    /// </summary>
    /// <returns>The maintenance result.</returns>
    [HttpPost("Database/Maintenance")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DatabaseMaintenanceResult>> RunDatabaseMaintenance()
    {
        if (_scanner.IsScanning)
        {
            return Conflict(new { message = "Cannot run database maintenance while a scan is in progress." });
        }

        var result = await _db.RunMaintenanceAsync().ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Re-resolve the ffmpeg/ffprobe binary paths right now, rather than
    /// waiting for the next restart/upgrade or a Jellyfin server-config save.
    /// Always safe to call, including mid-scan or with a custom override
    /// configured (it will simply re-confirm the override, since
    /// <see cref="FfmpegResolver.ResolveBinary"/> always checks it first).
    /// </summary>
    /// <returns>The resolved paths and whether either one changed.</returns>
    [HttpPost("Ffmpeg/Refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FfmpegRefreshResult> RefreshFfmpegPaths()
    {
        var changed = _ffmpeg.RefreshPaths();
        return Ok(new FfmpegRefreshResult
        {
            Changed = changed,
            FfmpegPath = _ffmpeg.FfmpegPath,
            FfprobePath = _ffmpeg.FfprobePath
        });
    }

    /// <summary>
    /// Get the current plugin version against the latest available versions
    /// on the stable and (if registered) development channels.
    /// </summary>
    /// <returns>Update status.</returns>
    [HttpGet("UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<UpdateStatus>> GetUpdateStatus()
    {
        var status = _updateChecker.CachedStatus
            ?? await _updateChecker.RefreshAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(status);
    }

    /// <summary>
    /// Force a fresh update check, bypassing the cached status.
    /// </summary>
    /// <returns>The freshly computed update status.</returns>
    [HttpPost("UpdateStatus/Refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<UpdateStatus>> RefreshUpdateStatus()
    {
        var status = await _updateChecker.RefreshAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(status);
    }

    /// <summary>
    /// Install the latest available version for the requested channel via
    /// Jellyfin's own plugin installation API.
    /// </summary>
    /// <param name="request">Which channel to install from.</param>
    /// <returns>Ok if the install was requested successfully.</returns>
    [HttpPost("UpdateStatus/Install")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> InstallUpdate([FromBody] InstallUpdateRequest request)
    {
        try
        {
            await _updateChecker.InstallAsync(request.Channel, HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new { message = "Update installed. Restart Jellyfin to finish applying it." });
        }
        catch (InvalidOperationException ex)
        {
            LogInstallUpdateError(ex);
            return BadRequest(new { message = ex.Message });
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Error during manually triggered scan")]
    private partial void LogManualScanError(Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Error installing plugin update")]
    private partial void LogInstallUpdateError(Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Error restoring database backup")]
    private partial void LogRestoreError(Exception ex);
}

/// <summary>
/// Response model for the bug-report diagnostic snapshot. Deliberately holds
/// only environment/version info and aggregate counts -- never file paths,
/// library names, or error text -- so it's safe to prefill into a public
/// GitHub issue.
/// </summary>
public class DiagnosticsResponse
{
    /// <summary>Gets or sets the currently running plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured update channel (Stable/Dev).</summary>
    public string UpdateChannel { get; set; } = string.Empty;

    /// <summary>Gets or sets the running Jellyfin server version.</summary>
    public string JellyfinServerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the server OS description (e.g. "Linux ... " or "Microsoft Windows ...").</summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>Gets or sets the .NET runtime description.</summary>
    public string DotNetVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether an admin-configured ffmpeg/ffprobe path override is in use.</summary>
    public bool UsingCustomFfmpegOverride { get; set; }

    /// <summary>Gets or sets the resolved ffmpeg path, or a placeholder if a custom override is configured.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolved ffprobe path, or a placeholder if a custom override is configured.</summary>
    public string FfprobePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured hardware acceleration type.</summary>
    public string HardwareAccelerationType { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured maximum concurrent scans.</summary>
    public int MaxConcurrentScans { get; set; }

    /// <summary>Gets or sets the total number of scanned files.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Gets or sets the number of files that passed.</summary>
    public int PassedFiles { get; set; }

    /// <summary>Gets or sets the number of files that failed.</summary>
    public int FailedFiles { get; set; }

    /// <summary>Gets or sets the number of files whose most recent scan ended in an error.</summary>
    public int ErroredFiles { get; set; }

    /// <summary>Gets or sets the library health percentage.</summary>
    public double HealthPercentage { get; set; }
}

/// <summary>
/// Response model for scan status.
/// </summary>
public class ScanStatusResponse
{
    /// <summary>Gets or sets a value indicating whether a scan is in progress.</summary>
    public bool IsScanning { get; set; }

    /// <summary>Gets or sets the <see cref="Scanner.ScanPhase"/> (as an int) the current library scan is running, or null when idle or between library scans.</summary>
    public int? CurrentPhase { get; set; }

    /// <summary>Gets or sets the library ID the current library scan is scoped to, or null when idle/unscoped.</summary>
    public string? CurrentLibraryId { get; set; }

    /// <summary>Gets or sets the name filter the current library scan is scoped to, or null when idle/unscoped.</summary>
    public string? CurrentNameFilter { get; set; }

    /// <summary>Gets or sets the season filter the current library scan is scoped to, or null when idle/unscoped.</summary>
    public IReadOnlyCollection<int>? CurrentSeasons { get; set; }

    /// <summary>Gets or sets the total number of tracked files.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Gets or sets the number of files that have been scanned.</summary>
    public int ScannedFiles { get; set; }

    /// <summary>Gets or sets the number of files that passed.</summary>
    public int PassedFiles { get; set; }

    /// <summary>Gets or sets the number of files that failed.</summary>
    public int FailedFiles { get; set; }

    /// <summary>Gets or sets the number of files whose most recent scan ended in an error.</summary>
    public int ErroredFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files still pending a Header (light/quick) scan.
    /// </summary>
    public int PendingHeaderFiles { get; set; }

    /// <summary>
    /// Gets or sets the number of files still pending a FullDecode (deep) scan.
    /// A file already scanned at the Header phase but not yet deep-scanned
    /// counts here, even though it does not count toward <see cref="PendingHeaderFiles"/>.
    /// </summary>
    public int PendingDeepFiles { get; set; }

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

    /// <summary>
    /// Gets or sets an optional case-insensitive name filter. Matched against a
    /// movie's title or an episode's series title -- e.g. "Simpsons" scopes the
    /// scan to every episode of that show, not just an episode literally titled
    /// "Simpsons".
    /// </summary>
    public string? NameFilter { get; set; }

    /// <summary>
    /// Gets or sets an optional set of season numbers to restrict TV episodes
    /// to (e.g. <c>[1, 2, 3]</c>). Ignored for movies and other non-episode items.
    /// </summary>
    public int[]? Seasons { get; set; }
}

/// <summary>
/// Request model for installing a plugin update.
/// </summary>
public class InstallUpdateRequest
{
    /// <summary>Gets or sets which channel to install the latest available version from.</summary>
    public UpdateChannel Channel { get; set; }
}

/// <summary>
/// Request model for restoring a database backup.
/// </summary>
public class RestoreBackupRequest
{
    /// <summary>Gets or sets the backup file name to restore, as returned by <c>GET Database/Backups</c>.</summary>
    public string FileName { get; set; } = string.Empty;
}

/// <summary>
/// Request model for the "Send N Selected" bulk Arr-remediation action on
/// the Media Issues page.
/// </summary>
public class ArrRemediationBulkRequest
{
    /// <summary>Gets or sets the Jellyfin item IDs to remediate.</summary>
    public List<string> ItemIds { get; set; } = new();
}

/// <summary>
/// Response model for a manual ffmpeg/ffprobe path re-resolution.
/// </summary>
public class FfmpegRefreshResult
{
    /// <summary>Gets or sets a value indicating whether either resolved path actually changed.</summary>
    public bool Changed { get; set; }

    /// <summary>Gets or sets the currently resolved ffmpeg path.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the currently resolved ffprobe path.</summary>
    public string FfprobePath { get; set; } = string.Empty;
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
