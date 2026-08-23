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
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Radarr;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration.Sonarr;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="ArrRemediationService"/> -- the orchestrator
/// implementing the full remediation flow from
/// <c>ARR-INTEGRATION-PROPOSAL.md</c> section 4. All Radarr/Sonarr calls
/// are mocked (see <see cref="ArrRemediationServiceTests"/>' sibling client
/// tests for real-HTTP-shape coverage); this class verifies the
/// *orchestration* -- step ordering, the pre-flight check's effect, the
/// blocklist-vs-search branch, and error/skip recording.
/// </summary>
[Collection("PluginInstance")]
public class ArrRemediationServiceTests : IDisposable
{
    private readonly Mock<IArrClientFactory> _clientFactory = new();
    private readonly Mock<IArrItemMatcher> _matcher = new();
    private readonly Mock<IDatabaseManager> _db = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IRadarrClient> _radarr = new();
    private readonly Mock<ISonarrClient> _sonarr = new();

    public ArrRemediationServiceTests()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            RadarrServers = new List<ArrServerConfig> { new() { Name = "main", Url = "http://radarr", ApiKey = "key" } },
            SonarrServers = new List<ArrServerConfig> { new() { Name = "main", Url = "http://sonarr", ApiKey = "key" } },
            HistoryLookbackDays = 30
        });

        _clientFactory.Setup(f => f.CreateRadarrClient(It.IsAny<ArrServerConfig>())).Returns(_radarr.Object);
        _clientFactory.Setup(f => f.CreateSonarrClient(It.IsAny<ArrServerConfig>())).Returns(_sonarr.Object);

        _db.Setup(d => d.CountSuccessfulRemediationsForItemAsync(It.IsAny<string>())).ReturnsAsync(0);
        _db.Setup(d => d.RecordRemediationAsync(It.IsAny<ArrRemediationRecord>())).ReturnsAsync(1L);
    }

    public void Dispose() => TestPluginContext.Clear();

    private ArrRemediationService CreateService() =>
        new(_clientFactory.Object, _matcher.Object, _db.Object, _library.Object, NullLogger<ArrRemediationService>.Instance);

    private static Movie MakeMovie() => new() { Id = Guid.NewGuid(), Path = "/movies/Sintel/Sintel.mkv" };

    private static RadarrReleaseCandidate ViableRelease() => new() { Title = "release", Rejected = false };

    private static RadarrHistoryRecord GrabbedHistory(int id, string date) =>
        new() { Id = id, MovieId = 1, EventType = "grabbed", Date = date, SourceTitle = "release" };

    // --- Unsupported item type ---

    [Fact]
    public async Task RemediateAsync_UnsupportedItemType_RecordsUnmatched_WithoutCallingAnyClient()
    {
        var audio = new Audio { Id = Guid.NewGuid(), Path = "/music/song.mp3" };
        var service = CreateService();

        var result = await service.RemediateAsync(audio, scanRecordId: null, CancellationToken.None);

        Assert.Equal("unmatched", result.MatchMethod);
        Assert.Equal("skipped", result.Status);
        _radarr.VerifyNoOtherCalls();
        _sonarr.VerifyNoOtherCalls();
    }

    // --- No server configured ---

    [Fact]
    public async Task RemediateAsync_NoRadarrServerConfigured_RecordsUnmatched_WithoutCallingMatcher()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { RadarrServers = new List<ArrServerConfig>() });
        var service = CreateService();

        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        Assert.Equal("skipped", result.Status);
        _matcher.VerifyNoOtherCalls();
    }

    // --- Unmatched ---

    [Fact]
    public async Task RemediateAsync_MatcherReturnsUnmatched_RecordsSkipped_WithoutDeletingAnything()
    {
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArrMatchResult.Unmatched);
        var service = CreateService();

        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        Assert.Equal("skipped", result.Status);
        Assert.Equal("unmatched", result.ActionTaken);
        _radarr.Verify(r => r.DeleteMovieFileAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Pre-flight availability check ---

    [Fact]
    public async Task RemediateAsync_NoViableReplacementCandidates_DoesNotDeleteTheFile()
    {
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 1, 99));
        _radarr.Setup(r => r.SearchReleasesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RadarrReleaseCandidate> { new() { Title = "bad", Rejected = true, Rejections = new[] { "too small" } } });

        var service = CreateService();
        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        Assert.Equal("no_replacement_available", result.ActionTaken);
        Assert.Equal("skipped", result.Status);
        _radarr.Verify(r => r.DeleteMovieFileAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemediateAsync_AtLeastOneViableCandidate_ProceedsToDelete()
    {
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 1, 99));
        _radarr.Setup(r => r.SearchReleasesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RadarrReleaseCandidate> { new() { Rejected = true }, ViableRelease() });
        _radarr.Setup(r => r.GetHistoryForMovieAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RadarrHistoryRecord>());

        var service = CreateService();
        await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        _radarr.Verify(r => r.DeleteMovieFileAsync(99, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Blocklist vs. plain-search branch ---

    [Fact]
    public async Task RemediateAsync_RecentGrabHistoryExists_BlocklistsIt_NotAPlainSearch()
    {
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 1, 99));
        _radarr.Setup(r => r.SearchReleasesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrReleaseCandidate> { ViableRelease() });
        _radarr.Setup(r => r.GetHistoryForMovieAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RadarrHistoryRecord> { GrabbedHistory(40, DateTime.UtcNow.AddDays(-1).ToString("O")) });

        var service = CreateService();
        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        _radarr.Verify(r => r.MarkHistoryAsFailedAsync(40, It.IsAny<CancellationToken>()), Times.Once);
        _radarr.Verify(r => r.TriggerMovieSearchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("deleted_and_blocklisted", result.ActionTaken);
        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task RemediateAsync_NoGrabHistory_FallsBackToPlainSearch()
    {
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 1, 99));
        _radarr.Setup(r => r.SearchReleasesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrReleaseCandidate> { ViableRelease() });
        _radarr.Setup(r => r.GetHistoryForMovieAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrHistoryRecord>());

        var service = CreateService();
        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        _radarr.Verify(r => r.TriggerMovieSearchAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _radarr.Verify(r => r.MarkHistoryAsFailedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("deleted_and_searched", result.ActionTaken);
    }

    [Fact]
    public async Task RemediateAsync_GrabHistoryOlderThanLookbackWindow_FallsBackToPlainSearch()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            RadarrServers = new List<ArrServerConfig> { new() { Name = "main", Url = "http://radarr", ApiKey = "key" } },
            HistoryLookbackDays = 30
        });

        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 1, 99));
        _radarr.Setup(r => r.SearchReleasesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrReleaseCandidate> { ViableRelease() });
        _radarr.Setup(r => r.GetHistoryForMovieAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RadarrHistoryRecord> { GrabbedHistory(40, DateTime.UtcNow.AddDays(-90).ToString("O")) });

        var service = CreateService();
        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        _radarr.Verify(r => r.MarkHistoryAsFailedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("deleted_and_searched", result.ActionTaken);
    }

    [Fact]
    public async Task RemediateAsync_MultipleGrabs_BlocklistsOnlyTheMostRecentOne()
    {
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 1, 99));
        _radarr.Setup(r => r.SearchReleasesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrReleaseCandidate> { ViableRelease() });
        _radarr.Setup(r => r.GetHistoryForMovieAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrHistoryRecord>
        {
            GrabbedHistory(1, DateTime.UtcNow.AddDays(-10).ToString("O")),
            GrabbedHistory(2, DateTime.UtcNow.AddDays(-1).ToString("O")),
            GrabbedHistory(3, DateTime.UtcNow.AddDays(-5).ToString("O"))
        });

        var service = CreateService();
        await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        _radarr.Verify(r => r.MarkHistoryAsFailedAsync(2, It.IsAny<CancellationToken>()), Times.Once);
        _radarr.Verify(r => r.MarkHistoryAsFailedAsync(1, It.IsAny<CancellationToken>()), Times.Never);
        _radarr.Verify(r => r.MarkHistoryAsFailedAsync(3, It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Error handling ---

    [Fact]
    public async Task RemediateAsync_ArrClientExceptionDuringDelete_RecordsFailedStatus_WithTheErrorMessage()
    {
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 1, 99));
        _radarr.Setup(r => r.SearchReleasesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new List<RadarrReleaseCandidate> { ViableRelease() });
        _radarr.Setup(r => r.DeleteMovieFileAsync(99, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArrClientException("[RadarrClient] DELETE moviefile/99 failed: connection refused"));

        var service = CreateService();
        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("connection refused", result.ErrorMessage);
    }

    // --- Cycle number ---

    [Fact]
    public async Task RemediateAsync_ComputesCycleNumber_FromPriorSuccessfulAttempts()
    {
        _db.Setup(d => d.CountSuccessfulRemediationsForItemAsync(It.IsAny<string>())).ReturnsAsync(2);
        _matcher.Setup(m => m.MatchMovieAsync(It.IsAny<BaseItem>(), _radarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArrMatchResult.Unmatched);

        var service = CreateService();
        var result = await service.RemediateAsync(MakeMovie(), scanRecordId: null, CancellationToken.None);

        Assert.Equal(3, result.CycleNumber);
    }

    // --- Episodes (mirrors the movie happy path, confirms the Sonarr-specific plumbing) ---

    [Fact]
    public async Task RemediateAsync_EpisodeHappyPath_ResolvesSeriesAndDeletesTheEpisodeFile()
    {
        var episode = new Episode { Id = Guid.NewGuid(), Path = "/tv/Show/S01E01.mkv", SeriesId = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 1 };
        var series = new Series { Id = episode.SeriesId };
        _library.Setup(l => l.GetItemById(episode.SeriesId)).Returns(series);

        _matcher.Setup(m => m.MatchEpisodeAsync(episode, series, _sonarr.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrMatchResult(true, "provider_id", 134, 7958, 7958));
        _sonarr.Setup(s => s.SearchReleasesAsync(7958, It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrReleaseCandidate>
        {
            new() { Title = "release", Rejected = false }
        });
        _sonarr.Setup(s => s.GetHistoryForEpisodeAsync(134, 7958, It.IsAny<CancellationToken>())).ReturnsAsync(new List<SonarrHistoryRecord>());

        var service = CreateService();
        var result = await service.RemediateAsync(episode, scanRecordId: null, CancellationToken.None);

        _sonarr.Verify(s => s.DeleteEpisodeFileAsync(7958, It.IsAny<CancellationToken>()), Times.Once);
        _sonarr.Verify(s => s.TriggerEpisodeSearchAsync(7958, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("success", result.Status);
        Assert.Equal("sonarr", result.ArrApp);
    }
}
