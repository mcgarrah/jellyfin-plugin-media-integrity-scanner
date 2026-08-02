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
using MediaBrowser.Common.Updates;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Updates;

/// <summary>
/// Wraps Jellyfin's own <see cref="IInstallationManager"/> to check for and
/// install newer versions of this plugin. <see cref="IInstallationManager.GetAvailablePackages"/>
/// only ever returns versions from repositories the admin has already
/// registered under Dashboard &gt; Plugins &gt; Repositories -- this class
/// never fetches a manifest URL itself, it only classifies versions Jellyfin
/// already discovered by which registered repository (<c>VersionInfo.RepositoryUrl</c>)
/// they came from.
/// </summary>
public partial class UpdateChecker : IUpdateChecker
{
    private readonly IInstallationManager _installationManager;
    private readonly ILogger<UpdateChecker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateChecker"/> class.
    /// </summary>
    /// <param name="installationManager">Jellyfin's plugin installation manager.</param>
    /// <param name="logger">Logger instance.</param>
    public UpdateChecker(IInstallationManager installationManager, ILogger<UpdateChecker> logger)
    {
        _installationManager = installationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public UpdateStatus? CachedStatus { get; private set; }

    /// <inheritdoc />
    public async Task<UpdateStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        var config = plugin.Configuration;

        var (latestStable, latestDev) = await FindLatestVersionsAsync(cancellationToken).ConfigureAwait(false);

        var effective = SelectEffectiveVersion(config.UpdateChannel, latestStable, latestDev);
        var currentVersion = plugin.Version;

        var status = new UpdateStatus
        {
            CurrentVersion = currentVersion.ToString(),
            LatestStableVersion = latestStable?.Version,
            LatestDevVersion = latestDev?.Version,
            Channel = config.UpdateChannel,
            AvailableVersion = effective?.Version,
            UpdateAvailable = effective != null && effective.VersionNumber > currentVersion,
            CheckedAt = DateTime.UtcNow
        };

        CachedStatus = status;
        LogRefreshed(status.CurrentVersion, status.LatestStableVersion ?? "none", status.LatestDevVersion ?? "none");
        return status;
    }

    /// <inheritdoc />
    public async Task InstallAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        var config = plugin.Configuration;
        var manifestUrl = channel == UpdateChannel.Development ? config.DevManifestUrl : config.StableManifestUrl;

        var (package, version) = await FindLatestForManifestAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        if (package == null || version == null)
        {
            throw new InvalidOperationException(
                $"No version of this plugin was found from the configured {channel} manifest ({manifestUrl}). " +
                "Is that manifest registered as a Jellyfin plugin repository under Dashboard > Plugins > Repositories?");
        }

        var installationInfo = new InstallationInfo
        {
            Id = plugin.Id,
            Name = plugin.Name,
            Version = version.VersionNumber,
            Changelog = version.Changelog,
            SourceUrl = version.SourceUrl,
            Checksum = version.Checksum,
            PackageInfo = package
        };

        LogInstalling(channel.ToString(), version.Version);
        await _installationManager.InstallPackage(installationInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(VersionInfo? Stable, VersionInfo? Dev)> FindLatestVersionsAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        var config = plugin.Configuration;
        var ourPackages = await GetOwnPackagesAsync(cancellationToken).ConfigureAwait(false);

        VersionInfo? latestStable = null;
        VersionInfo? latestDev = null;

        foreach (var package in ourPackages)
        {
            foreach (var version in package.Versions)
            {
                if (IsFromManifest(version, config.StableManifestUrl))
                {
                    latestStable = NewerOf(latestStable, version);
                }
                else if (IsFromManifest(version, config.DevManifestUrl))
                {
                    latestDev = NewerOf(latestDev, version);
                }
            }
        }

        return (latestStable, latestDev);
    }

    private async Task<(PackageInfo? Package, VersionInfo? Version)> FindLatestForManifestAsync(string manifestUrl, CancellationToken cancellationToken)
    {
        var ourPackages = await GetOwnPackagesAsync(cancellationToken).ConfigureAwait(false);

        PackageInfo? matchedPackage = null;
        VersionInfo? matchedVersion = null;

        foreach (var package in ourPackages)
        {
            foreach (var version in package.Versions)
            {
                if (!IsFromManifest(version, manifestUrl))
                {
                    continue;
                }

                if (matchedVersion == null || version.VersionNumber > matchedVersion.VersionNumber)
                {
                    matchedVersion = version;
                    matchedPackage = package;
                }
            }
        }

        return (matchedPackage, matchedVersion);
    }

    private async Task<IEnumerable<PackageInfo>> GetOwnPackagesAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        var available = await _installationManager.GetAvailablePackages(cancellationToken).ConfigureAwait(false);
        return _installationManager.FilterPackages(available, plugin.Name, plugin.Id, null!);
    }

    private static bool IsFromManifest(VersionInfo version, string manifestUrl) =>
        !string.IsNullOrEmpty(version.RepositoryUrl)
        && !string.IsNullOrEmpty(manifestUrl)
        && string.Equals(version.RepositoryUrl, manifestUrl, StringComparison.OrdinalIgnoreCase);

    private static VersionInfo NewerOf(VersionInfo? current, VersionInfo candidate) =>
        current == null || candidate.VersionNumber > current.VersionNumber ? candidate : current;

    private static VersionInfo? SelectEffectiveVersion(UpdateChannel channel, VersionInfo? stable, VersionInfo? dev)
    {
        if (channel != UpdateChannel.Development)
        {
            return stable;
        }

        if (dev == null)
        {
            return stable;
        }

        if (stable == null)
        {
            return dev;
        }

        return dev.VersionNumber > stable.VersionNumber ? dev : stable;
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Update check: current={Current} latestStable={Stable} latestDev={Dev}")]
    private partial void LogRefreshed(string current, string stable, string dev);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Installing {Channel} update: version {Version}")]
    private partial void LogInstalling(string channel, string version);
}
