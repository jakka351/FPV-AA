using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FpvGauges.Gauges;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices.Sensors;
using OBDXMAUI;

namespace FpvGauges.Obd;

/// <summary>
/// Orchestrates the live data feed: connects the OBDX Pro FT, polls exactly the PIDs the
/// current pod configuration needs, derives boost, reads the phone accelerometer for
/// g-force, and writes everything into <see cref="VehicleState.Instance"/>. Both renderers
/// (Android Auto surface + phone preview) then read that shared state.
/// </summary>
public sealed class GaugeDataService
{
    public static readonly GaugeDataService Instance = new();
    private GaugeDataService() { }

    private readonly ObdEngine _obd = ObdEngine.Instance;
    private readonly VehicleState _state = VehicleState.Instance;
    private CancellationTokenSource _pollCts;
    private volatile bool _accelRunning;

    // gravity estimate for the high-pass that turns "accel incl. gravity" into "dynamic g".
    private double _gx, _gy, _gz;
    private bool _gravityInit;

    public event Action<string> Log;
    private void L(string m) { try { Log?.Invoke(m); } catch { } _obd.Log -= Relay; _obd.Log += Relay; }
    private void Relay(string m) => Log?.Invoke(m);

    public bool IsConnected => _obd.IsConnected && _obd.ObdReady;

    public Task<(Scantool.Errors err, List<Scantool.OBDXDevice> devices)> ScanAsync(
        IOBDXBase.ConnectionTypeEnum transport = IOBDXBase.ConnectionTypeEnum.ClassicBT)
    {
        _obd.Log -= Relay; _obd.Log += Relay;
        return _obd.ScanAsync(transport);
    }

    /// <summary>Connect, bring up the HS-CAN session, and start polling + accelerometer.</summary>
    public async Task<bool> ConnectAndStartAsync(Scantool.OBDXDevice device)
    {
        _obd.Log -= Relay; _obd.Log += Relay;
        _state.Status = "Connecting…";
        var err = await _obd.ConnectAsync(device);
        if (err != Scantool.Errors.Success)
        {
            _state.Connected = false;
            _state.Status = $"Connect failed: {err}";
            return false;
        }
        _state.Connected = true;
        _state.ToolName = _obd.ToolDetails?.Name ?? "OBDX Pro";

        _state.Status = "Starting OBD session…";
        var (ok, msg) = await _obd.InitObdAsync();
        if (!ok)
        {
            _state.Status = msg;
            return false;
        }
        _state.ObdReady = true;
        _state.Status = "Live";
        _obd.StartKeepAlive();
        StartAccelerometer();
        StartPolling();
        return true;
    }

    private int _connecting;

    /// <summary>
    /// Idempotent auto-connect for the Android Auto path: if not already live, scan and connect
    /// to the first OBDX adapter. No-ops if a connection is already up or in progress. Requires
    /// Bluetooth permission to have been granted once (via the phone app on first run).
    /// </summary>
    public async Task EnsureConnectedAsync()
    {
        if (IsConnected) return;
        if (Interlocked.Exchange(ref _connecting, 1) == 1) return;
        try
        {
            var (err, devices) = await ScanAsync();
            if (err != Scantool.Errors.Success || devices == null || devices.Count == 0)
            {
                _state.Status = "No OBDX adapter found";
                return;
            }
            var dev = devices.FirstOrDefault(d => (d.Name ?? "").Contains("OBDX", StringComparison.OrdinalIgnoreCase))
                      ?? devices[0];
            await ConnectAndStartAsync(dev);
        }
        catch (Exception ex) { _state.Status = "Auto-connect failed"; L("Auto-connect failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _connecting, 0); }
    }

    public async Task StopAsync()
    {
        StopPolling();
        StopAccelerometer();
        await _obd.DisconnectAsync();
        _state.Connected = false;
        _state.ObdReady = false;
        _state.Status = "Disconnected";
    }

    // ── OBD poll loop ──

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(() => PollLoop(token), token);
    }

    private void StopPolling()
    {
        try { _pollCts?.Cancel(); } catch { }
        _pollCts = null;
    }

    private async Task PollLoop(CancellationToken token)
    {
        long lastBaro = 0, lastVolt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var pids = RequiredPids();

                // Boost needs barometric pressure; read it rarely (it barely changes).
                bool needBoost = ConfigUsesBoost();
                if (needBoost) pids.Add(0x0B);   // MAP every cycle

                foreach (var pid in pids)
                {
                    if (token.IsCancellationRequested) break;
                    var def = J1979Catalog.Lookup(pid);
                    if (def == null) continue;
                    var v = await _obd.ReadPidValueAsync(def);
                    if (v.HasValue) _state.SetPid(pid, v.Value);
                }

                long now = Environment.TickCount64;
                if (needBoost && now - lastBaro > 5000)
                {
                    var baro = await _obd.ReadPidValueAsync(J1979Catalog.Lookup(0x33));
                    if (baro.HasValue) _state.SetPid(0x33, baro.Value);
                    lastBaro = now;
                }

                // Voltage: prefer PID 0x42; if the ECU doesn't support it, fall back to the
                // adapter's own measurement every second.
                if (ConfigUsesVoltage() && !_state.TryGetPid(0x42, out _) && now - lastVolt > 1000)
                {
                    var bv = await _obd.ReadBatteryVoltageAsync();
                    if (bv.HasValue) _state.BatteryVolts = bv.Value;
                    lastVolt = now;
                }

                await Task.Delay(60, token);   // ~12–15 Hz aggregate poll
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { L("Poll error: " + ex.Message); await SafeDelay(250, token); }
        }
    }

    private static async Task SafeDelay(int ms, CancellationToken t)
    {
        try { await Task.Delay(ms, t); } catch { }
    }

    // The distinct PIDs the current config needs (coolant/oil/rpm/etc + custom). Boost/voltage
    // handled separately. Ordered so the primary gauges refresh first.
    private HashSet<byte> RequiredPids()
    {
        var set = new HashSet<byte>();
        foreach (var cfg in GaugeConfigStore.Load())
        {
            switch (cfg.Source)
            {
                case GaugeSourceKind.CoolantTemp: set.Add(0x05); break;
                case GaugeSourceKind.IntakeAirTemp: set.Add(0x0F); break;
                case GaugeSourceKind.OilTemp: set.Add(0x5C); break;
                case GaugeSourceKind.EngineRpm: set.Add(0x0C); break;
                case GaugeSourceKind.VehicleSpeed: set.Add(0x0D); break;
                case GaugeSourceKind.EngineLoad: set.Add(0x04); break;
                case GaugeSourceKind.ThrottlePosition: set.Add(0x11); break;
                case GaugeSourceKind.ManifoldPressure: set.Add(0x0B); break;
                case GaugeSourceKind.FuelLevel: set.Add(0x2F); break;
                case GaugeSourceKind.BatteryVoltage: set.Add(0x42); break;
                case GaugeSourceKind.CustomPid: if (cfg.CustomPid != 0) set.Add(cfg.CustomPid); break;
                case GaugeSourceKind.Boost: set.Add(0x0B); break;
            }
        }
        return set;
    }

    private bool ConfigUsesBoost() => GaugeConfigStore.Load().Any(c => c.Source == GaugeSourceKind.Boost);
    private bool ConfigUsesVoltage() => GaugeConfigStore.Load().Any(c => c.Source == GaugeSourceKind.BatteryVoltage);

    // ── accelerometer (g-force) ──

    private void StartAccelerometer()
    {
        if (_accelRunning) return;
        try
        {
            if (!Accelerometer.Default.IsSupported) { L("No accelerometer — g-force pod will stay centred."); return; }
            Accelerometer.Default.ReadingChanged += OnAccel;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { if (!Accelerometer.Default.IsMonitoring) Accelerometer.Default.Start(SensorSpeed.Game); }
                catch (Exception ex) { L("Accelerometer start failed: " + ex.Message); }
            });
            _accelRunning = true;
        }
        catch (Exception ex) { L("Accelerometer unavailable: " + ex.Message); }
    }

    private void StopAccelerometer()
    {
        if (!_accelRunning) return;
        try
        {
            Accelerometer.Default.ReadingChanged -= OnAccel;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { if (Accelerometer.Default.IsMonitoring) Accelerometer.Default.Stop(); } catch { }
            });
        }
        catch { }
        _accelRunning = false;
        _gravityInit = false;
    }

    // MAUI accelerometer returns g-units including gravity. A slow low-pass estimates gravity;
    // subtracting it leaves the dynamic (driving) acceleration. Device X → lateral, Y →
    // longitudinal — a phone mounted upright in a cradle reads cornering on X and accel/brake
    // on Y. The gravity high-pass makes the dot self-centre regardless of mount tilt.
    private void OnAccel(object sender, AccelerometerChangedEventArgs e)
    {
        var a = e.Reading.Acceleration;
        const double lp = 0.08;   // gravity tracking rate
        if (!_gravityInit) { _gx = a.X; _gy = a.Y; _gz = a.Z; _gravityInit = true; }
        _gx += (a.X - _gx) * lp;
        _gy += (a.Y - _gy) * lp;
        _gz += (a.Z - _gz) * lp;

        double lateral = a.X - _gx;         // g
        double longitudinal = a.Y - _gy;    // g
        _state.SetGForce(lateral, longitudinal);
    }
}
