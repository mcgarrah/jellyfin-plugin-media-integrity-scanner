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
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="DatabaseMaintenanceTask"/>. Uses a real, temp-file-backed
/// <see cref="SqliteDatabaseManager"/> (via <see cref="TestDatabaseFactory"/>) rather
/// than a mock, since the maintenance methods live directly on the concrete type --
/// this also lets tests assert on real before/after database state instead of just
/// verifying method calls were made.
/// </summary>
[Collection("PluginInstance")]
public class DatabaseMaintenanceTaskTests : IDisposable
{
    private readonly TestDatabaseFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        TestPluginContext.Clear();
    }

    private DatabaseMaintenanceTask CreateTask(Mock<IScanEngine> scanner)
    {
        return new DatabaseMaintenanceTask(_factory.Database, scanner.Object, NullLogger<DatabaseMaintenanceTask>.Instance);
    }

    private static void SetConfig(bool enableAutoMaintenance)
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            EnableAutoDatabaseMaintenance = enableAutoMaintenance
        });
    }

    /// <summary>
    /// Seeds the database with rows, then deletes most of them, leaving real
    /// reclaimable free pages behind -- the same setup used by
    /// <c>SqliteDatabaseManagerTests.RunMaintenanceAsync_ReclaimsSpaceFreedByDeletedRows</c>,
    /// used here as an observable "did maintenance actually run" signal.
    /// </summary>
    private async Task SeedReclaimableSpaceAsync()
    {
        var bulkyError = new string('x', 4000);
        for (var i = 0; i < 50; i++)
        {
            await _factory.Database.SaveResultAsync(new Data.Models.ScanRecord
            {
                ItemId = $"item-{i}",
                FilePath = $"/media/{i}.mkv",
                ScanPhase = 1,
                ScanStatus = 1,
                ScanTimestamp = DateTime.UtcNow.ToString("O"),
                ErrorOutput = bulkyError
            });
        }

        for (var i = 0; i < 45; i++)
        {
            await _factory.Database.PurgeItemAsync($"item-{i}");
        }
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsWeeklyTriggerSundayAt2AM()
    {
        var task = CreateTask(new Mock<IScanEngine>());

        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, trigger.Type);
        Assert.Equal(DayOfWeek.Sunday, trigger.DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void TaskMetadata_IsWellFormed()
    {
        var task = CreateTask(new Mock<IScanEngine>());

        Assert.Equal("MediaIntegrityDatabaseMaintenance", task.Key);
        Assert.Equal("Media Integrity", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntirely_WhenAutoMaintenanceDisabled()
    {
        SetConfig(enableAutoMaintenance: false);
        await SeedReclaimableSpaceAsync();
        var infoBefore = await _factory.Database.GetMaintenanceInfoAsync();
        Assert.True(infoBefore.ReclaimableBytes > 0);

        var scanner = new Mock<IScanEngine>();
        var task = CreateTask(scanner);
        double? reported = null;
        var progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        var infoAfter = await _factory.Database.GetMaintenanceInfoAsync();
        Assert.Equal(infoBefore.ReclaimableBytes, infoAfter.ReclaimableBytes);
        Assert.Equal(100, reported);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntirely_WhenAScanIsInProgress()
    {
        SetConfig(enableAutoMaintenance: true);
        await SeedReclaimableSpaceAsync();
        var infoBefore = await _factory.Database.GetMaintenanceInfoAsync();
        Assert.True(infoBefore.ReclaimableBytes > 0);

        var scanner = new Mock<IScanEngine>();
        scanner.SetupGet(s => s.IsScanning).Returns(true);
        var task = CreateTask(scanner);
        double? reported = null;
        var progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        var infoAfter = await _factory.Database.GetMaintenanceInfoAsync();
        Assert.Equal(infoBefore.ReclaimableBytes, infoAfter.ReclaimableBytes);
        Assert.Equal(100, reported);
    }

    [Fact]
    public async Task ExecuteAsync_EnabledAndNotScanning_ActuallyRunsMaintenance()
    {
        SetConfig(enableAutoMaintenance: true);
        await SeedReclaimableSpaceAsync();
        var infoBefore = await _factory.Database.GetMaintenanceInfoAsync();
        Assert.True(infoBefore.ReclaimableBytes > 0);

        var scanner = new Mock<IScanEngine>();
        scanner.SetupGet(s => s.IsScanning).Returns(false);
        var task = CreateTask(scanner);
        double? reported = null;
        var progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        var infoAfter = await _factory.Database.GetMaintenanceInfoAsync();
        Assert.Equal(0, infoAfter.ReclaimableBytes);
        Assert.Equal(100, reported);
    }

    // System.Progress<T>.Report() posts via the captured SynchronizationContext (or
    // the thread pool if none exists) rather than invoking synchronously -- xUnit has
    // no SynchronizationContext, making a bare Progress<T> a real, if rare, race for
    // any test that reads the reported value back (see DeepScanTaskTests for a case
    // where this was hit for real in CI). This test double invokes on the calling
    // thread instead.
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public SynchronousProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }
}
