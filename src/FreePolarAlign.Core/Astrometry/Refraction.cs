namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// Atmospheric conditions at the observing site. <see cref="Vacuum"/> (pressure
/// zero) disables refraction entirely, matching the fixture's *_vacuum_deg columns.
/// </summary>
public sealed record AtmosphericConditions(double PressureHPa, double TemperatureCelsius, double RelativeHumidity, double WavelengthMicrons)
{
    public static readonly AtmosphericConditions Vacuum = new(0.0, 10.0, 0.0, 0.55);
}

/// <summary>
/// Optical/IR atmospheric refraction, the "AT + B tan^3(Z)" model of Green
/// (1985) as used by IAU SOFA/ERFA (<c>iauRefco</c>/<c>eraRefco</c>) and hence
/// by Astropy's AltAz frame. Transcribed from ERFA's <c>eraRefco</c>.
/// </summary>
internal static class Refraction
{
    /// <summary>Refraction coefficients A and B (radians), from pressure/temperature/humidity/wavelength.</summary>
    public static (double RefA, double RefB) Coefficients(AtmosphericConditions atmosphere)
    {
        double t = Math.Clamp(atmosphere.TemperatureCelsius, -150.0, 200.0);
        double p = Math.Clamp(atmosphere.PressureHPa, 0.0, 10000.0);
        double r = Math.Clamp(atmosphere.RelativeHumidity, 0.0, 1.0);
        double w = Math.Clamp(atmosphere.WavelengthMicrons, 0.1, 1.0e6);

        bool optical = w <= 100.0;

        double pw;
        if (p > 0.0)
        {
            double ps = Math.Pow(10.0, (0.7859 + 0.03477 * t) / (1.0 + 0.00412 * t)) * (1.0 + p * (4.5e-6 + 6e-10 * t * t));
            pw = r * ps / (1.0 - (1.0 - r) * ps / p);
        }
        else
        {
            pw = 0.0;
        }

        double tk = t + 273.15;
        double gamma;
        if (optical)
        {
            double wlsq = w * w;
            gamma = ((77.53484e-6 + (4.39108e-7 + 3.666e-9 / wlsq) / wlsq) * p - 11.2684e-6 * pw) / tk;
        }
        else
        {
            gamma = (77.6890e-6 * p - (6.3938e-6 - 0.375463 / tk) * pw) / tk;
        }

        double beta = 4.4474e-6 * tk;
        if (!optical)
        {
            beta -= 0.0074 * pw * beta;
        }

        double refA = gamma * (1.0 - beta);
        double refB = -gamma * (beta - gamma / 2.0);
        return (refA, refB);
    }
}
