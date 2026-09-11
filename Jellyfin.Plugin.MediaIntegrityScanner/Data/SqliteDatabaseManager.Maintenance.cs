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
/// Database integrity-check/VACUUM maintenance and on-disk size reporting.
/// See <c>SqliteDatabaseManager.cs</c> for shared state and lifecycle.
/// </summary>
public partial class SqliteDatabaseManager
{
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

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Database maintenance complete: {SizeBefore} -> {SizeAfter} bytes in {DurationMs}ms")]
    private partial void LogMaintenanceCompleted(long sizeBefore, long sizeAfter, long durationMs);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Database integrity check failed: {Message}")]
    private partial void LogIntegrityCheckFailed(string message);

}
