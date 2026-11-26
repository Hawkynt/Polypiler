// Showcase: Spans, Bit Permutations & Unsafe Pointers
// Category: Low-Level & Performance
// Summary: Zero-copy ReadOnlySpan<int> slicing, bounded unsafe fixed pointers, and BMI2 bit extraction.
using System;
using System.Runtime.Intrinsics.X86;

public static class LowLevelShowcase {
  public static uint ExtractBits(uint source, uint mask)
    => Bmi2.ParallelBitExtract(source, mask);

  public static unsafe int SumSlice(int[] data, int offset, int length) {
    ReadOnlySpan<int> view = data.AsSpan(offset, length);
    int total = 0;
    fixed (int* ptr = view) {
      for (int i = 0; i < view.Length; ++i)
        total += ptr[i];
    }
    return total;
  }
}
