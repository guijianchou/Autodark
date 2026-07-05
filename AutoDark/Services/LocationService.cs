using AutoDark.Core.Services;
using Windows.Devices.Geolocation;

namespace AutoDark.Services;

public sealed record LocationAccessResult(bool Success, string Message);

public sealed record LocationResult(bool Success, string Latitude, string Longitude, string Message);

public sealed class LocationService
{
    public async Task<LocationAccessResult> RequestAccessAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                return new LocationAccessResult(false, "Location access was not granted.");
            }

            return new LocationAccessResult(true, "Location access is enabled.");
        }
        catch (Exception ex)
        {
            return new LocationAccessResult(false, $"Location access failed: {ex.Message}");
        }
    }

    public async Task<LocationResult> TryGetLocationAsync()
    {
        try
        {
            var access = await RequestAccessAsync();
            if (!access.Success)
            {
                return new LocationResult(false, string.Empty, string.Empty, access.Message);
            }

            var geolocator = new Geolocator { DesiredAccuracyInMeters = 1000 };
            // The WinRT timeout parameter is not reliably honored on desktop;
            // a client-side watchdog keeps the caller's UI from hanging.
            using var cancellation = new CancellationTokenSource();
            var positionTask = geolocator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(10),
                timeout: TimeSpan.FromSeconds(10)).AsTask(cancellation.Token);
            var winner = await Task.WhenAny(positionTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (winner != positionTask)
            {
                cancellation.Cancel();
                return new LocationResult(false, string.Empty, string.Empty, "Location request timed out.");
            }

            var position = await positionTask;

            var point = position.Coordinate.Point.Position;
            return new LocationResult(
                true,
                CoordinateFormat.Format(point.Latitude),
                CoordinateFormat.Format(point.Longitude),
                "Location synchronized.");
        }
        catch (Exception ex)
        {
            return new LocationResult(false, string.Empty, string.Empty, $"Location sync failed: {ex.Message}");
        }
    }
}
