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
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Groups every test class that touches <see cref="Plugin.Instance"/> (via
/// <see cref="TestPluginContext"/>) into one xUnit collection, so they run
/// sequentially against each other instead of racing on the shared static.
/// xUnit still parallelizes this collection against unrelated ones.
/// </summary>
[CollectionDefinition("PluginInstance")]
public class PluginInstanceCollection
{
}

/// <summary>
/// Sets <see cref="Plugin.Instance"/> and its <c>Configuration</c>/<c>Version</c>
/// for tests that need <c>ScanEngine</c>/<c>FfmpegResolver</c>/<c>UpdateChecker</c>
/// to read plugin state, without invoking Plugin's real constructor (which
/// requires a live Jellyfin host). All three members have non-public setters
/// by design, so this uses reflection -- test-only plumbing, never referenced
/// from production code. <c>Version</c> is declared on the non-generic
/// <c>BasePlugin</c> base class (not <c>BasePlugin&lt;T&gt;</c>), and is left
/// unset (null) unless a version is explicitly passed in -- calling
/// <c>.ToString()</c> on it without doing so is a test bug, not a production one.
///
/// <c>Plugin.Instance</c> is a process-wide static singleton — every test class
/// that uses this helper must be decorated with <c>[Collection("PluginInstance")]</c>
/// (see <see cref="PluginInstanceCollection"/>) so xUnit runs them sequentially
/// against each other rather than in parallel.
/// </summary>
internal static class TestPluginContext
{
    private static readonly PropertyInfo InstanceProperty =
        typeof(Plugin).GetProperty(nameof(Plugin.Instance), BindingFlags.Public | BindingFlags.Static)!;

    private static readonly PropertyInfo ConfigurationProperty =
        typeof(Plugin).GetProperty(nameof(Plugin.Configuration), BindingFlags.Public | BindingFlags.Instance)!;

    private static readonly PropertyInfo VersionProperty =
        typeof(MediaBrowser.Common.Plugins.BasePlugin).GetProperty(
            nameof(MediaBrowser.Common.Plugins.BasePlugin.Version), BindingFlags.Public | BindingFlags.Instance)!;

    public static void SetConfiguration(PluginConfiguration config, Version? version = null)
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        ConfigurationProperty.SetValue(plugin, config);
        if (version != null)
        {
            VersionProperty.SetValue(plugin, version);
        }

        InstanceProperty.SetValue(null, plugin);
    }

    public static void Clear()
    {
        InstanceProperty.SetValue(null, null);
    }
}
