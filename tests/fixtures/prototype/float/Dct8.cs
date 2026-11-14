// The 8-point discrete cosine transform of type II: output[k] = sum over n of input[n] cos(pi (2n + 1) k / 16),
// the k-th coefficient scaled by sqrt(1/8) for k = 0 and sqrt(2/8) otherwise. Written for this
// repository; every cosine is computed, none is taken from a table.
namespace Polypiler.Fixtures.Prototype;

public static class Dct8 {

  public static void Transform(double[] input, double[] output) {
    for (int k = 0; k < 8; ++k) {
      double sum = 0.0;
      for (int n = 0; n < 8; ++n)
        sum += input[n] * System.Math.Cos(System.Math.PI * (2 * n + 1) * k / 16.0);

      output[k] = sum * (k == 0 ? System.Math.Sqrt(0.125) : System.Math.Sqrt(0.25));
    }
  }

  public static void TransformSingle(float[] input, float[] output) {
    for (int k = 0; k < 8; ++k) {
      float sum = 0.0f;
      for (int n = 0; n < 8; ++n)
        sum += input[n] * System.MathF.Cos(System.MathF.PI * (2 * n + 1) * k / 16.0f);

      output[k] = sum * (k == 0 ? System.MathF.Sqrt(0.125f) : System.MathF.Sqrt(0.25f));
    }
  }

}
