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

        await using (var arrTableCommand = connection.CreateCommand())
        {
            arrTableCommand.CommandText = @"
                CREATE TABLE IF NOT EXISTS arr_remediation (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    item_id         TEXT NOT NULL,
                    scan_record_id  INTEGER,
                    file_path       TEXT NOT NULL,
                    arr_app         TEXT NOT NULL,
                    arr_server_name TEXT,
                    match_method    TEXT NOT NULL,
                    arr_item_id     INTEGER,
                    arr_file_id     INTEGER,
                    action_taken    TEXT,
                    status          TEXT NOT NULL,
                    error_message   TEXT,
                    requested_at    TEXT NOT NULL,
                    completed_at    TEXT,
                    retry_count     INTEGER NOT NULL DEFAULT 0,
                    cycle_number    INTEGER NOT NULL DEFAULT 1
                );

                CREATE INDEX IF NOT EXISTS idx_arr_remediation_item_id ON arr_remediation(item_id);
                CREATE INDEX IF NOT EXISTS idx_arr_remediation_status ON arr_remediation(status);
            ";
            await arrTableCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Migration: decode_mode/hardware_accel_type were added after this
        // table's original CREATE TABLE, so existing databases need an
        // idempotent ALTER TABLE rather than relying on CREATE TABLE IF NOT
        // EXISTS (which is a no-op once the table already exists).
        await EnsureColumnExistsAsync(connection, "scan_results", "decode_mode", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "scan_results", "hardware_accel_type", "TEXT").ConfigureAwait(false);

        // Backfill: FullDecode rows written before this migration were always
        // software-decoded (hardware decode support didn't exist yet), but
        // defaulted to 0 (NotApplicable) by the ALTER TABLE above -- correct
        // them to Software (1) so historical rows aren't misleadingly
        // indistinguishable from Header-phase rows. Safe to run every startup:
        // a no-op once no phase=2 row is still at the default.
        await using (var backfillCommand = connection.CreateCommand())
        {
            backfillCommand.CommandText = "UPDATE scan_results SET decode_mode = 1 WHERE scan_phase = 2 AND decode_mode = 0;";
            await backfillCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        LogSchemaInitialized();
    }

    /// <summary>
    /// Adds <paramref name="column"/> to <paramref name="table"/> if it doesn't
    /// already exist. SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so this
    /// checks <c>PRAGMA table_info</c> first -- <see cref="InitializeAsync"/>
    /// runs on every plugin startup, and an unconditional <c>ALTER TABLE</c>
    /// would throw "duplicate column name" on every run after the first.
    /// </summary>
    private static async Task EnsureColumnExistsAsync(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await checkCommand.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                // PRAGMA table_info's result set has the column name at index 1.
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task<long> RecordRemediationAsync(ArrRemediationRecord record)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO arr_remediation
                    (item_id, scan_record_id, file_path, arr_app, arr_server_name,
                     match_method, arr_item_id, arr_file_id, action_taken, status,
                     error_message, requested_at, completed_at, retry_count, cycle_number)
                VALUES
                    (@itemId, @scanRecordId, @filePath, @arrApp, @arrServerName,
                     @matchMethod, @arrItemId, @arrFileId, @actionTaken, @status,
                     @errorMessage, @requestedAt, @completedAt, @retryCount, @cycleNumber);
                SELECT last_insert_rowid();
            ";

            command.Parameters.AddWithValue("@itemId", record.ItemId);
            command.Parameters.AddWithValue("@scanRecordId", (object?)record.ScanRecordId ?? DBNull.Value);
            command.Parameters.AddWithValue("@filePath", record.FilePath);
            command.Parameters.AddWithValue("@arrApp", record.ArrApp);
            command.Parameters.AddWithValue("@arrServerName", (object?)record.ArrServerName ?? DBNull.Value);
            command.Parameters.AddWithValue("@matchMethod", record.MatchMethod);
            command.Parameters.AddWithValue("@arrItemId", (object?)record.ArrItemId ?? DBNull.Value);
            command.Parameters.AddWithValue("@arrFileId", (object?)record.ArrFileId ?? DBNull.Value);
            command.Parameters.AddWithValue("@actionTaken", (object?)record.ActionTaken ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", record.Status);
            command.Parameters.AddWithValue("@errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("@requestedAt", record.RequestedAt);
            command.Parameters.AddWithValue("@completedAt", (object?)record.CompletedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@retryCount", record.RetryCount);
            command.Parameters.AddWithValue("@cycleNumber", record.CycleNumber);

            var newId = Convert.ToInt64(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            LogRemediationRecorded(record.ItemId, record.ArrApp, record.Status);
            return newId;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ArrRemediationRecord?> GetLatestRemediationForItemAsync(string itemId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, item_id, scan_record_id, file_path, arr_app, arr_server_name,
                   match_method, arr_item_id, arr_file_id, action_taken, status,
                   error_message, requested_at, completed_at, retry_count, cycle_number
            FROM arr_remediation
            WHERE item_id = @itemId
            ORDER BY requested_at DESC, id DESC
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@itemId", itemId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        return ReadRemediationRecord(reader);
    }

    /// <inheritdoc />
    public async Task<int> CountSuccessfulRemediationsForItemAsync(string itemId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // Only counts successes recorded after the most recent "cycle_reset"
        // marker row (if any) for this item -- otherwise a reset action
        // (ResetCycleAsync) would never actually lower the next
        // CycleNumber, since history is append-only and old successes would
        // still be there to count. A COALESCE against 0 falls back to
        // counting everything when the item has never been reset.
        command.CommandText = @"
            SELECT COUNT(*) FROM arr_remediation
            WHERE item_id = @itemId
              AND status = 'success'
              AND id > COALESCE(
                  (SELECT MAX(id) FROM arr_remediation WHERE item_id = @itemId AND action_taken = 'cycle_reset'),
                  0
              );
        ";
        command.Parameters.AddWithValue("@itemId", itemId);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Maps the Media Issues page's Arr Action filter bucket names (matching
    /// <c>integrity_issues.html</c>'s <c>arrActionBucket()</c> exactly) to a
    /// SQL condition against the joined <c>ar.*</c> columns. Values come
    /// from a fixed switch, never interpolated raw, so an unrecognized
    /// <paramref name="arrAction"/> (including <c>null</c>/empty) safely
    /// falls through to "no filter" rather than risking injection.
    /// </summary>
    private static string? BuildArrActionClause(string? arrAction)
    {
        return arrAction switch
        {
            "not_sent" => "ar.id IS NULL",
            "pending" => "ar.status = 'pending'",
            "sent" => "ar.status = 'success'",
            "unmatched" => "ar.action_taken = 'unmatched'",
            "no_replacement" => "ar.action_taken = 'no_replacement_available'",
            "blocked" => "ar.status = 'blocked'",
            "failed" => "ar.status = 'failed'",
            "dry_run" => "ar.action_taken IN ('would_delete_and_blocklist', 'would_delete_and_search')",
            _ => null
        };
    }

    private static string BuildIssuesWhereClause(int? status, int? phase, string? arrAction)
    {
        var clauses = new List<string> { "sr.scan_status IN (2, 3)" }; // Fail=2, Error=3 -- this page never shows Pass/Pending
        if (status.HasValue)
        {
            clauses.Add("sr.scan_status = @status");
        }

        if (phase.HasValue)
        {
            clauses.Add("sr.scan_phase = @phase");
        }

        var arrActionClause = BuildArrActionClause(arrAction);
        if (arrActionClause is not null)
        {
            clauses.Add(arrActionClause);
        }

        return "WHERE " + string.Join(" AND ", clauses);
    }

    /// <inheritdoc />
    public async Task<PagedIssueResults> GetIssuesAsync(int? status, int? phase, string? arrAction, int page, int pageSize)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var whereClause = BuildIssuesWhereClause(status, phase, arrAction);

        // Both queries below need the identical LEFT JOIN -- the count query
        // used to omit it entirely, which worked only because the WHERE
        // clause never referenced ar.* columns before arrAction filtering
        // was added; without the join here, an arrAction filter would fail
        // with "no such column: ar.status".
        const string IssuesJoin = @"
            FROM scan_results sr
            LEFT JOIN arr_remediation ar ON ar.id = (
                SELECT ar2.id FROM arr_remediation ar2
                WHERE ar2.item_id = sr.item_id
                ORDER BY ar2.requested_at DESC, ar2.id DESC
                LIMIT 1
            )";

        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) {IssuesJoin} {whereClause};";
            if (status.HasValue)
            {
                countCmd.Parameters.AddWithValue("@status", status.Value);
            }

            if (phase.HasValue)
            {
                countCmd.Parameters.AddWithValue("@phase", phase.Value);
            }

            var totalCount = Convert.ToInt32(
                await countCmd.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            await using var queryCmd = connection.CreateCommand();
            queryCmd.CommandText = $@"
                SELECT sr.item_id, sr.file_path, sr.scan_phase, sr.scan_status,
                       sr.scan_timestamp, sr.error_output,
                       ar.id, ar.item_id, ar.scan_record_id, ar.file_path, ar.arr_app,
                       ar.arr_server_name, ar.match_method, ar.arr_item_id, ar.arr_file_id,
                       ar.action_taken, ar.status, ar.error_message, ar.requested_at,
                       ar.completed_at, ar.retry_count, ar.cycle_number
                {IssuesJoin}
                {whereClause}
                ORDER BY sr.scan_timestamp DESC
                LIMIT @limit OFFSET @offset;
            ";
            if (status.HasValue)
            {
                queryCmd.Parameters.AddWithValue("@status", status.Value);
            }

            if (phase.HasValue)
            {
                queryCmd.Parameters.AddWithValue("@phase", phase.Value);
            }

            queryCmd.Parameters.AddWithValue("@limit", pageSize);
            queryCmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);

            var items = new List<IssueRecord>();
            await using var reader = await queryCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                items.Add(new IssueRecord
                {
                    ItemId = reader.GetString(0),
                    FilePath = reader.GetString(1),
                    ScanPhase = reader.GetInt32(2),
                    ScanStatus = reader.GetInt32(3),
                    ScanTimestamp = reader.GetString(4),
                    ErrorOutput = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Remediation = reader.IsDBNull(6) ? null : ReadRemediationRecord(reader, offset: 6)
                });
            }

            return new PagedIssueResults { Items = items, TotalCount = totalCount };
        }
    }

    /// <summary>
    /// Unpaginated counterpart to <see cref="GetIssuesAsync"/> -- every
    /// matching row, not one page -- backing the Media Issues page's CSV/TSV
    /// export (<c>GET /MediaIntegrity/Issues/Export</c>), the same relationship
    /// <see cref="GetAllResultsAsync"/> has to the main dashboard's paginated
    /// <c>GetResultsAsync</c>.
    /// </summary>
    public async Task<IReadOnlyList<IssueRecord>> GetAllIssuesAsync(int? status, int? phase, string? arrAction)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var whereClause = BuildIssuesWhereClause(status, phase, arrAction);

        await using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = $@"
            SELECT sr.item_id, sr.file_path, sr.scan_phase, sr.scan_status,
                   sr.scan_timestamp, sr.error_output,
                   ar.id, ar.item_id, ar.scan_record_id, ar.file_path, ar.arr_app,
                   ar.arr_server_name, ar.match_method, ar.arr_item_id, ar.arr_file_id,
                   ar.action_taken, ar.status, ar.error_message, ar.requested_at,
                   ar.completed_at, ar.retry_count, ar.cycle_number
            FROM scan_results sr
            LEFT JOIN arr_remediation ar ON ar.id = (
                SELECT ar2.id FROM arr_remediation ar2
                WHERE ar2.item_id = sr.item_id
                ORDER BY ar2.requested_at DESC, ar2.id DESC
                LIMIT 1
            )
            {whereClause}
            ORDER BY sr.scan_timestamp DESC;
        ";
        if (status.HasValue)
        {
            queryCmd.Parameters.AddWithValue("@status", status.Value);
        }

        if (phase.HasValue)
        {
            queryCmd.Parameters.AddWithValue("@phase", phase.Value);
        }

        var items = new List<IssueRecord>();
        await using var reader = await queryCmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            items.Add(new IssueRecord
            {
                ItemId = reader.GetString(0),
                FilePath = reader.GetString(1),
                ScanPhase = reader.GetInt32(2),
                ScanStatus = reader.GetInt32(3),
                ScanTimestamp = reader.GetString(4),
                ErrorOutput = reader.IsDBNull(5) ? null : reader.GetString(5),
                Remediation = reader.IsDBNull(6) ? null : ReadRemediationRecord(reader, offset: 6)
            });
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArrRemediationRecord>> GetPendingRemediationsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, item_id, scan_record_id, file_path, arr_app, arr_server_name,
                   match_method, arr_item_id, arr_file_id, action_taken, status,
                   error_message, requested_at, completed_at, retry_count, cycle_number
            FROM arr_remediation
            WHERE status = 'pending'
            ORDER BY requested_at ASC, id ASC;
        ";

        var items = new List<ArrRemediationRecord>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            items.Add(ReadRemediationRecord(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingRemediationAsync(string itemId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM arr_remediation WHERE item_id = @itemId AND status = 'pending';";
        command.Parameters.AddWithValue("@itemId", itemId);

        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        return count > 0;
    }

    /// <inheritdoc />
    public async Task<ArrRemediationRecord?> GetLastCompletedRemediationForItemAsync(string itemId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, item_id, scan_record_id, file_path, arr_app, arr_server_name,
                   match_method, arr_item_id, arr_file_id, action_taken, status,
                   error_message, requested_at, completed_at, retry_count, cycle_number
            FROM arr_remediation
            WHERE item_id = @itemId AND status != 'pending'
            ORDER BY completed_at DESC, id DESC
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("@itemId", itemId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        return ReadRemediationRecord(reader);
    }

    /// <inheritdoc />
    public async Task<int> CountAutoRemediationsSinceAsync(DateTime sinceUtc)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*) FROM arr_remediation
            WHERE status IN ('success', 'failed') AND completed_at >= @sinceUtc;
        ";
        // ISO 8601 ('O' format, e.g. 2026-08-23T00:00:00.0000000Z) sorts
        // lexicographically the same as chronologically, so a plain string
        // comparison here is safe -- same trick already used elsewhere
        // (requested_at/completed_at ordering) in this file.
        command.Parameters.AddWithValue("@sinceUtc", sinceUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        return Convert.ToInt32(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task UpdateRemediationAsync(ArrRemediationRecord record)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE arr_remediation
                SET arr_server_name = @arrServerName,
                    match_method = @matchMethod,
                    arr_item_id = @arrItemId,
                    arr_file_id = @arrFileId,
                    action_taken = @actionTaken,
                    status = @status,
                    error_message = @errorMessage,
                    completed_at = @completedAt
                WHERE id = @id;
            ";

            command.Parameters.AddWithValue("@arrServerName", (object?)record.ArrServerName ?? DBNull.Value);
            command.Parameters.AddWithValue("@matchMethod", record.MatchMethod);
            command.Parameters.AddWithValue("@arrItemId", (object?)record.ArrItemId ?? DBNull.Value);
            command.Parameters.AddWithValue("@arrFileId", (object?)record.ArrFileId ?? DBNull.Value);
            command.Parameters.AddWithValue("@actionTaken", (object?)record.ActionTaken ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", record.Status);
            command.Parameters.AddWithValue("@errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("@completedAt", (object?)record.CompletedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@id", record.Id);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            LogRemediationUpdated(record.Id, record.Status);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Maps the 16-column <c>arr_remediation</c> projection used by both
    /// <see cref="GetLatestRemediationForItemAsync"/> (columns 0-15) and
    /// <see cref="GetIssuesAsync"/> (the joined <c>ar.*</c> columns, offset 6)
    /// to an <see cref="ArrRemediationRecord"/>.
    /// </summary>
    private static ArrRemediationRecord ReadRemediationRecord(SqliteDataReader reader, int offset = 0)
    {
        return new ArrRemediationRecord
        {
            Id = reader.GetInt64(offset),
            ItemId = reader.GetString(offset + 1),
            ScanRecordId = reader.IsDBNull(offset + 2) ? null : reader.GetInt64(offset + 2),
            FilePath = reader.GetString(offset + 3),
            ArrApp = reader.GetString(offset + 4),
            ArrServerName = reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
            MatchMethod = reader.GetString(offset + 6),
            ArrItemId = reader.IsDBNull(offset + 7) ? null : reader.GetInt32(offset + 7),
            ArrFileId = reader.IsDBNull(offset + 8) ? null : reader.GetInt32(offset + 8),
            ActionTaken = reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9),
            Status = reader.GetString(offset + 10),
            ErrorMessage = reader.IsDBNull(offset + 11) ? null : reader.GetString(offset + 11),
            RequestedAt = reader.GetString(offset + 12),
            CompletedAt = reader.IsDBNull(offset + 13) ? null : reader.GetString(offset + 13),
            RetryCount = reader.GetInt32(offset + 14),
            CycleNumber = reader.GetInt32(offset + 15)
        };
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
    /// Gets current size/health information for this database. Read-only,
    /// safe to call at any time (including mid-scan).
    /// </summary>
    /// <returns>Database maintenance info.</returns>
    public async Task<DatabaseMaintenanceInfo> GetMaintenanceInfoAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var pageCount = await ExecutePragmaScalarAsync(connection, "PRAGMA page_count;").ConfigureAwait(false);
        var pageSize = await ExecutePragmaScalarAsync(connection, "PRAGMA page_size;").ConfigureAwait(false);
        var freelistCount = await ExecutePragmaScalarAsync(connection, "PRAGMA freelist_count;").ConfigureAwait(false);

        return new DatabaseMaintenanceInfo
        {
            FileSizeBytes = GetFileSizeOnDisk(),
            LogicalSizeBytes = pageCount * pageSize,
            ReclaimableBytes = freelistCount * pageSize
        };
    }

    /// <summary>
    /// Runs <c>PRAGMA integrity_check</c> and, if it passes, a <c>VACUUM</c>
    /// against this database. <c>VACUUM</c> is skipped entirely when the
    /// integrity check fails, to avoid rewriting a database already known to
    /// be corrupt. Empirically verified safe to run against a live,
    /// concurrently-written WAL-mode database (see CODE_REVIEW.md item #30) --
    /// still serialized behind <see cref="_writeLock"/> like every other write
    /// path here, to keep this plugin's own concurrency model consistent
    /// rather than relying solely on SQLite's own locking.
    /// </summary>
    /// <returns>The maintenance result.</returns>
    public async Task<DatabaseMaintenanceResult> RunMaintenanceAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var sizeBefore = GetFileSizeOnDisk();
        string? integrityMessage;
        bool integrityOk;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using (var connection = new SqliteConnection(_connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);

                try
                {
                    await using var checkCommand = connection.CreateCommand();
                    checkCommand.CommandText = "PRAGMA integrity_check;";
                    integrityMessage = (string?)await checkCommand.ExecuteScalarAsync().ConfigureAwait(false);
                }
                catch (SqliteException ex)
                {
                    // Corruption severe enough can make running the pragma itself
                    // throw (e.g. "database disk image is malformed") rather than
                    // returning a descriptive row -- confirmed for real by actually
                    // corrupting a test database, not assumed. Either way the
                    // outcome for callers is the same: report it, skip VACUUM.
                    integrityMessage = ex.Message;
                }

                integrityOk = integrityMessage == "ok";
                if (integrityOk)
                {
                    await using (var vacuumCommand = connection.CreateCommand())
                    {
                        vacuumCommand.CommandText = "VACUUM;";
                        await vacuumCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // VACUUM's own writes go through the WAL like any other write --
                    // in WAL mode the main .db file doesn't actually shrink until the
                    // WAL is checkpointed and truncated back into it. Simply disposing
                    // the connection is not enough to guarantee this: Microsoft.Data.Sqlite
                    // pools connections by default, so "closing" one returns it to the
                    // pool rather than closing the underlying native handle, and SQLite's
                    // own last-connection-closes auto-checkpoint never fires as a result.
                    await using var checkpointCommand = connection.CreateCommand();
                    checkpointCommand.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await checkpointCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            } // Connection disposed here, after any checkpoint above, before re-measuring size below.

            var sizeAfter = integrityOk ? GetFileSizeOnDisk() : sizeBefore;

            if (integrityOk)
            {
                LogMaintenanceCompleted(sizeBefore, sizeAfter, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                LogIntegrityCheckFailed(integrityMessage ?? "unknown");
            }

            return new DatabaseMaintenanceResult
            {
                IntegrityCheckOk = integrityOk,
                IntegrityCheckMessage = integrityMessage,
                VacuumRan = integrityOk,
                SizeBeforeBytes = sizeBefore,
                SizeAfterBytes = sizeAfter,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private long GetFileSizeOnDisk()
    {
        return GetFileLengthOrZero(_dbPath)
            + GetFileLengthOrZero(_dbPath + "-wal")
            + GetFileLengthOrZero(_dbPath + "-shm");
    }

    private static long GetFileLengthOrZero(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private static async Task<long> ExecutePragmaScalarAsync(SqliteConnection connection, string pragma)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = pragma;
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
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

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Database maintenance complete: {SizeBefore} -> {SizeAfter} bytes in {DurationMs}ms")]
    private partial void LogMaintenanceCompleted(long sizeBefore, long sizeAfter, long durationMs);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Database integrity check failed: {Message}")]
    private partial void LogIntegrityCheckFailed(string message);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Reconciliation purged {Count} orphaned scan-history rows")]
    private partial void LogReconciled(int count);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Reconciliation skipped -- empty current-item-ids set (treated as a failed library query, not an empty library)")]
    private partial void LogReconcileSkippedEmptySet();

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Marked {Count} items pending for phase {Phase}")]
    private partial void LogMarkedPending(int count, int phase);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Recorded {ArrApp} remediation for item {ItemId}: {Status}")]
    private partial void LogRemediationRecorded(string itemId, string arrApp, string status);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Updated remediation {Id}: {Status}")]
    private partial void LogRemediationUpdated(long id, string status);
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
