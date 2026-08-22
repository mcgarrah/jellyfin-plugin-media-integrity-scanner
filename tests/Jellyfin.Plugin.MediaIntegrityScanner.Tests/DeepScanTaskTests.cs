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
/// Tests for DeepScanTask, which is now a thin IScheduledTask wrapper around
/// ScanEngine.ScanLibraryAsync, gated by the EnableDeepScan setting it still
/// checks directly. Uses Plugin.Instance reflection plumbing (TestPluginContext),
/// so it shares the "PluginInstance" xUnit collection with every other test
/// class that touches that static (see PluginInstanceCollection) to avoid races.
/// </summary>
[Collection("PluginInstance")]
public class DeepScanTaskTests : IDisposable
{
    public void Dispose() => TestPluginContext.Clear();

    private static DeepScanTask CreateTask(Mock<IScanEngine> scanner)
    {
        return new DeepScanTask(scanner.Object, NullLogger<DeepScanTask>.Instance);
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsWeeklyTriggerSundayAt1AM()
    {
        var task = CreateTask(new Mock<IScanEngine>());

        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, trigger.Type);
        Assert.Equal(DayOfWeek.Sunday, trigger.DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntirely_WhenDeepScanDisabled()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableDeepScan = false });

        var scanner = new Mock<IScanEngine>();
        var task = CreateTask(scanner);
        double? reported = null;
        // System.Progress<T>.Report() posts the callback via the captured
        // SynchronizationContext (or the thread pool if none exists) rather
        // than invoking it synchronously -- xUnit's test runner has no
        // SynchronizationContext, so the callback lands on a thread pool
        // thread with no guaranteed ordering relative to the very next line
        // of test code. That made this assertion a genuine, if rare, race
        // (hit for real in CI once) despite DeepScanTask.ExecuteAsync itself
        // calling Report() synchronously and correctly. A plain IProgress<T>
        // implementation invokes its callback on the calling thread instead.
        IProgress<double> progress = new SynchronousProgress<double>(v => reported = v);

        await task.ExecuteAsync(progress, CancellationToken.None);

        scanner.Verify(
            s => s.ScanLibraryAsync(It.IsAny<string>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<int>>()),
            Times.Never);
        Assert.Equal(100, reported);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;

        public SynchronousProgress(Action<T> callback) => _callback = callback;

        public void Report(T value) => _callback(value);
    }

    [Fact]
    public async Task ExecuteAsync_DelegatesToScanLibraryAsync_WithFullDecodePhase_WhenEnabled()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableDeepScan = true });

        var scanner = new Mock<IScanEngine>();
        scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.FullDecode, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<int>>()))
            .Returns(Task.CompletedTask);

        var task = CreateTask(scanner);
        var progress = new Progress<double>();

        await task.ExecuteAsync(progress, CancellationToken.None);

        scanner.Verify(
            s => s.ScanLibraryAsync(null, ScanPhase.FullDecode, It.IsAny<CancellationToken>(), progress, It.IsAny<string>(), It.IsAny<IReadOnlyCollection<int>>()),
            Times.Once);
    }

    [Fact]
    public void TaskMetadata_IsWellFormed()
    {
        var task = CreateTask(new Mock<IScanEngine>());

        Assert.Equal("MediaIntegrityDeepScan", task.Key);
        Assert.Equal("Media Integrity", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }
}
