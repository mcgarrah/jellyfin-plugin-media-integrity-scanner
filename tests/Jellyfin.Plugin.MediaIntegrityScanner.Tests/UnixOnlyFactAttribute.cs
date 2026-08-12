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
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (rather than fails) on Windows,
/// for tests that genuinely need a POSIX shell -- e.g. spawning <c>/bin/sh</c>
/// directly to exercise process-execution mechanics without needing real
/// ffmpeg/ffprobe binaries. xUnit v2 has no <c>Assert.SkipUnless</c>-style
/// runtime skip, so this is the standard v2 pattern: set <see cref="FactAttribute.Skip"/>
/// conditionally in the constructor. Shows up as "Skipped" with a clear
/// reason on Windows CI runs, not silently passing or failing.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Requires a POSIX shell (/bin/sh); not available on Windows.";
        }
    }
}
