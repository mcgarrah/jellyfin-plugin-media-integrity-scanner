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
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="ArrRemediationWorker"/> -- the Phase 2 <c>IHostedService</c>
/// that drains the pending-remediation queue, and its per-tick circuit
/// breaker for a down server (see the "Circuit breaker" region). Drives
/// <c>ProcessQueueAsync</c> directly (internal, not the real one-minute
/// timer) for determinism.
/// </summary>
[Collection("PluginInstance")]
public class ArrRemediationWorkerTests : IDisposable
{
    private readonly Mock<IArrRemediationService> _remediation = new();
    private readonly Mock<IDatabaseManager> _db = new();

    public void Dispose() => TestPluginContext.Clear();

    private ArrRemediationWorker CreateWorker(IArrServerSelector? serverSelector = null) =>
        new(_remediation.Object, _db.Object, serverSelector ?? new ArrServerSelector(), NullLogger<ArrRemediationWorker>.Instance);

    private static ArrRemediationRecord MakePending(string itemId, long id, string app = "radarr", string filePath = "/x.mkv") =>
        new() { Id = id, ItemId = itemId, FilePath = filePath, ArrApp = app, MatchMethod = "pending", Status = "pending", RequestedAt = DateTime.UtcNow.ToString("O") };

    [Fact]
    public async Task ProcessQueueAsync_ForwardingDisabled_NeverReadsThePendingQueue()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableArrForwarding = false });
        var worker = CreateWorker();

        await worker.ProcessQueueAsync();

        _db.Verify(d => d.GetPendingRemediationsAsync(), Times.Never);
    }

    [Fact]
    public async Task ProcessQueueAsync_NoPendingRows_DoesNothing()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableArrForwarding = true });
        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(new List<ArrRemediationRecord>());
        var worker = CreateWorker();

        await worker.ProcessQueueAsync();

        _remediation.Verify(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessQueueAsync_ProcessesEveryPendingRow_UnderTheCap()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableArrForwarding = true, MaxAutoRemediationsPerDay = 10 });
        var pending = new List<ArrRemediationRecord> { MakePending("a", 1), MakePending("b", 2) };
        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(pending);
        _db.Setup(d => d.CountAutoRemediationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
        _remediation.Setup(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArrRemediationRecord r, CancellationToken _) => { r.Status = "success"; return r; });

        var worker = CreateWorker();
        await worker.ProcessQueueAsync();

        _remediation.Verify(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessQueueAsync_DailyCapAlreadyReached_SkipsEveryRow_WithoutCallingProcessPendingAsync()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableArrForwarding = true, MaxAutoRemediationsPerDay = 5 });
        var pending = new List<ArrRemediationRecord> { MakePending("a", 1) };
        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(pending);
        _db.Setup(d => d.CountAutoRemediationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(5);

        var worker = CreateWorker();
        await worker.ProcessQueueAsync();

        _remediation.Verify(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        _db.Verify(d => d.UpdateRemediationAsync(It.Is<ArrRemediationRecord>(r => r.ActionTaken == "skipped_daily_cap")), Times.Once);
    }

    [Fact]
    public async Task ProcessQueueAsync_CapReachedMidBatch_StopsProcessingFurtherRows()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableArrForwarding = true, MaxAutoRemediationsPerDay = 1 });
        var pending = new List<ArrRemediationRecord> { MakePending("a", 1), MakePending("b", 2) };
        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(pending);
        _db.Setup(d => d.CountAutoRemediationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
        _remediation.Setup(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArrRemediationRecord r, CancellationToken _) => { r.Status = "success"; return r; });

        var worker = CreateWorker();
        await worker.ProcessQueueAsync();

        _remediation.Verify(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _db.Verify(d => d.UpdateRemediationAsync(It.Is<ArrRemediationRecord>(r => r.Id == 2 && r.ActionTaken == "skipped_daily_cap")), Times.Once);
    }

    [Fact]
    public async Task ProcessQueueAsync_OneRowThrows_StillProcessesTheRest()
    {
        // Distinct file paths (not the shared default "/x.mkv") so the
        // circuit breaker doesn't attribute both rows to the same predicted
        // server and skip the second one -- that specific behavior has its
        // own dedicated tests below.
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableArrForwarding = true, MaxAutoRemediationsPerDay = 10 });
        var pending = new List<ArrRemediationRecord> { MakePending("a", 1, filePath: "/data/movies/a.mkv"), MakePending("b", 2, filePath: "/data/movies/b.mkv") };
        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(pending);
        _db.Setup(d => d.CountAutoRemediationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
        _remediation.Setup(r => r.ProcessPendingAsync(It.Is<ArrRemediationRecord>(x => x.Id == 1), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _remediation.Setup(r => r.ProcessPendingAsync(It.Is<ArrRemediationRecord>(x => x.Id == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ArrRemediationRecord r, CancellationToken _) => { r.Status = "success"; return r; });

        var worker = CreateWorker();
        await worker.ProcessQueueAsync();

        // Both rows have no server configured at all (default empty
        // RadarrServers), so SelectForPath predicts null for both --
        // "no server configured" can't be attributed to a specific server
        // failing, so the breaker must not suppress row 2 here.
        _remediation.Verify(r => r.ProcessPendingAsync(It.Is<ArrRemediationRecord>(x => x.Id == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessQueueAsync_ReentrancyGuard_SkipsIfAlreadyRunning()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { EnableArrForwarding = true, MaxAutoRemediationsPerDay = 10 });
        var pending = new List<ArrRemediationRecord> { MakePending("a", 1) };
        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(pending);
        _db.Setup(d => d.CountAutoRemediationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);

        var gate = new TaskCompletionSource();
        _remediation.Setup(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()))
            .Returns(async (ArrRemediationRecord r, CancellationToken _) => { await gate.Task; r.Status = "success"; return r; });

        var worker = CreateWorker();
        var firstPass = worker.ProcessQueueAsync();
        var secondPass = worker.ProcessQueueAsync(); // should return immediately, guard is already held
        await secondPass;
        gate.SetResult();
        await firstPass;

        _remediation.Verify(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Circuit breaker ---

    [Fact]
    public async Task ProcessQueueAsync_ServerFailsOnFirstItem_SkipsLaterItemsRoutedToSameServer()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            EnableArrForwarding = true,
            MaxAutoRemediationsPerDay = 10,
            RadarrServers = new List<ArrServerConfig>
            {
                new() { Name = "Main", Url = "http://radarr.local", ApiKey = "key" }
            }
        });

        var records = new[]
        {
            MakePending("a", 1, filePath: "/data/movies/a.mkv"),
            MakePending("b", 2, filePath: "/data/movies/b.mkv"),
            MakePending("c", 3, filePath: "/data/movies/c.mkv")
        };

        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(records);
        _db.Setup(d => d.CountAutoRemediationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);

        var callCount = 0;
        _remediation.Setup(r => r.ProcessPendingAsync(It.IsAny<ArrRemediationRecord>(), It.IsAny<CancellationToken>()))
            .Returns((ArrRemediationRecord record, CancellationToken _) =>
            {
                callCount++;
                // Simulate the real failure mode: a timeout/connection error
                // escapes ProcessPendingAsync entirely rather than coming back
                // as a normal "failed" status -- see ArrClientBase's 15s
                // HttpClient.Timeout and the comment in ProcessQueueAsync.
                throw new ArrClientException($"[RadarrClient] GET movie failed for record {record.Id}");
            });

        var worker = CreateWorker();

        await worker.ProcessQueueAsync();

        // Only the first item should have actually reached the remediation
        // service -- the second and third are routed to the same (now known
        // to have just failed) "Main" server and should be skipped without
        // ever calling ProcessPendingAsync again this tick.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ProcessQueueAsync_ServerFailsOnFirstItem_StillProcessesItemsRoutedToADifferentServer()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            EnableArrForwarding = true,
            MaxAutoRemediationsPerDay = 10,
            RadarrServers = new List<ArrServerConfig>
            {
                new() { Name = "Main", Url = "http://radarr.local", ApiKey = "key" }
            },
            SonarrServers = new List<ArrServerConfig>
            {
                new() { Name = "TV", Url = "http://sonarr.local", ApiKey = "key" }
            }
        });

        var records = new[]
        {
            MakePending("a", 1, app: "radarr", filePath: "/data/movies/a.mkv"),
            MakePending("b", 2, app: "sonarr", filePath: "/data/tv/b.mkv")
        };

        _db.Setup(d => d.GetPendingRemediationsAsync()).ReturnsAsync(records);
        _db.Setup(d => d.CountAutoRemediationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);

        _remediation.Setup(r => r.ProcessPendingAsync(It.Is<ArrRemediationRecord>(rec => rec.Id == 1), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArrClientException("[RadarrClient] unreachable"));
        _remediation.Setup(r => r.ProcessPendingAsync(It.Is<ArrRemediationRecord>(rec => rec.Id == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync(records[1]);

        var worker = CreateWorker();

        await worker.ProcessQueueAsync();

        // Record 2 routes to Sonarr's "TV" server, unrelated to the Radarr
        // "Main" server that just failed -- it must still be attempted.
        _remediation.Verify(r => r.ProcessPendingAsync(It.Is<ArrRemediationRecord>(rec => rec.Id == 2), It.IsAny<CancellationToken>()), Times.Once);
    }
}
