// Horner's rule: a polynomial with the coefficients c[0] + c[1] x + ... is evaluated from the highest
// coefficient down with one multiplication and one addition per step, in double and in float. Written
// for this repository. The float version rounds to binary32 after every operation.
namespace Polypiler.Fixtures.Prototype;

public static class Horner {

  public static double Evaluate(double[] coefficients, double x) {
    double result = 0.0;
    for (int i = coefficients.Length - 1; i >= 0; --i)
      result = result * x + coefficients[i];

    return result;
  }

  public static float EvaluateSingle(float[] coefficients, float x) {
    float result = 0f;
    for (int i = coefficients.Length - 1; i >= 0; --i)
      result = result * x + coefficients[i];

    return result;
  }

  public static double EvaluateMixed(float[] coefficients, double x) {
    double result = 0.0;
    for (int i = coefficients.Length - 1; i >= 0; --i)
      result = result * x + coefficients[i];

    return result;
  }

}
