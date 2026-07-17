using System;
using System.Collections.Concurrent;

namespace FpvGauges.Gauges;

/// <summary>
/// Thread-safe snapshot of everything the gauges display: the latest decoded Mode 01
/// PID values, the derived boost figure, battery volts, g-force, and connection status.
/// The OBD poll loop and accelerometer writer update it; the surface renderer and the
/// phone preview read it. Framework-neutral (no Android / MAUI types) so both renderers
/// share it.
/// </summary>
public sealed class VehicleState
{
    public static readonly VehicleState Instance = new();

    private readonly ConcurrentDictionary<byte, double> _pids = new();

    // ── connection ──
    public volatile bool Connected;
    public volatile bool ObdReady;
    private volatile string _status = "Not connected";
    public string Status { get => _status; set => _status = value ?? ""; }
    public string ToolName = "";

    // ── derived / non-PID sources ──
    public double BatteryVolts;      // adapter-reported fallback for voltage
    public double Baro = 101.3;      // last barometric pressure (kPa) for boost math

    // g-force (g units). Lateral = +right, Longitudinal = +accel/forward.
    public double GLateral;
    public double GLongitudinal;
    public double GMagnitude;

    public long LastPidUpdateTicks;  // Environment.TickCount64 of the last successful PID read

    /// <summary>Store a freshly decoded PID value.</summary>
    public void SetPid(byte pid, double value)
    {
        _pids[pid] = value;
        LastPidUpdateTicks = Environment.TickCount64;
        if (pid == 0x33) Baro = value;         // keep baro current for boost
    }

    public bool TryGetPid(byte pid, out double value) => _pids.TryGetValue(pid, out value);

    /// <summary>Set the accelerometer-derived g-force (already in g units, mount-corrected).</summary>
    public void SetGForce(double lateral, double longitudinal)
    {
        GLateral = lateral;
        GLongitudinal = longitudinal;
        GMagnitude = Math.Sqrt(lateral * lateral + longitudinal * longitudinal);
    }

    /// <summary>
    /// Resolve the numeric value that drives a pod, or null if that source hasn't been
    /// read yet. Boost = (MAP − baro)/100 bar, clamped ≥ 0.
    /// </summary>
    public double? Resolve(GaugePodConfig cfg)
    {
        switch (cfg.Source)
        {
            case GaugeSourceKind.CoolantTemp:      return Pid(0x05);
            case GaugeSourceKind.IntakeAirTemp:    return Pid(0x0F);
            case GaugeSourceKind.OilTemp:          return Pid(0x5C);
            case GaugeSourceKind.EngineRpm:        return Pid(0x0C);
            case GaugeSourceKind.VehicleSpeed:     return Pid(0x0D);
            case GaugeSourceKind.EngineLoad:       return Pid(0x04);
            case GaugeSourceKind.ThrottlePosition: return Pid(0x11);
            case GaugeSourceKind.ManifoldPressure: return Pid(0x0B);
            case GaugeSourceKind.FuelLevel:        return Pid(0x2F);
            case GaugeSourceKind.CustomPid:        return Pid(cfg.CustomPid);

            case GaugeSourceKind.BatteryVoltage:
                if (_pids.TryGetValue(0x42, out var v)) return v;
                return BatteryVolts > 0 ? BatteryVolts : (double?)null;

            case GaugeSourceKind.Boost:
                if (_pids.TryGetValue(0x0B, out var map))
                {
                    double bar = (map - Baro) / 100.0;
                    return bar < 0 ? 0 : bar;
                }
                return null;

            case GaugeSourceKind.GForce:
                return GMagnitude;

            default:
                return null;
        }
    }

    private double? Pid(byte pid) => _pids.TryGetValue(pid, out var v) ? v : (double?)null;
}
