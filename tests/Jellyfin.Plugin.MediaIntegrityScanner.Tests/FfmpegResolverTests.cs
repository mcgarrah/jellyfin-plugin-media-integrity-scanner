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
using System.Linq;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for FfmpegResolver's pure, dependency-free static helpers. CI (and this
/// dev environment) runs on Linux only, so only the Linux branch of the
/// OS-conditional GetPlatformCandidates is exercised here — Windows/macOS branches
/// aren't reachable without running on those platforms.
/// </summary>
public class FfmpegResolverTests
{
    [Fact]
    public void GetPlatformCandidates_Linux_ReturnsExpectedPaths()
    {
        // This assembly's tests only run on Linux (CI is ubuntu-latest; this dev
        // environment is Linux too), so the Linux branch is what's reachable here.
        Assert.True(OperatingSystem.IsLinux(), "This test suite only runs on Linux; Windows/macOS branches are not covered.");

        var candidates = FfmpegResolver.GetPlatformCandidates("ffmpeg").ToList();

        Assert.Equal(
            new[]
            {
                "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg"
            },
            candidates);
    }

    [Theory]
    [InlineData("/usr/lib/jellyfin-ffmpeg/ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffprobe")]
    [InlineData("/usr/bin/ffmpeg", "/usr/bin/ffprobe")]
    [InlineData("/opt/tools/custom-ffmpeg", "/opt/tools/ffprobe")]
    public void DeriveProbeFromFfmpeg_ReplacesBinaryNameKeepingDirectory(string ffmpegPath, string expected)
    {
        Assert.Equal(expected, FfmpegResolver.DeriveProbeFromFfmpeg(ffmpegPath));
    }

    [Fact]
    public void DeriveProbeFromFfmpeg_NoDirectory_ReturnsBareFfprobe()
    {
        Assert.Equal("ffprobe", FfmpegResolver.DeriveProbeFromFfmpeg("ffmpeg"));
    }

    [Fact]
    public void FindInPath_FindsRealExecutableOnPath()
    {
        var result = FfmpegResolver.FindInPath("ls");

        Assert.NotNull(result);
        Assert.EndsWith("/ls", result);
    }

    [Fact]
    public void FindInPath_ReturnsNull_ForNonexistentExecutable()
    {
        Assert.Null(FfmpegResolver.FindInPath("this-executable-definitely-does-not-exist-xyz123"));
    }
}
