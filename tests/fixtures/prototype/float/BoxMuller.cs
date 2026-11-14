// The Box-Muller transform: two uniform numbers u1 in (0, 1] and u2 in [0, 1) become two standard
// normal ones, sqrt(-2 ln u1) cos(2 pi u2) and sqrt(-2 ln u1) sin(2 pi u2). Written for this
// repository; the uniform numbers are a fixed sequence the caller passes in.
namespace Polypiler.Fixtures.Prototype;

public static class BoxMuller {

  public static void Normals(double[] uniform, double[] normal) {
    for (int i = 0; i + 1 < uniform.Length; i += 2) {
      double radius = System.Math.Sqrt(-2.0 * System.Math.Log(uniform[i]));
      double angle = 2.0 * System.Math.PI * uniform[i + 1];
      normal[i] = radius * System.Math.Cos(angle);
      normal[i + 1] = radius * System.Math.Sin(angle);
    }
  }

  public static void NormalsSingle(float[] uniform, float[] normal) {
    for (int i = 0; i + 1 < uniform.Length; i += 2) {
      float radius = System.MathF.Sqrt(-2.0f * System.MathF.Log(uniform[i]));
      float angle = 2.0f * System.MathF.PI * uniform[i + 1];
      normal[i] = radius * System.MathF.Cos(angle);
      normal[i + 1] = radius * System.MathF.Sin(angle);
    }
  }

}
