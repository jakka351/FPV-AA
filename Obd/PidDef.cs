using System;
using System.Collections.Generic;

namespace FpvGauges.Obd;

/// <summary>One SAE J1979 / ISO 15031-5 Mode 01 PID with its standard decode formula.</summary>
public sealed class PidDef
{
    public byte Pid;
    public string Name;
    public string Unit;
    public int Bytes;
    public double Min;
    public double Max;
    public Func<byte[], double> Decode;   // data bytes (A=d[0], B=d[1] …) → engineering value
}

/// <summary>The Mode 01 PIDs used by the gauges plus a useful catalogue for custom pod assignment.</summary>
public static class J1979Catalog
{
    public static readonly List<PidDef> Pids = new()
    {
        P(0x04, "Calculated Load",   "%",   1, 0, 100,  d => d[0] * 100.0 / 255.0),
        P(0x05, "Coolant Temp",      "°C",  1, -40, 215, d => d[0] - 40.0),
        P(0x0B, "Intake MAP",        "kPa", 1, 0, 255,  d => d[0]),
        P(0x0C, "Engine RPM",        "rpm", 2, 0, 8000, d => (d[0] * 256.0 + d[1]) / 4.0),
        P(0x0D, "Vehicle Speed",     "km/h",1, 0, 255,  d => d[0]),
        P(0x0F, "Intake Air Temp",   "°C",  1, -40, 215, d => d[0] - 40.0),
        P(0x10, "MAF Rate",          "g/s", 2, 0, 655,  d => (d[0] * 256.0 + d[1]) / 100.0),
        P(0x11, "Throttle Position", "%",   1, 0, 100,  d => d[0] * 100.0 / 255.0),
        P(0x1F, "Run Time",          "s",   2, 0, 65535, d => d[0] * 256.0 + d[1]),
        P(0x2F, "Fuel Level",        "%",   1, 0, 100,  d => d[0] * 100.0 / 255.0),
        P(0x33, "Barometric Press",  "kPa", 1, 0, 255,  d => d[0]),
        P(0x42, "Module Voltage",    "V",   2, 0, 65.5, d => (d[0] * 256.0 + d[1]) / 1000.0),
        P(0x46, "Ambient Air Temp",  "°C",  1, -40, 215, d => d[0] - 40.0),
        P(0x5C, "Engine Oil Temp",   "°C",  1, -40, 210, d => d[0] - 40.0),
        P(0x5E, "Fuel Rate",         "L/h", 2, 0, 3277, d => (d[0] * 256.0 + d[1]) / 20.0),
    };

    private static readonly Dictionary<byte, PidDef> ByPid = Build();

    private static Dictionary<byte, PidDef> Build()
    {
        var m = new Dictionary<byte, PidDef>();
        foreach (var p in Pids) m[p.Pid] = p;
        return m;
    }

    public static PidDef Lookup(byte pid) => ByPid.TryGetValue(pid, out var p) ? p : null;

    private static PidDef P(byte pid, string name, string unit, int bytes, double min, double max, Func<byte[], double> dec)
        => new() { Pid = pid, Name = name, Unit = unit, Bytes = bytes, Min = min, Max = max, Decode = dec };
}
