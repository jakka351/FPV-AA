using System;
using System.Threading;
using System.Threading.Tasks;

namespace FpvGauges.Gauges;

/// <summary>
/// Feeds synthetic-but-plausible values into <see cref="VehicleState"/> so the cluster can be
/// demonstrated (and the recreation validated) without a car or adapter connected. Drives the
/// needles through their full range and occasionally trips the engine warning zone.
/// </summary>
public sealed class DemoDriver
{
    public static readonly DemoDriver Instance = new();
    private DemoDriver() { }

    private CancellationTokenSource _cts;
    public bool Running { get; private set; }

    public void Start()
    {
        if (Running) return;
        Running = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var state = VehicleState.Instance;
        state.Connected = true;
        state.ObdReady = true;
        state.Status = "Demo mode";
        state.ToolName = "Demo";
        state.SetPid(0x33, 101.3);   // baro

        _ = Task.Run(async () =>
        {
            double t = 0;
            while (!token.IsCancellationRequested)
            {
                t += 0.05;

                // Coolant: warm up to ~92 °C, breathe a little, and every ~40 s push into the
                // 135–150 warning band so the strip + red needle can be seen.
                double warm = Math.Min(92, 20 + t * 6);
                double coolant = warm + 3 * Math.Sin(t * 0.5);
                if ((int)(t / 40) % 3 == 2) coolant = 138 + 6 * Math.Sin(t);
                state.SetPid(0x05, coolant);

                // Boost: idle vacuum → spool to ~0.8 bar in waves (set MAP so Resolve computes it).
                double bar = 0.4 + 0.42 * Math.Sin(t * 0.8);
                if (bar < 0) bar = 0;
                state.SetPid(0x0B, 101.3 + bar * 100.0);

                // Voltage: charging system ~13.8–14.4 V.
                state.SetPid(0x42, 14.1 + 0.3 * Math.Sin(t * 0.3));

                // G-force: sweep a circle so the scope dot orbits.
                state.SetGForce(0.55 * Math.Sin(t * 1.1), 0.55 * Math.Cos(t * 0.9));

                try { await Task.Delay(50, token); } catch { break; }
            }
        }, token);
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        var state = VehicleState.Instance;
        if (state.ToolName == "Demo")
        {
            state.Connected = false;
            state.ObdReady = false;
            state.Status = "Not connected";
        }
    }
}
