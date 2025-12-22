using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AutoRetainerAPI;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.Reflection;

namespace ARControl.External;

internal sealed class AutoRetainerReflection(IPluginLog pluginLog, AutoRetainerApi autoRetainerApi)
{
    [SuppressMessage("Performance", "CA2000", Justification = "Should not dispose other plugins")]
    public bool ShouldReassign
    {
        get
        {
            try
            {
                if (autoRetainerApi.Ready &&
                    DalamudReflector.TryGetDalamudPlugin("AutoRetainer", out var autoRetainer, false, true))
                {
                    var config =
                        autoRetainer.GetType().GetProperty("C", BindingFlags.Static | BindingFlags.NonPublic)!
                            .GetValue(null);
                    if (config == null)
                    {
                        pluginLog.Warning("Could not retrieve AR config");
                        return true;
                    }

                    bool dontReassign = (bool)config.GetType()
                        .GetField("_dontReassign", BindingFlags.Instance | BindingFlags.Public)!
                        .GetValue(config)!;
                    pluginLog.Verbose($"DontReassign is set to {dontReassign}");
                    return !dontReassign;
                }

                pluginLog.Warning("Could not check if reassign is enabled, AutoRetainer not loaded");
                return true;
            }
            catch (Exception e)
            {
                pluginLog.Warning(e, "Unable to check if reassign is enabled");
                return true;
            }
        }
    }
}
