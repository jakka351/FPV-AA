using System;
using System.Collections.Generic;
using Android.Graphics;
using FpvGauges.Gauges;
using FpvGauges.Rendering;

namespace FpvGauges.Car;

/// <summary>
/// Draws the FPV GT-F cluster onto an Android <see cref="Canvas"/> (the Android Auto head-unit
/// surface). The static face is the real 800×480 artwork; needles and the g-force dot are the
/// only overlaid dynamic elements, so the result is pixel-identical to the source image plus
/// live indicators. All drawing is done in base 800×480 coordinates via a canvas transform, so
/// it scales cleanly to any head-unit resolution.
/// </summary>
public sealed class SurfaceGaugeRenderer : IDisposable
{
    private Bitmap _face;
    private readonly Paint _bg = new() { Color = Color.Argb(255, 20, 24, 32) };
    private readonly Paint _facePaint = new(PaintFlags.FilterBitmap | PaintFlags.AntiAlias);
    private readonly Paint _needle = new(PaintFlags.AntiAlias);
    private readonly Paint _needleGlow = new(PaintFlags.AntiAlias);
    private readonly Paint _hub = new(PaintFlags.AntiAlias);
    private readonly Paint _hubRim = new(PaintFlags.AntiAlias) { StrokeWidth = 2 };
    private readonly Paint _dot = new(PaintFlags.AntiAlias);
    private readonly Paint _dotGlow = new(PaintFlags.AntiAlias);
    private readonly Paint _text = new(PaintFlags.AntiAlias) { TextAlign = Paint.Align.Center, TextSize = 20 };

    private readonly Dictionary<PodId, Queue<PointF>> _trails = new();

    public SurfaceGaugeRenderer()
    {
        _hubRim.SetStyle(Paint.Style.Stroke);
        _text.Color = Color.Argb(200, 220, 224, 230);
    }

    private void EnsureFace()
    {
        if (_face != null) return;
        try
        {
            using var s = Android.App.Application.Context.Assets.Open("background_volts.png");
            _face = BitmapFactory.DecodeStream(s);
        }
        catch { _face = null; }
    }

    /// <summary>Render one frame. <paramref name="scene"/> must already be updated for this frame.</summary>
    public void DrawFrame(Canvas canvas, int width, int height, GaugeSceneModel scene)
    {
        EnsureFace();
        canvas.DrawColor(Color.Argb(255, 20, 24, 32));

        // Uniform "contain" fit so no pod is ever cropped, centred in the surface.
        float s = Math.Min(width / (float)Face.Width, height / (float)Face.Height);
        float offX = (width - Face.Width * s) / 2f;
        float offY = (height - Face.Height * s) / 2f;

        int save = canvas.Save();
        canvas.Translate(offX, offY);
        canvas.Scale(s, s);

        if (_face != null)
            canvas.DrawBitmap(_face, null, new RectF(0, 0, Face.Width, Face.Height), _facePaint);

        foreach (var f in scene.Frames)
        {
            if (f.Kind == PodKind.Scope) DrawScope(canvas, f);
            else DrawNeedle(canvas, f);
        }

        canvas.RestoreToCount(save);

        if (!scene.Connected)
            DrawStatus(canvas, width, height, scene.Status);
    }

    private void DrawNeedle(Canvas canvas, PodFrame f)
    {
        float len = f.NeedleLength;
        float baseHalf = 6.5f;
        float tail = 18f;

        bool warn = f.Warning;
        var tip = warn ? Color.Argb(255, 255, 40, 24) : Color.Argb(255, 255, 120, 28);
        var body = warn ? Color.Argb(255, 210, 20, 12) : Color.Argb(255, 240, 90, 20);

        int save = canvas.Save();
        canvas.Translate(f.CenterX, f.CenterY);
        canvas.Rotate(f.NeedleAngleDeg);   // 0 = up; Android rotate is clockwise (y-down)

        // soft glow underlay
        _needleGlow.Color = warn ? Color.Argb(120, 255, 40, 24) : Color.Argb(110, 255, 120, 28);
        _needleGlow.SetMaskFilter(new BlurMaskFilter(6f, BlurMaskFilter.Blur.Normal));
        _needleGlow.SetStyle(Paint.Style.Fill);
        canvas.DrawPath(NeedlePath(len, baseHalf, tail), _needleGlow);

        // needle body with a length gradient (bright at the tip)
        _needle.SetShader(new LinearGradient(0, tail, 0, -len,
            new[] { body.ToArgb(), body.ToArgb(), tip.ToArgb() },
            new float[] { 0f, 0.55f, 1f }, Shader.TileMode.Clamp));
        _needle.SetStyle(Paint.Style.Fill);
        canvas.DrawPath(NeedlePath(len, baseHalf, tail), _needle);
        _needle.SetShader(null);

        // bright centre spine
        var spine = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(210, 255, 225, 200), StrokeWidth = 1.4f };
        spine.SetStyle(Paint.Style.Stroke);
        canvas.DrawLine(0, tail - 4, 0, -len + 3, spine);
        spine.Dispose();

        canvas.RestoreToCount(save);

        // metallic hub cap over the needle base (matches the printed pivot)
        _hub.SetShader(new RadialGradient(f.CenterX - 3, f.CenterY - 3, 16,
            new[] { Color.Argb(255, 90, 96, 104).ToArgb(), Color.Argb(255, 26, 30, 36).ToArgb() },
            new float[] { 0f, 1f }, Shader.TileMode.Clamp));
        canvas.DrawCircle(f.CenterX, f.CenterY, 12f, _hub);
        _hub.SetShader(null);
        _hubRim.Color = Color.Argb(255, 120, 126, 134);
        canvas.DrawCircle(f.CenterX, f.CenterY, 12f, _hubRim);
    }

    private static Path NeedlePath(float len, float baseHalf, float tail)
    {
        var p = new Path();
        p.MoveTo(-baseHalf, 0);
        p.LineTo(0, -len);          // tip (points up before rotation)
        p.LineTo(baseHalf, 0);
        p.LineTo(baseHalf * 0.55f, tail);
        p.LineTo(-baseHalf * 0.55f, tail);
        p.Close();
        return p;
    }

    private void DrawScope(Canvas canvas, PodFrame f)
    {
        float px = f.CenterX + f.DotX;
        float py = f.CenterY + f.DotY;

        // fading trail
        if (!_trails.TryGetValue(f.Id, out var trail)) { trail = new Queue<PointF>(); _trails[f.Id] = trail; }
        trail.Enqueue(new PointF(px, py));
        while (trail.Count > 14) trail.Dequeue();

        int i = 0, n = trail.Count;
        foreach (var pt in trail)
        {
            float a = (i + 1) / (float)n;
            var tp = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb((int)(90 * a), 255, 120, 28) };
            canvas.DrawCircle(pt.X, pt.Y, 3.5f * a + 1.5f, tp);
            tp.Dispose();
            i++;
        }

        // glow + dot
        _dotGlow.Color = Color.Argb(150, 255, 120, 28);
        _dotGlow.SetMaskFilter(new BlurMaskFilter(7f, BlurMaskFilter.Blur.Normal));
        canvas.DrawCircle(px, py, 7.5f, _dotGlow);
        _dot.Color = Color.Argb(255, 255, 150, 40);
        canvas.DrawCircle(px, py, 6.5f, _dot);
        var core = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(255, 255, 235, 210) };
        canvas.DrawCircle(px, py, 2.6f, core);
        core.Dispose();
    }

    private void DrawStatus(Canvas canvas, int width, int height, string status)
    {
        _text.Color = Color.Argb(180, 235, 170, 120);
        canvas.DrawText(string.IsNullOrEmpty(status) ? "Connecting…" : status,
            width / 2f, height - 24f, _text);
    }

    public void Dispose()
    {
        _face?.Dispose(); _face = null;
        _bg.Dispose(); _facePaint.Dispose(); _needle.Dispose(); _needleGlow.Dispose();
        _hub.Dispose(); _hubRim.Dispose(); _dot.Dispose(); _dotGlow.Dispose(); _text.Dispose();
    }
}
