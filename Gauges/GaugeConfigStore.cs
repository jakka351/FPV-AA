using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace FpvGauges.Gauges;

/// <summary>
/// Loads/saves the pod configuration. Persisted as JSON in MAUI <see cref="Preferences"/>
/// so it is shared by the phone UI and the Android Auto surface (same process).
/// Defaults to the pictured FPV GT-F cluster on first run.
/// </summary>
public static class GaugeConfigStore
{
    private const string Key = "fpv_gauge_config_v1";

    private static List<GaugePodConfig> _cache;
    private static readonly object Gate = new();

    public static List<GaugePodConfig> Load()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;
            try
            {
                var json = Preferences.Default.Get(Key, "");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var list = JsonSerializer.Deserialize<List<GaugePodConfig>>(json);
                    if (list != null && list.Count == 4) { _cache = Normalize(list); return _cache; }
                }
            }
            catch { /* fall through to defaults */ }
            _cache = Face.DefaultConfig();
            return _cache;
        }
    }

    public static void Save(List<GaugePodConfig> config)
    {
        lock (Gate)
        {
            _cache = Normalize(config);
            try { Preferences.Default.Set(Key, JsonSerializer.Serialize(_cache)); } catch { }
        }
    }

    public static void ResetToFpvDefault()
    {
        lock (Gate)
        {
            _cache = Face.DefaultConfig();
            try { Preferences.Default.Set(Key, JsonSerializer.Serialize(_cache)); } catch { }
        }
    }

    /// <summary>Return the config for a pod (always present after Load()).</summary>
    public static GaugePodConfig For(PodId pod)
    {
        foreach (var c in Load()) if (c.Pod == pod) return c;
        return Face.DefaultConfig().Find(c => c.Pod == pod);
    }

    // Guarantee one entry per pod, in Engine/Boost/Voltage/GForce order.
    private static List<GaugePodConfig> Normalize(List<GaugePodConfig> list)
    {
        var defaults = Face.DefaultConfig();
        var result = new List<GaugePodConfig>(4);
        foreach (var d in defaults)
            result.Add(list.Find(c => c.Pod == d.Pod) ?? d);
        return result;
    }
}
