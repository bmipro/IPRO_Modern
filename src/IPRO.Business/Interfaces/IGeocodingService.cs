namespace IPRO.Business.Interfaces;

public record GeocodeResult(double Latitude, double Longitude, double South, double North, double West, double East);

public interface IGeocodingService
{
    Task<GeocodeResult?> GeocodeAsync(string address);
}
