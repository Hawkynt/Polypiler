// The great-circle distance by the haversine formula: with the latitudes and longitudes in degrees,
// a = sin^2(dlat / 2) + cos(lat1) cos(lat2) sin^2(dlon / 2), and the distance is 2 r atan2(sqrt(a), sqrt(1 - a)).
// Written for this repository.
namespace Polypiler.Fixtures.Prototype;

public static class Haversine {

  public static double Distance(double latitude1, double longitude1, double latitude2, double longitude2, double radius) {
    double phi1 = double.DegreesToRadians(latitude1);
    double phi2 = double.DegreesToRadians(latitude2);
    double halfLatitude = System.Math.Sin((phi2 - phi1) / 2.0);
    double halfLongitude = System.Math.Sin(double.DegreesToRadians(longitude2 - longitude1) / 2.0);
    double a = halfLatitude * halfLatitude + System.Math.Cos(phi1) * System.Math.Cos(phi2) * halfLongitude * halfLongitude;
    return 2.0 * radius * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1.0 - a));
  }

  public static float DistanceSingle(float latitude1, float longitude1, float latitude2, float longitude2, float radius) {
    float phi1 = float.DegreesToRadians(latitude1);
    float phi2 = float.DegreesToRadians(latitude2);
    float halfLatitude = System.MathF.Sin((phi2 - phi1) / 2.0f);
    float halfLongitude = System.MathF.Sin(float.DegreesToRadians(longitude2 - longitude1) / 2.0f);
    float a = halfLatitude * halfLatitude + System.MathF.Cos(phi1) * System.MathF.Cos(phi2) * halfLongitude * halfLongitude;
    return 2.0f * radius * System.MathF.Atan2(System.MathF.Sqrt(a), System.MathF.Sqrt(1.0f - a));
  }

}
