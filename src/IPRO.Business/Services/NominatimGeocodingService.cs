using System.Globalization;
using System.Text.Json;
using IPRO.Business.Interfaces;
using Microsoft.Extensions.Logging;

namespace IPRO.Business.Services;

// Uses OpenStreetMap's free Nominatim geocoding API to convert an address into coordinates once,
// at block-save time -- never on a public page view. Nominatim's usage policy caps requests at
// roughly 1/sec and requires an identifying User-Agent; both are satisfied by only geocoding on an
// explicit agent save/preview action (not per visitor) and setting a fixed, identifying header below.
public class NominatimGeocodingService : IGeocodingService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ILogger<NominatimGeocodingService> _logger;

    static NominatimGeocodingService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("IPRO-Modern-WebsiteBuilder/1.0 (support@iproadvisers.com)");
    }

    public NominatimGeocodingService(ILogger<NominatimGeocodingService> logger)
    {
        _logger = logger;
    }

    public async Task<GeocodeResult?> GeocodeAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        try
        {
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&format=json&limit=1";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;

            var first = doc.RootElement[0];
            var lat = double.Parse(first.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
            var lon = double.Parse(first.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
            var bbox = first.GetProperty("boundingbox");
            var south = double.Parse(bbox[0].GetString()!, CultureInfo.InvariantCulture);
            var north = double.Parse(bbox[1].GetString()!, CultureInfo.InvariantCulture);
            var west = double.Parse(bbox[2].GetString()!, CultureInfo.InvariantCulture);
            var east = double.Parse(bbox[3].GetString()!, CultureInfo.InvariantCulture);
            return new GeocodeResult(lat, lon, south, north, west, east);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geocoding failed for address {Address}", address);
            return null;
        }
    }
}
