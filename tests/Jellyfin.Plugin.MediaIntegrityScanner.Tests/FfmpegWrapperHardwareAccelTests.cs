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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for FfmpegWrapper's hardware-acceleration support: mapping a
/// configured <see cref="HardwareAccelerationType"/> to the real ffmpeg
/// <c>-hwaccel</c> value, and DecodeAsync/ProbeAsync reporting the right
/// DecodeMode/HardwareAccelType. Uses a tiny real shell script standing in
/// for ffmpeg (logs its own argv to a file, then exits 0) rather than a mock
/// -- this exercises FfmpegWrapper's actual argument-building code and proves
/// the -hwaccel flag genuinely reaches the process command line, not just
/// that the right value gets reported back. No GPU is needed: the fake
/// binary never actually decodes anything.
/// </summary>
[Collection("PluginInstance")]
public class FfmpegWrapperHardwareAccelTests : IDisposable
{
    private readonly string _fakeBinaryPath;
    private readonly string _argvLogPath;

    public FfmpegWrapperHardwareAccelTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _fakeBinaryPath = Path.Combine(Path.GetTempPath(), $"fake-ffmpeg-{id}.sh");
        _argvLogPath = Path.Combine(Path.GetTempPath(), $"fake-ffmpeg-{id}.argv");

        // The [UnixOnlyFact] tests below never actually run this fake binary on
        // Windows, but the constructor still runs regardless -- File.SetUnixFileMode
        // throws PlatformNotSupportedException there, so this whole block is
        // skipped defensively rather than relying on xUnit's skip timing.
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(_fakeBinaryPath, $"#!/bin/sh\necho \"$@\" > \"{_argvLogPath}\"\nexit 0\n");
            File.SetUnixFileMode(
                _fakeBinaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public void Dispose()
    {
        TestPluginContext.Clear();
        TryDelete(_fakeBinaryPath);
        TryDelete(_argvLogPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private FfmpegWrapper CreateWrapper()
    {
        var resolverMock = new Mock<FfmpegResolver>(
            Mock.Of<IServerConfigurationManager>(), NullLogger<FfmpegResolver>.Instance);
        resolverMock.Setup(r => r.ResolveFfmpegPath()).Returns(_fakeBinaryPath);
        resolverMock.Setup(r => r.ResolveFfprobePath()).Returns(_fakeBinaryPath);

        return new FfmpegWrapper(resolverMock.Object, NullLogger<FfmpegWrapper>.Instance);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.none, null)]
    [InlineData(HardwareAccelerationType.nvenc, "cuda")]
    [InlineData(HardwareAccelerationType.vaapi, "vaapi")]
    [InlineData(HardwareAccelerationType.qsv, "qsv")]
    [InlineData(HardwareAccelerationType.videotoolbox, "videotoolbox")]
    [InlineData(HardwareAccelerationType.amf, null)]
    [InlineData(HardwareAccelerationType.v4l2m2m, null)]
    [InlineData(HardwareAccelerationType.rkmpp, null)]
    public void ResolveHwAccelFlag_MapsToTheRealFfmpegHwaccelName(HardwareAccelerationType type, string? expected)
    {
        // NVIDIA decode is requested as "cuda", not "nvenc" -- nvenc is the
        // encode-only name. This is the exact mistake class this test guards
        // against; confirmed against Jellyfin's own EncodingHelper source.
        Assert.Equal(expected, FfmpegWrapper.ResolveHwAccelFlag(type));
    }

    [UnixOnlyFact]
    public async Task DecodeAsync_ConfiguredForSoftware_ReportsSoftwareAndNoHardwareType()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { HardwareAccelerationType = HardwareAccelerationType.none });
        var wrapper = CreateWrapper();

        var result = await wrapper.DecodeAsync("/media/some-file.mkv", CancellationToken.None);

        Assert.Equal(DecodeMode.Software, result.DecodeMode);
        Assert.Null(result.HardwareAccelType);
    }

    [UnixOnlyFact]
    public async Task DecodeAsync_ConfiguredForNvidia_ActuallyPassesHwaccelCudaOnTheRealCommandLine()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { HardwareAccelerationType = HardwareAccelerationType.nvenc });
        var wrapper = CreateWrapper();

        var result = await wrapper.DecodeAsync("/media/some-file.mkv", CancellationToken.None);

        Assert.Equal(DecodeMode.Hardware, result.DecodeMode);
        Assert.Equal("cuda", result.HardwareAccelType);

        // Confirm the flag genuinely reached the process argv, not just the
        // ScanResult's self-reported metadata.
        var argv = await File.ReadAllTextAsync(_argvLogPath);
        Assert.Contains("-hwaccel cuda", argv, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task DecodeAsync_ConfiguredForUnsupportedType_FallsBackToSoftware()
    {
        // amf has no decode-only -hwaccel mapping here (see ResolveHwAccelFlag) --
        // must behave identically to "none" rather than passing an unverified flag.
        TestPluginContext.SetConfiguration(new PluginConfiguration { HardwareAccelerationType = HardwareAccelerationType.amf });
        var wrapper = CreateWrapper();

        var result = await wrapper.DecodeAsync("/media/some-file.mkv", CancellationToken.None);

        Assert.Equal(DecodeMode.Software, result.DecodeMode);
        Assert.Null(result.HardwareAccelType);

        var argv = await File.ReadAllTextAsync(_argvLogPath);
        Assert.DoesNotContain("-hwaccel", argv, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public async Task ProbeAsync_AlwaysReportsNotApplicable_RegardlessOfHardwareAccelConfig()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { HardwareAccelerationType = HardwareAccelerationType.nvenc });
        var wrapper = CreateWrapper();

        var result = await wrapper.ProbeAsync("/media/some-file.mkv", CancellationToken.None);

        Assert.Equal(DecodeMode.NotApplicable, result.DecodeMode);
        Assert.Null(result.HardwareAccelType);
    }
}
