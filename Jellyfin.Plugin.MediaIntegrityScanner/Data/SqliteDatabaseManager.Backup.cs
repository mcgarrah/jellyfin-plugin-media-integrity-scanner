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
/// Database backup and restore. See <c>SqliteDatabaseManager.cs</c> for
/// shared state and lifecycle.
/// </summary>
public partial class SqliteDatabaseManager
{
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

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Database backup created: {FileName}")]
    private partial void LogBackupCreated(string fileName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Database restored from backup: {FileName}")]
    private partial void LogBackupRestored(string fileName);

}
