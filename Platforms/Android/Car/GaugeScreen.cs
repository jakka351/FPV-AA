using System;
using System.Threading;
using Android.Graphics;
using Android.Views;
using AndroidX.Car.App;
using AndroidX.Car.App.Model;
using AndroidX.Car.App.Navigation.Model;
using FpvGauges.Obd;
using FpvGauges.Rendering;
using JavaObject = Java.Lang.Object;

namespace FpvGauges.Car;

/// <summary>
/// The Android Auto gauge screen. Registers a surface callback, then runs its own render loop
/// that locks the head-unit surface and draws the FPV cluster (<see cref="SurfaceGaugeRenderer"/>)
/// at ~50 fps. Presents a minimal <see cref="NavigationTemplate"/> (surface apps must supply a
/// template even though all the pixels come from the surface).
/// </summary>
public sealed class GaugeScreen : Screen, ISurfaceCallback
{
    private readonly SurfaceGaugeRenderer _renderer = new();
    private readonly GaugeSceneModel _scene = new();

    private Surface _surface;
    private int _width, _height;
    private volatile bool _running;
    private Thread _loop;
    private readonly object _gate = new();

    public GaugeScreen(CarContext carContext) : base(carContext)
    {
        var appManager = (AppManager)carContext.GetCarService(CarContext.AppService);
        appManager.SetSurfaceCallback(this);

        // Ensure the data feed is live even when AA is opened without the phone UI in front.
        _ = GaugeDataService.Instance.EnsureConnectedAsync();
    }

    public override ITemplate OnGetTemplate()
    {
        var strip = new ActionStrip.Builder()
            .AddAction(new AndroidX.Car.App.Model.Action.Builder().SetTitle("FPV GT-F").Build())
            .Build();
        return new NavigationTemplate.Builder().SetActionStrip(strip).Build();
    }

    // ── ISurfaceCallback ──

    public void OnSurfaceAvailable(SurfaceContainer surfaceContainer)
    {
        lock (_gate)
        {
            _surface = surfaceContainer.Surface;
            _width = surfaceContainer.Width;
            _height = surfaceContainer.Height;
            StartLoop();
        }
    }

    public void OnSurfaceDestroyed(SurfaceContainer surfaceContainer)
    {
        lock (_gate)
        {
            StopLoop();
            _surface = null;
        }
    }

    public void OnVisibleAreaChanged(Rect visibleArea) { }
    public void OnStableAreaChanged(Rect stableArea) { }
    public void OnScroll(float distanceX, float distanceY) { }
    public void OnFling(float velocityX, float velocityY) { }
    public void OnScale(float focusX, float focusY, float scaleFactor) { }
    public void OnClick(float x, float y) { }

    // ── render loop ──

    private void StartLoop()
    {
        if (_running) return;
        _running = true;
        _loop = new Thread(RenderLoop) { IsBackground = true, Name = "FpvGaugeRender" };
        _loop.Start();
    }

    private void StopLoop()
    {
        _running = false;
        try { _loop?.Join(500); } catch { }
        _loop = null;
    }

    private void RenderLoop()
    {
        long last = Environment.TickCount64;
        while (_running)
        {
            Surface surface;
            int w, h;
            lock (_gate) { surface = _surface; w = _width; h = _height; }

            if (surface == null || !surface.IsValid || w <= 0 || h <= 0)
            {
                Thread.Sleep(50);
                continue;
            }

            long now = Environment.TickCount64;
            double dt = (now - last) / 1000.0;
            last = now;
            if (dt > 0.25) dt = 0.25;

            Canvas canvas = null;
            try
            {
                canvas = surface.LockCanvas(null);   // software canvas → blur/glow honoured
                if (canvas != null)
                {
                    _scene.Update(dt);
                    _renderer.DrawFrame(canvas, w, h, _scene);
                }
            }
            catch { /* surface may be mid-teardown */ }
            finally
            {
                if (canvas != null)
                {
                    try { surface.UnlockCanvasAndPost(canvas); } catch { }
                }
            }

            Thread.Sleep(20);   // ~50 fps
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopLoop();
            _renderer.Dispose();
        }
        base.Dispose(disposing);
    }
}
