using FpvGauges.Pages;

namespace FpvGauges;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState activationState)
    {
        // Branded FPV splash first; it navigates on to the dashboard.
        return new Window(new NavigationPage(new SplashPage()));
    }
}
