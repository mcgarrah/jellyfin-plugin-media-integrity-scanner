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
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for ScanEngine's orchestration logic: concurrency bounding, IsScanning
/// tracking, DB persistence, cancellation, and library-scan skip/filter behavior.
/// Uses Plugin.Instance reflection plumbing (see TestPluginContext) to control
/// config, so it must not run in parallel with any other test class that also
/// touches Plugin.Instance — none currently do.
///
/// Quiet-hours and playback-pause *window/session logic itself* is not
/// re-verified here (ScanThrottleTests already covers the pure time-window
/// math); this class only checks that ScanEngine actually engages those gates
/// via a quick cancel-while-waiting probe, since neither DateTime.Now nor the
/// 30-second playback poll interval are injectable/mockable.
/// </summary>
public class ScanEngineTests : IDisposable
{
    public ScanEngineTests()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            MaxConcurrentScans = 1,
            DelayBetweenFilesMs = 0,
            PauseDuringPlayback = false,
            UseQuietHoursOnly = false,
            MaxReadRateMbPerSec = 0
        });
    }

    public void Dispose() => TestPluginContext.Clear();

    private static Mock<FfmpegWrapper> CreateFakeWrapper()
    {
        var resolverMock = new Mock<FfmpegResolver>(
            Mock.Of<IServerConfigurationManager>(), NullLogger<FfmpegResolver>.Instance);
        resolverMock.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg");
        resolverMock.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe");

        return new Mock<FfmpegWrapper>(resolverMock.Object, NullLogger<FfmpegWrapper>.Instance);
    }

    private static ScanEngine CreateEngine(
        Mock<FfmpegWrapper> wrapper,
        Mock<IDatabaseManager>? db = null,
        Mock<ISessionManager>? sessions = null,
        Mock<ILibraryManager>? library = null)
    {
        return new ScanEngine(
            wrapper.Object,
            (db ?? new Mock<IDatabaseManager>()).Object,
            (sessions ?? new Mock<ISessionManager>()).Object,
            (library ?? new Mock<ILibraryManager>()).Object,
            NullLogger<ScanEngine>.Instance);
    }

    private static Movie MakeItem()
    {
        var id = Guid.NewGuid();
        return new Movie { Id = id, Path = $"/media/{id:N}.mkv" };
    }

    // --- Basic scan + persistence ---

    [Fact]
    public async Task ScanItemAsync_PersistsPassResult_OnSuccessfulProbe()
    {
        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 10 });

        var db = new Mock<IDatabaseManager>();
        ScanRecord? saved = null;
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>()))
            .Callback<ScanRecord>(r => saved = r)
            .Returns(Task.CompletedTask);

        var engine = CreateEngine(wrapper, db);
        var item = MakeItem();

        await engine.ScanItemAsync(item, ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(item.Id.ToString(), saved!.ItemId);
        Assert.Equal((int)ScanStatus.Pass, saved.ScanStatus);
        Assert.Equal((int)ScanPhase.Header, saved.ScanPhase);
    }

    [Fact]
    public async Task ScanItemAsync_PersistsFailResult_OnFailedProbe()
    {
        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = false, ErrorOutput = "corrupt", DurationMs = 10 });

        var db = new Mock<IDatabaseManager>();
        ScanRecord? saved = null;
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>()))
            .Callback<ScanRecord>(r => saved = r)
            .Returns(Task.CompletedTask);

        var engine = CreateEngine(wrapper, db);

        await engine.ScanItemAsync(MakeItem(), ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal((int)ScanStatus.Fail, saved!.ScanStatus);
        Assert.Equal("corrupt", saved.ErrorOutput);
    }

    [Fact]
    public async Task ScanItemAsync_PersistsErrorResult_WhenFfmpegThrows()
    {
        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg not found"));

        var db = new Mock<IDatabaseManager>();
        ScanRecord? saved = null;
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>()))
            .Callback<ScanRecord>(r => saved = r)
            .Returns(Task.CompletedTask);

        var engine = CreateEngine(wrapper, db);

        await engine.ScanItemAsync(MakeItem(), ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal((int)ScanStatus.Error, saved!.ScanStatus);
        Assert.Equal("ffmpeg not found", saved.ErrorOutput);
    }

    [Fact]
    public async Task ScanItemAsync_FullDecodePhase_CallsDecodeAsyncNotProbeAsync()
    {
        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.DecodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 10 });

        var engine = CreateEngine(wrapper);

        await engine.ScanItemAsync(MakeItem(), ScanPhase.FullDecode, CancellationToken.None);

        wrapper.Verify(w => w.DecodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- IsScanning ---

    [Fact]
    public async Task IsScanning_TrueDuringScan_FalseAfterCompletion()
    {
        var gate = new TaskCompletionSource();
        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task;
                return new ScanResult { Success = true, DurationMs = 1 };
            });

        var engine = CreateEngine(wrapper);
        Assert.False(engine.IsScanning);

        var scanTask = engine.ScanItemAsync(MakeItem(), ScanPhase.Header, CancellationToken.None);

        // Give the scan a moment to actually enter ProbeAsync.
        await Task.Delay(50);
        Assert.True(engine.IsScanning);

        gate.SetResult();
        await scanTask;

        Assert.False(engine.IsScanning);
    }

    // --- Concurrency ---

    [Fact]
    public async Task MaxConcurrentScans_BoundsActualConcurrency()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            MaxConcurrentScans = 2,
            DelayBetweenFilesMs = 0
        });

        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;
        var lockObj = new object();

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (lockObj)
                {
                    currentConcurrency++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, currentConcurrency);
                }

                await Task.Delay(100);

                lock (lockObj)
                {
                    currentConcurrency--;
                }

                return new ScanResult { Success = true, DurationMs = 1 };
            });

        var engine = CreateEngine(wrapper);

        var tasks = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(engine.ScanItemAsync(MakeItem(), ScanPhase.Header, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        Assert.True(maxObservedConcurrency <= 2, $"Expected max concurrency <= 2, observed {maxObservedConcurrency}");
        Assert.True(maxObservedConcurrency >= 2, "Expected concurrency to actually reach the configured limit");
    }

    // --- Cancellation ---

    [Fact]
    public async Task Cancel_CancelsInFlightScan()
    {
        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new ScanResult { Success = true, DurationMs = 1 };
            });

        var engine = CreateEngine(wrapper);

        var scanTask = engine.ScanItemAsync(MakeItem(), ScanPhase.Header, CancellationToken.None);
        await Task.Delay(50);

        engine.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanTask);
    }

    // --- Playback pause gate ---

    [Fact]
    public async Task ScanItemAsync_PausesForPlayback_NeverInvokesFfmpeg_UntilCancelled()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            MaxConcurrentScans = 1,
            DelayBetweenFilesMs = 0,
            PauseDuringPlayback = true
        });

        var activeSession = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance)
        {
            NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto()
        };

        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Sessions).Returns(new[] { activeSession });

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, sessions: sessions);

        using var cts = new CancellationTokenSource();
        var scanTask = engine.ScanItemAsync(MakeItem(), ScanPhase.Header, cts.Token);

        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanTask);
        wrapper.Verify(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- ScanLibraryAsync ---

    [Fact]
    public async Task ScanLibraryAsync_SkipsItemsWhereIsCurrentAsyncReturnsTrue()
    {
        var current = MakeItem();
        var stale = MakeItem();

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { current, stale });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(current.Id.ToString(), current.Path, (int)ScanPhase.Header)).ReturnsAsync(true);
        db.Setup(d => d.IsCurrentAsync(stale.Id.ToString(), stale.Path, (int)ScanPhase.Header)).ReturnsAsync(false);
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>())).Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None);

        wrapper.Verify(w => w.ProbeAsync(stale.Path, It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(current.Path, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanLibraryAsync_PassesParsedLibraryIdAsParentId()
    {
        var libraryId = Guid.NewGuid();
        InternalItemsQuery? capturedQuery = null;

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        var wrapper = CreateFakeWrapper();
        var engine = CreateEngine(wrapper, library: library);

        await engine.ScanLibraryAsync(libraryId.ToString(), ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Equal(libraryId, capturedQuery!.ParentId);
    }

    [Fact]
    public async Task ScanLibraryAsync_IsScanning_TrueDuringRun_FalseAfter()
    {
        var gate = new TaskCompletionSource();
        var item = MakeItem();

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { item });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>())).Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task;
                return new ScanResult { Success = true, DurationMs = 1 };
            });

        var engine = CreateEngine(wrapper, db, library: library);

        var libraryScanTask = engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None);
        await Task.Delay(50);

        Assert.True(engine.IsScanning);

        gate.SetResult();
        await libraryScanTask;

        Assert.False(engine.IsScanning);
    }

    // --- Dispose ---

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var wrapper = CreateFakeWrapper();
        var engine = CreateEngine(wrapper);

        var exception = Record.Exception(() => engine.Dispose());
        Assert.Null(exception);
    }
}
