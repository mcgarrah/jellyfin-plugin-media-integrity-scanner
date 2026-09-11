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

using System.Collections.Generic;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="Plugin.Sanitize"/> -- the single server-side
/// validation choke point for <see cref="PluginConfiguration"/>
/// (CODE-REVIEW-ARCHITECTURE.md H2). The settings page's own HTML min/max
/// and JS clamping only guard the UI path; these tests cover what a
/// hand-edited XML file or a raw REST config save can contain.
/// </summary>
public class PluginConfigurationSanitizeTests
{
    [Theory]
    [InlineData(0, 1)] // the real v0.4.3 incident: SemaphoreSlim(0,0) threw at startup
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(999, 16)]
    public void Sanitize_ClampsMaxConcurrentScans(int input, int expected)
    {
        var config = new PluginConfiguration { MaxConcurrentScans = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.MaxConcurrentScans);
    }

    [Theory]
    [InlineData(-1, 0)] // negative would throw ArgumentOutOfRangeException in Task.Delay mid-scan
    [InlineData(0, 0)]
    [InlineData(60000, 60000)]
    [InlineData(999999, 60000)]
    public void Sanitize_ClampsDelayBetweenFilesMs(int input, int expected)
    {
        var config = new PluginConfiguration { DelayBetweenFilesMs = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.DelayBetweenFilesMs);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)] // 0 = unlimited, deliberately allowed with no upper cap
    [InlineData(500, 500)]
    public void Sanitize_FloorsMaxReadRateMbPerSec_WithNoUpperCap(int input, int expected)
    {
        var config = new PluginConfiguration { MaxReadRateMbPerSec = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.MaxReadRateMbPerSec);
    }

    [Theory]
    [InlineData(0, 1)] // 0 or negative would invert the blocklist lookback window
    [InlineData(-30, 1)]
    [InlineData(30, 30)]
    [InlineData(400, 365)]
    public void Sanitize_ClampsHistoryLookbackDays(int input, int expected)
    {
        var config = new PluginConfiguration { HistoryLookbackDays = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.HistoryLookbackDays);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 10)]
    [InlineData(5000, 1000)]
    public void Sanitize_ClampsMaxAutoRemediationsPerDay(int input, int expected)
    {
        var config = new PluginConfiguration { MaxAutoRemediationsPerDay = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.MaxAutoRemediationsPerDay);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(168, 168)]
    [InlineData(99999, 8760)]
    public void Sanitize_ClampsRemediationCooldownHours(int input, int expected)
    {
        var config = new PluginConfiguration { RemediationCooldownHours = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.RemediationCooldownHours);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(500, 100)]
    public void Sanitize_ClampsMaxRemediationCycles(int input, int expected)
    {
        var config = new PluginConfiguration { MaxRemediationCycles = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.MaxRemediationCycles);
    }

    [Theory]
    [InlineData("02:00", "02:00")] // canonical form kept
    [InlineData("2:00", "2:00")] // lenient form ScanThrottle accepts is kept, not "corrected"
    [InlineData("23:59", "23:59")]
    [InlineData("not a time", "02:00")]
    [InlineData("", "02:00")]
    [InlineData("25:99", "02:00")]
    public void Sanitize_RestoresUnparseableQuietHoursStart_ToDefault(string input, string expected)
    {
        var config = new PluginConfiguration { QuietHoursStart = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.QuietHoursStart);
    }

    [Theory]
    [InlineData("06:00", "06:00")]
    [InlineData("garbage", "06:00")]
    public void Sanitize_RestoresUnparseableQuietHoursEnd_ToDefault(string input, string expected)
    {
        var config = new PluginConfiguration { QuietHoursEnd = input };
        Plugin.Sanitize(config);
        Assert.Equal(expected, config.QuietHoursEnd);
    }

    [Fact]
    public void Sanitize_RestoresBlankManifestUrls_ToDefaults()
    {
        var config = new PluginConfiguration { StableManifestUrl = " ", DevManifestUrl = string.Empty };
        Plugin.Sanitize(config);

        var defaults = new PluginConfiguration();
        Assert.Equal(defaults.StableManifestUrl, config.StableManifestUrl);
        Assert.Equal(defaults.DevManifestUrl, config.DevManifestUrl);
    }

    [Fact]
    public void Sanitize_DropsArrServersWithNoUrlOrApiKey_AndTrimsTheRest()
    {
        var config = new PluginConfiguration
        {
            RadarrServers = new List<ArrServerConfig>
            {
                new() { Name = " Main ", Url = " http://radarr.local:7878 ", ApiKey = " key ", LibraryPathPrefixes = new List<string> { " /data/movies ", string.Empty, "  " } },
                new() { Name = "NoUrl", Url = string.Empty, ApiKey = "key" },
                new() { Name = "NoKey", Url = "http://radarr2.local", ApiKey = "   " }
            },
            SonarrServers = new List<ArrServerConfig>
            {
                new() { Name = "TV", Url = "http://sonarr.local:8989", ApiKey = "key2" }
            }
        };

        Plugin.Sanitize(config);

        var radarr = Assert.Single(config.RadarrServers);
        Assert.Equal("Main", radarr.Name);
        Assert.Equal("http://radarr.local:7878", radarr.Url);
        Assert.Equal("key", radarr.ApiKey);
        Assert.Equal(new List<string> { "/data/movies" }, radarr.LibraryPathPrefixes);

        Assert.Single(config.SonarrServers);
    }

    [Fact]
    public void Sanitize_LeavesAnAlreadyValidConfiguration_Unchanged()
    {
        var config = new PluginConfiguration
        {
            MaxConcurrentScans = 4,
            DelayBetweenFilesMs = 2500,
            MaxReadRateMbPerSec = 25,
            HistoryLookbackDays = 60,
            MaxAutoRemediationsPerDay = 50,
            RemediationCooldownHours = 24,
            MaxRemediationCycles = 5,
            QuietHoursStart = "01:30",
            QuietHoursEnd = "05:45"
        };

        Plugin.Sanitize(config);

        Assert.Equal(4, config.MaxConcurrentScans);
        Assert.Equal(2500, config.DelayBetweenFilesMs);
        Assert.Equal(25, config.MaxReadRateMbPerSec);
        Assert.Equal(60, config.HistoryLookbackDays);
        Assert.Equal(50, config.MaxAutoRemediationsPerDay);
        Assert.Equal(24, config.RemediationCooldownHours);
        Assert.Equal(5, config.MaxRemediationCycles);
        Assert.Equal("01:30", config.QuietHoursStart);
        Assert.Equal("05:45", config.QuietHoursEnd);
    }
}
