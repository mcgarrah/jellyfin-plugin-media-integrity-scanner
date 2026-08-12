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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Data;

/// <summary>
/// SQLite implementation of the scan results database manager.
/// </summary>
public partial class SqliteDatabaseManager : IDatabaseManager, IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
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

        _dbPath = Path.Combine(dataDir, "media-integrity.db");
        _connectionString = $"Data Source={_dbPath}";

        LogDatabasePath(_dbPath);
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
        LogSchemaInitialized();
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
    public async Task<bool> IsCurrentAsync(string itemId, string filePath, int minPhase)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT last_modified FROM scan_results
            WHERE item_id = @itemId AND scan_status = 1 AND scan_phase >= @minPhase
            ORDER BY scan_phase DESC
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@minPhase", minPhase);

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
            WITH latest AS (
                SELECT item_id, scan_status, scan_phase,
                       ROW_NUMBER() OVER (PARTITION BY item_id ORDER BY scan_phase DESC) AS rn
                FROM scan_results
            )
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN scan_phase >= 2 THEN 1 ELSE 0 END) AS deep_scanned,
                SUM(CASE WHEN scan_status = 1 THEN 1 ELSE 0 END) AS passed,
                SUM(CASE WHEN scan_status = 2 THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN scan_status = 3 THEN 1 ELSE 0 END) AS errored,
                (SELECT MAX(scan_timestamp) FROM scan_results) AS last_scan
            FROM latest
            WHERE rn = 1;
        ";

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            stats.ScannedFiles = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            stats.DeepScannedFiles = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            stats.PassedFiles = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            stats.FailedFiles = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            stats.ErroredFiles = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            stats.LastScanTimestamp = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        return stats;
    }

    /// <summary>
    /// Gets paginated scan results with optional status and item-id filters.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of results per page.</param>
    /// <param name="itemIds">Optional set of item IDs to restrict results to (e.g., all items in a library). An empty (non-null) collection matches nothing.</param>
    /// <returns>Paged result set.</returns>
    public async Task<PagedScanResults> GetResultsAsync(
        int? status, int page, int pageSize, IReadOnlyCollection<string>? itemIds)
    {
        if (itemIds != null && itemIds.Count == 0)
        {
            return new PagedScanResults { Items = new List<ScanRecord>(), TotalCount = 0 };
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var whereClause = BuildWhereClause(status, itemIds, out var itemIdList);

        // Get total count
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM scan_results {whereClause}";
        AddFilterParameters(countCmd, status, itemIdList);

        var totalCount = Convert.ToInt32(
            await countCmd.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        // Get page of results
        await using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = $@"
            SELECT item_id, file_path, file_size, last_modified,
                   scan_phase, scan_status, scan_timestamp,
                   error_output, scan_duration_ms
            FROM scan_results
            {whereClause}
            ORDER BY scan_timestamp DESC
            LIMIT @limit OFFSET @offset;
        ";
        AddFilterParameters(queryCmd, status, itemIdList);

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScanRecord>> GetAllResultsAsync(int? status)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var whereClause = BuildWhereClause(status, null, out var itemIdList);

        await using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = $@"
            SELECT item_id, file_path, file_size, last_modified,
                   scan_phase, scan_status, scan_timestamp,
                   error_output, scan_duration_ms
            FROM scan_results
            {whereClause}
            ORDER BY scan_timestamp DESC;
        ";
        AddFilterParameters(queryCmd, status, itemIdList);

        var items = new List<ScanRecord>();
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

        return items;
    }

    /// <summary>
    /// Builds a parameterized WHERE clause for the optional status and item-id
    /// filters shared by the count and page queries in <see cref="GetResultsAsync"/>.
    /// </summary>
    private static string BuildWhereClause(int? status, IReadOnlyCollection<string>? itemIds, out IReadOnlyList<string> itemIdList)
    {
        itemIdList = itemIds is null ? Array.Empty<string>() : itemIds.ToArray();

        var clauses = new List<string>();
        if (status.HasValue)
        {
            clauses.Add("scan_status = @status");
        }

        if (itemIdList.Count > 0)
        {
            var placeholders = string.Join(", ", Enumerable.Range(0, itemIdList.Count).Select(i => $"@item{i}"));
            clauses.Add($"item_id IN ({placeholders})");
        }

        return clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : string.Empty;
    }

    /// <summary>
    /// Adds the parameter values matching the clause built by <see cref="BuildWhereClause"/>.
    /// </summary>
    private static void AddFilterParameters(SqliteCommand command, int? status, IReadOnlyList<string> itemIdList)
    {
        if (status.HasValue)
        {
            command.Parameters.AddWithValue("@status", status.Value);
        }

        for (var i = 0; i < itemIdList.Count; i++)
        {
            command.Parameters.AddWithValue($"@item{i}", itemIdList[i]);
        }
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
                LogPurged(deleted, itemId);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> BackupAsync()
    {
        var backupDir = GetBackupDirectory();
        Directory.CreateDirectory(backupDir);

        // A bare second-precision timestamp collides if two backups are
        // triggered within the same second (e.g. a double-click, or two rapid
        // API calls) -- VACUUM INTO refuses to overwrite an existing file, so
        // that would throw. The random suffix keeps the human-readable
        // timestamp prefix for the UI list while guaranteeing uniqueness
        // regardless of call frequency.
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..6];
        var fileName = $"media-integrity-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{uniqueSuffix}.db";
        var backupPath = Path.Combine(backupDir, fileName);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // VACUUM INTO produces a single, self-contained, consistent snapshot
            // file directly from a live WAL-mode database -- it takes a read
            // snapshot rather than requiring exclusive access, so this is safe
            // to run without stopping the scanner (unlike copying the raw
            // .db/-wal/-shm files by hand, which could capture an inconsistent
            // mid-checkpoint state).
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", backupPath);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        LogBackupCreated(fileName);
        return fileName;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync()
    {
        var backupDir = GetBackupDirectory();
        if (!Directory.Exists(backupDir))
        {
            return Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(Array.Empty<DatabaseBackupInfo>());
        }

        var backups = new DirectoryInfo(backupDir)
            .GetFiles("media-integrity-backup-*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new DatabaseBackupInfo
            {
                FileName = f.Name,
                SizeBytes = f.Length,
                CreatedUtc = f.LastWriteTimeUtc.ToString("O")
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(backups);
    }

    /// <inheritdoc />
    public async Task RestoreAsync(string backupFileName)
    {
        // backupFileName comes from an API request body -- reject anything that
        // isn't a bare file name before it ever reaches Path.Combine, so a
        // caller can't traverse outside the backups directory.
        if (string.IsNullOrEmpty(backupFileName) || Path.GetFileName(backupFileName) != backupFileName)
        {
            throw new ArgumentException("Invalid backup file name.", nameof(backupFileName));
        }

        var backupPath = Path.Combine(GetBackupDirectory(), backupFileName);
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException($"Backup file not found: {backupFileName}", backupPath);
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Microsoft.Data.Sqlite pools connections by connection string --
            // without this, a pooled connection could still be holding the old
            // file open (or the old -wal/-shm) after we overwrite it below.
            SqliteConnection.ClearAllPools();

            File.Delete(_dbPath);
            var walPath = _dbPath + "-wal";
            var shmPath = _dbPath + "-shm";
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }

            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }

            File.Copy(backupPath, _dbPath);
        }
        finally
        {
            _writeLock.Release();
        }

        LogBackupRestored(backupFileName);
    }

    private string GetBackupDirectory() => Path.Combine(Path.GetDirectoryName(_dbPath)!, "backups");

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _writeLock.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Database path: {Path}")]
    private partial void LogDatabasePath(string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Database schema initialized")]
    private partial void LogSchemaInitialized();

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Database backup created: {FileName}")]
    private partial void LogBackupCreated(string fileName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Database restored from backup: {FileName}")]
    private partial void LogBackupRestored(string fileName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Purged {Count} scan records for item {ItemId}")]
    private partial void LogPurged(int count, string itemId);
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
