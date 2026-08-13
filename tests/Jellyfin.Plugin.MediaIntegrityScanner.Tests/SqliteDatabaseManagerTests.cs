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
using Microsoft.Data.Sqlite;
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
        int? durationMs = 50,
        int decodeMode = 0,
        string? hardwareAccelType = null)
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
            DecodeMode = decodeMode,
            HardwareAccelType = hardwareAccelType,
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

        var results = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(1, results.TotalCount);
        Assert.Equal((int)ScanStatus.Fail, results.Items.Single().ScanStatus);
        Assert.Equal("now failing", results.Items.Single().ErrorOutput);
    }

    [Fact]
    public async Task SaveResultAsync_SameItemDifferentPhases_CreatesSeparateRows()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header));
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.FullDecode));

        var results = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(2, results.TotalCount);
    }

    [Fact]
    public async Task SaveResultAsync_PersistsHardwareDecodeMode_AndSurvivesGetResultsAsyncToo()
    {
        await _db.SaveResultAsync(MakeRecord(
            "item-1", phase: (int)ScanPhase.FullDecode, decodeMode: (int)DecodeMode.Hardware, hardwareAccelType: "cuda"));

        var detail = await _db.GetItemDetailAsync("item-1");
        Assert.NotNull(detail);
        Assert.Equal((int)DecodeMode.Hardware, detail!.DecodeMode);
        Assert.Equal("cuda", detail.HardwareAccelType);

        var results = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 50, itemIds: null);
        var fromResults = Assert.Single(results.Items);
        Assert.Equal((int)DecodeMode.Hardware, fromResults.DecodeMode);
        Assert.Equal("cuda", fromResults.HardwareAccelType);
    }

    [Fact]
    public async Task SaveResultAsync_HeaderPhase_DefaultsToNotApplicableDecodeMode()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header));

        var detail = await _db.GetItemDetailAsync("item-1");

        Assert.NotNull(detail);
        Assert.Equal((int)DecodeMode.NotApplicable, detail!.DecodeMode);
        Assert.Null(detail.HardwareAccelType);
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

    // --- MarkPendingAsync ---

    [Fact]
    public async Task MarkPendingAsync_InsertsNewPendingRows()
    {
        await _db.MarkPendingAsync(
            new List<(string ItemId, string FilePath)> { ("item-1", "/media/item-1.mkv"), ("item-2", "/media/item-2.mkv") },
            (int)ScanPhase.Header);

        var results = await _db.GetResultsAsync(status: (int)ScanStatus.Pending, phase: null, page: 1, pageSize: 10, itemIds: null);

        Assert.Equal(2, results.TotalCount);
        Assert.All(results.Items, r => Assert.Equal((int)ScanStatus.Pending, r.ScanStatus));
    }

    [Fact]
    public async Task MarkPendingAsync_NoOp_WhenListIsEmpty()
    {
        await _db.MarkPendingAsync(new List<(string ItemId, string FilePath)>(), (int)ScanPhase.Header);

        var stats = await _db.GetStatisticsAsync();
        Assert.Equal(0, stats.ScannedFiles);
    }

    [Fact]
    public async Task MarkPendingAsync_OverwritesAStaleFailRecordForTheSamePhase()
    {
        // A file that failed last time and is now queued for a rescan should show
        // Pending, not its stale Fail status, while the rescan is in flight.
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Fail));

        await _db.MarkPendingAsync(
            new List<(string ItemId, string FilePath)> { ("item-1", "/media/item-1.mkv") },
            (int)ScanPhase.Header);

        var detail = await _db.GetItemDetailAsync("item-1");
        Assert.NotNull(detail);
        Assert.Equal((int)ScanStatus.Pending, detail!.ScanStatus);
    }

    [Fact]
    public async Task MarkPendingAsync_DoesNotDowngradeAGenuinelyCurrentPassRecord()
    {
        // Defense-in-depth: callers are only supposed to pass items IsCurrentAsync
        // has already said need scanning, but a race (another trigger completing
        // the same item between that check and this batch call) should not be able
        // to regress a real Pass back to Pending.
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass));

        await _db.MarkPendingAsync(
            new List<(string ItemId, string FilePath)> { ("item-1", "/media/item-1.mkv") },
            (int)ScanPhase.Header);

        var detail = await _db.GetItemDetailAsync("item-1");
        Assert.NotNull(detail);
        Assert.Equal((int)ScanStatus.Pass, detail!.ScanStatus);
    }

    // --- GetStatisticsAsync ---

    [Fact]
    public async Task GetStatisticsAsync_ReturnsZeros_WhenEmpty()
    {
        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(0, stats.ScannedFiles);
        Assert.Equal(0, stats.DeepScannedFiles);
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
        // MakeRecord defaults to Header phase, so this item has not been deep-scanned.
        Assert.Equal(0, stats.DeepScannedFiles);
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
        Assert.Equal(1, stats.DeepScannedFiles);
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
    public async Task GetStatisticsAsync_DeepScannedFiles_CountsOnlyItemsWithAFullDecodeRecord()
    {
        // Regression test for the bug where the dashboard showed "0 pending"
        // during an active Deep Scan because ScannedFiles counted any item with
        // ANY prior record (e.g. an old Header-phase result) as fully "scanned".
        // header-only: has a record, but never deep-scanned.
        await _db.SaveResultAsync(MakeRecord("header-only", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass));
        // deep-only: went straight to a deep scan with no prior header record.
        await _db.SaveResultAsync(MakeRecord("deep-only", phase: (int)ScanPhase.FullDecode, status: (int)ScanStatus.Pass));
        // both: scanned at both phases -- the deep record is the "latest" (highest phase) one.
        await _db.SaveResultAsync(MakeRecord("both", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("both", phase: (int)ScanPhase.FullDecode, status: (int)ScanStatus.Pass));

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(3, stats.ScannedFiles); // all 3 items have at least a Header record
        Assert.Equal(2, stats.DeepScannedFiles); // only deep-only and both
    }

    [Fact]
    public async Task GetStatisticsAsync_ExcludesPendingRows_FromEveryCount()
    {
        // Regression test for the exact interaction MarkPendingAsync introduces:
        // a Pending row is a queue placeholder, not a real result, and must never
        // count toward ScannedFiles/DeepScannedFiles/Passed/Failed/Errored or
        // LastScanTimestamp -- otherwise a large in-flight queue would make the
        // dashboard look more "done" than it actually is, silently re-breaking the
        // exact bug item #49 already fixed once for a different root cause.
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));
        await _db.MarkPendingAsync(
            new List<(string ItemId, string FilePath)> { ("item-2", "/media/item-2.mkv") },
            (int)ScanPhase.Header);

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(1, stats.ScannedFiles); // only item-1, not the pending item-2
        Assert.Equal(0, stats.DeepScannedFiles);
        Assert.Equal(1, stats.PassedFiles);
        Assert.Equal(0, stats.FailedFiles);
        Assert.Equal(0, stats.ErroredFiles);
    }

    [Fact]
    public async Task GetStatisticsAsync_PendingDeepScanRow_DoesNotHideAnAlreadyCompletedHeaderResult()
    {
        // A subtler version of the same interaction: an item can have a real,
        // completed Header Pass AND a freshly pre-seeded Pending row for a Deep
        // scan that hasn't run yet. The "latest per item" logic here orders by
        // scan_phase DESC, so without excluding Pending rows first, the Pending
        // phase-2 placeholder would outrank and hide the completed phase-1 Pass --
        // making this item vanish from ScannedFiles even though it genuinely has
        // a completed Header result.
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass));
        await _db.MarkPendingAsync(
            new List<(string ItemId, string FilePath)> { ("item-1", "/media/item-1.mkv") },
            (int)ScanPhase.FullDecode);

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(1, stats.ScannedFiles); // the completed Header result still counts
        Assert.Equal(0, stats.DeepScannedFiles); // the Deep scan is genuinely still pending
        Assert.Equal(1, stats.PassedFiles);
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

    [Fact]
    public async Task GetStatisticsAsync_LastScanTimestamp_IgnoresAMoreRecentPendingRow()
    {
        // A file just queued for scanning (Pending, timestamped "now") is not a
        // completed scan -- LastScanTimestamp must keep reflecting the last actual
        // completed result, not the moment something was merely added to the queue.
        var completedAt = DateTime.UtcNow.AddMinutes(-5).ToString("O");
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass, timestamp: completedAt));

        await _db.MarkPendingAsync(
            new List<(string ItemId, string FilePath)> { ("item-2", "/media/item-2.mkv") },
            (int)ScanPhase.Header);

        var stats = await _db.GetStatisticsAsync();

        Assert.Equal(completedAt, stats.LastScanTimestamp);
    }

    // --- GetResultsAsync ---

    [Fact]
    public async Task GetResultsAsync_ReturnsAllRows_WhenUnfiltered()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("item-2"));

        var results = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(2, results.TotalCount);
        Assert.Equal(2, results.Items.Count);
    }

    [Fact]
    public async Task GetResultsAsync_FiltersByStatus()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-2", status: (int)ScanStatus.Fail));

        var results = await _db.GetResultsAsync(status: (int)ScanStatus.Fail, phase: null, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(1, results.TotalCount);
        Assert.Equal("item-2", results.Items.Single().ItemId);
    }

    [Fact]
    public async Task GetResultsAsync_FiltersByPhase()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header));
        await _db.SaveResultAsync(MakeRecord("item-2", phase: (int)ScanPhase.FullDecode));

        var results = await _db.GetResultsAsync(status: null, phase: (int)ScanPhase.FullDecode, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(1, results.TotalCount);
        Assert.Equal("item-2", results.Items.Single().ItemId);
    }

    [Fact]
    public async Task GetResultsAsync_CombinesStatusAndPhaseFilters()
    {
        await _db.SaveResultAsync(MakeRecord("header-pass", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("header-fail", phase: (int)ScanPhase.Header, status: (int)ScanStatus.Fail));
        await _db.SaveResultAsync(MakeRecord("deep-fail", phase: (int)ScanPhase.FullDecode, status: (int)ScanStatus.Fail));

        var results = await _db.GetResultsAsync(
            status: (int)ScanStatus.Fail, phase: (int)ScanPhase.FullDecode, page: 1, pageSize: 50, itemIds: null);

        Assert.Equal(1, results.TotalCount);
        Assert.Equal("deep-fail", results.Items.Single().ItemId);
    }

    [Fact]
    public async Task GetResultsAsync_FiltersByItemIds()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("item-2"));
        await _db.SaveResultAsync(MakeRecord("item-3"));

        var results = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 50, itemIds: new[] { "item-1", "item-3" });

        Assert.Equal(2, results.TotalCount);
        Assert.All(results.Items, r => Assert.Contains(r.ItemId, new[] { "item-1", "item-3" }));
    }

    [Fact]
    public async Task GetResultsAsync_ItemIdsContainingSqlMetacharacters_MatchesOnlyExactValue()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("weird'id;--"));

        var results = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 50, itemIds: new[] { "weird'id;--" });

        Assert.Equal(1, results.TotalCount);
        Assert.Equal("weird'id;--", results.Items.Single().ItemId);
    }

    [Fact]
    public async Task GetResultsAsync_EmptyItemIdsCollection_ShortCircuitsToNoResults()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));

        var results = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 50, itemIds: Array.Empty<string>());

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
            status: (int)ScanStatus.Fail, phase: null, page: 1, pageSize: 50, itemIds: new[] { "item-1", "item-2" });

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

        var page1 = await _db.GetResultsAsync(status: null, phase: null, page: 1, pageSize: 2, itemIds: null);
        var page2 = await _db.GetResultsAsync(status: null, phase: null, page: 2, pageSize: 2, itemIds: null);
        var page3 = await _db.GetResultsAsync(status: null, phase: null, page: 3, pageSize: 2, itemIds: null);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page2.Items.Count);
        Assert.Equal(5, page3.TotalCount);
        Assert.Single(page3.Items);

        // Ordered by scan_timestamp DESC, so item-4 (latest) should be first.
        Assert.Equal("item-4", page1.Items[0].ItemId);
    }

    // --- GetAllResultsAsync ---
    // Used exclusively by CSV/TSV export (MediaIntegrityController.ExportResults),
    // which needs every matching row in one shot rather than a page at a time.

    [Fact]
    public async Task GetAllResultsAsync_ReturnsAllRows_WhenUnfiltered()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-2", status: (int)ScanStatus.Fail));
        await _db.SaveResultAsync(MakeRecord("item-3", status: (int)ScanStatus.Error));

        var results = await _db.GetAllResultsAsync(status: null, phase: null);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task GetAllResultsAsync_FiltersByStatus()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));
        await _db.SaveResultAsync(MakeRecord("item-2", status: (int)ScanStatus.Fail));
        await _db.SaveResultAsync(MakeRecord("item-3", status: (int)ScanStatus.Fail));

        var results = await _db.GetAllResultsAsync(status: (int)ScanStatus.Fail, phase: null);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal((int)ScanStatus.Fail, r.ScanStatus));
    }

    [Fact]
    public async Task GetAllResultsAsync_FiltersByPhase()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", phase: (int)ScanPhase.Header));
        await _db.SaveResultAsync(MakeRecord("item-2", phase: (int)ScanPhase.FullDecode));

        var results = await _db.GetAllResultsAsync(status: null, phase: (int)ScanPhase.FullDecode);

        Assert.Equal("item-2", Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task GetAllResultsAsync_ReturnsEmptyList_WhenNoRowsMatch()
    {
        await _db.SaveResultAsync(MakeRecord("item-1", status: (int)ScanStatus.Pass));

        var results = await _db.GetAllResultsAsync(status: (int)ScanStatus.Fail, phase: null);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetAllResultsAsync_IsNotPageLimited_UnlikeGetResultsAsync()
    {
        // The whole reason this method exists separately from GetResultsAsync --
        // export needs every row, not one page's worth.
        for (var i = 0; i < 60; i++)
        {
            await _db.SaveResultAsync(MakeRecord($"item-{i}"));
        }

        var results = await _db.GetAllResultsAsync(status: null, phase: null);

        Assert.Equal(60, results.Count);
    }

    [Fact]
    public async Task GetAllResultsAsync_OrdersByScanTimestampDescending()
    {
        await _db.SaveResultAsync(MakeRecord("oldest", timestamp: "2026-01-01T00:00:00.0000000Z"));
        await _db.SaveResultAsync(MakeRecord("newest", timestamp: "2026-01-03T00:00:00.0000000Z"));
        await _db.SaveResultAsync(MakeRecord("middle", timestamp: "2026-01-02T00:00:00.0000000Z"));

        var results = await _db.GetAllResultsAsync(status: null, phase: null);

        Assert.Equal(new[] { "newest", "middle", "oldest" }, results.Select(r => r.ItemId));
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

    // --- ReconcileAsync ---

    [Fact]
    public async Task ReconcileAsync_PurgesItemsNotInCurrentSet()
    {
        await _db.SaveResultAsync(MakeRecord("still-here"));
        await _db.SaveResultAsync(MakeRecord("orphaned"));

        var purged = await _db.ReconcileAsync(new[] { "still-here" });

        Assert.Equal(1, purged);
        Assert.NotNull(await _db.GetItemDetailAsync("still-here"));
        Assert.Null(await _db.GetItemDetailAsync("orphaned"));
    }

    [Fact]
    public async Task ReconcileAsync_PurgesAllPhasesForAnOrphanedItem()
    {
        await _db.SaveResultAsync(MakeRecord("orphaned", phase: (int)ScanPhase.Header));
        await _db.SaveResultAsync(MakeRecord("orphaned", phase: (int)ScanPhase.FullDecode));

        var purged = await _db.ReconcileAsync(new[] { "unrelated-item" });

        Assert.Equal(2, purged);
        Assert.Null(await _db.GetItemDetailAsync("orphaned"));
    }

    [Fact]
    public async Task ReconcileAsync_ReturnsZero_WhenNothingIsOrphaned()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));
        await _db.SaveResultAsync(MakeRecord("item-2"));

        var purged = await _db.ReconcileAsync(new[] { "item-1", "item-2" });

        Assert.Equal(0, purged);
        Assert.NotNull(await _db.GetItemDetailAsync("item-1"));
        Assert.NotNull(await _db.GetItemDetailAsync("item-2"));
    }

    [Fact]
    public async Task ReconcileAsync_ReturnsZero_AndPurgesNothing_WhenCurrentItemIdsIsEmpty()
    {
        // Safety guard: an empty set is treated as "don't reconcile" (e.g. a
        // failed library query), not "the library is genuinely empty" -- a
        // real bug upstream should never be able to wipe the whole table.
        await _db.SaveResultAsync(MakeRecord("item-1"));

        var purged = await _db.ReconcileAsync(Array.Empty<string>());

        Assert.Equal(0, purged);
        Assert.NotNull(await _db.GetItemDetailAsync("item-1"));
    }

    // --- BackupAsync / ListBackupsAsync / RestoreAsync ---

    [Fact]
    public async Task BackupAsync_RestoreAsync_RoundTripsToTheSnapshottedState()
    {
        await _db.SaveResultAsync(MakeRecord("before-backup"));
        var backupFileName = await _db.BackupAsync();

        await _db.SaveResultAsync(MakeRecord("after-backup"));
        var statsBeforeRestore = await _db.GetStatisticsAsync();
        Assert.Equal(2, statsBeforeRestore.ScannedFiles);

        await _db.RestoreAsync(backupFileName);

        var statsAfterRestore = await _db.GetStatisticsAsync();
        Assert.Equal(1, statsAfterRestore.ScannedFiles);
        Assert.NotNull(await _db.GetItemDetailAsync("before-backup"));
        Assert.Null(await _db.GetItemDetailAsync("after-backup"));
    }

    [Fact]
    public async Task RestoreAsync_DatabaseRemainsFullyUsableAfterRestore()
    {
        // Specifically exercises the SqliteConnection.ClearAllPools() call in
        // RestoreAsync -- without it, a pooled connection could still be
        // holding the pre-restore file/WAL open, breaking subsequent queries.
        var backupFileName = await _db.BackupAsync();
        await _db.RestoreAsync(backupFileName);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await _db.SaveResultAsync(MakeRecord("after-restore"));
            await _db.GetStatisticsAsync();
        });

        Assert.Null(exception);
        Assert.NotNull(await _db.GetItemDetailAsync("after-restore"));
    }

    [Fact]
    public async Task BackupAsync_CalledTwiceInQuickSuccession_ProducesTwoDistinctBackups()
    {
        // Regression test: BackupAsync's file name used to be a bare
        // second-precision timestamp, which VACUUM INTO would refuse to
        // overwrite if two backups landed in the same second.
        var first = await _db.BackupAsync();
        var second = await _db.BackupAsync();

        Assert.NotEqual(first, second);

        var backups = await _db.ListBackupsAsync();
        Assert.Equal(2, backups.Count);
    }

    [Fact]
    public async Task ListBackupsAsync_ReturnsEmptyList_WhenNoBackupsExist()
    {
        var backups = await _db.ListBackupsAsync();
        Assert.Empty(backups);
    }

    [Fact]
    public async Task ListBackupsAsync_ReturnsNewestFirst()
    {
        var older = await _db.BackupAsync();
        await Task.Delay(50); // ext4's LastWriteTimeUtc resolution is well under this; keeps the test fast
        var newer = await _db.BackupAsync();

        var backups = await _db.ListBackupsAsync();

        Assert.Equal(2, backups.Count);
        Assert.Equal(newer, backups[0].FileName);
        Assert.Equal(older, backups[1].FileName);
    }

    [Fact]
    public async Task ListBackupsAsync_ReportsNonZeroSize()
    {
        var fileName = await _db.BackupAsync();

        var backups = await _db.ListBackupsAsync();

        var backup = Assert.Single(backups, b => b.FileName == fileName);
        Assert.True(backup.SizeBytes > 0);
        Assert.NotEmpty(backup.CreatedUtc);
    }

    [Fact]
    public async Task RestoreAsync_ThrowsFileNotFoundException_ForUnknownBackup()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _db.RestoreAsync("media-integrity-backup-does-not-exist.db"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape.db")]
    [InlineData("../../etc/passwd")]
    [InlineData("subdir/backup.db")]
    public async Task RestoreAsync_ThrowsArgumentException_ForNonBareFileNames(string maliciousFileName)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _db.RestoreAsync(maliciousFileName));
    }

    // --- GetMaintenanceInfoAsync / RunMaintenanceAsync ---

    [Fact]
    public async Task GetMaintenanceInfoAsync_ReportsNonZeroSizes_ForAnInitializedDatabase()
    {
        var info = await _db.GetMaintenanceInfoAsync();

        Assert.True(info.FileSizeBytes > 0);
        Assert.True(info.LogicalSizeBytes > 0);
        Assert.True(info.ReclaimableBytes >= 0);
    }

    [Fact]
    public async Task RunMaintenanceAsync_OnAHealthyDatabase_PassesIntegrityCheckAndRunsVacuum()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));

        var result = await _db.RunMaintenanceAsync();

        Assert.True(result.IntegrityCheckOk);
        Assert.Equal("ok", result.IntegrityCheckMessage);
        Assert.True(result.VacuumRan);
        Assert.True(result.SizeBeforeBytes > 0);
        Assert.True(result.SizeAfterBytes > 0);
    }

    [Fact]
    public async Task RunMaintenanceAsync_ReclaimsSpaceFreedByDeletedRows()
    {
        // Insert enough rows with sizable content to span multiple SQLite
        // pages, then delete most of them -- this is the exact scenario
        // VACUUM exists for: freed pages sitting inside the file, not yet
        // returned to the OS.
        var bulkyError = new string('x', 4000);
        for (var i = 0; i < 200; i++)
        {
            await _db.SaveResultAsync(MakeRecord($"item-{i}", error: bulkyError));
        }

        for (var i = 0; i < 180; i++)
        {
            await _db.PurgeItemAsync($"item-{i}");
        }

        var infoBefore = await _db.GetMaintenanceInfoAsync();
        Assert.True(infoBefore.ReclaimableBytes > 0);

        var result = await _db.RunMaintenanceAsync();

        Assert.True(result.VacuumRan);
        Assert.True(result.SizeAfterBytes < result.SizeBeforeBytes);

        var infoAfter = await _db.GetMaintenanceInfoAsync();
        Assert.Equal(0, infoAfter.ReclaimableBytes);
    }

    [Fact]
    public async Task RunMaintenanceAsync_OnACorruptedDatabase_FailsIntegrityCheckAndSkipsVacuum()
    {
        await _db.SaveResultAsync(MakeRecord("item-1"));

        // Run maintenance once first, purely so its own WAL checkpoint puts
        // all committed content into the main .db file on disk -- otherwise
        // corrupting the main file's bytes below could miss data that's still
        // only present in the -wal file, and integrity_check would see the
        // (uncorrupted) WAL version instead of what we actually broke.
        var baseline = await _db.RunMaintenanceAsync();
        Assert.True(baseline.IntegrityCheckOk);

        // Microsoft.Data.Sqlite pools connections by connection string, so
        // without this the baseline call above could still be holding a
        // native handle open on _factory.DbPath. On Linux that doesn't stop a
        // second, independent handle from reading/writing the same file, but
        // Windows' default file-sharing rules do enforce that exclusivity --
        // the raw File.ReadAllBytesAsync below would fail with "the process
        // cannot access the file because it is being used by another
        // process." Clearing pools here, before that read, releases the
        // pooled handle so the corruption below can actually take the file.
        // (This is also why RestoreAsync's own implementation clears pools
        // before its own raw file swap, for the same reason.)
        SqliteConnection.ClearAllPools();

        // Overwrite real page content with garbage, well past the 100-byte
        // header, so SQLite can still open the file (valid header) but
        // integrity_check finds real corruption inside -- not just a
        // malformed/unopenable file. Scattered rather than contiguous bytes,
        // to raise the odds of actually hitting structurally-significant
        // b-tree fields rather than incidental padding.
        var bytes = await File.ReadAllBytesAsync(_factory.DbPath);
        Assert.True(bytes.Length > 1000, "expected the database file to span more than one page");
        for (var i = 100; i < bytes.Length; i += 11)
        {
            bytes[i] = 0xFF;
        }

        await File.WriteAllBytesAsync(_factory.DbPath, bytes);

        // Clear pools again: the next RunMaintenanceAsync() call below could
        // otherwise reuse the same native handle as the baseline call above --
        // with page 1 still cached in memory from before the corruption,
        // masking it entirely.
        SqliteConnection.ClearAllPools();

        var result = await _db.RunMaintenanceAsync();

        Assert.False(result.IntegrityCheckOk);
        Assert.NotEqual("ok", result.IntegrityCheckMessage);
        Assert.False(result.VacuumRan);
        Assert.Equal(result.SizeBeforeBytes, result.SizeAfterBytes);
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
