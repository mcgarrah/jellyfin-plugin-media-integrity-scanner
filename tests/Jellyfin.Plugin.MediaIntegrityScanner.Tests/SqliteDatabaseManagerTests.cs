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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

public class SqliteDatabaseManagerTests : IDisposable
{
    private readonly TestDatabaseFactory _factory = new();
    private readonly SqliteDatabaseManager _db;
    private readonly List<string> _tempFiles = new();

    public SqliteDatabaseManagerTests()
    {
        _db = _factory.Database;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private static ScanRecord MakeRecord(
        string itemId,
        int phase = 1,
        int status = 1,
        string? filePath = null,
        long? fileSize = 1000,
        string? lastModified = "2026-01-01T00:00:00.0000000Z",
        string? timestamp = null,
        string? error = null,
        int? durationMs = 50)
    {
        return new ScanRecord
        {
            ItemId = itemId,
            FilePath = filePath ?? $"/media/{itemId}.mkv",
            FileSize = fileSize,
            LastModified = lastModified,
            ScanPhase = phase,
            ScanStatus = status,
            ScanTimestamp = timestamp ?? DateTime.UtcNow.ToString("O"),
            ErrorOutput = error,
            ScanDurationMs = durationMs
        };
    }

    // --- InitializeAsync ---

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await _db.InitializeAsync();
        await _db.InitializeAsync();

        // No exception, and the table is still usable.
        await _db.SaveResultAsync(MakeRecord("item-1"));
        var stats = await _db.GetStatisticsAsync();
        Assert.Equal(1, stats.ScannedFiles);
    }

    // --- SaveResultAsync ---

    [Fact]
    public async Task SaveResultAsync_PersistsAllFields()
    {
        await _db.SaveResultAsync(MakeRecord(
            "item-1",
            filePath: "/media/movie.mkv",
            fileSize: 123456,
            lastModified: "2026-01-01T00:00:00.0000000Z",
            error: "boom",
            durationMs: 999));

        var detail = await _db.GetItemDetailAsync("item-1");

        Assert.NotNull(detail);
        Assert.Equal("item-1", detail!.ItemId);
        Assert.Equal("/media/movie.mkv", detail.FilePath);
        Assert.Equal(123456, detail.FileSize);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", detail.LastModified);
        Assert.Equal("boom", detail.ErrorOutput);
        Assert.Equal(999, detail.ScanDurationMs);
    }

    [Fact]
    public async Task SaveResultAsync_PersistsNullableFieldsAsNull()
    {
        await _db.SaveResultAsync(MakeRecord(
            "item-1",
            fileSize: null,
            lastModified: null,
            error: null,
            durationMs: null));

        var detail = await _db.GetItemDetailAsync("item-1");

        Assert.NotNull(detail);
        Assert.Null(detail!.FileSize);
        Assert.Null(detail.LastModified);
        Assert.Null(detail.ErrorOutput);
        Assert.Null(detail.ScanDurationMs);
    }

    [Fact]
    public async Task SaveResultAsync_SameItemAndPhase_UpsertsInPlace()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass, error: null));
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Fail, error: "now failing"));

        var results = await _db.GetResultsAsync(status: null, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(1, results.TotalCount);
        Assert.Equal((int)ScanStatus.Fail, results.Items.Single().ScanStatus);
        Assert.Equal("now failing", results.Items.Single().ErrorOutput);
    }

    [Fact]
    public async Task SaveResultAsync_SameItemDifferentPhases_CreatesSeparateRows()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header));
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.FullDecode));

        var results = await _db.GetResultsAsync(status: null, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(2, results.TotalCount);
    }

    // --- IsCurrentAsync ---

    [Fact]
    public async Task IsCurrentAsync_ReturnsFalse_WhenNoRecordExists()
    {
        Assert.False(await _db.IsCurrentAsync("missing-item", "/media/missing.mkv", (int)ScanPhase.Header));
    }

    [Fact]
    public async Task IsCurrentAsync_ReturnsFalse_WhenOnlyFailedRecordExists()
    {
        var path = CreateTempFile();
        var mtime = File.GetLastWriteTimeUtc(path).ToString("O");

        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Fail, lastModified: mtime));

        Assert.False(await _db.IsCurrentAsync("item-1", path, (int)ScanPhase.Header));
    }

    [Fact]
    public async Task IsCurrentAsync_ReturnsTrue_WhenPassedAndFileUnchanged()
    {
        var path = CreateTempFile();
        var mtime = File.GetLastWriteTimeUtc(path).ToString("O");

        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass, lastModified: mtime));

        Assert.True(await _db.IsCurrentAsync("item-1", path, (int)ScanPhase.Header));
    }

    [Fact]
    public async Task IsCurrentAsync_ReturnsFalse_WhenFileModifiedSinceLastScan()
    {
        var path = CreateTempFile();

        await _db.SaveResultAsync(MakeRecord(
            "item-1", status: (int)ScanStatus.Pass, lastModified: "2000-01-01T00:00:00.0000000Z"));

        Assert.False(await _db.IsCurrentAsync("item-1", path, (int)ScanPhase.Header));
    }

    [Fact]
    public async Task IsCurrentAsync_ReturnsFalse_WhenFileNoLongerExists()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv");

        await _db.SaveResultAsync(MakeRecord(
            "item-1", status: (int)ScanStatus.Pass, lastModified: "2026-01-01T00:00:00.0000000Z"));

        Assert.False(await _db.IsCurrentAsync("item-1", path, (int)ScanPhase.Header));
    }

    [Fact]
    public async Task IsCurrentAsync_UsesHighestPhase_WhenMultiplePassedRecordsExist()
    {
        var path = CreateTempFile();
        var currentMtime = File.GetLastWriteTimeUtc(path).ToString("O");

        // Header (phase 1) has a stale mtime; FullDecode (phase 2) has the current one.
        await _db.SaveResultAsync(MakeRecord(
            "item-1", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass, lastModified: "2000-01-01T00:00:00.0000000Z"));
        await _db.SaveResultAsync(MakeRecord(
            "item-1", phase: (int)ScanPhase.FullDecode, status: (int)ScanStatus.Pass, lastModified: currentMtime));

        Assert.True(await _db.IsCurrentAsync("item-1", path, (int)ScanPhase.Header));
    }

    [Fact]
    public async Task IsCurrentAsync_ReturnsFalse_WhenOnlyHeaderPassed_ButFullDecodeRequired()
    {
        // Regression test: a file that only ever passed a Header (phase 1) scan
        // must NOT be treated as current when a FullDecode (phase 2) scan is
        // requested, even though the file itself hasn't changed. Before this fix,
        // IsCurrentAsync ignored scan_phase entirely, so DeepScanTask would skip
        // any file that had already passed a header check -- silently defeating
        // the deep scan's purpose of catching mid-file corruption header checks miss.
        var path = CreateTempFile();
        var mtime = File.GetLastWriteTimeUtc(path).ToString("O");

        await _db.SaveResultAsync(MakeRecord(
            "item-1", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass, lastModified: mtime));

        Assert.True(await _db.IsCurrentAsync("item-1", path, (int)ScanPhase.Header));
        Assert.False(await _db.IsCurrentAsync("item-1", path, (int)ScanPhase.FullDecode));
    }

    // --- GetStatisticsAsync ---

    [Fact]
    public async Task GetStatisticsAsync_ReturnsZeros_WhenEmpty()
    {
        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(0, stats.ScannedFiles);
        Assert.Equal(0, stats.PassedFiles);
        Assert.Equal(0, stats.FailedFiles);
        Assert.Equal(0, stats.ErroredFiles);
        Assert.Null(stats.LastScanTimestamp);
    }

    [Fact]
    public async Task GetStatisticsAsync_CountsSingleScannedItem()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(1, stats.ScannedFiles);
        Assert.Equal(1, stats.PassedFiles);
        Assert.Equal(0, stats.FailedFiles);
        Assert.Equal(0, stats.ErroredFiles);
    }

    [Fact]
    public async Task GetStatisticsAsync_DoesNotDoubleCount_ItemScannedInBothPhases()
    {
        // Header passed, but the more authoritative deep decode failed.
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.FullDecode, status: (int)ScanStatus.Fail));

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(1, stats.ScannedFiles);
        Assert.Equal(0, stats.PassedFiles);
        Assert.Equal(1, stats.FailedFiles);
    }

    [Fact]
    public async Task GetStatisticsAsync_BucketsMixedStatusesAcrossItems()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-2", status: (int)ScanStatus.Fail));
        await _db.SaveResultAsync(MakeRecord("item-3", status: (int)ScanStatus.Error));
        await _db.SaveResultAsync(MakeRecord("item-4", status: (int)ScanStatus.Pass));

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(4, stats.ScannedFiles);
        Assert.Equal(2, stats.PassedFiles);
        Assert.Equal(1, stats.FailedFiles);
        Assert.Equal(1, stats.ErroredFiles);
    }

    [Fact]
    public async Task GetStatisticsAsync_LastScanTimestamp_ReflectsMaxAcrossAllRows()
    {
        var older = DateTime.UtcNow.AddDays(-1).ToString("O");
        var newer = DateTime.UtcNow.ToString("O");

        // The most-recently-touched row (phase 1) is not the "latest phase" row,
        // but should still count toward the overall last-activity timestamp.
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.FullDecode, timestamp: older));
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header, timestamp: newer));

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(newer, stats.LastScanTimestamp);
    }

    // --- GetResultsAsync ---

    [Fact]
    public async Task GetResultsAsync_ReturnsAllRows_WhenUnfiltered()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("item-2"));

        var results = await _db.GetResultsAsync(status: null, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(2, results.TotalCount);
        Assert.Equal(2, results.Items.Count);
    }

    [Fact]
    public async Task GetResultsAsync_FiltersByStatus()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-2", status: (int)ScanStatus.Fail));

        var results = await _db.GetResultsAsync(status: (int)ScanStatus.Fail, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(1, results.TotalCount);
        Assert.Equal("item-2", results.Items.Single().ItemId);
    }

    [Fact]
    public async Task GetResultsAsync_FiltersByItemIds()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("item-2"));
        await _db.SaveResultAsync(MakeRecord("item-3"));

        var results = await _db.GetResultsAsync(status: null, page: 1, pageSize: 50, itemIds: new[] { "item-1", "item-3" });

        Assert.Equal(2, results.TotalCount);
        Assert.All(results.Items, r => Assert.Contains(r.ItemId, new[] { "item-1", "item-3" }));
    }

    [Fact]
    public async Task GetResultsAsync_ItemIdsContainingSqlMetacharacters_MatchesOnlyExactValue()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("weird'id;--"));

        var results = await _db.GetResultsAsync(status: null, page: 1, pageSize: 50, itemIds: new[] { "weird'id;--" });

        Assert.Equal(1, results.TotalCount);
        Assert.Equal("weird'id;--", results.Items.Single().ItemId);
    }

    [Fact]
    public async Task GetResultsAsync_EmptyItemIdsCollection_ShortCircuitsToNoResults()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));

        var results = await _db.GetResultsAsync(status: null, page: 1, pageSize: 50, itemIds: Array.Empty<string>());

        Assert.Equal(0, results.TotalCount);
        Assert.Empty(results.Items);
    }

    [Fact]
    public async Task GetResultsAsync_CombinesStatusAndItemIdsFilters()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-2", status: (int)ScanStatus.Fail));
        await _db.SaveResultAsync(MakeRecord("item-3", status: (int)ScanStatus.Fail));

        var results = await _db.GetResultsAsync(
            status: (int)ScanStatus.Fail, page: 1, pageSize: 50, itemIds: new[] { "item-1", "item-2" });

        Assert.Equal(1, results.TotalCount);
        Assert.Equal("item-2", results.Items.Single().ItemId);
    }

    [Fact]
    public async Task GetResultsAsync_PaginatesCorrectly()
    {
        for (var i = 0; i < 5; i++)
        {
            await _db.SaveResultAsync(MakeRecord($"item-{i}", timestamp: DateTime.UtcNow.AddSeconds(i).ToString("O")));
        }

        var page1 = await _db.GetResultsAsync(status: null, page: 1, pageSize: 2, itemIds: null);
        var page2 = await _db.GetResultsAsync(status: null, page: 2, pageSize: 2, itemIds: null);
        var page3 = await _db.GetResultsAsync(status: null, page: 3, pageSize: 2, itemIds: null);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page2.Items.Count);
        Assert.Equal(5, page3.TotalCount);
        Assert.Single(page3.Items);

        // Ordered by scan_timestamp DESC, so item-4 (latest) should be first.
        Assert.Equal("item-4", page1.Items[0].ItemId);
    }

    // --- GetItemDetailAsync ---

    [Fact]
    public async Task GetItemDetailAsync_ReturnsNull_WhenNotFound()
    {
        Assert.Null(await _db.GetItemDetailAsync("missing"));
    }

    [Fact]
    public async Task GetItemDetailAsync_ReturnsHighestPhase_WhenMultiplePhasesExist()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.FullDecode, status: (int)ScanStatus.Fail));

        var detail = await _db.GetItemDetailAsync("item-1");

        Assert.NotNull(detail);
        Assert.Equal((int)ScanPhase.FullDecode, detail!.ScanPhase);
        Assert.Equal((int)ScanStatus.Fail, detail.ScanStatus);
    }

    // --- PurgeItemAsync ---

    [Fact]
    public async Task PurgeItemAsync_RemovesAllPhasesForItem()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header));
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.FullDecode));

        await _db.PurgeItemAsync("item-1");

        Assert.Null(await _db.GetItemDetailAsync("item-1"));
    }

    [Fact]
    public async Task PurgeItemAsync_DoesNotAffectOtherItems()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("item-2"));

        await _db.PurgeItemAsync("item-1");

        Assert.Null(await _db.GetItemDetailAsync("item-1"));
        Assert.NotNull(await _db.GetItemDetailAsync("item-2"));
    }

    [Fact]
    public async Task PurgeItemAsync_NoOpForUnknownItem_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() => _db.PurgeItemAsync("never-existed"));
        Assert.Null(exception);
    }

    // --- Dispose ---

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        _db.Dispose();
        var exception = Record.Exception(() => _db.Dispose());
        Assert.Null(exception);
    }

    private string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(path, "test");
        _tempFiles.Add(path);
        return path;
    }
}
