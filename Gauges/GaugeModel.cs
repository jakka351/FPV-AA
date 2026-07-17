using System.Collections.Generic;

namespace FpvGauges.Gauges;

/// <summary>The four physical pods printed on the FPV GT-F face (fixed positions on the artwork).</summary>
public enum PodId
{
    Engine = 0,   // left dial   — 20..150 °C
    Boost = 1,    // centre dial — 0..1.0 BAR (raised, orange ring)
    Voltage = 2,  // right dial  — 6..18 V
    GForce = 3,   // bottom scope — accelerometer radar
}

/// <summary>What data drives a pod. Configurable per pod; defaults recreate the pictured cluster.</summary>
public enum GaugeSourceKind
{
    CoolantTemp,        // PID 0x05  °C
    Boost,              // (MAP 0x0B − Baro 0x33) → bar, clamped ≥ 0
    BatteryVoltage,     // PID 0x42  V (falls back to adapter ReadBatteryVoltage)
    EngineRpm,          // PID 0x0C  rpm
    VehicleSpeed,       // PID 0x0D  km/h
    IntakeAirTemp,      // PID 0x0F  °C
    OilTemp,            // PID 0x5C  °C
    EngineLoad,         // PID 0x04  %
    ThrottlePosition,   // PID 0x11  %
    ManifoldPressure,   // PID 0x0B  kPa (absolute)
    FuelLevel,          // PID 0x2F  %
    GForce,             // phone accelerometer (scope pod only)
    CustomPid,          // any Mode 01 PID from the J1979 catalog (uses CustomPid + Min/Max/Unit)
}

/// <summary>Whether a pod renders a rotating needle or the g-force scope dot.</summary>
public enum PodKind { Dial, Scope }

/// <summary>
/// Fixed drawing geometry of a pod, in the 800×480 coordinate space of the face
/// image (<c>background_volts.png</c>). The renderer scales these to the live surface.
/// The needle sweep is uniform across all FPV dials: min at 225° (lower-left),
/// sweeping +270° clockwise to max at 135° (lower-right). 0° = straight up.
/// </summary>
public sealed class PodGeometry
{
    public required PodId Id { get; init; }
    public required PodKind Kind { get; init; }
    public required float CenterX { get; init; }
    public required float CenterY { get; init; }
    public required float NeedleLength { get; init; }   // dial: needle tip radius; scope: 1g radius

    public const float StartAngleDeg = 225f;   // value = min
    public const float SweepDeg = 270f;         // clockwise to value = max
}

/// <summary>
/// Per-pod user configuration. The <see cref="Min"/>/<see cref="Max"/> default to the
/// scale printed on the artwork so the needle lines up with the numbers; the source
/// mapping is what the user re-assigns to make a pod configurable.
/// </summary>
public sealed class GaugePodConfig
{
    public PodId Pod { get; set; }
    public GaugeSourceKind Source { get; set; }
    public byte CustomPid { get; set; }       // used when Source == CustomPid
    public double Min { get; set; }
    public double Max { get; set; }
    public double WarnStart { get; set; }     // value at which the pod goes into its warning colour
    public string Label { get; set; } = "";   // printed on the face already; kept for the phone UI
    public string Unit { get; set; } = "";
}

/// <summary>Base dimensions of the FPV face artwork.</summary>
public static class Face
{
    public const int Width = 800;
    public const int Height = 480;

    /// <summary>Fixed pod geometry, measured from the artwork.</summary>
    public static readonly IReadOnlyDictionary<PodId, PodGeometry> Geometry = new Dictionary<PodId, PodGeometry>
    {
        [PodId.Engine]  = new() { Id = PodId.Engine,  Kind = PodKind.Dial,  CenterX = 149f, CenterY = 175f, NeedleLength = 82f },
        [PodId.Boost]   = new() { Id = PodId.Boost,   Kind = PodKind.Dial,  CenterX = 399f, CenterY = 140f, NeedleLength = 80f },
        [PodId.Voltage] = new() { Id = PodId.Voltage, Kind = PodKind.Dial,  CenterX = 650f, CenterY = 175f, NeedleLength = 82f },
        [PodId.GForce]  = new() { Id = PodId.GForce,  Kind = PodKind.Scope, CenterX = 400f, CenterY = 358f, NeedleLength = 62f },
    };

    /// <summary>The factory default configuration — exactly the pictured FPV GT-F cluster.</summary>
    public static List<GaugePodConfig> DefaultConfig() => new()
    {
        new GaugePodConfig { Pod = PodId.Engine,  Source = GaugeSourceKind.CoolantTemp,    Min = 20,  Max = 150, WarnStart = 130, Label = "ENGINE",  Unit = "°C"  },
        new GaugePodConfig { Pod = PodId.Boost,   Source = GaugeSourceKind.Boost,          Min = 0,   Max = 1.0, WarnStart = 0.9, Label = "BOOST",   Unit = "BAR" },
        new GaugePodConfig { Pod = PodId.Voltage, Source = GaugeSourceKind.BatteryVoltage, Min = 6,   Max = 18,  WarnStart = 16,  Label = "VOLTAGE", Unit = "V"   },
        new GaugePodConfig { Pod = PodId.GForce,  Source = GaugeSourceKind.GForce,         Min = 0,   Max = 1.0, WarnStart = 1.0, Label = "G-FORCE", Unit = "g"   },
    };
}
