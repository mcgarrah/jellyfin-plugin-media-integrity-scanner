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

using System.Collections.Generic;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="ArrServerSelector"/> -- the Phase 3 multi-server
/// routing logic (<c>ARR-INTEGRATION-PROPOSAL.md</c> section 11).
/// </summary>
public class ArrServerSelectorTests
{
    private readonly ArrServerSelector _selector = new();

    private static ArrServerConfig MakeServer(string name, params string[] prefixes) =>
        new() { Name = name, Url = $"http://{name}", ApiKey = "key", LibraryPathPrefixes = new List<string>(prefixes) };

    [Fact]
    public void SelectForPath_NoServersConfigured_ReturnsNull()
    {
        var result = _selector.SelectForPath(new List<ArrServerConfig>(), "/data/movies/Foo/Foo.mkv");

        Assert.Null(result);
    }

    [Fact]
    public void SelectForPath_ExactlyOneServer_AlwaysUsesIt_RegardlessOfPrefixes()
    {
        var only = MakeServer("Main");

        var result = _selector.SelectForPath(new List<ArrServerConfig> { only }, "/data/movies/Foo/Foo.mkv");

        Assert.Same(only, result);
    }

    [Fact]
    public void SelectForPath_MultipleServers_RoutesToTheOneWhosePrefixMatches()
    {
        var main = MakeServer("Main", "/data/movies");
        var disney = MakeServer("Disney", "/data/disney-movies");

        var result = _selector.SelectForPath(new List<ArrServerConfig> { main, disney }, "/data/disney-movies/Frozen/Frozen.mkv");

        Assert.Same(disney, result);
    }

    [Fact]
    public void SelectForPath_PathUnderNoConfiguredPrefix_FallsBackToTheFirstServer()
    {
        var main = MakeServer("Main", "/data/movies");
        var disney = MakeServer("Disney", "/data/disney-movies");

        var result = _selector.SelectForPath(new List<ArrServerConfig> { main, disney }, "/data/documentaries/Foo.mkv");

        Assert.Same(main, result);
    }

    [Fact]
    public void SelectForPath_PrefixMatch_IsCaseInsensitive()
    {
        var disney = MakeServer("Disney", "/Data/Disney-Movies");
        var main = MakeServer("Main", "/data/movies");

        var result = _selector.SelectForPath(new List<ArrServerConfig> { main, disney }, "/data/disney-movies/Frozen/Frozen.mkv");

        Assert.Same(disney, result);
    }

    [Fact]
    public void SelectForPath_DoesNotFalsePositiveOnASimilarlyNamedSiblingFolder()
    {
        // "/data/movies2" must not match a configured prefix of "/data/movies" --
        // a naive string.StartsWith would get this wrong.
        var main = MakeServer("Main", "/data/movies");
        var other = MakeServer("Other", "/data/movies2");

        var result = _selector.SelectForPath(new List<ArrServerConfig> { main, other }, "/data/movies2/Foo/Foo.mkv");

        Assert.Same(other, result);
    }

    [Fact]
    public void SelectForPath_TrailingSlashOnConfiguredPrefix_StillMatches()
    {
        var disney = MakeServer("Disney", "/data/disney-movies/");

        var result = _selector.SelectForPath(new List<ArrServerConfig> { MakeServer("Main", "/data/movies"), disney }, "/data/disney-movies/Frozen/Frozen.mkv");

        Assert.Same(disney, result);
    }

    [Fact]
    public void SelectForPath_ServerWithNoPrefixesConfigured_IsNeverMatchedByPath_OnlyByFallback()
    {
        var noPrefixes = MakeServer("NoPrefixes");
        var withPrefix = MakeServer("WithPrefix", "/data/disney-movies");

        var result = _selector.SelectForPath(new List<ArrServerConfig> { noPrefixes, withPrefix }, "/data/disney-movies/Frozen/Frozen.mkv");

        Assert.Same(withPrefix, result);
    }
}
