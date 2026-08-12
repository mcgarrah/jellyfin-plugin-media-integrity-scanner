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
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

public class ScanThrottleTests
{
    [Theory]
    [InlineData("02:00", "06:00", "01:59:59", false)]
    [InlineData("02:00", "06:00", "02:00:00", true)]
    [InlineData("02:00", "06:00", "04:00:00", true)]
    [InlineData("02:00", "06:00", "05:59:59", true)]
    [InlineData("02:00", "06:00", "06:00:00", false)]
    [InlineData("02:00", "06:00", "12:00:00", false)]
    public void IsWithinQuietHours_SameDayWindow(string start, string end, string now, bool expected)
    {
        var result = ScanThrottle.IsWithinQuietHours(start, end, TimeSpan.Parse(now));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("22:00", "06:00", "23:00:00", true)]
    [InlineData("22:00", "06:00", "03:00:00", true)]
    [InlineData("22:00", "06:00", "21:59:59", false)]
    [InlineData("22:00", "06:00", "06:00:00", false)]
    [InlineData("22:00", "06:00", "12:00:00", false)]
    public void IsWithinQuietHours_OvernightWraparoundWindow(string start, string end, string now, bool expected)
    {
        var result = ScanThrottle.IsWithinQuietHours(start, end, TimeSpan.Parse(now));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, "06:00")]
    [InlineData("02:00", null)]
    [InlineData("not-a-time", "06:00")]
    [InlineData("", "")]
    public void IsWithinQuietHours_FailsOpen_WhenUnparsable(string? start, string? end)
    {
        var result = ScanThrottle.IsWithinQuietHours(start, end, TimeSpan.Parse("12:00:00"));
        Assert.True(result);
    }

    [Fact]
    public void IsWithinQuietHours_ZeroWidthWindow_TreatedAsAlways()
    {
        var result = ScanThrottle.IsWithinQuietHours("03:00", "03:00", TimeSpan.Parse("15:00:00"));
        Assert.True(result);
    }

    [Theory]
    [InlineData("00:00", "06:00", "00:00:00", true)]
    [InlineData("00:00", "06:00", "23:59:59", false)]
    [InlineData("22:00", "00:00", "23:00:00", true)]
    [InlineData("22:00", "00:00", "00:00:00", false)]
    public void IsWithinQuietHours_MidnightBoundaries(string start, string end, string now, bool expected)
    {
        var result = ScanThrottle.IsWithinQuietHours(start, end, TimeSpan.Parse(now));
        Assert.Equal(expected, result);
    }
}
