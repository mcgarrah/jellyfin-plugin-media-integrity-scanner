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
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Data;

/// <summary>
/// SQLite implementation of the scan results database manager.
/// </summary>
public class SqliteDatabaseManager : IDatabaseManager, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDatabaseManager> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDatabaseManager"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths for locating plugin data directory.</param>
    /// <param name="logger">Logger instance.</param>
    public SqliteDatabaseManager(
        IApplicationPaths applicationPaths,
        ILogger<SqliteDatabaseManager> logger)
    {
        _logger = logger;

        var dataDir = Path.Combine(
            applicationPaths.PluginConfigurationsPath,
            "MediaIntegrityScanner");
        Directory.CreateDirectory(dataDir);

        var dbPath = Path.Combine(dataDir, "media-integrity.db");
        _connectionString = $"Data Source={dbPath}";

        _logger.LogInformation("Database path: {Path}", dbPath);
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS scan_results (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                item_id TEXT NOT NULL,
                file_path TEXT NOT NULL,
                file_size INTEGER,
                last_modified TEXT,
                scan_phase INTEGER NOT NULL,
                scan_status INTEGER NOT NULL,
                scan_timestamp TEXT NOT NULL,
                error_output TEXT,
                scan_duration_ms INTEGER,
                UNIQUE(item_id, scan_phase)
            );

            CREATE INDEX IF NOT EXISTS idx_scan_results_status
                ON scan_results(scan_status);
            CREATE INDEX IF NOT EXISTS idx_scan_results_item
                ON scan_results(item_id);
            CREATE INDEX IF NOT EXISTS idx_scan_results_timestamp
                ON scan_results(scan_timestamp);

            -- Enable WAL mode for better concurrent read performance
            PRAGMA journal_mode=WAL;
        ";

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        _logger.LogInformation("Database schema initialized");
    }

    /// <inheritdoc />
    public async Task SaveResultAsync(ScanRecord record)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO scan_results
                    (item_id, file_path, file_size, last_modified,
                     scan_phase, scan_status, scan_timestamp,
                     error_output, scan_duration_ms)
                VALUES
                    (@itemId, @filePath, @fileSize, @lastModified,
                     @scanPhase, @scanStatus, @scanTimestamp,
                     @errorOutput, @scanDurationMs)
                ON CONFLICT(item_id, scan_phase) DO UPDATE SET
                    file_path = excluded.file_path,
                    file_size = excluded.file_size,
                    last_modified = excluded.last_modified,
                    scan_status = excluded.scan_status,
                    scan_timestamp = excluded.scan_timestamp,
                    error_output = excluded.error_output,
                    scan_duration_ms = excluded.scan_duration_ms;
            ";

            command.Parameters.AddWithValue("@itemId", record.ItemId);
            command.Parameters.AddWithValue("@filePath", record.FilePath);
            command.Parameters.AddWithValue("@fileSize", (object?)record.FileSize ?? DBNull.Value);
            command.Parameters.AddWithValue("@lastModified", (object?)record.LastModified ?? DBNull.Value);
            command.Parameters.AddWithValue("@scanPhase", record.ScanPhase);
            command.Parameters.AddWithValue("@scanStatus", record.ScanStatus);
            command.Parameters.AddWithValue("@scanTimestamp", record.ScanTimestamp);
            command.Parameters.AddWithValue("@errorOutput", (object?)record.ErrorOutput ?? DBNull.Value);
            command.Parameters.AddWithValue("@scanDurationMs", (object?)record.ScanDurationMs ?? DBNull.Value);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsCurrentAsync(string itemId, string filePath)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT last_modified FROM scan_results
            WHERE item_id = @itemId AND scan_status = 1
            ORDER BY scan_phase DESC
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@itemId", itemId);

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (result is not string lastModified)
        {
            return false;
        }

        // Compare stored mtime with current file mtime
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return false;
            }

            var currentMtime = fileInfo.LastWriteTimeUtc.ToString("O");
            return string.Equals(lastModified, currentMtime, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ScanStatistics> GetStatisticsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var stats = new ScanStatistics();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN scan_status = 1 THEN 1 ELSE 0 END) AS passed,
                SUM(CASE WHEN scan_status = 2 THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN scan_status = 0 THEN 1 ELSE 0 END) AS pending,
                MAX(scan_timestamp) AS last_scan
            FROM scan_results;
        ";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            stats.ScannedFiles = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            stats.PassedFiles = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            stats.FailedFiles = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            stats.PendingFiles = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            stats.LastScanTimestamp = reader.IsDBNull(4) ? null : reader.GetString(4);
        }

        return stats;
    }

    /// <summary>
    /// Gets paginated scan results with optional status filter.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of results per page.</param>
    /// <param name="libraryId">Optional library ID filter (not yet implemented).</param>
    /// <returns>Paged result set.</returns>
    public async Task<PagedScanResults> GetResultsAsync(
        int? status, int page, int pageSize, string? libraryId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        // Get total count
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = status.HasValue
            ? "SELECT COUNT(*) FROM scan_results WHERE scan_status = @status"
            : "SELECT COUNT(*) FROM scan_results";
        if (status.HasValue)
        {
            countCmd.Parameters.AddWithValue("@status", status.Value);
        }

        var totalCount = Convert.ToInt32(
            await countCmd.ExecuteScalarAsync().ConfigureAwait(false));

        // Get page of results
        await using var queryCmd = connection.CreateCommand();
        var whereClause = status.HasValue ? "WHERE scan_status = @status" : string.Empty;
        queryCmd.CommandText = $@"
            SELECT item_id, file_path, file_size, last_modified,
                   scan_phase, scan_status, scan_timestamp,
                   error_output, scan_duration_ms
            FROM scan_results
            {whereClause}
            ORDER BY scan_timestamp DESC
            LIMIT @limit OFFSET @offset;
        ";

        if (status.HasValue)
        {
            queryCmd.Parameters.AddWithValue("@status", status.Value);
        }

        queryCmd.Parameters.AddWithValue("@limit", pageSize);
        queryCmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);

        var items = new System.Collections.Generic.List<ScanRecord>();
        await using var reader = await queryCmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            items.Add(new ScanRecord
            {
                ItemId = reader.GetString(0),
                FilePath = reader.GetString(1),
                FileSize = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                LastModified = reader.IsDBNull(3) ? null : reader.GetString(3),
                ScanPhase = reader.GetInt32(4),
                ScanStatus = reader.GetInt32(5),
                ScanTimestamp = reader.GetString(6),
                ErrorOutput = reader.IsDBNull(7) ? null : reader.GetString(7),
                ScanDurationMs = reader.IsDBNull(8) ? null : reader.GetInt32(8)
            });
        }

        return new PagedScanResults
        {
            Items = items,
            TotalCount = totalCount
        };
    }

    /// <summary>
    /// Gets scan detail for a specific item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>The scan record or null.</returns>
    public async Task<ScanRecord?> GetItemDetailAsync(string itemId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT item_id, file_path, file_size, last_modified,
                   scan_phase, scan_status, scan_timestamp,
                   error_output, scan_duration_ms
            FROM scan_results
            WHERE item_id = @itemId
            ORDER BY scan_phase DESC
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@itemId", itemId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return new ScanRecord
            {
                ItemId = reader.GetString(0),
                FilePath = reader.GetString(1),
                FileSize = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                LastModified = reader.IsDBNull(3) ? null : reader.GetString(3),
                ScanPhase = reader.GetInt32(4),
                ScanStatus = reader.GetInt32(5),
                ScanTimestamp = reader.GetString(6),
                ErrorOutput = reader.IsDBNull(7) ? null : reader.GetString(7),
                ScanDurationMs = reader.IsDBNull(8) ? null : reader.GetInt32(8)
            };
        }

        return null;
    }

    /// <inheritdoc />
    public async Task PurgeItemAsync(string itemId)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM scan_results WHERE item_id = @itemId";
            command.Parameters.AddWithValue("@itemId", itemId);

            var deleted = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (deleted > 0)
            {
                _logger.LogDebug("Purged {Count} scan records for item {ItemId}", deleted, itemId);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _writeLock.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Paged scan results response.
/// </summary>
public class PagedScanResults
{
    /// <summary>
    /// Gets or sets the list of scan records for this page.
    /// </summary>
    public System.Collections.Generic.List<ScanRecord> Items { get; set; } = new();

    /// <summary>
    /// Gets or sets the total count of matching records.
    /// </summary>
    public int TotalCount { get; set; }
}
