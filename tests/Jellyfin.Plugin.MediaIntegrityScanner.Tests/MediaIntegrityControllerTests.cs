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
using Jellyfin.Plugin.MediaIntegrityScanner.Api;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

public class MediaIntegrityControllerTests : IDisposable
{
    private readonly TestDatabaseFactory _dbFactory = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IScanEngine> _scanner = new();

    public void Dispose() => _dbFactory.Dispose();

    private MediaIntegrityController CreateController()
    {
        return new MediaIntegrityController(
            _dbFactory.Database,
            _scanner.Object,
            _library.Object,
            NullLogger<MediaIntegrityController>.Instance);
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
        Assert.Equal(2, response.PendingFiles);
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
        Assert.Equal(2, response.PendingFiles);
        Assert.Equal(0, response.HealthPercentage);
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
            s => s.ScanLibraryAsync(It.IsAny<string>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()),
            Times.Never);
    }

    [Fact]
    public async Task TriggerScan_ReturnsAccepted_AndStartsLibraryScan_WhenNotScanning()
    {
        var tcs = new TaskCompletionSource();
        _scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        var controller = CreateController();
        var result = controller.TriggerScan(new ScanRequest());

        Assert.IsType<AcceptedResult>(result);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _scanner.Verify(s => s.ScanLibraryAsync(null, ScanPhase.Header, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()), Times.Once);
    }

    [Fact]
    public async Task TriggerScan_DeepScanRequest_UsesFullDecodePhase()
    {
        var tcs = new TaskCompletionSource();
        _scanner.Setup(s => s.ScanLibraryAsync(null, ScanPhase.FullDecode, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        var controller = CreateController();
        controller.TriggerScan(new ScanRequest { DeepScan = true });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _scanner.Verify(s => s.ScanLibraryAsync(null, ScanPhase.FullDecode, It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()), Times.Once);
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
            s => s.ScanLibraryAsync(It.IsAny<string>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<double>>()),
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
}
