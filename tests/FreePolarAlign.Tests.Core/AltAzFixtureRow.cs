using System.Text.Json.Serialization;

namespace FreePolarAlign.Tests.Core;

public sealed class AltAzFixtureRow
{
    [JsonPropertyName("site")]
    public string Site { get; set; } = "";

    [JsonPropertyName("lat_deg")]
    public double LatDeg { get; set; }

    [JsonPropertyName("lon_deg")]
    public double LonDeg { get; set; }

    [JsonPropertyName("height_m")]
    public double HeightM { get; set; }

    [JsonPropertyName("utc")]
    public string Utc { get; set; } = "";

    [JsonPropertyName("ra_icrs_deg")]
    public double RaIcrsDeg { get; set; }

    [JsonPropertyName("dec_icrs_deg")]
    public double DecIcrsDeg { get; set; }

    [JsonPropertyName("az_vacuum_deg")]
    public double AzVacuumDeg { get; set; }

    [JsonPropertyName("alt_vacuum_deg")]
    public double AltVacuumDeg { get; set; }

    [JsonPropertyName("az_refracted_deg")]
    public double AzRefractedDeg { get; set; }

    [JsonPropertyName("alt_refracted_deg")]
    public double AltRefractedDeg { get; set; }

    [JsonPropertyName("pressure_hpa")]
    public double PressureHpa { get; set; }

    [JsonPropertyName("temperature_c")]
    public double TemperatureC { get; set; }

    [JsonPropertyName("relative_humidity")]
    public double RelativeHumidity { get; set; }

    [JsonPropertyName("wavelength_um")]
    public double WavelengthUm { get; set; }
}

public sealed class AltAzFixtureFile
{
    [JsonPropertyName("generator")]
    public string Generator { get; set; } = "";

    [JsonPropertyName("astropy_version")]
    public string AstropyVersion { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("rows")]
    public List<AltAzFixtureRow> Rows { get; set; } = new();
}
