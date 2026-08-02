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

using Jellyfin.Plugin.MediaIntegrityScanner.Api;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Data.Models;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Basic property/default-value tests for the plain data-transfer models.
/// Cheap to run and cheap to maintain; their value is catching accidental
/// property renames, type changes, or default-value regressions as the
/// plugin's models evolve.
/// </summary>
public class ModelTests
{
    [Fact]
    public void ScanRecord_DefaultsAreSane()
    {
        var record = new ScanRecord();

        Assert.Equal(string.Empty, record.ItemId);
        Assert.Equal(string.Empty, record.FilePath);
        Assert.Null(record.FileSize);
        Assert.Null(record.LastModified);
        Assert.Equal(string.Empty, record.ScanTimestamp);
        Assert.Null(record.ErrorOutput);
        Assert.Null(record.ScanDurationMs);
    }

    [Fact]
    public void ScanRecord_PropertiesRoundTrip()
    {
        var record = new ScanRecord
        {
            Id = 42,
            ItemId = "item-1",
            FilePath = "/media/movie.mkv",
            FileSize = 12345,
            LastModified = "2026-01-01T00:00:00.0000000Z",
            ScanPhase = (int)ScanPhase.FullDecode,
            ScanStatus = (int)ScanStatus.Fail,
            ScanTimestamp = "2026-01-02T00:00:00.0000000Z",
            ErrorOutput = "corrupt frame",
            ScanDurationMs = 500
        };

        Assert.Equal(42, record.Id);
        Assert.Equal("item-1", record.ItemId);
        Assert.Equal("/media/movie.mkv", record.FilePath);
        Assert.Equal(12345, record.FileSize);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", record.LastModified);
        Assert.Equal((int)ScanPhase.FullDecode, record.ScanPhase);
        Assert.Equal((int)ScanStatus.Fail, record.ScanStatus);
        Assert.Equal("2026-01-02T00:00:00.0000000Z", record.ScanTimestamp);
        Assert.Equal("corrupt frame", record.ErrorOutput);
        Assert.Equal(500, record.ScanDurationMs);
    }

    [Fact]
    public void ScanResult_PropertiesRoundTrip()
    {
        var result = new ScanResult
        {
            Success = true,
            ErrorOutput = null,
            DurationMs = 250
        };

        Assert.True(result.Success);
        Assert.Null(result.ErrorOutput);
        Assert.Equal(250, result.DurationMs);
    }

    [Theory]
    [InlineData(ScanPhase.Header, 1)]
    [InlineData(ScanPhase.FullDecode, 2)]
    public void ScanPhase_HasExpectedNumericValues(ScanPhase phase, int expected)
    {
        Assert.Equal(expected, (int)phase);
    }

    [Theory]
    [InlineData(ScanStatus.Pending, 0)]
    [InlineData(ScanStatus.Pass, 1)]
    [InlineData(ScanStatus.Fail, 2)]
    [InlineData(ScanStatus.Error, 3)]
    public void ScanStatus_HasExpectedNumericValues(ScanStatus status, int expected)
    {
        Assert.Equal(expected, (int)status);
    }

    [Fact]
    public void ScanStatistics_DefaultsAreZero()
    {
        var stats = new ScanStatistics();

        Assert.Equal(0, stats.ScannedFiles);
        Assert.Equal(0, stats.PassedFiles);
        Assert.Equal(0, stats.FailedFiles);
        Assert.Equal(0, stats.ErroredFiles);
        Assert.Null(stats.LastScanTimestamp);
    }

    [Fact]
    public void PagedScanResults_DefaultsToEmptyList()
    {
        var paged = new PagedScanResults();

        Assert.NotNull(paged.Items);
        Assert.Empty(paged.Items);
        Assert.Equal(0, paged.TotalCount);
    }

    [Fact]
    public void ScanStatusResponse_PropertiesRoundTrip()
    {
        var response = new ScanStatusResponse
        {
            IsScanning = true,
            TotalFiles = 100,
            ScannedFiles = 80,
            PassedFiles = 70,
            FailedFiles = 5,
            ErroredFiles = 5,
            PendingFiles = 20,
            LastScanTimestamp = "2026-01-01T00:00:00.0000000Z",
            HealthPercentage = 87.5
        };

        Assert.True(response.IsScanning);
        Assert.Equal(100, response.TotalFiles);
        Assert.Equal(80, response.ScannedFiles);
        Assert.Equal(70, response.PassedFiles);
        Assert.Equal(5, response.FailedFiles);
        Assert.Equal(5, response.ErroredFiles);
        Assert.Equal(20, response.PendingFiles);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", response.LastScanTimestamp);
        Assert.Equal(87.5, response.HealthPercentage);
    }

    [Fact]
    public void ScanRequest_DefaultsToNonDeepLibraryScan()
    {
        var request = new ScanRequest();

        Assert.Null(request.ItemId);
        Assert.Null(request.LibraryId);
        Assert.False(request.DeepScan);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, 0)]
    [InlineData(UpdateChannel.Development, 1)]
    public void UpdateChannel_HasExpectedNumericValues(UpdateChannel channel, int expected)
    {
        // The dashboard/settings pages read and send this as a plain number
        // (no JsonStringEnumConverter is configured for this plugin's API),
        // so the numeric value is a real serialization contract, not an
        // implementation detail.
        Assert.Equal(expected, (int)channel);
    }

    [Fact]
    public void PluginConfiguration_UpdateFieldsDefaultToStableChannel()
    {
        var config = new PluginConfiguration();

        Assert.Equal(UpdateChannel.Stable, config.UpdateChannel);
        Assert.False(string.IsNullOrEmpty(config.StableManifestUrl));
        Assert.False(string.IsNullOrEmpty(config.DevManifestUrl));
        Assert.NotEqual(config.StableManifestUrl, config.DevManifestUrl);
    }

    [Fact]
    public void InstallUpdateRequest_PropertyRoundTrips()
    {
        var request = new InstallUpdateRequest { Channel = UpdateChannel.Development };

        Assert.Equal(UpdateChannel.Development, request.Channel);
    }

    [Fact]
    public void PagedResultResponse_DefaultsToEmptyList()
    {
        var response = new PagedResultResponse();

        Assert.NotNull(response.Items);
        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalCount);
        Assert.Equal(0, response.Page);
        Assert.Equal(0, response.PageSize);
    }
}
