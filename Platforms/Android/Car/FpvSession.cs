using Android.Content;
using AndroidX.Car.App;

namespace FpvGauges.Car;

/// <summary>The car session — creates the single gauge screen.</summary>
public sealed class FpvSession : Session
{
    public override Screen OnCreateScreen(Intent intent) => new GaugeScreen(CarContext);
}
