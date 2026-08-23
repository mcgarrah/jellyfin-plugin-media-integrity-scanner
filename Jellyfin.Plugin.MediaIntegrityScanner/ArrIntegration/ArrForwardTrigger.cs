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

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Which scan outcomes are eligible for automatic Radarr/Sonarr forwarding
/// (<c>ARR-INTEGRATION-PROPOSAL.md</c> section 7). Manual forwarding from the
/// Media Issues page ignores this entirely -- it only gates the automatic
/// path.
/// </summary>
public enum ArrForwardTrigger
{
    /// <summary>
    /// Only a scan that affirmatively detected corruption
    /// (<see cref="Jellyfin.Plugin.MediaIntegrityScanner.Scanner.ScanStatus.Fail"/>)
    /// triggers automatic forwarding. The default -- an Error means the *scan itself*
    /// broke (ffprobe crashed, a read error), a weaker signal that the media is
    /// actually bad than a completed scan that found real corruption.
    /// </summary>
    FailOnly,

    /// <summary>
    /// Both <see cref="Jellyfin.Plugin.MediaIntegrityScanner.Scanner.ScanStatus.Fail"/> and
    /// <see cref="Jellyfin.Plugin.MediaIntegrityScanner.Scanner.ScanStatus.Error"/>
    /// trigger automatic forwarding.
    /// </summary>
    FailAndError
}
