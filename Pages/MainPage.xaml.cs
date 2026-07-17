using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FpvGauges.Gauges;
using FpvGauges.Obd;
using FpvGauges.Rendering;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using OBDXMAUI;

namespace FpvGauges.Pages;

public partial class MainPage : ContentPage
{
    private readonly StringBuilder _log = new();
    private IDispatcherTimer _timer;
    private bool _busy;

    public MainPage()
    {
        InitializeComponent();
        Preview.Drawable = new GaugePreviewDrawable();
        GaugeDataService.Instance.Log += OnLog;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);   // ~30 fps preview + status refresh
        _timer.Tick += (_, _) =>
        {
            Preview.Invalidate();
            var s = VehicleState.Instance;
            StatusLabel.Text = s.Status;
            ToolLabel.Text = s.Connected && !string.IsNullOrEmpty(s.ToolName) ? $"Adapter: {s.ToolName}" : "";
            ConnectButton.Text = GaugeDataService.Instance.IsConnected ? "Disconnect" : "Scan & Connect";
            DemoButton.Text = DemoDriver.Instance.Running ? "Stop Demo" : "Demo";
        };
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _timer = null;
    }

    private void OnLog(string msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _log.Insert(0, msg + "\n");
            if (_log.Length > 6000) _log.Length = 6000;
            LogView.Text = _log.ToString();
        });
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        if (_busy) return;

        if (GaugeDataService.Instance.IsConnected)
        {
            await GaugeDataService.Instance.StopAsync();
            return;
        }

        _busy = true;
        ConnectButton.IsEnabled = false;
        try
        {
            if (DemoDriver.Instance.Running) DemoDriver.Instance.Stop();

            var (err, devices) = await GaugeDataService.Instance.ScanAsync();
            if (err != Scantool.Errors.Success || devices == null || devices.Count == 0)
            {
                await DisplayAlert("No adapter",
                    "No OBDX Pro found. Pair the OBDX Pro FT in Android Bluetooth settings first, then try again.",
                    "OK");
                return;
            }

            Scantool.OBDXDevice device;
            if (devices.Count == 1)
            {
                device = devices[0];
            }
            else
            {
                var names = devices.Select(d => string.IsNullOrWhiteSpace(d.Name) ? d.UniqueIDString : d.Name).ToArray();
                var choice = await DisplayActionSheet("Select OBDX adapter", "Cancel", null, names);
                if (choice == null || choice == "Cancel") return;
                int idx = Array.IndexOf(names, choice);
                device = idx >= 0 ? devices[idx] : devices[0];
            }

            var ok = await GaugeDataService.Instance.ConnectAndStartAsync(device);
            if (!ok)
                await DisplayAlert("Connection", VehicleState.Instance.Status, "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            _busy = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private void OnDemoClicked(object sender, EventArgs e)
    {
        if (DemoDriver.Instance.Running) DemoDriver.Instance.Stop();
        else DemoDriver.Instance.Start();
    }

    private async void OnConfigureClicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage());
    }
}
