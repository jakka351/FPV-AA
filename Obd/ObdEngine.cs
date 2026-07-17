using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using OBDXMAUI;
using static OBDXMAUI.Scantool;

namespace FpvGauges.Obd;

/// <summary>
/// SAE J1979 (OBD-II) engine layered on the OBDX Pro <see cref="Scantool"/> transport —
/// the exact request/response path OBDX ships in their J1979 sample, trimmed to what the
/// FPV gauges need: permissions, scan, connect, HS-CAN session bring-up, Mode 01 PID reads,
/// adapter battery voltage, and a tester-present keepalive.
///
/// All bus access is serialised through <c>_busLock</c> (a single BT serial stream). Run the
/// poll loop off the UI thread.
/// </summary>
public sealed class ObdEngine
{
    public static readonly ObdEngine Instance = new();
    private ObdEngine() { }

    // Standard 11-bit OBD-II CAN addressing.
    public const uint TxPhysical = 0x7E0;
    public const uint RxEcm = 0x7E8;

    private Scantool _tool;
    private readonly SemaphoreSlim _busLock = new(1, 1);
    private CancellationTokenSource _keepAliveCts;

    public bool IsConnected { get; private set; }
    public bool ObdReady { get; private set; }
    public Scantool.Details ToolDetails => _tool?.ToolDetails;

    public event Action<string> Log;
    private void L(string m) => Log?.Invoke(m);

    // ── permissions + connection ──

    // Runtime permissions MUST be granted before `new Scantool()` — its ctor builds the
    // Classic-Bluetooth layer, which reads the adapter scan mode and throws on Android 12+
    // without BLUETOOTH_SCAN.
    public async Task<bool> EnsurePermissionsAsync()
    {
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var bt = await Permissions.RequestAsync<Permissions.Bluetooth>();
                if (bt != PermissionStatus.Granted) { L("Bluetooth permission not granted."); return false; }
                var loc = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                if (loc != PermissionStatus.Granted) L("Location permission not granted (classic discovery may be limited).");
                return true;
            });
        }
        catch (Exception ex) { L("Permission request failed: " + ex.Message); return false; }
    }

    private bool EnsureTool()
    {
        if (_tool != null) return true;
        try { _tool = new Scantool(); return true; }
        catch (Exception ex) { L("Library init failed: " + ex.Message); return false; }
    }

    /// <summary>Scan for OBDX devices. Classic BT is paired-first (the OBDX Pro FT is bonded
    /// in Android settings and bonded devices don't appear in active discovery), then falls
    /// back to discovery.</summary>
    public async Task<(Scantool.Errors err, List<Scantool.OBDXDevice> devices)> ScanAsync(
        IOBDXBase.ConnectionTypeEnum transport = IOBDXBase.ConnectionTypeEnum.ClassicBT, int timeoutMs = 10000)
    {
        var empty = new List<Scantool.OBDXDevice>();
        if (!await EnsurePermissionsAsync()) return (Scantool.Errors.Unknown, empty);
        if (!EnsureTool()) return (Scantool.Errors.Unknown, empty);

        _tool.SelectCommunicationType(transport);
        if (!await _tool.CheckIfPermissionsAreAllowed() && !await _tool.RequestForPermissions())
            return (Scantool.Errors.Unknown, empty);

        bool classic = transport == IOBDXBase.ConnectionTypeEnum.ClassicBT;
        L($"Scanning ({transport}, {(classic ? "paired" : "discovery")})…");
        var r = await _tool.SearchForDevices(classic, timeoutMs, 12);
        if (classic && (r.Item1 != Scantool.Errors.Success || r.Item2.Count == 0))
        {
            L("No paired devices — trying active discovery…");
            r = await _tool.SearchForDevices(false, timeoutMs, 12);
        }
        L($"Scan result: {r.Item1}, {r.Item2.Count} device(s).");
        return (r.Item1, r.Item2);
    }

    public async Task<Scantool.Errors> ConnectAsync(Scantool.OBDXDevice device)
    {
        if (!EnsureTool()) return Scantool.Errors.Unknown;
        L($"Connecting to {device.Name} ({device.UniqueIDString})…");
        var r = await _tool.Connect(device);
        IsConnected = r == Scantool.Errors.Success;
        if (IsConnected)
        {
            var d = _tool.ToolDetails;
            L($"Connected: {d.Name}  FW {d.Firmware}  HW {d.Hardware}  SN {d.UniqueSerial}");
        }
        return r;
    }

    /// <summary>Bring up an OBD-II HS-CAN session (500k, 11-bit 7E0/7E8 flow filter).</summary>
    public async Task<(bool ok, string msg)> InitObdAsync(Scantool.Protocols proto = Scantool.Protocols.HSCAN)
    {
        if (!IsConnected) return (false, "Not connected to a tool.");
        await _busLock.WaitAsync();
        try
        {
            var p = await _tool.SetOBDProtocol(proto);
            if (p.Item1 != Scantool.Errors.Success) return (false, $"SetOBDProtocol failed: {p.Item1}");

            var filter = new CAN_Class.Filter_Struct
            {
                Enabled = 1,
                Type = 1,           // ISO-TP flow control
                RTR = 0,
                Flow = TxPhysical,  // 0x7E0
                Mask = 0x7FF,
                ID = RxEcm,         // 0x7E8
                IsExtended = 0,
            };
            var f = await _tool.CANCommands.SetRxFilterEntire(0, filter);
            if (f.Item1 != Scantool.Errors.Success) return (false, $"SetRxFilterEntire failed: {f.Item1}");

            var e = await _tool.SetOBDEnabledStatus(1);
            if (e.Item1 != Scantool.Errors.Success) return (false, $"SetOBDEnabledStatus failed: {e.Item1}");

            ObdReady = true;
            return (true, "OBD-II session ready.");
        }
        finally { _busLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        StopKeepAlive();
        ObdReady = false;
        if (_tool != null && IsConnected)
        {
            try { await _tool.Disconnect(); } catch { }
        }
        IsConnected = false;
    }

    // ── raw OBD query ──

    /// <summary>Send an OBD request payload (no PCI byte); return the response service payload
    /// (service byte + data), or null on failure / negative response.</summary>
    public async Task<byte[]> QueryAsync(byte[] request, uint txId = TxPhysical, uint rxId = RxEcm,
        int timeoutMs = 1200, int retries = 2, bool takeLock = true)
    {
        if (!ObdReady || _tool == null) return null;
        if (takeLock) await _busLock.WaitAsync();
        try
        {
            var r = await _tool.WriteThenReadNetworkFrame(txId, request, rxId, 0U, timeoutMs, retries);
            if (r.Item1 != Scantool.Errors.Success) return null;
            byte[] raw = r.Item2.RawMsg;
            if (raw == null || raw.Length < 5) return null;
            byte[] payload = new byte[raw.Length - 4];
            Array.Copy(raw, 4, payload, 0, payload.Length);   // strip 4-byte CAN id header
            if (payload.Length >= 3 && payload[0] == 0x7F) return null;   // negative response
            return payload;
        }
        catch { return null; }
        finally { if (takeLock) _busLock.Release(); }
    }

    /// <summary>Mode 01 data bytes for a PID (after [0x41, pid]).</summary>
    public async Task<byte[]> ReadPidDataAsync(byte pid, bool takeLock = true)
    {
        var p = await QueryAsync(new byte[] { 0x01, pid }, takeLock: takeLock);
        if (p == null || p.Length < 2 || p[0] != 0x41 || p[1] != pid) return null;
        byte[] data = new byte[p.Length - 2];
        Array.Copy(p, 2, data, 0, data.Length);
        return data;
    }

    public async Task<double?> ReadPidValueAsync(PidDef def, bool takeLock = true)
    {
        var data = await ReadPidDataAsync(def.Pid, takeLock);
        if (data == null || data.Length < def.Bytes) return null;
        try { return def.Decode(data); } catch { return null; }
    }

    /// <summary>Adapter-measured battery voltage (J1962 pin 16), volts.</summary>
    public async Task<double?> ReadBatteryVoltageAsync()
    {
        if (_tool == null) return null;
        await _busLock.WaitAsync();
        try
        {
            var r = await _tool.ReadBatteryVoltage();
            return r.Item1 == Scantool.Errors.Success ? r.Item2 : (double?)null;
        }
        catch { return null; }
        finally { _busLock.Release(); }
    }

    /// <summary>Discover which Mode 01 PIDs the ECU supports (00/20/40/60/80 bitmaps).</summary>
    public async Task<HashSet<byte>> ReadSupportedPidsAsync()
    {
        var set = new HashSet<byte>();
        byte[] bases = { 0x00, 0x20, 0x40, 0x60, 0x80, 0xA0, 0xC0 };
        await _busLock.WaitAsync();
        try
        {
            foreach (byte b in bases)
            {
                var data = await ReadPidDataAsync(b, takeLock: false);
                if (data == null || data.Length < 4) break;
                uint bits = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                for (int i = 0; i < 32; i++)
                    if ((bits & (1u << (31 - i))) != 0) set.Add((byte)(b + 1 + i));
                if ((bits & 0x01) == 0) break;
            }
        }
        finally { _busLock.Release(); }
        return set;
    }

    // ── tester-present keepalive ──

    public void StartKeepAlive(int periodMs = 2500)
    {
        StopKeepAlive();
        _keepAliveCts = new CancellationTokenSource();
        var token = _keepAliveCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(periodMs, token); } catch { break; }
                if (token.IsCancellationRequested || !ObdReady) continue;
                if (!await _busLock.WaitAsync(50)) continue;
                try { await QueryAsync(new byte[] { 0x01, 0x00 }, takeLock: false, timeoutMs: 800, retries: 1); }
                catch { }
                finally { _busLock.Release(); }
            }
        }, token);
    }

    public void StopKeepAlive()
    {
        try { _keepAliveCts?.Cancel(); } catch { }
        _keepAliveCts = null;
    }
}
