using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FpvGauges.Gauges;
using FpvGauges.Obd;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace FpvGauges.Pages;

public partial class SettingsPage : ContentPage
{
    // Sources offered for dial pods (the scope pod is fixed to g-force).
    private static readonly GaugeSourceKind[] DialSources =
    {
        GaugeSourceKind.CoolantTemp, GaugeSourceKind.OilTemp, GaugeSourceKind.IntakeAirTemp,
        GaugeSourceKind.Boost, GaugeSourceKind.ManifoldPressure, GaugeSourceKind.BatteryVoltage,
        GaugeSourceKind.EngineRpm, GaugeSourceKind.VehicleSpeed, GaugeSourceKind.EngineLoad,
        GaugeSourceKind.ThrottlePosition, GaugeSourceKind.FuelLevel, GaugeSourceKind.CustomPid,
    };

    private readonly List<PodEditor> _editors = new();

    public SettingsPage()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        Container.Children.Clear();
        _editors.Clear();

        foreach (var cfg in GaugeConfigStore.Load())
            Container.Children.Add(BuildPodCard(cfg));

        Container.Children.Add(new Label
        {
            Text = "Min/Max default to the numbers printed on the FPV face so the needle lines up. " +
                   "Changing the data source is what re-purposes a pod; changing Min/Max will make the " +
                   "needle no longer match the printed scale.",
            TextColor = Color.FromArgb("#9AA0A6"),
            FontSize = 11,
            Margin = new Thickness(2, 4, 2, 0),
        });
    }

    private View BuildPodCard(GaugePodConfig cfg)
    {
        bool scope = Face.Geometry[cfg.Pod].Kind == PodKind.Scope;

        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(new Label
        {
            Text = $"{cfg.Label}  ·  {PodPositionText(cfg.Pod)}",
            TextColor = Color.FromArgb("#FF6A1A"),
            FontFamily = "OpenSansSemibold",
            FontSize = 15,
        });

        var editor = new PodEditor { Pod = cfg.Pod, IsScope = scope };

        if (scope)
        {
            stack.Children.Add(Muted("Source: phone accelerometer (g-force scope)"));
            editor.MaxEntry = LabelledEntry(stack, "Full-scale (g)", cfg.Max.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            var picker = new Picker { TextColor = Colors.White, TitleColor = Color.FromArgb("#9AA0A6") };
            foreach (var s in DialSources) picker.Items.Add(Friendly(s));
            int sel = Array.IndexOf(DialSources, cfg.Source);
            picker.SelectedIndex = sel >= 0 ? sel : 0;
            stack.Children.Add(RowLabel("Data source"));
            stack.Children.Add(picker);
            editor.SourcePicker = picker;

            editor.CustomPidEntry = LabelledEntry(stack, "Custom PID (hex, e.g. 0C)",
                cfg.CustomPid == 0 ? "" : cfg.CustomPid.ToString("X2"));

            var grid = new Grid { ColumnDefinitions = Cols(3), ColumnSpacing = 8 };
            editor.MinEntry = BareEntry(cfg.Min.ToString(CultureInfo.InvariantCulture));
            editor.MaxEntry = BareEntry(cfg.Max.ToString(CultureInfo.InvariantCulture));
            editor.WarnEntry = BareEntry(cfg.WarnStart.ToString(CultureInfo.InvariantCulture));
            grid.Add(Field("Min", editor.MinEntry), 0, 0);
            grid.Add(Field("Max", editor.MaxEntry), 1, 0);
            grid.Add(Field("Warn ≥", editor.WarnEntry), 2, 0);
            stack.Children.Add(grid);
        }

        _editors.Add(editor);

        return new Border
        {
            Stroke = Color.FromArgb("#22FFFFFF"),
            StrokeThickness = 1,
            BackgroundColor = Color.FromArgb("#12161F"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = 12,
            Content = stack,
        };
    }

    private void OnSaveClicked(object sender, EventArgs e)
    {
        var list = new List<GaugePodConfig>();
        foreach (var ed in _editors)
        {
            var cfg = GaugeConfigStore.For(ed.Pod);
            var next = new GaugePodConfig
            {
                Pod = cfg.Pod,
                Source = cfg.Source,
                CustomPid = cfg.CustomPid,
                Min = cfg.Min,
                Max = cfg.Max,
                WarnStart = cfg.WarnStart,
                Label = cfg.Label,
                Unit = cfg.Unit,
            };

            if (ed.IsScope)
            {
                next.Source = GaugeSourceKind.GForce;
                next.Max = ParseOr(ed.MaxEntry?.Text, cfg.Max);
                next.Min = 0;
            }
            else
            {
                if (ed.SourcePicker != null && ed.SourcePicker.SelectedIndex >= 0)
                    next.Source = DialSources[ed.SourcePicker.SelectedIndex];
                next.CustomPid = ParseHex(ed.CustomPidEntry?.Text, cfg.CustomPid);
                next.Min = ParseOr(ed.MinEntry?.Text, cfg.Min);
                next.Max = ParseOr(ed.MaxEntry?.Text, cfg.Max);
                next.WarnStart = ParseOr(ed.WarnEntry?.Text, cfg.WarnStart);
            }
            list.Add(next);
        }

        GaugeConfigStore.Save(list);
        DisplayAlert("Saved", "Gauge configuration updated. It applies live on the phone and head unit.", "OK");
    }

    private void OnResetClicked(object sender, EventArgs e)
    {
        GaugeConfigStore.ResetToFpvDefault();
        Build();
    }

    // ── small UI helpers ──

    private sealed class PodEditor
    {
        public PodId Pod;
        public bool IsScope;
        public Picker SourcePicker;
        public Entry CustomPidEntry, MinEntry, MaxEntry, WarnEntry;
    }

    private static ColumnDefinitionCollection Cols(int n)
    {
        var c = new ColumnDefinitionCollection();
        for (int i = 0; i < n; i++) c.Add(new ColumnDefinition { Width = GridLength.Star });
        return c;
    }

    private static Label RowLabel(string t) => new() { Text = t, TextColor = Color.FromArgb("#C8CDD4"), FontSize = 12 };
    private static Label Muted(string t) => new() { Text = t, TextColor = Color.FromArgb("#9AA0A6"), FontSize = 12 };

    private static View Field(string caption, Entry entry)
    {
        var s = new VerticalStackLayout { Spacing = 2 };
        s.Children.Add(new Label { Text = caption, TextColor = Color.FromArgb("#9AA0A6"), FontSize = 11 });
        s.Children.Add(entry);
        return s;
    }

    private static Entry BareEntry(string text) => new()
    {
        Text = text,
        Keyboard = Keyboard.Numeric,
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#0A0D14"),
    };

    private static Entry LabelledEntry(Layout parent, string caption, string text)
    {
        parent.Children.Add(new Label { Text = caption, TextColor = Color.FromArgb("#9AA0A6"), FontSize = 11 });
        var e = new Entry { Text = text, TextColor = Colors.White, BackgroundColor = Color.FromArgb("#0A0D14") };
        parent.Children.Add(e);
        return e;
    }

    private static double ParseOr(string s, double fallback)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static byte ParseHex(string s, byte fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static string PodPositionText(PodId pod) => pod switch
    {
        PodId.Engine => "left dial",
        PodId.Boost => "centre dial",
        PodId.Voltage => "right dial",
        PodId.GForce => "bottom scope",
        _ => "",
    };

    private static string Friendly(GaugeSourceKind k) => k switch
    {
        GaugeSourceKind.CoolantTemp => "Coolant Temp (°C)",
        GaugeSourceKind.OilTemp => "Oil Temp (°C)",
        GaugeSourceKind.IntakeAirTemp => "Intake Air Temp (°C)",
        GaugeSourceKind.Boost => "Boost (bar)",
        GaugeSourceKind.ManifoldPressure => "Manifold Pressure (kPa)",
        GaugeSourceKind.BatteryVoltage => "Battery Voltage (V)",
        GaugeSourceKind.EngineRpm => "Engine RPM",
        GaugeSourceKind.VehicleSpeed => "Vehicle Speed (km/h)",
        GaugeSourceKind.EngineLoad => "Engine Load (%)",
        GaugeSourceKind.ThrottlePosition => "Throttle (%)",
        GaugeSourceKind.FuelLevel => "Fuel Level (%)",
        GaugeSourceKind.CustomPid => "Custom PID…",
        GaugeSourceKind.GForce => "G-Force",
        _ => k.ToString(),
    };
}
