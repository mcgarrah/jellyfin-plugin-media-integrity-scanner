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

namespace Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;

/// <summary>
/// Thrown when a call to a Radarr/Sonarr API fails, with a contextual
/// message identifying which client and operation failed -- the pattern
/// Seerr's own Radarr/Sonarr client uses (see
/// <c>ARR-INTEGRATION-PROPOSAL.md</c> section 2.1), adopted here so
/// failures are legible in logs instead of a bare
/// <see cref="System.Net.Http.HttpRequestException"/>.
/// </summary>
public class ArrClientException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClientException"/> class.
    /// </summary>
    public ArrClientException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClientException"/> class.
    /// </summary>
    /// <param name="message">The contextual error message.</param>
    public ArrClientException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClientException"/> class.
    /// </summary>
    /// <param name="message">The contextual error message.</param>
    /// <param name="innerException">The original exception this wraps.</param>
    public ArrClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
