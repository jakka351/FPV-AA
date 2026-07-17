using Android.App;
using Android.Content;
using AndroidX.Car.App;
using AndroidX.Car.App.Validation;

namespace FpvGauges.Car;

/// <summary>
/// Android Auto entry point. A navigation-category car app so it is allowed to draw the FPV
/// gauges straight onto the head-unit surface. The [Service]/[IntentFilter] attributes emit the
/// manifest &lt;service&gt; the Car App host looks for.
/// </summary>
[Service(Exported = true, Label = "FPV GT-F Gauges")]
[IntentFilter(new[] { "androidx.car.app.CarAppService" },
    Categories = new[] { "androidx.car.app.category.NAVIGATION" })]
public sealed class FpvCarAppService : CarAppService
{
    // Sideloaded/personal build: allow any host. A Play-published build would use the
    // allow-listed hosts validator instead.
    public override HostValidator CreateHostValidator() => HostValidator.AllowAllHostsValidator;

    public override Session OnCreateSession() => new FpvSession();
}
