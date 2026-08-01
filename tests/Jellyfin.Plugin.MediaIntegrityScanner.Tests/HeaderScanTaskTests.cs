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
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Jellyfin.Plugin.MediaIntegrityScanner.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for HeaderScanTask, which is now a thin IScheduledTask wrapper around
/// ScanEngine.ScanLibraryAsync (the item-iteration/skip logic it used to
/// duplicate lives there, and is covered by ScanEngineTests). No Plugin.Instance
/// plumbing is needed here since ExecuteAsync doesn't read configuration itself.
/// </summary>
public class HeaderScanTaskTests
{
    private static HeaderScanTask CreateTask(Mock<IScanEngine> scanner)
    {
        return new HeaderScanTask(scanner.Object, NullLogger<HeaderScanTask>.Instance);
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsDailyTriggerAt3AM()
    {
        var task = CreateTask(new Mock<IScanEngine>());

        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public async Task ExecuteAsync_DelegatesToScanLibraryAsync_WithHeaderPhaseAndNoLibraryScope()
    {
        var scanner = new Mock<IScanEngine>();
        scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()))
            .Returns(Task.CompletedTask);

        var task = CreateTask(scanner);
        var progress = new Progress<double>();

        await task.ExecuteAsync(progress, CancellationToken.None);

        scanner.Verify(
            s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), progress),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesScanLibraryAsyncFailure()
    {
        var scanner = new Mock<IScanEngine>();
        scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var task = CreateTask(scanner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
    }

    [Fact]
    public void TaskMetadata_IsWellFormed()
    {
        var task = CreateTask(new Mock<IScanEngine>());

        Assert.Equal("MediaIntegrityHeaderScan", task.Key);
        Assert.Equal("Media Integrity", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }
}
