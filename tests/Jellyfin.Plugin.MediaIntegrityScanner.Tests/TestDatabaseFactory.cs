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
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Builds a real, temp-file-backed <see cref="SqliteDatabaseManager"/> for tests,
/// avoiding mocked/fake SQL behavior so tests exercise the real query logic.
/// </summary>
internal sealed class TestDatabaseFactory : IDisposable
{
    private readonly string _tempDir;
    private bool _disposed;

    public TestDatabaseFactory()
    {
        _tempDir = Directory.CreateTempSubdirectory("mis-tests-").FullName;

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_tempDir);

        Database = new SqliteDatabaseManager(appPaths.Object, NullLogger<SqliteDatabaseManager>.Instance);
        Database.InitializeAsync().GetAwaiter().GetResult();
    }

    public SqliteDatabaseManager Database { get; }

    /// <summary>
    /// Gets the underlying SQLite file's path, for tests that need to
    /// inspect or mutate the raw file directly (e.g. simulating corruption).
    /// Mirrors the exact join <see cref="SqliteDatabaseManager"/>'s own
    /// constructor uses.
    /// </summary>
    public string DbPath => Path.Combine(_tempDir, "MediaIntegrityScanner", "media-integrity.db");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Database.Dispose();

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite may still hold a brief file handle on some platforms.
        }
    }
}
