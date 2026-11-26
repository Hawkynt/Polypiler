// Showcase: Hardware SIMD Vectorization (Boundary)
// Category: Unsupported Boundaries
// Summary: System.Numerics.Vector<T> explicit SIMD; explicitly refused until SIMD IR lowering lands.
using System.Numerics;

public static class SimdVectorizationBoundary {
  public static int SumVector(int[] data) {
    var acc = Vector<int>.Zero;
    for (int i = 0; i <= data.Length - Vector<int>.Count; i += Vector<int>.Count)
      acc += new Vector<int>(data, i);
    return Vector.Sum(acc);
  }
}
