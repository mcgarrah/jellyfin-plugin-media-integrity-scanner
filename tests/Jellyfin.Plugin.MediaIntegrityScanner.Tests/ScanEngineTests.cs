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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
/// config; decorated with [Collection("PluginInstance")] so it doesn't race
/// other test classes that touch the same static (see PluginInstanceCollection).
///
/// Quiet-hours and playback-pause *window/session logic itself* is not
/// re-verified here (ScanThrottleTests already covers the pure time-window
/// math); this class only checks that ScanEngine actually engages those gates
/// via a quick cancel-while-waiting probe, since neither DateTime.Now nor the
/// 30-second playback poll interval are injectable/mockable.
/// </summary>
[Collection("PluginInstance")]
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
        Mock<ILibraryManager>? library = null,
        SharedBandwidthLimiter? bandwidthLimiter = null)
    {
        return new ScanEngine(
            wrapper.Object,
            (db ?? new Mock<IDatabaseManager>()).Object,
            (sessions ?? new Mock<ISessionManager>()).Object,
            (library ?? new Mock<ILibraryManager>()).Object,
            NullLogger<ScanEngine>.Instance,
            bandwidthLimiter);
    }

    private static Movie MakeItem()
    {
        var id = Guid.NewGuid();
        return new Movie { Id = id, Path = $"/media/{id:N}.mkv" };
    }

    private static Movie MakeMovie(string name)
    {
        var id = Guid.NewGuid();
        return new Movie { Id = id, Name = name, Path = $"/media/{id:N}.mkv" };
    }

    private static Episode MakeEpisode(string seriesName, int? seasonNumber)
    {
        var id = Guid.NewGuid();
        return new Episode
        {
            Id = id,
            Name = "Episode " + id,
            SeriesName = seriesName,
            ParentIndexNumber = seasonNumber,
            Path = $"/media/{id:N}.mkv"
        };
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

    [Fact]
    public async Task ScanItemAsync_FullDecode_PersistsDecodeModeAndHardwareAccelType_FromTheResult()
    {
        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.DecodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult
            {
                Success = true,
                DurationMs = 10,
                DecodeMode = DecodeMode.Hardware,
                HardwareAccelType = "cuda"
            });

        var db = new Mock<IDatabaseManager>();
        ScanRecord? saved = null;
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>()))
            .Callback<ScanRecord>(r => saved = r)
            .Returns(Task.CompletedTask);

        var engine = CreateEngine(wrapper, db);

        await engine.ScanItemAsync(MakeItem(), ScanPhase.FullDecode, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal((int)DecodeMode.Hardware, saved!.DecodeMode);
        Assert.Equal("cuda", saved.HardwareAccelType);
    }

    [Fact]
    public async Task ScanItemAsync_FullDecode_WhenDecodeThrows_RecordsAttemptedDecodeModeFromConfig()
    {
        // Even though DecodeAsync itself threw before returning a ScanResult, the
        // saved error record should still reflect which mode was actually in play
        // at attempt time -- same resolution FfmpegWrapper.ResolveHwAccelFlag would
        // have used -- so a hardware-decode-related crash isn't indistinguishable
        // from a software one after the fact.
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            MaxConcurrentScans = 1,
            DelayBetweenFilesMs = 0,
            PauseDuringPlayback = false,
            UseQuietHoursOnly = false,
            MaxReadRateMbPerSec = 0,
            HardwareAccelerationType = MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc
        });

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.DecodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("decoder crashed"));

        var db = new Mock<IDatabaseManager>();
        ScanRecord? saved = null;
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>()))
            .Callback<ScanRecord>(r => saved = r)
            .Returns(Task.CompletedTask);

        var engine = CreateEngine(wrapper, db);

        await engine.ScanItemAsync(MakeItem(), ScanPhase.FullDecode, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal((int)ScanStatus.Error, saved!.ScanStatus);
        Assert.Equal((int)DecodeMode.Hardware, saved.DecodeMode);
        Assert.Equal("cuda", saved.HardwareAccelType);
    }

    [Fact]
    public async Task ScanItemAsync_Header_WhenProbeThrows_RecordsNotApplicableDecodeMode()
    {
        // A Header-phase failure should never claim a decode mode -- ffprobe
        // never decodes, regardless of what hardware acceleration is configured.
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            MaxConcurrentScans = 1,
            DelayBetweenFilesMs = 0,
            PauseDuringPlayback = false,
            UseQuietHoursOnly = false,
            MaxReadRateMbPerSec = 0,
            HardwareAccelerationType = MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc
        });

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffprobe not found"));

        var db = new Mock<IDatabaseManager>();
        ScanRecord? saved = null;
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>()))
            .Callback<ScanRecord>(r => saved = r)
            .Returns(Task.CompletedTask);

        var engine = CreateEngine(wrapper, db);

        await engine.ScanItemAsync(MakeItem(), ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal((int)DecodeMode.NotApplicable, saved!.DecodeMode);
        Assert.Null(saved.HardwareAccelType);
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

    // --- CurrentPhase ---

    [Fact]
    public void CurrentPhase_IsNull_WhenIdle()
    {
        var engine = CreateEngine(CreateFakeWrapper());

        Assert.Null(engine.CurrentPhase);
    }

    [Fact]
    public async Task CurrentPhase_ReflectsPhase_DuringLibraryScan_AndIsNullAfter()
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
        wrapper.Setup(w => w.DecodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task;
                return new ScanResult { Success = true, DurationMs = 1 };
            });

        var engine = CreateEngine(wrapper, db, library: library);
        Assert.Null(engine.CurrentPhase);

        var libraryScanTask = engine.ScanLibraryAsync(null, ScanPhase.FullDecode, CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal((int)ScanPhase.FullDecode, engine.CurrentPhase);

        gate.SetResult();
        await libraryScanTask;

        Assert.Null(engine.CurrentPhase);
    }

    [Fact]
    public async Task CurrentScope_ReflectsLibraryNameFilterAndSeasons_DuringLibraryScan_AndIsNullAfter()
    {
        var gate = new TaskCompletionSource();
        // Named to actually survive the "Simpsons" name filter below -- this
        // test is only checking that the exposed Current* properties reflect
        // the active scan's scope, not re-verifying the filtering logic
        // itself (covered separately), so the item must not get excluded.
        var item = MakeMovie("Simpsons");
        var libraryId = Guid.NewGuid().ToString();

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
        Assert.Null(engine.CurrentLibraryId);
        Assert.Null(engine.CurrentNameFilter);
        Assert.Null(engine.CurrentSeasons);

        var libraryScanTask = engine.ScanLibraryAsync(
            libraryId, ScanPhase.Header, CancellationToken.None, nameFilter: "Simpsons", seasons: new[] { 1, 2 });
        await Task.Delay(50);

        Assert.Equal(libraryId, engine.CurrentLibraryId);
        Assert.Equal("Simpsons", engine.CurrentNameFilter);
        Assert.Equal(new[] { 1, 2 }, engine.CurrentSeasons);

        gate.SetResult();
        await libraryScanTask;

        Assert.Null(engine.CurrentLibraryId);
        Assert.Null(engine.CurrentNameFilter);
        Assert.Null(engine.CurrentSeasons);
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
    public async Task ScanLibraryAsync_PreSeedsPending_ForNotCurrentItemsOnly()
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

        IReadOnlyList<(string ItemId, string FilePath)>? markedPending = null;
        db.Setup(d => d.MarkPendingAsync(It.IsAny<IReadOnlyList<(string ItemId, string FilePath)>>(), (int)ScanPhase.Header))
            .Callback<IReadOnlyList<(string ItemId, string FilePath)>, int>((items, _) => markedPending = items)
            .Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(markedPending);
        var markedItem = Assert.Single(markedPending!);
        Assert.Equal(stale.Id.ToString(), markedItem.ItemId);
        Assert.Equal(stale.Path, markedItem.FilePath);
    }

    [Fact]
    public async Task ScanLibraryAsync_CallsMarkPendingAsync_WithAnEmptyBatch_WhenEverythingIsAlreadyCurrent()
    {
        var current = MakeItem();

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { current });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(current.Id.ToString(), current.Path, (int)ScanPhase.Header)).ReturnsAsync(true);

        IReadOnlyList<(string ItemId, string FilePath)>? markedPending = null;
        db.Setup(d => d.MarkPendingAsync(It.IsAny<IReadOnlyList<(string ItemId, string FilePath)>>(), (int)ScanPhase.Header))
            .Callback<IReadOnlyList<(string ItemId, string FilePath)>, int>((items, _) => markedPending = items)
            .Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(markedPending);
        Assert.Empty(markedPending!);
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
    public async Task ScanLibraryAsync_AlwaysSetsRecursiveTrue_SoLibraryScopedTvShowsAreActuallyFound()
    {
        // Regression test: Jellyfin's LibraryManager only descends into a
        // library folder's Series -> Season -> Episode hierarchy when
        // Recursive is true *and* ParentId is set. Without Recursive, a
        // library-scoped scan silently found 0 items for any TV library
        // (its direct children are Series, not the Video-typed episodes the
        // MediaTypes filter requires) -- caught via a live report ("set
        // library + name filter, Deep Scan button never changed state") that
        // traced back to this exact query.
        InternalItemsQuery? capturedQuery = null;

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        var wrapper = CreateFakeWrapper();
        var engine = CreateEngine(wrapper, library: library);

        await engine.ScanLibraryAsync(Guid.NewGuid().ToString(), ScanPhase.Header, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.True(capturedQuery!.Recursive);
    }

    [Fact]
    public async Task ScanLibraryAsync_NameFilter_MatchesMoviesByTitle_CaseInsensitive()
    {
        var wanted = MakeMovie("Dune: Part Two");
        var unwanted = MakeMovie("The Matrix");

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { wanted, unwanted });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>())).Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None, nameFilter: "dune");

        wrapper.Verify(w => w.ProbeAsync(wanted.Path, It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(unwanted.Path, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanLibraryAsync_NameFilter_MatchesEpisodesBySeriesName_NotEpisodeTitle()
    {
        // An episode's own Name (its episode title) should never match here --
        // scoping a scan "by name" means "this show", not "this one episode
        // that happens to share a word with the filter".
        var wanted = MakeEpisode("The Simpsons", seasonNumber: 1);
        var unwanted = MakeEpisode("Futurama", seasonNumber: 1);

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { wanted, unwanted });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>())).Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None, nameFilter: "simpsons");

        wrapper.Verify(w => w.ProbeAsync(wanted.Path, It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(unwanted.Path, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanLibraryAsync_SeasonFilter_RestrictsEpisodesToRequestedSeasons()
    {
        var seasonOne = MakeEpisode("The Simpsons", seasonNumber: 1);
        var seasonTwo = MakeEpisode("The Simpsons", seasonNumber: 2);
        var seasonThree = MakeEpisode("The Simpsons", seasonNumber: 3);

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { seasonOne, seasonTwo, seasonThree });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>())).Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None, seasons: new[] { 1, 3 });

        wrapper.Verify(w => w.ProbeAsync(seasonOne.Path, It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(seasonThree.Path, It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(seasonTwo.Path, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanLibraryAsync_SeasonFilter_DoesNotExcludeMovies()
    {
        // Movies have no season concept -- a season filter should never
        // silently drop them from a mixed-media library scan.
        var movie = MakeMovie("Dune: Part Two");
        var wrongSeasonEpisode = MakeEpisode("The Simpsons", seasonNumber: 5);

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie, wrongSeasonEpisode });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>())).Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None, seasons: new[] { 1 });

        wrapper.Verify(w => w.ProbeAsync(movie.Path, It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(wrongSeasonEpisode.Path, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanLibraryAsync_NameAndSeasonFilters_CombineAsAnd()
    {
        var matches = MakeEpisode("The Simpsons", seasonNumber: 2);
        var wrongShow = MakeEpisode("Futurama", seasonNumber: 2);
        var wrongSeason = MakeEpisode("The Simpsons", seasonNumber: 5);

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { matches, wrongShow, wrongSeason });

        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.IsCurrentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(false);
        db.Setup(d => d.SaveResultAsync(It.IsAny<ScanRecord>())).Returns(Task.CompletedTask);

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var engine = CreateEngine(wrapper, db, library: library);

        await engine.ScanLibraryAsync(null, ScanPhase.Header, CancellationToken.None, nameFilter: "simpsons", seasons: new[] { 2 });

        wrapper.Verify(w => w.ProbeAsync(matches.Path, It.IsAny<CancellationToken>()), Times.Once);
        wrapper.Verify(w => w.ProbeAsync(wrongShow.Path, It.IsAny<CancellationToken>()), Times.Never);
        wrapper.Verify(w => w.ProbeAsync(wrongSeason.Path, It.IsAny<CancellationToken>()), Times.Never);
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

    // --- Bandwidth throttling wiring ---
    //
    // SharedBandwidthLimiterTests already covers the pure TryConsume logic
    // deterministically. These two tests only check that ScanEngine actually
    // engages that limiter for a real file on disk (the existing tests above
    // all use MakeItem()'s nonexistent /media/{id}.mkv path, which never
    // reaches the fileInfo.Exists branch at all) -- using the real system
    // clock rather than a fake one, since the point is to prove genuine
    // wall-clock waiting happens, bounded to well under a second by a small
    // file size and a low configured rate.

    private static string CreateTempFile(int sizeBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mis-bandwidth-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    [Fact]
    public async Task ScanItemAsync_ThrottlesAcrossCalls_ViaTheSharedBandwidthBudget()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            MaxConcurrentScans = 1,
            DelayBetweenFilesMs = 0,
            PauseDuringPlayback = false,
            UseQuietHoursOnly = false,
            MaxReadRateMbPerSec = 2
        });

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var limiter = new SharedBandwidthLimiter();
        var engine = CreateEngine(wrapper, bandwidthLimiter: limiter);

        // Bucket seeds at 2 MB. Two 1.5 MB files back-to-back need 3 MB total
        // -- the second must wait roughly 0.5s (at 2 MB/s) for enough budget
        // to refill, via ScanEngine's real 200ms poll loop.
        var fileA = CreateTempFile(1024 * 1024 + 512 * 1024);
        var fileB = CreateTempFile(1024 * 1024 + 512 * 1024);
        try
        {
            var itemA = new Movie { Id = Guid.NewGuid(), Path = fileA };
            var itemB = new Movie { Id = Guid.NewGuid(), Path = fileB };

            var stopwatch = Stopwatch.StartNew();
            await engine.ScanItemAsync(itemA, ScanPhase.Header, CancellationToken.None);
            await engine.ScanItemAsync(itemB, ScanPhase.Header, CancellationToken.None);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed > TimeSpan.FromMilliseconds(300),
                $"Expected the second scan to wait on the shared budget (~0.5s), but both completed in {stopwatch.Elapsed}.");
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
    }

    [Fact]
    public async Task ScanItemAsync_DoesNotThrottle_WhenMaxReadRateIsZero()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            MaxConcurrentScans = 1,
            DelayBetweenFilesMs = 0,
            PauseDuringPlayback = false,
            UseQuietHoursOnly = false,
            MaxReadRateMbPerSec = 0
        });

        var wrapper = CreateFakeWrapper();
        wrapper.Setup(w => w.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult { Success = true, DurationMs = 1 });

        var limiter = new SharedBandwidthLimiter();
        var engine = CreateEngine(wrapper, bandwidthLimiter: limiter);

        var fileA = CreateTempFile(10 * 1024 * 1024);
        var fileB = CreateTempFile(10 * 1024 * 1024);
        try
        {
            var itemA = new Movie { Id = Guid.NewGuid(), Path = fileA };
            var itemB = new Movie { Id = Guid.NewGuid(), Path = fileB };

            var stopwatch = Stopwatch.StartNew();
            await engine.ScanItemAsync(itemA, ScanPhase.Header, CancellationToken.None);
            await engine.ScanItemAsync(itemB, ScanPhase.Header, CancellationToken.None);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(300),
                $"Expected no throttling wait with MaxReadRateMbPerSec=0, but both scans took {stopwatch.Elapsed}.");
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
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
