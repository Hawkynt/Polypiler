// A finite impulse response filter with three taps: output[i] = b0 input[i] + b1 input[i-1] +
// b2 input[i-2], with the samples before the start taken as zero. Written for this repository, in
// double and in float; the float version rounds to binary32 after every operation.
namespace Polypiler.Fixtures.Prototype;

public static class Fir {

  public static void Filter(double[] input, double[] output, double b0, double b1, double b2) {
    for (int i = 0; i < input.Length; ++i) {
      double sum = b0 * input[i];
      if (i >= 1)
        sum += b1 * input[i - 1];

      if (i >= 2)
        sum += b2 * input[i - 2];

      output[i] = sum;
    }
  }

  public static void FilterSingle(float[] input, float[] output, float b0, float b1, float b2) {
    for (int i = 0; i < input.Length; ++i) {
      float sum = b0 * input[i];
      if (i >= 1)
        sum += b1 * input[i - 1];

      if (i >= 2)
        sum += b2 * input[i - 2];

      output[i] = sum;
    }
  }

  public static float[] MovingAverage(float[] input) {
    float[] output = new float[input.Length];
    FilterSingle(input, output, 1f / 3f, 1f / 3f, 1f / 3f);
    return output;
  }

  public static double Energy(double[] samples) {
    double energy = 0.0;
    for (int i = 0; i < samples.Length; ++i)
      energy += samples[i] * samples[i];

    return energy;
  }

}
