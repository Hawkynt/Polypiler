namespace Polypiler.Core.IR;

/// <summary>
/// The conversion between degrees and radians as .NET 10 computes it (MIT): the product with the
/// rounded pi, or with 180, divided by the other constant - two operations, each rounded.
/// </summary>
public static partial class FloatOperations {

  /// <summary><c>(degrees * pi) / 180</c>, pi rounded to <c>double</c>.</summary>
  /// <param name="degrees">The angle in degrees.</param>
  /// <returns>The angle in radians.</returns>
  public static double DegreesToRadians(double degrees) => degrees * 3.141592653589793 / 180.0;

  /// <summary><c>(degrees * pi) / 180</c> of <c>float</c>, pi rounded to <c>float</c>.</summary>
  /// <param name="degrees">The angle in degrees.</param>
  /// <returns>The angle in radians.</returns>
  public static float DegreesToRadiansF(float degrees) => degrees * 3.14159274f / 180.0f;

  /// <summary><c>(radians * 180) / pi</c>, pi rounded to <c>double</c>.</summary>
  /// <param name="radians">The angle in radians.</param>
  /// <returns>The angle in degrees.</returns>
  public static double RadiansToDegrees(double radians) => radians * 180.0 / 3.141592653589793;

  /// <summary><c>(radians * 180) / pi</c> of <c>float</c>, pi rounded to <c>float</c>.</summary>
  /// <param name="radians">The angle in radians.</param>
  /// <returns>The angle in degrees.</returns>
  public static float RadiansToDegreesF(float radians) => radians * 180.0f / 3.14159274f;

}
