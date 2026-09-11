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
/// SQLite implementation of the scan results database manager. Split into
/// partial-class files by responsibility (CODE-REVIEW-ARCHITECTURE.md M1):
/// this file owns shared state (connection string, write lock) and
/// lifecycle (construction, schema init, disposal). See
/// <c>SqliteDatabaseManager.ScanResults.cs</c>,
/// <c>SqliteDatabaseManager.ArrRemediation.cs</c>,
/// <c>SqliteDatabaseManager.Backup.cs</c>, and
/// <c>SqliteDatabaseManager.Maintenance.cs</c> for the rest.
/// </summary>
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

}
