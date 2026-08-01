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
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Sets <see cref="Plugin.Instance"/> and its <c>Configuration</c> for tests that
/// need <c>ScanEngine</c>/<c>FfmpegResolver</c> to read plugin config, without
/// invoking Plugin's real constructor (which requires a live Jellyfin host).
/// Both members have non-public setters by design, so this uses reflection —
/// test-only plumbing, never referenced from production code.
///
/// <c>Plugin.Instance</c> is a process-wide static singleton. Tests that use this
/// helper must not run in parallel with each other; ScanEngineTests relies on
/// xUnit's default "one collection per test class" behavior, which runs all
/// tests within this class sequentially (collections run in parallel with each
/// other, not with themselves), so no other test class may use this helper.
/// </summary>
internal static class TestPluginContext
{
    private static readonly PropertyInfo InstanceProperty =
        typeof(Plugin).GetProperty(nameof(Plugin.Instance), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly PropertyInfo ConfigurationProperty =
        typeof(Plugin).GetProperty(nameof(Plugin.Configuration), BindingFlags.Public | BindingFlags.Instance)!;

    public static void SetConfiguration(PluginConfiguration config)
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        ConfigurationProperty.SetValue(plugin, config);
        InstanceProperty.SetValue(null, plugin);
    }

    public static void Clear()
    {
        InstanceProperty.SetValue(null, null);
    }
}
