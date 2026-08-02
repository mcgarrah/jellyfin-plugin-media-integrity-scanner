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
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Common.Updates;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="UpdateChecker"/>'s channel classification and install
/// logic against a mocked <see cref="IInstallationManager"/>. Uses
/// Plugin.Instance reflection plumbing (see TestPluginContext) for config and
/// the plugin's own Name/Id/Version, matching the pattern already used by
/// ScanEngineTests.
/// </summary>
[Collection("PluginInstance")]
public class UpdateCheckerTests : IDisposable
{
    private const string StableUrl = "https://raw.githubusercontent.com/example/repo/main/manifest.json";
    private const string DevUrl = "https://raw.githubusercontent.com/example/repo/main/manifest-unstable.json";
    private static readonly Version CurrentVersion = new(1, 0, 0, 0);

    private readonly PluginConfiguration _config;

    public UpdateCheckerTests()
    {
        _config = new PluginConfiguration
        {
            UpdateChannel = UpdateChannel.Stable,
            StableManifestUrl = StableUrl,
            DevManifestUrl = DevUrl
        };
        TestPluginContext.SetConfiguration(_config, CurrentVersion);
    }

    public void Dispose() => TestPluginContext.Clear();

    private static UpdateChecker CreateChecker(Mock<IInstallationManager> installationManager) =>
        new(installationManager.Object, NullLogger<UpdateChecker>.Instance);

    private static PackageInfo BuildPackage(params VersionInfo[] versions) =>
        new()
        {
            Name = Plugin.Instance!.Name,
            Id = Plugin.Instance!.Id,
            Versions = new List<VersionInfo>(versions)
        };

    private static VersionInfo BuildVersion(Version version, string repositoryUrl) =>
        new()
        {
            Version = version.ToString(),
            SourceUrl = "https://example.com/plugin.zip",
            Checksum = "deadbeef",
            RepositoryUrl = repositoryUrl
        };

    private static void SetupAvailablePackages(Mock<IInstallationManager> manager, params PackageInfo[] packages)
    {
        var list = new List<PackageInfo>(packages);
        manager.Setup(m => m.GetAvailablePackages(It.IsAny<CancellationToken>()))
            .ReturnsAsync(list);
        manager.Setup(m => m.FilterPackages(
                It.IsAny<IEnumerable<PackageInfo>>(),
                Plugin.Instance!.Name,
                Plugin.Instance!.Id,
                It.IsAny<Version>()))
            .Returns(list);
    }

    [Fact]
    public async Task RefreshAsync_NoVersionsFound_ReturnsNoUpdateAvailable()
    {
        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(manager);

        var status = await CreateChecker(manager).RefreshAsync(CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestStableVersion);
        Assert.Null(status.LatestDevVersion);
        Assert.Null(status.AvailableVersion);
    }

    [Fact]
    public async Task RefreshAsync_NewerStableVersionRegistered_ReportsUpdateAvailable()
    {
        var current = CurrentVersion;
        var newer = new Version(current.Major + 1, 0, 0, 0);
        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(manager, BuildPackage(BuildVersion(newer, StableUrl)));

        var status = await CreateChecker(manager).RefreshAsync(CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal(newer.ToString(), status.LatestStableVersion);
        Assert.Equal(newer.ToString(), status.AvailableVersion);
        Assert.Null(status.LatestDevVersion);
    }

    [Fact]
    public async Task RefreshAsync_OlderVersionRegistered_DoesNotReportUpdateAvailable()
    {
        var current = CurrentVersion;
        var older = new Version(0, 0, 1, 0);
        Assert.True(older < current, "test fixture assumption: 0.0.1.0 is older than the built assembly version");

        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(manager, BuildPackage(BuildVersion(older, StableUrl)));

        var status = await CreateChecker(manager).RefreshAsync(CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Equal(older.ToString(), status.LatestStableVersion);
    }

    [Fact]
    public async Task RefreshAsync_UnrecognizedRepositoryUrl_IsIgnored()
    {
        var current = CurrentVersion;
        var newer = new Version(current.Major + 1, 0, 0, 0);
        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(manager, BuildPackage(BuildVersion(newer, "https://some-other-repo.example.com/manifest.json")));

        var status = await CreateChecker(manager).RefreshAsync(CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestStableVersion);
        Assert.Null(status.LatestDevVersion);
    }

    [Fact]
    public async Task RefreshAsync_StableChannel_IgnoresNewerDevVersion()
    {
        var current = CurrentVersion;
        var newerDev = new Version(current.Major + 2, 0, 0, 0);
        _config.UpdateChannel = UpdateChannel.Stable;

        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(manager, BuildPackage(BuildVersion(newerDev, DevUrl)));

        var status = await CreateChecker(manager).RefreshAsync(CancellationToken.None);

        Assert.Equal(newerDev.ToString(), status.LatestDevVersion);
        Assert.False(status.UpdateAvailable);
        Assert.Null(status.AvailableVersion);
    }

    [Fact]
    public async Task RefreshAsync_DevChannel_PicksNewerOfStableAndDev()
    {
        var current = CurrentVersion;
        var newerStable = new Version(current.Major + 1, 0, 0, 0);
        var newerDev = new Version(current.Major + 2, 0, 0, 0);
        _config.UpdateChannel = UpdateChannel.Development;

        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(
            manager,
            BuildPackage(BuildVersion(newerStable, StableUrl), BuildVersion(newerDev, DevUrl)));

        var status = await CreateChecker(manager).RefreshAsync(CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal(newerDev.ToString(), status.AvailableVersion);
    }

    [Fact]
    public async Task RefreshAsync_DevChannel_FallsBackToStableWhenDevIsOlder()
    {
        var current = CurrentVersion;
        var newerStable = new Version(current.Major + 2, 0, 0, 0);
        var olderDev = new Version(current.Major + 1, 0, 0, 0);
        _config.UpdateChannel = UpdateChannel.Development;

        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(
            manager,
            BuildPackage(BuildVersion(newerStable, StableUrl), BuildVersion(olderDev, DevUrl)));

        var status = await CreateChecker(manager).RefreshAsync(CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal(newerStable.ToString(), status.AvailableVersion);
    }

    [Fact]
    public async Task InstallAsync_NoMatchingVersionForChannel_ThrowsInvalidOperationException()
    {
        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(manager);

        var checker = CreateChecker(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.InstallAsync(UpdateChannel.Stable, CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_MatchingVersionFound_CallsInstallPackageWithExpectedInfo()
    {
        var current = CurrentVersion;
        var newer = new Version(current.Major + 1, 0, 0, 0);
        var manager = new Mock<IInstallationManager>();
        SetupAvailablePackages(manager, BuildPackage(BuildVersion(newer, StableUrl)));
        manager.Setup(m => m.InstallPackage(It.IsAny<InstallationInfo>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateChecker(manager).InstallAsync(UpdateChannel.Stable, CancellationToken.None);

        manager.Verify(
            m => m.InstallPackage(
                It.Is<InstallationInfo>(info =>
                    info.Id == Plugin.Instance!.Id
                    && info.Version == newer
                    && info.SourceUrl == "https://example.com/plugin.zip"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
