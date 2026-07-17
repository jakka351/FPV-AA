using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace FpvGauges.Pages;

public partial class SplashPage : ContentPage
{
    private bool _started;

    public SplashPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        await RunIntroAsync();
    }

    private async Task RunIntroAsync()
    {
        Splash.Scale = 1.06;
        await Task.WhenAll(
            Splash.FadeTo(1, 700, Easing.CubicOut),
            Splash.ScaleTo(1.0, 3000, Easing.CubicOut));
        await Title.FadeTo(1, 350);
        await Status.FadeTo(1, 250);

        string[] steps = { "Preparing gauges…", "Warming up transports…", "Ready" };
        foreach (var t in steps)
        {
            Status.Text = t;
            await Task.Delay(500);
        }

        var window = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0] : null;
        if (window != null)
            window.Page = new NavigationPage(new MainPage());
    }
}
