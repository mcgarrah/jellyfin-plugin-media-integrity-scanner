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
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for FfmpegResolver's pure, dependency-free static helpers.
/// GetPlatformCandidates_ReturnsExpectedPaths asserts a different candidate
/// list per <see cref="OperatingSystem"/> branch, so which one actually runs
/// depends on the CI runner's OS -- CI now runs this suite on ubuntu-latest,
/// windows-latest, and macos-latest (see build.yml's test-matrix job) so all
/// three branches genuinely execute somewhere, not just get compiled.
/// </summary>
[Collection("PluginInstance")]
public class FfmpegResolverTests : IDisposable
{
    public void Dispose() => TestPluginContext.Clear();

    private static FfmpegResolver CreateResolver(Mock<IServerConfigurationManager>? config = null)
    {
        return new FfmpegResolver(
            (config ?? new Mock<IServerConfigurationManager>()).Object,
            NullLogger<FfmpegResolver>.Instance);
    }


    [Fact]
    public void GetPlatformCandidates_ReturnsExpectedPaths()
    {
        var candidates = FfmpegResolver.GetPlatformCandidates("ffmpeg").ToList();

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                new[]
                {
                    "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                    "/usr/bin/ffmpeg",
                    "/usr/local/bin/ffmpeg"
                },
                candidates);
        }
        else if (OperatingSystem.IsWindows())
        {
            var expectedFirst = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Jellyfin", "Server", "ffmpeg.exe");

            Assert.Equal(new[] { expectedFirst, @"C:\ffmpeg\bin\ffmpeg.exe" }, candidates);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(
                new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg" },
                candidates);
        }
        else
        {
            // A platform GetPlatformCandidates has no branch for at all -- the
            // real production behavior is an empty candidate list (falls straight
            // through to the PATH-lookup fallback in ResolveBinary), so that's
            // what a genuinely unhandled OS should assert here too.
            Assert.Empty(candidates);
        }
    }

    [Theory]
    [InlineData("/usr/lib/jellyfin-ffmpeg/ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffprobe")]
    [InlineData("/usr/bin/ffmpeg", "/usr/bin/ffprobe")]
    [InlineData("/opt/tools/custom-ffmpeg", "/opt/tools/ffprobe")]
    public void DeriveProbeFromFfmpeg_ReplacesBinaryNameKeepingDirectory(string ffmpegPath, string expected)
    {
        // Inputs are deliberately Unix-style forward-slash paths -- what
        // matters here is the directory-preservation/extension logic, not
        // which separator Path.GetDirectoryName/Path.Combine normalize to.
        // On Windows those two calls rewrite the separator to '\', so the
        // actual result is normalized back to '/' before comparing rather
        // than asserting an OS-native convention this test isn't about
        // (GetPlatformCandidates_ReturnsExpectedPaths already covers that).
        var actual = FfmpegResolver.DeriveProbeFromFfmpeg(ffmpegPath).Replace('\\', '/');
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DeriveProbeFromFfmpeg_NoDirectory_ReturnsBareFfprobe()
    {
        Assert.Equal("ffprobe", FfmpegResolver.DeriveProbeFromFfmpeg("ffmpeg"));
    }

    [Fact]
    public void FindInPath_FindsRealExecutableOnPath()
    {
        // "dotnet" rather than a Unix tool like "ls": guaranteed on PATH on every
        // OS this suite now runs on (actions/setup-dotnet puts it there), unlike
        // "ls", which doesn't exist on Windows at all.
        var result = FfmpegResolver.FindInPath("dotnet");

        Assert.NotNull(result);
        Assert.Contains("dotnet", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindInPath_ReturnsNull_ForNonexistentExecutable()
    {
        Assert.Null(FfmpegResolver.FindInPath("this-executable-definitely-does-not-exist-xyz123"));
    }

    [Fact]
    public void IsUsingCustomOverride_False_WhenNeitherOverrideSet()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration());
        var resolver = CreateResolver();

        Assert.False(resolver.IsUsingCustomOverride());
    }

    [Fact]
    public void IsUsingCustomOverride_False_WhenOnlyOneOverrideSet()
    {
        var realFile = System.IO.Path.GetTempFileName();
        try
        {
            TestPluginContext.SetConfiguration(new PluginConfiguration { FfmpegPathOverride = realFile });
            var resolver = CreateResolver();

            Assert.False(resolver.IsUsingCustomOverride());
        }
        finally
        {
            System.IO.File.Delete(realFile);
        }
    }

    [Fact]
    public void IsUsingCustomOverride_False_WhenOverrideSetButFileMissing()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration
        {
            FfmpegPathOverride = "/definitely/does/not/exist/ffmpeg",
            FfprobePathOverride = "/definitely/does/not/exist/ffprobe"
        });
        var resolver = CreateResolver();

        Assert.False(resolver.IsUsingCustomOverride());
    }

    [Fact]
    public void IsUsingCustomOverride_True_WhenBothOverridesSetAndFilesExist()
    {
        var ffmpegFile = System.IO.Path.GetTempFileName();
        var ffprobeFile = System.IO.Path.GetTempFileName();
        try
        {
            TestPluginContext.SetConfiguration(new PluginConfiguration
            {
                FfmpegPathOverride = ffmpegFile,
                FfprobePathOverride = ffprobeFile
            });
            var resolver = CreateResolver();

            Assert.True(resolver.IsUsingCustomOverride());
        }
        finally
        {
            System.IO.File.Delete(ffmpegFile);
            System.IO.File.Delete(ffprobeFile);
        }
    }

    [Fact]
    public void ServerConfigurationChanged_Fires_WhenConfigurationUpdatedFires()
    {
        var config = new Mock<IServerConfigurationManager>();
        var resolver = CreateResolver(config);

        var raised = false;
        resolver.ServerConfigurationChanged += (_, _) => raised = true;

        config.Raise(c => c.ConfigurationUpdated += null, config.Object, EventArgs.Empty);

        Assert.True(raised);
    }
}
