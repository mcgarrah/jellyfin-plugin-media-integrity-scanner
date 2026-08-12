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
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests the decode_mode/hardware_accel_type migration against a database
/// built by hand to match the real pre-migration schema (no
/// TestDatabaseFactory here deliberately -- its constructor already runs
/// InitializeAsync, which would apply the migration before the test gets a
/// chance to start from an unmigrated database).
/// </summary>
public class SqliteDatabaseManagerMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteDatabaseManagerMigrationTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("mis-migration-tests-").FullName;
        _dbPath = Path.Combine(_tempDir, "MediaIntegrityScanner", "media-integrity.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite may still hold a brief file handle on some platforms.
        }
    }

    private SqliteDatabaseManager CreateManager()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_tempDir);
        return new SqliteDatabaseManager(appPaths.Object, NullLogger<SqliteDatabaseManager>.Instance);
    }

    [Fact]
    public async Task InitializeAsync_OnALegacyDatabaseMissingTheNewColumns_AddsThemAndBackfillsFullDecodeRows()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        // Build the real pre-migration schema by hand -- no decode_mode/
        // hardware_accel_type columns, matching this table's shape before
        // this feature existed.
        await using (var legacyConnection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await legacyConnection.OpenAsync();

            await using (var createCmd = legacyConnection.CreateCommand())
            {
                createCmd.CommandText = @"
                    CREATE TABLE scan_results (
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
                ";
                await createCmd.ExecuteNonQueryAsync();
            }

            await using (var insertCmd = legacyConnection.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO scan_results (item_id, file_path, scan_phase, scan_status, scan_timestamp)
                    VALUES
                        ('legacy-header', '/media/a.mkv', 1, 1, '2026-01-01T00:00:00.0000000Z'),
                        ('legacy-deep', '/media/b.mkv', 2, 1, '2026-01-01T00:00:00.0000000Z');
                ";
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        var db = CreateManager();
        await db.InitializeAsync(); // must not throw, and must migrate + backfill
        await db.InitializeAsync(); // must also not throw a second time (idempotency)

        var headerRecord = await db.GetItemDetailAsync("legacy-header");
        var deepRecord = await db.GetItemDetailAsync("legacy-deep");

        Assert.NotNull(headerRecord);
        Assert.Equal(0, headerRecord!.DecodeMode); // NotApplicable -- backfill only targets phase=2
        Assert.Null(headerRecord.HardwareAccelType);

        Assert.NotNull(deepRecord);
        Assert.Equal(1, deepRecord!.DecodeMode); // Software -- backfilled from the default 0
        Assert.Null(deepRecord.HardwareAccelType);

        db.Dispose();
    }
}
