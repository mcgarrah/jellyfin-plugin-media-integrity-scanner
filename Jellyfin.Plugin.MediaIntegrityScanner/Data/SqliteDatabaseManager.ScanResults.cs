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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Data;

/// <summary>
/// Scan-result persistence: saving results, checking currency, statistics,
/// paginated/full result queries, per-item detail, purge, and reconciliation.
/// See <c>SqliteDatabaseManager.cs</c> for shared state and lifecycle.
/// </summary>
public partial class SqliteDatabaseManager
{
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
                     error_output, scan_duration_ms, decode_mode, hardware_accel_type)
                VALUES
                    (@itemId, @filePath, @fileSize, @lastModified,
                     @scanPhase, @scanStatus, @scanTimestamp,
                     @errorOutput, @scanDurationMs, @decodeMode, @hardwareAccelType)
                ON CONFLICT(item_id, scan_phase) DO UPDATE SET
                    file_path = excluded.file_path,
                    file_size = excluded.file_size,
                    last_modified = excluded.last_modified,
                    scan_status = excluded.scan_status,
                    scan_timestamp = excluded.scan_timestamp,
                    error_output = excluded.error_output,
                    scan_duration_ms = excluded.scan_duration_ms,
                    decode_mode = excluded.decode_mode,
                    hardware_accel_type = excluded.hardware_accel_type;
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
            command.Parameters.AddWithValue("@decodeMode", record.DecodeMode);
            command.Parameters.AddWithValue("@hardwareAccelType", (object?)record.HardwareAccelType ?? DBNull.Value);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkPendingAsync(IReadOnlyList<(string ItemId, string FilePath)> items, int phase)
    {
        if (items.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // Same upsert shape as SaveResultAsync, batched in one transaction with a
            // single reused parameterized command -- avoids one round-trip per item on
            // a large library, matching the pattern ReconcileAsync already established.
            // Deliberately does not touch file_size/last_modified/error_output/duration/
            // decode_mode/hardware_accel_type -- those stay whatever they were (or null,
            // for a brand-new row) until the real scan result overwrites this placeholder.
            await using var transaction = connection.BeginTransaction();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO scan_results (item_id, file_path, scan_phase, scan_status, scan_timestamp)
                    VALUES (@itemId, @filePath, @scanPhase, @scanStatus, @scanTimestamp)
                    ON CONFLICT(item_id, scan_phase) DO UPDATE SET
                        scan_status = excluded.scan_status,
                        scan_timestamp = excluded.scan_timestamp
                    WHERE scan_status != 1;
                ";

                var itemIdParam = command.CreateParameter();
                itemIdParam.ParameterName = "@itemId";
                command.Parameters.Add(itemIdParam);

                var filePathParam = command.CreateParameter();
                filePathParam.ParameterName = "@filePath";
                command.Parameters.Add(filePathParam);

                var phaseParam = command.CreateParameter();
                phaseParam.ParameterName = "@scanPhase";
                phaseParam.Value = phase;
                command.Parameters.Add(phaseParam);

                var statusParam = command.CreateParameter();
                statusParam.ParameterName = "@scanStatus";
                statusParam.Value = (int)Scanner.ScanStatus.Pending;
                command.Parameters.Add(statusParam);

                var timestampParam = command.CreateParameter();
                timestampParam.ParameterName = "@scanTimestamp";
                command.Parameters.Add(timestampParam);

                var now = DateTime.UtcNow.ToString("O");
                foreach (var (itemId, filePath) in items)
                {
                    itemIdParam.Value = itemId;
                    filePathParam.Value = filePath;
                    timestampParam.Value = now;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            LogMarkedPending(items.Count, phase);
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
                WHERE scan_status != 0
            )
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN scan_phase >= 2 THEN 1 ELSE 0 END) AS deep_scanned,
                SUM(CASE WHEN scan_status = 1 THEN 1 ELSE 0 END) AS passed,
                SUM(CASE WHEN scan_status = 2 THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN scan_status = 3 THEN 1 ELSE 0 END) AS errored,
                (SELECT MAX(scan_timestamp) FROM scan_results WHERE scan_status != 0) AS last_scan
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
    /// Gets paginated scan results with optional status, phase, and item-id filters.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="phase">Optional scan phase filter.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of results per page.</param>
    /// <param name="itemIds">Optional set of item IDs to restrict results to (e.g., all items in a library). An empty (non-null) collection matches nothing.</param>
    /// <returns>Paged result set.</returns>
    public async Task<PagedScanResults> GetResultsAsync(
        int? status, int? phase, int page, int pageSize, IReadOnlyCollection<string>? itemIds)
    {
        if (itemIds != null && itemIds.Count == 0)
        {
            return new PagedScanResults { Items = new List<ScanRecord>(), TotalCount = 0 };
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var whereClause = BuildWhereClause(status, phase, itemIds, out var itemIdList);

        // Get total count
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM scan_results {whereClause}";
        AddFilterParameters(countCmd, status, phase, itemIdList);

        var totalCount = Convert.ToInt32(
            await countCmd.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        // Get page of results
        await using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = $@"
            SELECT item_id, file_path, file_size, last_modified,
                   scan_phase, scan_status, scan_timestamp,
                   error_output, scan_duration_ms, decode_mode, hardware_accel_type
            FROM scan_results
            {whereClause}
            ORDER BY scan_timestamp DESC
            LIMIT @limit OFFSET @offset;
        ";
        AddFilterParameters(queryCmd, status, phase, itemIdList);

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
                ScanDurationMs = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                DecodeMode = reader.GetInt32(9),
                HardwareAccelType = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return new PagedScanResults
        {
            Items = items,
            TotalCount = totalCount
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScanRecord>> GetAllResultsAsync(int? status, int? phase)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var whereClause = BuildWhereClause(status, phase, null, out var itemIdList);

        await using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = $@"
            SELECT item_id, file_path, file_size, last_modified,
                   scan_phase, scan_status, scan_timestamp,
                   error_output, scan_duration_ms, decode_mode, hardware_accel_type
            FROM scan_results
            {whereClause}
            ORDER BY scan_timestamp DESC;
        ";
        AddFilterParameters(queryCmd, status, phase, itemIdList);

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
                ScanDurationMs = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                DecodeMode = reader.GetInt32(9),
                HardwareAccelType = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return items;
    }

    /// <summary>
    /// Builds a parameterized WHERE clause for the optional status, phase, and item-id
    /// filters shared by the count and page queries in <see cref="GetResultsAsync"/>.
    /// </summary>
    private static string BuildWhereClause(int? status, int? phase, IReadOnlyCollection<string>? itemIds, out IReadOnlyList<string> itemIdList)
    {
        itemIdList = itemIds is null ? Array.Empty<string>() : itemIds.ToArray();

        var clauses = new List<string>();
        if (status.HasValue)
        {
            clauses.Add("scan_status = @status");
        }

        if (phase.HasValue)
        {
            clauses.Add("scan_phase = @phase");
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
    private static void AddFilterParameters(SqliteCommand command, int? status, int? phase, IReadOnlyList<string> itemIdList)
    {
        if (status.HasValue)
        {
            command.Parameters.AddWithValue("@status", status.Value);
        }

        if (phase.HasValue)
        {
            command.Parameters.AddWithValue("@phase", phase.Value);
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
                   error_output, scan_duration_ms, decode_mode, hardware_accel_type
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
                ScanDurationMs = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                DecodeMode = reader.GetInt32(9),
                HardwareAccelType = reader.IsDBNull(10) ? null : reader.GetString(10)
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
    public async Task<int> ReconcileAsync(IReadOnlyCollection<string> currentItemIds)
    {
        if (currentItemIds.Count == 0)
        {
            LogReconcileSkippedEmptySet();
            return 0;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // A TEMP table (connection-scoped, dropped automatically once this
            // connection closes) avoids the parameter-count risk a plain
            // "item_id NOT IN (@id0, @id1, ...)" list would carry on a very
            // large library -- scales to any library size.
            await using (var createTempCommand = connection.CreateCommand())
            {
                createTempCommand.CommandText = "CREATE TEMP TABLE reconcile_current_ids (item_id TEXT PRIMARY KEY);";
                await createTempCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using (var transaction = connection.BeginTransaction())
            {
                await using (var insertCommand = connection.CreateCommand())
                {
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = "INSERT OR IGNORE INTO reconcile_current_ids (item_id) VALUES (@id);";
                    var idParam = insertCommand.CreateParameter();
                    idParam.ParameterName = "@id";
                    insertCommand.Parameters.Add(idParam);

                    foreach (var itemId in currentItemIds)
                    {
                        idParam.Value = itemId;
                        await insertCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync().ConfigureAwait(false);
            }

            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = @"
                DELETE FROM scan_results
                WHERE item_id NOT IN (SELECT item_id FROM reconcile_current_ids);
            ";
            var deleted = await deleteCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (deleted > 0)
            {
                LogReconciled(deleted);
            }

            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Purged {Count} scan records for item {ItemId}")]
    private partial void LogPurged(int count, string itemId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Reconciliation purged {Count} orphaned scan-history rows")]
    private partial void LogReconciled(int count);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Reconciliation skipped -- empty current-item-ids set (treated as a failed library query, not an empty library)")]
    private partial void LogReconcileSkippedEmptySet();

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Marked {Count} items pending for phase {Phase}")]
    private partial void LogMarkedPending(int count, int phase);

}
