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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="MediaIntegrityReconciliationTask"/>. Uses a real,
/// temp-file-backed <see cref="Jellyfin.Plugin.MediaIntegrityScanner.Data.SqliteDatabaseManager"/>
/// (via <see cref="TestDatabaseFactory"/>) so tests assert on real before/after
/// database state, mirroring <see cref="DatabaseMaintenanceTaskTests"/>'s approach.
/// </summary>
[Collection("PluginInstance")]
public class MediaIntegrityReconciliationTaskTests : IDisposable
{
    private readonly TestDatabaseFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        TestPluginContext.Clear();
    }

    private MediaIntegrityReconciliationTask CreateTask(Mock<ILibraryManager> library, Mock<IScanEngine> scanner)
    {
        return new MediaIntegrityReconciliationTask(
            _factory.Database, library.Object, scanner.Object, NullLogger<MediaIntegrityReconciliationTask>.Instance);
    }

    private static void SetConfig(bool enableAutoReconciliation)
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            EnableAutoReconciliation = enableAutoReconciliation
        });
    }

    private static Mock<ILibraryManager> LibraryReturning(params Guid[] currentItemIds)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns(currentItemIds);
        return library;
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsWeeklyTriggerSundayAt230AM()
    {
        var task = CreateTask(new Mock<ILibraryManager>(), new Mock<IScanEngine>());

        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, trigger.Type);
        Assert.Equal(DayOfWeek.Sunday, trigger.DayOfWeek);
        Assert.Equal(TimeSpan.FromMinutes(150).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void TaskMetadata_IsWellFormed()
    {
        var task = CreateTask(new Mock<ILibraryManager>(), new Mock<IScanEngine>());

        Assert.Equal("MediaIntegrityReconciliation", task.Key);
        Assert.Equal("Media Integrity", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntirely_WhenReconciliationDisabled()
    {
        SetConfig(enableAutoReconciliation: false);
        await _factory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "orphaned",
            FilePath = "/media/orphaned.mkv",
            ScanPhase = 1,
            ScanStatus = 1,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var library = LibraryReturning(); // empty -- would purge everything if it ran
        var scanner = new Mock<IScanEngine>();
        var task = CreateTask(library, scanner);
        double? reported = null;
        var progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.NotNull(await _factory.Database.GetItemDetailAsync("orphaned"));
        Assert.Equal(100, reported);
        library.Verify(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntirely_WhenAScanIsInProgress()
    {
        SetConfig(enableAutoReconciliation: true);
        await _factory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "orphaned",
            FilePath = "/media/orphaned.mkv",
            ScanPhase = 1,
            ScanStatus = 1,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var library = LibraryReturning();
        var scanner = new Mock<IScanEngine>();
        scanner.SetupGet(s => s.IsScanning).Returns(true);
        var task = CreateTask(library, scanner);
        double? reported = null;
        var progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.NotNull(await _factory.Database.GetItemDetailAsync("orphaned"));
        Assert.Equal(100, reported);
        library.Verify(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EnabledAndNotScanning_PurgesOrphanedItems()
    {
        SetConfig(enableAutoReconciliation: true);
        var stillHereId = Guid.NewGuid();
        await _factory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = stillHereId.ToString(),
            FilePath = "/media/still-here.mkv",
            ScanPhase = 1,
            ScanStatus = 1,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });
        await _factory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "orphaned",
            FilePath = "/media/orphaned.mkv",
            ScanPhase = 1,
            ScanStatus = 1,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var library = LibraryReturning(stillHereId);
        var scanner = new Mock<IScanEngine>();
        scanner.SetupGet(s => s.IsScanning).Returns(false);
        var task = CreateTask(library, scanner);
        double? reported = null;
        var progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.NotNull(await _factory.Database.GetItemDetailAsync(stillHereId.ToString()));
        Assert.Null(await _factory.Database.GetItemDetailAsync("orphaned"));
        Assert.Equal(100, reported);
    }

    // See DatabaseMaintenanceTaskTests for why this exists instead of a bare Progress<T>.
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public SynchronousProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }
}
