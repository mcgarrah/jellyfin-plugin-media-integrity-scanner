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
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="FfmpegWrapper"/>'s live path re-resolution (task #29):
/// the manual <see cref="FfmpegWrapper.RefreshPaths"/> path, and the automatic
/// path triggered by <see cref="FfmpegResolver.ServerConfigurationChanged"/>.
/// </summary>
[Collection("PluginInstance")]
public class FfmpegWrapperTests : IDisposable
{
    public void Dispose() => TestPluginContext.Clear();

    private static Mock<FfmpegResolver> CreateResolverMock(Mock<IServerConfigurationManager>? config = null)
    {
        return new Mock<FfmpegResolver>(
            (config ?? new Mock<IServerConfigurationManager>()).Object,
            NullLogger<FfmpegResolver>.Instance);
    }

    [Fact]
    public void RefreshPaths_ReturnsTrue_AndSwapsPaths_WhenResolverReturnsDifferentPath()
    {
        var resolver = CreateResolverMock();
        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg-v1");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe-v1");
        var wrapper = new FfmpegWrapper(resolver.Object, NullLogger<FfmpegWrapper>.Instance);

        Assert.Equal("/fake/ffmpeg-v1", wrapper.FfmpegPath);
        Assert.Equal("/fake/ffprobe-v1", wrapper.FfprobePath);

        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg-v2");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe-v2");

        var changed = wrapper.RefreshPaths();

        Assert.True(changed);
        Assert.Equal("/fake/ffmpeg-v2", wrapper.FfmpegPath);
        Assert.Equal("/fake/ffprobe-v2", wrapper.FfprobePath);
    }

    [Fact]
    public void RefreshPaths_ReturnsFalse_WhenResolverReturnsSamePaths()
    {
        var resolver = CreateResolverMock();
        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe");
        var wrapper = new FfmpegWrapper(resolver.Object, NullLogger<FfmpegWrapper>.Instance);

        var changed = wrapper.RefreshPaths();

        Assert.False(changed);
        Assert.Equal("/fake/ffmpeg", wrapper.FfmpegPath);
        Assert.Equal("/fake/ffprobe", wrapper.FfprobePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsUsingCustomOverride_PassesThroughToResolver(bool resolverValue)
    {
        var resolver = CreateResolverMock();
        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe");
        resolver.Setup(r => r.IsUsingCustomOverride()).Returns(resolverValue);
        var wrapper = new FfmpegWrapper(resolver.Object, NullLogger<FfmpegWrapper>.Instance);

        Assert.Equal(resolverValue, wrapper.IsUsingCustomOverride);
    }

    [Fact]
    public void ServerConfigurationChanged_TriggersRefresh_WhenNotUsingCustomOverride()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration());
        var resolver = CreateResolverMock();
        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg-v1");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe-v1");
        resolver.Setup(r => r.IsUsingCustomOverride()).Returns(false);
        var wrapper = new FfmpegWrapper(resolver.Object, NullLogger<FfmpegWrapper>.Instance);

        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg-v2");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe-v2");
        wrapper.OnServerConfigurationChanged(resolver.Object, EventArgs.Empty);

        Assert.Equal("/fake/ffmpeg-v2", wrapper.FfmpegPath);
        Assert.Equal("/fake/ffprobe-v2", wrapper.FfprobePath);
    }

    [Fact]
    public void ServerConfigurationChanged_DoesNothing_WhenUsingCustomOverride()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration());
        var resolver = CreateResolverMock();
        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg-v1");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe-v1");
        resolver.Setup(r => r.IsUsingCustomOverride()).Returns(true);
        var wrapper = new FfmpegWrapper(resolver.Object, NullLogger<FfmpegWrapper>.Instance);

        // If the event handler ignored IsUsingCustomOverride() and refreshed anyway,
        // these paths would show up on the wrapper -- they must not.
        resolver.Setup(r => r.ResolveFfmpegPath()).Returns("/fake/ffmpeg-should-not-apply");
        resolver.Setup(r => r.ResolveFfprobePath()).Returns("/fake/ffprobe-should-not-apply");
        wrapper.OnServerConfigurationChanged(resolver.Object, EventArgs.Empty);

        Assert.Equal("/fake/ffmpeg-v1", wrapper.FfmpegPath);
        Assert.Equal("/fake/ffprobe-v1", wrapper.FfprobePath);
    }
}
