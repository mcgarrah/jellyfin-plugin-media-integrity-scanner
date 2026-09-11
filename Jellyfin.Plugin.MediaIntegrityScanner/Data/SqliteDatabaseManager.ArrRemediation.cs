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
/// Radarr/Sonarr remediation persistence: recording/updating remediation
/// attempts, the pending queue, per-item history, and the Media Issues
/// page's filtered/joined queries. See <c>SqliteDatabaseManager.cs</c> for
/// shared state and lifecycle.
/// </summary>
public partial class SqliteDatabaseManager
{
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

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Recorded {ArrApp} remediation for item {ItemId}: {Status}")]
    private partial void LogRemediationRecorded(string itemId, string arrApp, string status);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Updated remediation {Id}: {Status}")]
    private partial void LogRemediationUpdated(long id, string status);

}
