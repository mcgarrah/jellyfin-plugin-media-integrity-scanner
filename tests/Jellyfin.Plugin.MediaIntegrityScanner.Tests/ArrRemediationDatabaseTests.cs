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
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for the arr_remediation table's data-access methods on
/// <see cref="SqliteDatabaseManager"/>: recording an attempt, looking up the
/// latest attempt for an item, cycle counting, and the Issues-page join
/// query. Uses a real temp-file-backed database (via
/// <see cref="TestDatabaseFactory"/>) so these exercise real SQL, not mocked
/// behavior -- the join in <c>GetIssuesAsync</c> especially is worth
/// verifying for real.
/// </summary>
public class ArrRemediationDatabaseTests : IDisposable
{
    private readonly TestDatabaseFactory _factory = new();
    private readonly SqliteDatabaseManager _db;

    public ArrRemediationDatabaseTests()
    {
        _db = _factory.Database;
    }

    public void Dispose() => _factory.Dispose();

    private static ArrRemediationRecord MakeRemediation(
        string itemId,
        string arrApp = "radarr",
        string status = "success",
        string actionTaken = "deleted_and_blocklisted",
        string matchMethod = "provider_id",
        string? requestedAt = null,
        int cycleNumber = 1)
    {
        return new ArrRemediationRecord
        {
            ItemId = itemId,
            FilePath = $"/media/{itemId}.mkv",
            ArrApp = arrApp,
            MatchMethod = matchMethod,
            ArrItemId = 42,
            ArrFileId = 99,
            ActionTaken = actionTaken,
            Status = status,
            RequestedAt = requestedAt ?? DateTime.UtcNow.ToString("O"),
            CompletedAt = DateTime.UtcNow.ToString("O"),
            CycleNumber = cycleNumber
        };
    }

    private static ScanRecord MakeFailedScan(string itemId, int status = 2, int phase = 1, string? filePath = null, string? timestamp = null)
    {
        return new ScanRecord
        {
            ItemId = itemId,
            FilePath = filePath ?? $"/media/{itemId}.mkv",
            ScanPhase = phase,
            ScanStatus = status,
            ScanTimestamp = timestamp ?? DateTime.UtcNow.ToString("O"),
            ErrorOutput = "corrupt"
        };
    }

    [Fact]
    public async Task RecordRemediationAsync_ReturnsANewRowIdEachTime()
    {
        var itemId = Guid.NewGuid().ToString();

        var firstId = await _db.RecordRemediationAsync(MakeRemediation(itemId));
        var secondId = await _db.RecordRemediationAsync(MakeRemediation(itemId, cycleNumber: 2));

        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task GetLatestRemediationForItemAsync_ReturnsNull_WhenNeverForwarded()
    {
        var result = await _db.GetLatestRemediationForItemAsync(Guid.NewGuid().ToString());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestRemediationForItemAsync_ReturnsTheMostRecentAttempt_ByRequestedAt()
    {
        var itemId = Guid.NewGuid().ToString();
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "failed", requestedAt: "2026-08-01T00:00:00.0000000Z"));
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "success", requestedAt: "2026-08-20T00:00:00.0000000Z"));

        var latest = await _db.GetLatestRemediationForItemAsync(itemId);

        Assert.NotNull(latest);
        Assert.Equal("success", latest!.Status);
    }

    [Fact]
    public async Task GetLatestRemediationForItemAsync_RoundTripsAllFields()
    {
        var itemId = Guid.NewGuid().ToString();
        var record = MakeRemediation(itemId, arrApp: "sonarr", status: "failed", actionTaken: "unmatched", matchMethod: "path_suffix", cycleNumber: 3);
        record.ArrServerName = "main";
        record.ErrorMessage = "connection refused";
        record.RetryCount = 2;

        await _db.RecordRemediationAsync(record);
        var result = await _db.GetLatestRemediationForItemAsync(itemId);

        Assert.NotNull(result);
        Assert.Equal(itemId, result!.ItemId);
        Assert.Equal("sonarr", result.ArrApp);
        Assert.Equal("main", result.ArrServerName);
        Assert.Equal("path_suffix", result.MatchMethod);
        Assert.Equal(42, result.ArrItemId);
        Assert.Equal(99, result.ArrFileId);
        Assert.Equal("unmatched", result.ActionTaken);
        Assert.Equal("failed", result.Status);
        Assert.Equal("connection refused", result.ErrorMessage);
        Assert.Equal(2, result.RetryCount);
        Assert.Equal(3, result.CycleNumber);
    }

    [Fact]
    public async Task CountSuccessfulRemediationsForItemAsync_OnlyCountsSuccessStatus()
    {
        var itemId = Guid.NewGuid().ToString();
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "success"));
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "failed"));
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "success"));

        var count = await _db.CountSuccessfulRemediationsForItemAsync(itemId);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetIssuesAsync_OnlyReturnsFailAndErrorStatuses_NeverPassOrPending()
    {
        var passId = Guid.NewGuid().ToString();
        var failId = Guid.NewGuid().ToString();
        var errorId = Guid.NewGuid().ToString();
        var pendingId = Guid.NewGuid().ToString();

        await _db.SaveResultAsync(MakeFailedScan(passId, status: 1)); // Pass
        await _db.SaveResultAsync(MakeFailedScan(failId, status: 2)); // Fail
        await _db.SaveResultAsync(MakeFailedScan(errorId, status: 3)); // Error
        await _db.SaveResultAsync(MakeFailedScan(pendingId, status: 0)); // Pending

        var result = await _db.GetIssuesAsync(status: null, phase: null, arrAction: null, page: 1, pageSize: 50);

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, i => i.ItemId == failId);
        Assert.Contains(result.Items, i => i.ItemId == errorId);
        Assert.DoesNotContain(result.Items, i => i.ItemId == passId);
        Assert.DoesNotContain(result.Items, i => i.ItemId == pendingId);
    }

    [Fact]
    public async Task GetIssuesAsync_FiltersByStatusAndPhase()
    {
        var failHeaderId = Guid.NewGuid().ToString();
        var errorDeepId = Guid.NewGuid().ToString();
        await _db.SaveResultAsync(MakeFailedScan(failHeaderId, status: 2, phase: 1));
        await _db.SaveResultAsync(MakeFailedScan(errorDeepId, status: 3, phase: 2));

        var failOnly = await _db.GetIssuesAsync(status: 2, phase: null, arrAction: null, page: 1, pageSize: 50);
        Assert.Equal(1, failOnly.TotalCount);
        Assert.Equal(failHeaderId, failOnly.Items[0].ItemId);

        var deepOnly = await _db.GetIssuesAsync(status: null, phase: 2, arrAction: null, page: 1, pageSize: 50);
        Assert.Equal(1, deepOnly.TotalCount);
        Assert.Equal(errorDeepId, deepOnly.Items[0].ItemId);
    }

    [Fact]
    public async Task GetIssuesAsync_LeavesRemediationNull_ForAnItemNeverForwarded()
    {
        var itemId = Guid.NewGuid().ToString();
        await _db.SaveResultAsync(MakeFailedScan(itemId, status: 2));

        var result = await _db.GetIssuesAsync(status: null, phase: null, arrAction: null, page: 1, pageSize: 50);

        var row = Assert.Single(result.Items);
        Assert.Null(row.Remediation);
    }

    [Fact]
    public async Task GetIssuesAsync_JoinsTheMostRecentRemediationAttempt_NotAnOlderOne()
    {
        var itemId = Guid.NewGuid().ToString();
        await _db.SaveResultAsync(MakeFailedScan(itemId, status: 2));
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "failed", requestedAt: "2026-08-01T00:00:00.0000000Z"));
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "success", requestedAt: "2026-08-20T00:00:00.0000000Z"));

        var result = await _db.GetIssuesAsync(status: null, phase: null, arrAction: null, page: 1, pageSize: 50);

        var row = Assert.Single(result.Items);
        Assert.NotNull(row.Remediation);
        Assert.Equal("success", row.Remediation!.Status);
    }

    [Fact]
    public async Task GetIssuesAsync_Paginates()
    {
        for (var i = 0; i < 5; i++)
        {
            await _db.SaveResultAsync(MakeFailedScan(Guid.NewGuid().ToString(), status: 2, timestamp: $"2026-08-{10 + i:00}T00:00:00.0000000Z"));
        }

        var page1 = await _db.GetIssuesAsync(status: null, phase: null, arrAction: null, page: 1, pageSize: 2);
        var page2 = await _db.GetIssuesAsync(status: null, phase: null, arrAction: null, page: 2, pageSize: 2);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.DoesNotContain(page2.Items, i => i.ItemId == page1.Items[0].ItemId);
    }

    [Fact]
    public async Task GetPendingRemediationsAsync_ReturnsOnlyPendingRows_OldestFirst()
    {
        var itemA = Guid.NewGuid().ToString();
        var itemB = Guid.NewGuid().ToString();
        await _db.RecordRemediationAsync(MakeRemediation(itemA, status: "success"));
        await _db.RecordRemediationAsync(MakeRemediation(itemB, status: "pending", requestedAt: "2026-08-20T00:00:00.0000000Z"));
        await _db.RecordRemediationAsync(MakeRemediation(itemA, status: "pending", requestedAt: "2026-08-01T00:00:00.0000000Z"));

        var pending = await _db.GetPendingRemediationsAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal(itemA, pending[0].ItemId);
        Assert.Equal(itemB, pending[1].ItemId);
    }

    [Fact]
    public async Task HasPendingRemediationAsync_TrueOnlyWhileAPendingRowExists()
    {
        var itemId = Guid.NewGuid().ToString();
        Assert.False(await _db.HasPendingRemediationAsync(itemId));

        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "pending"));
        Assert.True(await _db.HasPendingRemediationAsync(itemId));
    }

    [Fact]
    public async Task GetLastCompletedRemediationForItemAsync_SkipsAPendingRow_ReturnsThePriorCompletedOne()
    {
        var itemId = Guid.NewGuid().ToString();
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "failed", requestedAt: "2026-08-01T00:00:00.0000000Z"));
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "pending", requestedAt: "2026-08-20T00:00:00.0000000Z"));

        var result = await _db.GetLastCompletedRemediationForItemAsync(itemId);

        Assert.NotNull(result);
        Assert.Equal("failed", result!.Status);
    }

    [Fact]
    public async Task GetLastCompletedRemediationForItemAsync_ReturnsNull_WhenOnlyPendingRowsExist()
    {
        var itemId = Guid.NewGuid().ToString();
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "pending"));

        var result = await _db.GetLastCompletedRemediationForItemAsync(itemId);

        Assert.Null(result);
    }

    [Fact]
    public async Task CountAutoRemediationsSinceAsync_OnlyCountsSuccessAndFailed_CompletedSinceTheCutoff()
    {
        var itemId = Guid.NewGuid().ToString();
        var old = MakeRemediation(itemId, status: "success");
        old.CompletedAt = "2026-08-01T00:00:00.0000000Z";
        await _db.RecordRemediationAsync(old);

        var recentSuccess = MakeRemediation(itemId, status: "success");
        recentSuccess.CompletedAt = "2026-08-23T12:00:00.0000000Z";
        await _db.RecordRemediationAsync(recentSuccess);

        var recentFailed = MakeRemediation(itemId, status: "failed");
        recentFailed.CompletedAt = "2026-08-23T13:00:00.0000000Z";
        await _db.RecordRemediationAsync(recentFailed);

        var recentSkipped = MakeRemediation(itemId, status: "skipped");
        recentSkipped.CompletedAt = "2026-08-23T14:00:00.0000000Z";
        await _db.RecordRemediationAsync(recentSkipped);

        var count = await _db.CountAutoRemediationsSinceAsync(new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task UpdateRemediationAsync_TransitionsAPendingRowToItsTerminalState()
    {
        var itemId = Guid.NewGuid().ToString();
        var id = await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "pending", matchMethod: "pending"));

        var pending = await _db.GetLatestRemediationForItemAsync(itemId);
        Assert.NotNull(pending);
        pending!.Status = "success";
        pending.ActionTaken = "deleted_and_searched";
        pending.MatchMethod = "provider_id";
        pending.ArrServerName = "Main";
        pending.ArrItemId = 7;
        pending.ArrFileId = 8;

        await _db.UpdateRemediationAsync(pending);

        var updated = await _db.GetLatestRemediationForItemAsync(itemId);
        Assert.NotNull(updated);
        Assert.Equal(id, updated!.Id);
        Assert.Equal("success", updated.Status);
        Assert.Equal("deleted_and_searched", updated.ActionTaken);
        Assert.Equal("provider_id", updated.MatchMethod);
        Assert.Equal("Main", updated.ArrServerName);
        Assert.Equal(7, updated.ArrItemId);
        Assert.Equal(8, updated.ArrFileId);
        Assert.NotNull(updated.CompletedAt);
    }

    /// <summary>
    /// Sets up two Fail rows: one whose (optional) remediation matches the
    /// given bucket, one whose remediation deliberately doesn't (a fixed
    /// "success"/"deleted_and_blocklisted" distractor, or -- when testing
    /// the "sent" bucket itself, which that distractor would also match --
    /// a "failed" one instead). Asserts the filter returns only the matching row.
    /// </summary>
    private async Task AssertArrActionBucketMatchesOnly(string bucket, string? matchStatus, string? matchAction)
    {
        var matchingId = Guid.NewGuid().ToString();
        var nonMatchingId = Guid.NewGuid().ToString();
        await _db.SaveResultAsync(MakeFailedScan(matchingId, status: 2));
        await _db.SaveResultAsync(MakeFailedScan(nonMatchingId, status: 2));

        if (matchStatus is not null)
        {
            await _db.RecordRemediationAsync(MakeRemediation(matchingId, status: matchStatus, actionTaken: matchAction!));
        }

        // The non-matching row always gets *some* remediation -- this is what
        // disqualifies it from "not_sent" as well as every other bucket.
        var distractorStatus = bucket == "sent" ? "failed" : "success";
        await _db.RecordRemediationAsync(MakeRemediation(nonMatchingId, status: distractorStatus));

        var result = await _db.GetIssuesAsync(status: null, phase: null, arrAction: bucket, page: 1, pageSize: 50);

        var row = Assert.Single(result.Items);
        Assert.Equal(matchingId, row.ItemId);
    }

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_NotSent() => AssertArrActionBucketMatchesOnly("not_sent", null, null);

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_Pending() => AssertArrActionBucketMatchesOnly("pending", "pending", "pending");

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_Sent() => AssertArrActionBucketMatchesOnly("sent", "success", "deleted_and_searched");

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_Unmatched() => AssertArrActionBucketMatchesOnly("unmatched", "skipped", "unmatched");

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_NoReplacement() => AssertArrActionBucketMatchesOnly("no_replacement", "skipped", "no_replacement_available");

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_Blocked() => AssertArrActionBucketMatchesOnly("blocked", "blocked", "skipped_cycle_limit");

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_Failed() => AssertArrActionBucketMatchesOnly("failed", "failed", "deleted_and_searched");

    [Fact]
    public Task GetIssuesAsync_ArrActionFilter_DryRun() => AssertArrActionBucketMatchesOnly("dry_run", "skipped", "would_delete_and_search");

    [Fact]
    public async Task GetAllIssuesAsync_ReturnsEveryMatchingRow_NotJustOnePage()
    {
        for (var i = 0; i < 5; i++)
        {
            await _db.SaveResultAsync(MakeFailedScan(Guid.NewGuid().ToString(), status: 2, timestamp: $"2026-08-{10 + i:00}T00:00:00.0000000Z"));
        }

        var all = await _db.GetAllIssuesAsync(status: null, phase: null, arrAction: null);

        Assert.Equal(5, all.Count);
    }

    [Fact]
    public async Task CountSuccessfulRemediationsForItemAsync_OnlyCountsSuccessesAfterTheMostRecentCycleReset()
    {
        var itemId = Guid.NewGuid().ToString();
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "success", requestedAt: "2026-08-01T00:00:00.0000000Z"));
        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "success", requestedAt: "2026-08-02T00:00:00.0000000Z"));

        Assert.Equal(2, await _db.CountSuccessfulRemediationsForItemAsync(itemId));

        var reset = MakeRemediation(itemId, status: "skipped", actionTaken: "cycle_reset", requestedAt: "2026-08-10T00:00:00.0000000Z");
        await _db.RecordRemediationAsync(reset);

        Assert.Equal(0, await _db.CountSuccessfulRemediationsForItemAsync(itemId));

        await _db.RecordRemediationAsync(MakeRemediation(itemId, status: "success", requestedAt: "2026-08-11T00:00:00.0000000Z"));

        Assert.Equal(1, await _db.CountSuccessfulRemediationsForItemAsync(itemId));
    }
}
