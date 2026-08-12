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
using Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for CheckForUpdatesTask, which refreshes the cached plugin update
/// status on a schedule so the dashboard/REST API don't make a live
/// network-dependent check on every page load.
/// </summary>
public class CheckForUpdatesTaskTests
{
    private static CheckForUpdatesTask CreateTask(Mock<IUpdateChecker> updateChecker)
    {
        return new CheckForUpdatesTask(updateChecker.Object, NullLogger<CheckForUpdatesTask>.Instance);
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsDailyTriggerAt4AM()
    {
        var task = CreateTask(new Mock<IUpdateChecker>());

        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(4).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void TaskMetadata_IsWellFormed()
    {
        var task = CreateTask(new Mock<IUpdateChecker>());

        Assert.Equal("MediaIntegrityCheckForUpdates", task.Key);
        Assert.Equal("Media Integrity", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysRefreshes_RegardlessOfWhetherAnUpdateIsFound()
    {
        var updateChecker = new Mock<IUpdateChecker>();
        updateChecker.Setup(u => u.RefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStatus { UpdateAvailable = false });

        var task = CreateTask(updateChecker);
        double? reported = null;
        var progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        updateChecker.Verify(u => u.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(100, reported);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateAvailable_DoesNotInstallOrThrow_OnlyRefreshes()
    {
        // This task is a passive status refresh -- confirming an update is
        // available must never trigger an install by itself (that's
        // AutoUpdateTask's job, gated by its own EnableAutoUpdate setting).
        var updateChecker = new Mock<IUpdateChecker>();
        updateChecker.Setup(u => u.RefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStatus { UpdateAvailable = true, Channel = UpdateChannel.Stable, AvailableVersion = "0.2.0.0" });

        var task = CreateTask(updateChecker);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        updateChecker.Verify(u => u.InstallAsync(It.IsAny<UpdateChannel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesRefreshAsyncFailure()
    {
        var updateChecker = new Mock<IUpdateChecker>();
        updateChecker.Setup(u => u.RefreshAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network unreachable"));

        var task = CreateTask(updateChecker);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
    }

    // System.Progress<T>.Report() posts via the captured SynchronizationContext (or
    // the thread pool if none exists) rather than invoking synchronously -- see
    // DeepScanTaskTests for the real, once-flaky-in-CI case this guards against.
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public SynchronousProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }
}
