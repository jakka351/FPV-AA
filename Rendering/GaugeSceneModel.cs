using System;
using System.Collections.Generic;
using FpvGauges.Gauges;

namespace FpvGauges.Rendering;

/// <summary>One pod resolved for the current frame — everything a renderer needs, in base (800×480) space.</summary>
public struct PodFrame
{
    public PodId Id;
    public PodKind Kind;
    public float CenterX, CenterY, NeedleLength;

    public bool HasData;         // false ⇒ source not read yet (needle parks at min / dot centred)
    public double Value;         // smoothed display value
    public double Fraction;      // 0..1 across the printed scale
    public float NeedleAngleDeg; // 0 = up, clockwise; = 225 + 270*Fraction
    public bool Warning;         // value ≥ WarnStart

    // scope pods only (g-force): dot offset from centre in base px, and the current magnitude
    public float DotX, DotY;
    public double GMagnitude;
}

/// <summary>
/// Framework-neutral per-frame gauge computation with needle easing. One instance per
/// renderer (car surface / phone preview); call <see cref="Update"/> once per frame, then
/// read <see cref="Frames"/>. No Android or MAUI types here.
/// </summary>
public sealed class GaugeSceneModel
{
    private readonly VehicleState _state;
    private readonly Dictionary<PodId, double> _display = new();
    private readonly Dictionary<PodId, double> _gx = new();
    private readonly Dictionary<PodId, double> _gy = new();

    public PodFrame[] Frames { get; private set; } = Array.Empty<PodFrame>();
    public bool Connected => _state.Connected;
    public string Status => _state.Status;

    public GaugeSceneModel(VehicleState state = null)
    {
        _state = state ?? VehicleState.Instance;
    }

    public void Update(double dtSeconds)
    {
        var config = GaugeConfigStore.Load();
        var frames = new PodFrame[config.Count];

        for (int i = 0; i < config.Count; i++)
        {
            var cfg = config[i];
            var geom = Face.Geometry[cfg.Pod];
            var f = new PodFrame
            {
                Id = cfg.Pod,
                Kind = geom.Kind,
                CenterX = geom.CenterX,
                CenterY = geom.CenterY,
                NeedleLength = geom.NeedleLength,
            };

            if (geom.Kind == PodKind.Scope)
            {
                // g-force scope: smooth the (lateral, longitudinal) vector, scale 1g = NeedleLength.
                double tgx = _state.GLateral, tgy = _state.GLongitudinal;
                double a = EaseAlpha(dtSeconds, 0.07);
                double sx = Approach(_gx, cfg.Pod, tgx, a);
                double sy = Approach(_gy, cfg.Pod, tgy, a);
                double mag = Math.Sqrt(sx * sx + sy * sy);
                // clamp to the outer ring
                double scale = mag > 1.0 ? 1.0 / mag : 1.0;
                f.HasData = _state.Connected || mag > 0.001;
                f.GMagnitude = mag;
                // screen: +lateral → +x (right), +longitudinal(accel) → dot moves UP (−y)
                f.DotX = (float)(sx * scale * geom.NeedleLength);
                f.DotY = (float)(-sy * scale * geom.NeedleLength);
                frames[i] = f;
                continue;
            }

            // dial pod
            double? raw = _state.Resolve(cfg);
            double min = cfg.Min, max = cfg.Max;
            double target = raw ?? min;
            if (target < min) target = min;
            if (target > max) target = max;

            double alpha = EaseAlpha(dtSeconds, 0.12);
            double disp = Approach(_display, cfg.Pod, target, alpha);

            double span = Math.Abs(max - min) < 1e-9 ? 1 : (max - min);
            double frac = (disp - min) / span;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;

            f.HasData = raw.HasValue;
            f.Value = disp;
            f.Fraction = frac;
            f.NeedleAngleDeg = (float)(PodGeometry.StartAngleDeg + PodGeometry.SweepDeg * frac);
            f.Warning = raw.HasValue && disp >= cfg.WarnStart;
            frames[i] = f;
        }

        Frames = frames;
    }

    // Exponential smoothing factor for a given time-constant tau (seconds).
    private static double EaseAlpha(double dt, double tau)
    {
        if (dt <= 0) return 1;
        if (tau <= 0) return 1;
        return 1.0 - Math.Exp(-dt / tau);
    }

    private static double Approach(Dictionary<PodId, double> store, PodId id, double target, double alpha)
    {
        double cur = store.TryGetValue(id, out var v) ? v : target;
        cur += (target - cur) * alpha;
        store[id] = cur;
        return cur;
    }
}
