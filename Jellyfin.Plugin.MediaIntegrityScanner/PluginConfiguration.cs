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

using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaIntegrityScanner;

/// <summary>
/// Plugin configuration for Media Integrity Scanner.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the maximum number of files scanned concurrently.
    /// </summary>
    public int MaxConcurrentScans { get; set; } = 1;

    /// <summary>
    /// Gets or sets the delay in milliseconds between scanning each file.
    /// </summary>
    public int DelayBetweenFilesMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets a value indicating whether scanning pauses during active playback.
    /// </summary>
    public bool PauseDuringPlayback { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Phase 2 deep scanning is enabled.
    /// </summary>
    public bool EnableDeepScan { get; set; }

    /// <summary>
    /// Gets or sets the maximum read rate in MB/s for scanning I/O.
    /// </summary>
    public int MaxReadRateMbPerSec { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether scanning is restricted to quiet hours.
    /// </summary>
    public bool UseQuietHoursOnly { get; set; }

    /// <summary>
    /// Gets or sets the start of the quiet hours window (HH:mm format).
    /// </summary>
    public string QuietHoursStart { get; set; } = "02:00";

    /// <summary>
    /// Gets or sets the end of the quiet hours window (HH:mm format).
    /// </summary>
    public string QuietHoursEnd { get; set; } = "06:00";

    /// <summary>
    /// Gets or sets a user-specified override path for the ffmpeg binary.
    /// </summary>
    public string? FfmpegPathOverride { get; set; }

    /// <summary>
    /// Gets or sets a user-specified override path for the ffprobe binary.
    /// </summary>
    public string? FfprobePathOverride { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether newly added items are scanned automatically.
    /// </summary>
    public bool ScanOnItemAdded { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether scan records are purged when items are removed.
    /// </summary>
    public bool PurgeOnItemRemoved { get; set; } = true;

    /// <summary>
    /// Gets or sets which release channel to check for updates against.
    /// </summary>
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>
    /// Gets or sets the manifest URL used to classify a discovered version as a
    /// stable release. Only versions Jellyfin already knows about (i.e. from a
    /// repository the admin has registered under Dashboard &gt; Plugins &gt;
    /// Repositories) can ever be found -- this URL is used purely to label
    /// which of those already-discovered versions counts as "stable", not to
    /// fetch anything directly.
    /// </summary>
    public string StableManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest.json";

    /// <summary>
    /// Gets or sets the manifest URL used to classify a discovered version as a
    /// development/pre-release build. See <see cref="StableManifestUrl"/> for
    /// how this is used.
    /// </summary>
    public string DevManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest-unstable.json";

    /// <summary>
    /// Gets or sets a value indicating whether a weekly scheduled task should
    /// automatically install (stage) a newer version for the configured
    /// <see cref="UpdateChannel"/>, if one is available. Off by default --
    /// conservative installs can leave this disabled and only ever update via
    /// the dashboard's manual "Update Now" button. Installing a new version
    /// this way does not by itself apply it; Jellyfin still needs a restart
    /// to load it (see <see cref="AutoRestartAfterUpdate"/>).
    /// </summary>
    public bool EnableAutoUpdate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether, after a successful automatic
    /// install, the plugin should also restart Jellyfin itself once no one
    /// has an active playback session -- rather than leaving the update
    /// staged until an admin restarts manually. Off by default, and has no
    /// effect unless <see cref="EnableAutoUpdate"/> is also on.
    /// </summary>
    public bool AutoRestartAfterUpdate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a weekly scheduled task should
    /// run an integrity check and (if it passes) a VACUUM against the
    /// plugin's own SQLite database. On by default -- unlike
    /// <see cref="EnableAutoUpdate"/>, this doesn't change any running code,
    /// and the plugin's whole purpose is catching corruption early, so its
    /// own database getting a periodic integrity check is in the same
    /// spirit. Always skips itself while a scan is in progress, regardless
    /// of this setting.
    /// </summary>
    public bool EnableAutoDatabaseMaintenance { get; set; } = true;

    /// <summary>
    /// Gets or sets the hardware acceleration backend to request via ffmpeg's
    /// <c>-hwaccel</c> flag for Phase 2 (FullDecode) scans. Reuses Jellyfin's own
    /// <see cref="HardwareAccelerationType"/> so this setting reads the same as
    /// Jellyfin's own Playback transcoding settings. <c>none</c> (the default)
    /// means pure CPU software decode, matching this plugin's behavior before
    /// this setting existed. Has no effect on Phase 1 (Header) scans, which use
    /// ffprobe and never decode the file. Types with no supported decode-only
    /// mapping (<c>amf</c>, <c>v4l2m2m</c>, <c>rkmpp</c>) silently fall back to
    /// software decode rather than passing an unverified flag to ffmpeg.
    /// </summary>
    public HardwareAccelerationType HardwareAccelerationType { get; set; } = HardwareAccelerationType.none;
}
