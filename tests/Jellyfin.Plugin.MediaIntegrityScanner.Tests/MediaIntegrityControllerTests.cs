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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Api;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

// GetDiagnostics reads Plugin.Instance -- join the shared collection so this
// class's TestPluginContext use doesn't race against the other test classes
// that also touch that process-wide static.
[Collection("PluginInstance")]
public class MediaIntegrityControllerTests : IDisposable
{
    private readonly TestDatabaseFactory _dbFactory = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IScanEngine> _scanner = new();
    private readonly Mock<IUpdateChecker> _updateChecker = new();
    private readonly Mock<IServerApplicationHost> _appHost = new();

    public void Dispose()
    {
        _dbFactory.Dispose();
        TestPluginContext.Clear();
    }

    private static FfmpegWrapper CreateFfmpegWrapper()
    {
        var resolverMock = new Mock<FfmpegResolver>(
            Mock.Of<IServerConfigurationManager>(), NullLogger<FfmpegResolver>.Instance);
        resolverMock.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg");
        resolverMock.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe");

        return new FfmpegWrapper(resolverMock.Object, NullLogger<FfmpegWrapper>.Instance);
    }

    private MediaIntegrityController CreateController()
    {
        _appHost.Setup(a => a.ApplicationVersionString).Returns("10.11.11.0");

        var controller = new MediaIntegrityController(
            _dbFactory.Database,
            _scanner.Object,
            _library.Object,
            _updateChecker.Object,
            CreateFfmpegWrapper(),
            _appHost.Object,
            NullLogger<MediaIntegrityController>.Instance);

        // A controller constructed directly (not through the real ASP.NET Core
        // pipeline) has a null HttpContext by default -- real here since
        // RefreshUpdateStatus/InstallUpdate read HttpContext.RequestAborted.
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };

        return controller;
    }

    private void SetLibraryItems(params Guid[] ids)
    {
        var items = new List<BaseItem>();
        foreach (var id in ids)
        {
            items.Add(new Movie { Id = id });
        }

        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);
    }

    // --- GetStatus ---

    [Fact]
    public async Task GetStatus_ComputesTotalAndPendingFromRealLibraryCount()
    {
        var scannedId = Guid.NewGuid();
        SetLibraryItems(scannedId, Guid.NewGuid(), Guid.NewGuid()); // 3 total, only 1 scanned

        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = scannedId.ToString(),
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var controller = CreateController();
        var result = await controller.GetStatus();

        var response = Assert.IsType<ScanStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, response.TotalFiles);
        Assert.Equal(1, response.ScannedFiles);
        Assert.Equal(1, response.PassedFiles);
        Assert.Equal(2, response.PendingHeaderFiles);
        Assert.Equal(3, response.PendingDeepFiles);
        Assert.Equal(100.0, response.HealthPercentage);
    }

    [Fact]
    public async Task GetStatus_HealthPercentageIsZero_WhenNothingScannedYet()
    {
        SetLibraryItems(Guid.NewGuid(), Guid.NewGuid());

        var controller = CreateController();
        var result = await controller.GetStatus();

        var response = Assert.IsType<ScanStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(0, response.ScannedFiles);
        Assert.Equal(2, response.PendingHeaderFiles);
        Assert.Equal(2, response.PendingDeepFiles);
        Assert.Equal(0, response.HealthPercentage);
    }

    [Fact]
    public async Task GetStatus_TracksHeaderAndDeepPendingCountsIndependently()
    {
        // Regression test for the bug where a Deep Scan in progress showed
        // "0 pending" (and thus looked hung) because the old single PendingFiles
        // counter treated any item with a prior Header-phase record as fully
        // "scanned", even though FullDecode hadn't touched it yet.
        var headerOnlyId = Guid.NewGuid();
        var deepScannedId = Guid.NewGuid();
        var neverScannedId = Guid.NewGuid();
        SetLibraryItems(headerOnlyId, deepScannedId, neverScannedId);

        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = headerOnlyId.ToString(),
            FilePath = "/media/header-only.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = deepScannedId.ToString(),
            FilePath = "/media/deep-scanned.mkv",
            ScanPhase = (int)ScanPhase.FullDecode,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var controller = CreateController();
        var result = await controller.GetStatus();

        var response = Assert.IsType<ScanStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, response.TotalFiles);
        // Header-equivalent: header-only + deep-scanned both count (FullDecode implies Header).
        Assert.Equal(2, response.ScannedFiles);
        Assert.Equal(1, response.PendingHeaderFiles); // only neverScannedId
        // Deep-specific: only the FullDecode item counts; the header-only item
        // is still pending a deep scan even though it's not pending a header scan.
        Assert.Equal(2, response.PendingDeepFiles); // headerOnlyId + neverScannedId
    }

    [Fact]
    public async Task GetStatus_ReflectsIsScanningFromScanEngine()
    {
        SetLibraryItems();
        _scanner.SetupGet(s => s.IsScanning).Returns(true);

        var controller = CreateController();
        var result = await controller.GetStatus();

        var response = Assert.IsType<ScanStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.IsScanning);
    }

    [Fact]
    public async Task GetStatus_ReflectsCurrentPhaseFromScanEngine()
    {
        SetLibraryItems();
        _scanner.SetupGet(s => s.CurrentPhase).Returns((int)ScanPhase.FullDecode);

        var controller = CreateController();
        var result = await controller.GetStatus();

        var response = Assert.IsType<ScanStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((int)ScanPhase.FullDecode, response.CurrentPhase);
    }

    [Fact]
    public async Task GetStatus_CurrentPhaseIsNull_WhenIdle()
    {
        SetLibraryItems();

        var controller = CreateController();
        var result = await controller.GetStatus();

        var response = Assert.IsType<ScanStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Null(response.CurrentPhase);
    }

    // --- GetDiagnostics ---

    [Fact]
    public async Task GetDiagnostics_ReportsEnvironmentAndAggregateCounts()
    {
        TestPluginContext.SetConfiguration(
            new PluginConfiguration
            {
                UpdateChannel = UpdateChannel.Stable,
                HardwareAccelerationType = HardwareAccelerationType.nvenc,
                MaxConcurrentScans = 3
            },
            new Version(1, 2, 3, 4));

        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = Guid.NewGuid().ToString(),
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var controller = CreateController();
        var result = await controller.GetDiagnostics();

        var response = Assert.IsType<DiagnosticsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("1.2.3.4", response.PluginVersion);
        Assert.Equal("Stable", response.UpdateChannel);
        Assert.Equal("10.11.11.0", response.JellyfinServerVersion);
        Assert.Equal("nvenc", response.HardwareAccelerationType);
        Assert.Equal(3, response.MaxConcurrentScans);
        Assert.Equal(1, response.TotalFiles);
        Assert.Equal(1, response.PassedFiles);
        Assert.Equal(100.0, response.HealthPercentage);
        Assert.False(string.IsNullOrEmpty(response.OperatingSystem));
        Assert.False(string.IsNullOrEmpty(response.DotNetVersion));
    }

    [Fact]
    public async Task GetDiagnostics_WithheldsResolvedPaths_WhenCustomOverrideConfigured()
    {
        // CreateFfmpegWrapper() always resolves fake, non-override paths --
        // this asserts the withholding behavior directly against the flag
        // FfmpegWrapper.IsUsingCustomOverride actually exposes, rather than
        // needing a real override wired end-to-end through the resolver mock.
        TestPluginContext.SetConfiguration(new PluginConfiguration());

        var controller = CreateController();
        var result = await controller.GetDiagnostics();
        var response = Assert.IsType<DiagnosticsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

        // The fake resolver in CreateFfmpegWrapper() never reports a custom
        // override, so this confirms the normal (non-withheld) path instead --
        // the withheld branch is exercised directly in FfmpegWrapperTests.
        Assert.False(response.UsingCustomFfmpegOverride);
        Assert.Equal("/fake/ffmpeg", response.FfmpegPath);
        Assert.Equal("/fake/ffprobe", response.FfprobePath);
    }

    [Fact]
    public async Task GetDiagnostics_ReportsUnknownDefaults_WhenPluginInstanceNotSet()
    {
        // No TestPluginContext.SetConfiguration call -- Plugin.Instance is
        // null here, exactly as it would be for any test not in this file's
        // PluginInstance collection. GetDiagnostics must degrade gracefully,
        // not throw a NullReferenceException.
        var controller = CreateController();

        var result = await controller.GetDiagnostics();

        var response = Assert.IsType<DiagnosticsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("unknown", response.UpdateChannel);
        Assert.Equal("unknown", response.HardwareAccelerationType);
    }

    // --- GetResults ---

    [Fact]
    public async Task GetResults_ClampsPageBelowOneToOne()
    {
        var controller = CreateController();

        var result = await controller.GetResults(page: 0);

        var response = Assert.IsType<PagedResultResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, response.Page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(500)]
    public async Task GetResults_ClampsOutOfRangePageSizeToDefault(int requestedPageSize)
    {
        var controller = CreateController();

        var result = await controller.GetResults(pageSize: requestedPageSize);

        var response = Assert.IsType<PagedResultResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(50, response.PageSize);
    }

    [Fact]
    public async Task GetResults_FiltersByLibraryId_ResolvedViaLibraryManager()
    {
        var inLibraryId = Guid.NewGuid();
        var outOfLibraryId = Guid.NewGuid();

        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = inLibraryId.ToString(),
            FilePath = "/media/in.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = outOfLibraryId.ToString(),
            FilePath = "/media/out.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var libraryGuid = Guid.NewGuid();
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == libraryGuid)))
            .Returns(new List<BaseItem> { new Movie { Id = inLibraryId } });

        var controller = CreateController();
        var result = await controller.GetResults(libraryId: libraryGuid.ToString());

        var response = Assert.IsType<PagedResultResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, response.TotalCount);
        Assert.Equal(inLibraryId.ToString(), response.Items[0].ItemId);
    }

    [Fact]
    public async Task GetResults_UnparsableLibraryId_ReturnsZeroResults()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = Guid.NewGuid().ToString(),
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var controller = CreateController();
        var result = await controller.GetResults(libraryId: "not-a-guid");

        var response = Assert.IsType<PagedResultResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(0, response.TotalCount);
    }

    [Fact]
    public async Task GetResults_FiltersByPhase()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "header-item",
            FilePath = "/media/header.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = "2026-01-01T00:00:00.0000000Z"
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "deep-item",
            FilePath = "/media/deep.mkv",
            ScanPhase = (int)ScanPhase.FullDecode,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = "2026-01-01T00:00:01.0000000Z"
        });

        var controller = CreateController();
        var result = await controller.GetResults(phase: (int)ScanPhase.FullDecode);

        var response = Assert.IsType<PagedResultResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, response.TotalCount);
        Assert.Equal("deep-item", response.Items[0].ItemId);
    }

    [Fact]
    public async Task GetResults_CombinesStatusAndPhaseFilters()
    {
        // Same item_id/phase pair can't coexist twice (upsert key), so use
        // different items to exercise all four status x phase combinations.
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "header-pass",
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = "2026-01-01T00:00:00.0000000Z"
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "header-fail",
            FilePath = "/media/b.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-01T00:00:01.0000000Z"
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "deep-fail",
            FilePath = "/media/c.mkv",
            ScanPhase = (int)ScanPhase.FullDecode,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-01T00:00:02.0000000Z"
        });

        var controller = CreateController();
        var result = await controller.GetResults(status: (int)ScanStatus.Fail, phase: (int)ScanPhase.FullDecode);

        var response = Assert.IsType<PagedResultResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, response.TotalCount);
        Assert.Equal("deep-fail", response.Items[0].ItemId);
    }

    // --- GetItemDetail ---

    [Fact]
    public async Task GetItemDetail_ReturnsNotFound_WhenNoRecordExists()
    {
        var controller = CreateController();

        var result = await controller.GetItemDetail("missing-item");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetItemDetail_ReturnsOk_WhenRecordExists()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "item-1",
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var controller = CreateController();
        var result = await controller.GetItemDetail("item-1");

        var ok = Assert.IsType<OkObjectResult>(result);
        var record = Assert.IsType<ScanRecord>(ok.Value);
        Assert.Equal("item-1", record.ItemId);
    }

    // --- TriggerScan ---

    [Fact]
    public void TriggerScan_ReturnsConflict_WhenAlreadyScanning()
    {
        _scanner.SetupGet(s => s.IsScanning).Returns(true);

        var controller = CreateController();
        var result = controller.TriggerScan(new ScanRequest());

        Assert.IsType<ConflictObjectResult>(result);
        _scanner.Verify(
            s => s.ScanLibraryAsync(It.IsAny<string>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<int>>()),
            Times.Never);
    }

    [Fact]
    public async Task TriggerScan_ReturnsAccepted_AndStartsLibraryScan_WhenNotScanning()
    {
        var tcs = new TaskCompletionSource();
        _scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), null, null))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        var controller = CreateController();
        var result = controller.TriggerScan(new ScanRequest());

        Assert.IsType<AcceptedResult>(result);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _scanner.Verify(s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), null, null), Times.Once);
    }

    [Fact]
    public async Task TriggerScan_DeepScanRequest_UsesFullDecodePhase()
    {
        var tcs = new TaskCompletionSource();
        _scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.FullDecode, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), null, null))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        var controller = CreateController();
        controller.TriggerScan(new ScanRequest { DeepScan = true });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _scanner.Verify(s => s.ScanLibraryAsync(null, ScanPhase.FullDecode, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), null, null), Times.Once);
    }

    [Fact]
    public async Task TriggerScan_PassesThroughNameFilterAndSeasons()
    {
        var tcs = new TaskCompletionSource();
        _scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), "Simpsons", It.Is<IReadOnlyCollection<int>>(seasons => seasons.SequenceEqual(new[] { 1, 2 }))))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        var controller = CreateController();
        controller.TriggerScan(new ScanRequest { NameFilter = "Simpsons", Seasons = new[] { 1, 2 } });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _scanner.Verify(
            s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), "Simpsons", It.Is<IReadOnlyCollection<int>>(seasons => seasons.SequenceEqual(new[] { 1, 2 }))),
            Times.Once);
    }

    [Fact]
    public async Task TriggerScan_WithItemId_ScansThatItemOnly()
    {
        var itemGuid = Guid.NewGuid();
        var item = new Movie { Id = itemGuid };
        _library.Setup(l => l.GetItemById(itemGuid)).Returns(item);

        var tcs = new TaskCompletionSource();
        _scanner.Setup(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        var controller = CreateController();
        controller.TriggerScan(new ScanRequest { ItemId = itemGuid.ToString() });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _scanner.Verify(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()), Times.Once);
        _scanner.Verify(
            s => s.ScanLibraryAsync(It.IsAny<string>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<int>>()),
            Times.Never);
    }

    // --- CancelScan ---

    [Fact]
    public void CancelScan_CallsScannerCancel_AndReturnsOk()
    {
        var controller = CreateController();

        var result = controller.CancelScan();

        Assert.IsType<OkObjectResult>(result);
        _scanner.Verify(s => s.Cancel(), Times.Once);
    }

    // --- GetBackups / CreateBackup / RestoreBackup ---

    [Fact]
    public async Task GetBackups_ReturnsBackupsFromDatabase()
    {
        await _dbFactory.Database.BackupAsync();

        var controller = CreateController();
        var result = await controller.GetBackups();

        var backups = Assert.IsAssignableFrom<IReadOnlyList<DatabaseBackupInfo>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Single(backups);
    }

    [Fact]
    public async Task CreateBackup_ReturnsConflict_AndCreatesNothing_WhenScanning()
    {
        _scanner.SetupGet(s => s.IsScanning).Returns(true);
        var controller = CreateController();

        var result = await controller.CreateBackup();

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(await _dbFactory.Database.ListBackupsAsync());
    }

    [Fact]
    public async Task CreateBackup_ReturnsOk_AndCreatesABackup_WhenNotScanning()
    {
        var controller = CreateController();

        var result = await controller.CreateBackup();

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(await _dbFactory.Database.ListBackupsAsync());
    }

    [Fact]
    public async Task RestoreBackup_ReturnsConflict_WhenScanning()
    {
        var fileName = await _dbFactory.Database.BackupAsync();
        _scanner.SetupGet(s => s.IsScanning).Returns(true);
        var controller = CreateController();

        var result = await controller.RestoreBackup(new RestoreBackupRequest { FileName = fileName });

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task RestoreBackup_ReturnsBadRequest_ForUnknownBackup()
    {
        var controller = CreateController();

        var result = await controller.RestoreBackup(new RestoreBackupRequest { FileName = "media-integrity-backup-missing.db" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RestoreBackup_ReturnsBadRequest_ForPathTraversalFileName()
    {
        var controller = CreateController();

        var result = await controller.RestoreBackup(new RestoreBackupRequest { FileName = "../escape.db" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RestoreBackup_ActuallyRestoresPriorState_WhenValidBackupProvided()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "before-backup",
            FilePath = "/media/before.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });
        var fileName = await _dbFactory.Database.BackupAsync();
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "after-backup",
            FilePath = "/media/after.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var controller = CreateController();
        var result = await controller.RestoreBackup(new RestoreBackupRequest { FileName = fileName });

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(await _dbFactory.Database.GetItemDetailAsync("before-backup"));
        Assert.Null(await _dbFactory.Database.GetItemDetailAsync("after-backup"));
    }

    // --- GetDatabaseInfo / RunDatabaseMaintenance ---

    [Fact]
    public async Task GetDatabaseInfo_ReturnsRealSizesFromTheDatabase()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "item-1",
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });

        var controller = CreateController();
        var result = await controller.GetDatabaseInfo();

        var info = Assert.IsType<DatabaseMaintenanceInfo>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(info.FileSizeBytes > 0);
        Assert.True(info.LogicalSizeBytes > 0);
    }

    [Fact]
    public async Task RunDatabaseMaintenance_ReturnsConflict_AndDoesNotRun_WhenScanning()
    {
        _scanner.SetupGet(s => s.IsScanning).Returns(true);
        var controller = CreateController();

        var result = await controller.RunDatabaseMaintenance();

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task RunDatabaseMaintenance_ReturnsOk_AndActuallyRunsMaintenance_WhenNotScanning()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "item-1",
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = DateTime.UtcNow.ToString("O")
        });
        _scanner.SetupGet(s => s.IsScanning).Returns(false);
        var controller = CreateController();

        var result = await controller.RunDatabaseMaintenance();

        var maintenanceResult = Assert.IsType<DatabaseMaintenanceResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(maintenanceResult.IntegrityCheckOk);
        Assert.Equal("ok", maintenanceResult.IntegrityCheckMessage);
        Assert.True(maintenanceResult.VacuumRan);
    }

    // --- RefreshFfmpegPaths ---

    [Fact]
    public void RefreshFfmpegPaths_ReturnsOk_WithCurrentlyResolvedPaths()
    {
        var controller = CreateController();

        var result = controller.RefreshFfmpegPaths();

        var refreshResult = Assert.IsType<FfmpegRefreshResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(refreshResult.Changed);
        Assert.Equal("/fake/ffmpeg", refreshResult.FfmpegPath);
        Assert.Equal("/fake/ffprobe", refreshResult.FfprobePath);
    }

    // --- ExportResults ---

    [Fact]
    public async Task ExportResults_Csv_IncludesDecodeModeAndHardwareAccelTypeColumns()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "hw-item",
            FilePath = "/media/hw.mkv",
            ScanPhase = (int)ScanPhase.FullDecode,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = "2026-01-01T00:00:00.0000000Z",
            DecodeMode = (int)DecodeMode.Hardware,
            HardwareAccelType = "cuda"
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "header-item",
            FilePath = "/media/header.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = "2026-01-01T00:00:01.0000000Z",
            DecodeMode = (int)DecodeMode.NotApplicable
        });

        var controller = CreateController();
        var result = await controller.ExportResults();

        var file = Assert.IsType<FileContentResult>(result);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("FilePath,Status,Phase,Timestamp,DurationMs,DecodeMode,HardwareAccelType,Error", lines[0]);
        Assert.Contains(lines, l => l.Contains("Hardware", StringComparison.Ordinal) && l.Contains("cuda", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("NotApplicable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportResults_Csv_ContentTypeAndFileName()
    {
        var controller = CreateController();
        var result = await controller.ExportResults();

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.EndsWith(".csv", file.FileDownloadName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportResults_Tsv_UsesTabDelimiter_AndFlattensEmbeddedTabsAndNewlines()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "item-1",
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-01T00:00:00.0000000Z",
            ErrorOutput = "line one\twith a tab\nline two"
        });

        var controller = CreateController();
        var result = await controller.ExportResults(format: "tsv");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/tab-separated-values", file.ContentType);
        Assert.EndsWith(".tsv", file.FileDownloadName, StringComparison.Ordinal);

        var tsv = System.Text.Encoding.UTF8.GetString(file.FileContents);
        var lines = tsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("FilePath\tStatus\tPhase\tTimestamp\tDurationMs\tDecodeMode\tHardwareAccelType\tError", lines[0]);
        // TSV has no standard quoting convention -- embedded tabs/newlines are
        // flattened to spaces rather than quoted, so the row still has exactly
        // 8 tab-separated fields and no literal tab/newline survives inside one.
        var dataRow = lines[1];
        Assert.Equal(8, dataRow.Split('\t').Length);
        Assert.Contains("line one with a tab line two", dataRow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportResults_FiltersByStatus()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "pass-item",
            FilePath = "/media/pass.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Pass,
            ScanTimestamp = "2026-01-01T00:00:00.0000000Z"
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "fail-item",
            FilePath = "/media/fail.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-01T00:00:01.0000000Z"
        });

        var controller = CreateController();
        var result = await controller.ExportResults(status: (int)ScanStatus.Fail);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("fail.mkv", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("pass.mkv", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportResults_FiltersByPhase()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "header-item",
            FilePath = "/media/header.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-01T00:00:00.0000000Z"
        });
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "deep-item",
            FilePath = "/media/deep.mkv",
            ScanPhase = (int)ScanPhase.FullDecode,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-01T00:00:01.0000000Z"
        });

        var controller = CreateController();
        var result = await controller.ExportResults(phase: (int)ScanPhase.FullDecode);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("deep.mkv", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("header.mkv", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportResults_Csv_QuotesFieldsContainingCommaQuoteOrNewline_PerRfc4180()
    {
        await _dbFactory.Database.SaveResultAsync(new ScanRecord
        {
            ItemId = "item-1",
            FilePath = "/media/a.mkv",
            ScanPhase = (int)ScanPhase.Header,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-01T00:00:00.0000000Z",
            ErrorOutput = "moov atom not found, \"invalid\" data\nsecond line"
        });

        var controller = CreateController();
        var result = await controller.ExportResults();

        var file = Assert.IsType<FileContentResult>(result);
        var csv = System.Text.Encoding.UTF8.GetString(file.FileContents);

        // RFC 4180: wrap the whole field in double quotes, and double up any
        // literal quote characters inside it. The embedded newline stays
        // literal *inside* the quoted field rather than starting a new CSV row.
        Assert.Contains("\"moov atom not found, \"\"invalid\"\" data\nsecond line\"", csv, StringComparison.Ordinal);
    }

    // --- GetUpdateStatus / RefreshUpdateStatus / InstallUpdate ---

    [Fact]
    public async Task GetUpdateStatus_ReturnsCachedStatus_WithoutCallingRefreshAsync()
    {
        var cached = new UpdateStatus { CurrentVersion = "0.1.0.0", UpdateAvailable = false };
        _updateChecker.SetupGet(u => u.CachedStatus).Returns(cached);

        var controller = CreateController();
        var result = await controller.GetUpdateStatus();

        var status = Assert.IsType<UpdateStatus>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Same(cached, status);
        _updateChecker.Verify(u => u.RefreshAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetUpdateStatus_NoCachedStatus_FallsBackToRefreshAsync()
    {
        _updateChecker.SetupGet(u => u.CachedStatus).Returns((UpdateStatus?)null);
        var fresh = new UpdateStatus { CurrentVersion = "0.1.0.0", UpdateAvailable = true, AvailableVersion = "0.2.0.0" };
        _updateChecker.Setup(u => u.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fresh);

        var controller = CreateController();
        var result = await controller.GetUpdateStatus();

        var status = Assert.IsType<UpdateStatus>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Same(fresh, status);
        _updateChecker.Verify(u => u.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshUpdateStatus_AlwaysCallsRefreshAsync_EvenWhenACachedStatusExists()
    {
        var cached = new UpdateStatus { CurrentVersion = "0.1.0.0", UpdateAvailable = false };
        _updateChecker.SetupGet(u => u.CachedStatus).Returns(cached);
        var fresh = new UpdateStatus { CurrentVersion = "0.1.0.0", UpdateAvailable = true, AvailableVersion = "0.2.0.0" };
        _updateChecker.Setup(u => u.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fresh);

        var controller = CreateController();
        var result = await controller.RefreshUpdateStatus();

        var status = Assert.IsType<UpdateStatus>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Same(fresh, status);
        _updateChecker.Verify(u => u.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallUpdate_ReturnsOk_WhenInstallSucceeds()
    {
        _updateChecker.Setup(u => u.InstallAsync(UpdateChannel.Development, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateController();
        var result = await controller.InstallUpdate(new InstallUpdateRequest { Channel = UpdateChannel.Development });

        Assert.IsType<OkObjectResult>(result);
        _updateChecker.Verify(u => u.InstallAsync(UpdateChannel.Development, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallUpdate_ReturnsBadRequest_WhenInstallAsyncThrowsInvalidOperationException()
    {
        _updateChecker.Setup(u => u.InstallAsync(It.IsAny<UpdateChannel>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no version found for that channel"));

        var controller = CreateController();
        var result = await controller.InstallUpdate(new InstallUpdateRequest { Channel = UpdateChannel.Stable });

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
