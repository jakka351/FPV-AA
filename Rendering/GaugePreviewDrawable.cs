using System;
using System.Collections.Generic;
using System.Diagnostics;
using FpvGauges.Gauges;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using Microsoft.Maui.Storage;

namespace FpvGauges.Rendering;

/// <summary>
/// Phone-side live preview of the FPV cluster, drawn with cross-platform
/// <see cref="Microsoft.Maui.Graphics"/> into a <c>GraphicsView</c>. Same face artwork and same
/// needle geometry as the Android Auto surface — so what the user sees on the phone matches the
/// head unit. Owns its own <see cref="GaugeSceneModel"/> + clock; the page just invalidates it.
/// </summary>
public sealed class GaugePreviewDrawable : IDrawable
{
    private readonly GaugeSceneModel _scene = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSec;

    private IImage _face;
    private bool _faceTried;
    private readonly Dictionary<PodId, Queue<PointF>> _trails = new();

    public void Draw(ICanvas canvas, RectF rect)
    {
        double t = _clock.Elapsed.TotalSeconds;
        double dt = t - _lastSec;
        _lastSec = t;
        if (dt <= 0 || dt > 0.25) dt = 0.016;
        _scene.Update(dt);

        EnsureFace();

        canvas.FillColor = Color.FromRgb(20, 24, 32);
        canvas.FillRectangle(rect);

        float s = Math.Min(rect.Width / Face.Width, rect.Height / Face.Height);
        float offX = rect.X + (rect.Width - Face.Width * s) / 2f;
        float offY = rect.Y + (rect.Height - Face.Height * s) / 2f;

        canvas.SaveState();
        canvas.Translate(offX, offY);
        canvas.Scale(s, s);

        if (_face != null)
            canvas.DrawImage(_face, 0, 0, Face.Width, Face.Height);

        foreach (var f in _scene.Frames)
        {
            if (f.Kind == PodKind.Scope) DrawScope(canvas, f);
            else DrawNeedle(canvas, f);
        }

        canvas.RestoreState();

        if (!_scene.Connected)
        {
            canvas.FontColor = Color.FromRgba(235, 170, 120, 200);
            canvas.FontSize = 13;
            canvas.DrawString(string.IsNullOrEmpty(_scene.Status) ? "Connecting…" : _scene.Status,
                rect.X, rect.Bottom - 22, rect.Width, 18, HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    private void EnsureFace()
    {
        if (_faceTried) return;
        _faceTried = true;
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync("background_volts.png").GetAwaiter().GetResult();
            _face = PlatformImage.FromStream(stream);
        }
        catch { _face = null; }
    }

    private void DrawNeedle(ICanvas canvas, PodFrame f)
    {
        float len = f.NeedleLength, baseHalf = 6.5f, tail = 18f;
        bool warn = f.Warning;
        Color tip = warn ? Color.FromRgb(255, 40, 24) : Color.FromRgb(255, 120, 28);
        Color body = warn ? Color.FromRgb(210, 20, 12) : Color.FromRgb(240, 90, 20);

        canvas.SaveState();
        canvas.Translate(f.CenterX, f.CenterY);
        canvas.Rotate(f.NeedleAngleDeg);   // 0 = up, clockwise

        var path = new PathF();
        path.MoveTo(-baseHalf, 0);
        path.LineTo(0, -len);
        path.LineTo(baseHalf, 0);
        path.LineTo(baseHalf * 0.55f, tail);
        path.LineTo(-baseHalf * 0.55f, tail);
        path.Close();

        canvas.FillColor = body;
        canvas.FillPath(path);

        // brighter tip triangle
        var tipPath = new PathF();
        tipPath.MoveTo(-baseHalf * 0.5f, -len * 0.45f);
        tipPath.LineTo(0, -len);
        tipPath.LineTo(baseHalf * 0.5f, -len * 0.45f);
        tipPath.Close();
        canvas.FillColor = tip;
        canvas.FillPath(tipPath);

        canvas.StrokeColor = Color.FromRgba(255, 225, 200, 210);
        canvas.StrokeSize = 1.2f;
        canvas.DrawLine(0, tail - 4, 0, -len + 3);

        canvas.RestoreState();

        // metallic hub cap
        canvas.FillColor = Color.FromRgb(40, 44, 50);
        canvas.FillCircle(f.CenterX, f.CenterY, 12f);
        canvas.StrokeColor = Color.FromRgb(120, 126, 134);
        canvas.StrokeSize = 2f;
        canvas.DrawCircle(f.CenterX, f.CenterY, 12f);
        canvas.FillColor = Color.FromRgb(70, 76, 84);
        canvas.FillCircle(f.CenterX - 2, f.CenterY - 2, 4f);
    }

    private void DrawScope(ICanvas canvas, PodFrame f)
    {
        float px = f.CenterX + f.DotX;
        float py = f.CenterY + f.DotY;

        if (!_trails.TryGetValue(f.Id, out var trail)) { trail = new Queue<PointF>(); _trails[f.Id] = trail; }
        trail.Enqueue(new PointF(px, py));
        while (trail.Count > 14) trail.Dequeue();

        int i = 0, n = trail.Count;
        foreach (var pt in trail)
        {
            float a = (i + 1) / (float)n;
            canvas.FillColor = Color.FromRgba(255, 120, 28, (int)(90 * a));
            canvas.FillCircle(pt.X, pt.Y, 3.5f * a + 1.5f);
            i++;
        }

        canvas.FillColor = Color.FromRgb(255, 150, 40);
        canvas.FillCircle(px, py, 6.5f);
        canvas.FillColor = Color.FromRgb(255, 235, 210);
        canvas.FillCircle(px, py, 2.6f);
    }
}
