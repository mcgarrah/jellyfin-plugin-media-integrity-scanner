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
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="AutoUpdateTask"/>'s gating on <c>EnableAutoUpdate</c>/
/// <c>AutoRestartAfterUpdate</c> and its session-aware wait before restarting,
/// mirroring the playback-pause pattern already covered by ScanEngineTests.
/// </summary>
[Collection("PluginInstance")]
public class AutoUpdateTaskTests : IDisposable
{
    public void Dispose() => TestPluginContext.Clear();

    private static void SetConfig(bool enableAutoUpdate, bool autoRestart = false)
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            EnableAutoUpdate = enableAutoUpdate,
            AutoRestartAfterUpdate = autoRestart
        });
    }

    private static AutoUpdateTask CreateTask(
        Mock<IUpdateChecker> updateChecker,
        Mock<ISessionManager>? sessions = null,
        Mock<ISystemManager>? systemManager = null)
    {
        return new AutoUpdateTask(
            updateChecker.Object,
            (sessions ?? new Mock<ISessionManager>()).Object,
            (systemManager ?? new Mock<ISystemManager>()).Object,
            NullLogger<AutoUpdateTask>.Instance);
    }

    private static Mock<IUpdateChecker> UpdateAvailableChecker(UpdateChannel channel = UpdateChannel.Stable)
    {
        var updateChecker = new Mock<IUpdateChecker>();
        updateChecker.Setup(u => u.RefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStatus { UpdateAvailable = true, Channel = channel, AvailableVersion = "0.2.0.0" });
        updateChecker.Setup(u => u.InstallAsync(channel, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return updateChecker;
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsWeeklyTriggerSundayAt4AM()
    {
        var task = CreateTask(new Mock<IUpdateChecker>());

        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, trigger.Type);
        Assert.Equal(DayOfWeek.Sunday, trigger.DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(4).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void TaskMetadata_IsWellFormed()
    {
        var task = CreateTask(new Mock<IUpdateChecker>());

        Assert.Equal("MediaIntegrityAutoUpdate", task.Key);
        Assert.Equal("Media Integrity", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }

    [Fact]
    public async Task ExecuteAsync_AutoUpdateDisabled_SkipsRefreshEntirely()
    {
        SetConfig(enableAutoUpdate: false);
        var updateChecker = new Mock<IUpdateChecker>();
        var task = CreateTask(updateChecker);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        updateChecker.Verify(u => u.RefreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NoUpdateAvailable_DoesNotInstall()
    {
        SetConfig(enableAutoUpdate: true);
        var updateChecker = new Mock<IUpdateChecker>();
        updateChecker.Setup(u => u.RefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateStatus { UpdateAvailable = false });
        var task = CreateTask(updateChecker);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        updateChecker.Verify(u => u.InstallAsync(It.IsAny<UpdateChannel>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateAvailable_AutoRestartDisabled_InstallsButNeverRestarts()
    {
        SetConfig(enableAutoUpdate: true, autoRestart: false);
        var updateChecker = UpdateAvailableChecker();
        var systemManager = new Mock<ISystemManager>();
        var task = CreateTask(updateChecker, systemManager: systemManager);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        updateChecker.Verify(u => u.InstallAsync(UpdateChannel.Stable, It.IsAny<CancellationToken>()), Times.Once);
        systemManager.Verify(s => s.Restart(), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateAvailable_AutoRestartEnabled_NoActiveSessions_InstallsThenRestarts()
    {
        SetConfig(enableAutoUpdate: true, autoRestart: true);
        var updateChecker = UpdateAvailableChecker();
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Sessions).Returns(Array.Empty<SessionInfo>());
        var systemManager = new Mock<ISystemManager>();
        var task = CreateTask(updateChecker, sessions, systemManager);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        updateChecker.Verify(u => u.InstallAsync(UpdateChannel.Stable, It.IsAny<CancellationToken>()), Times.Once);
        systemManager.Verify(s => s.Restart(), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateAvailable_AutoRestartEnabled_ActivePlayback_WaitsAndNeverRestartsUntilCancelled()
    {
        SetConfig(enableAutoUpdate: true, autoRestart: true);
        var updateChecker = UpdateAvailableChecker();

        var activeSession = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance)
        {
            NowPlayingItem = new BaseItemDto()
        };
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Sessions).Returns(new[] { activeSession });
        var systemManager = new Mock<ISystemManager>();
        var task = CreateTask(updateChecker, sessions, systemManager);

        using var cts = new CancellationTokenSource();
        var executeTask = task.ExecuteAsync(new Progress<double>(), cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
        systemManager.Verify(s => s.Restart(), Times.Never);
    }
}
