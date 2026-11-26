// Showcase: Standard Output (Stdio)
// Category: I/O & Interop
// Summary: Console.WriteLine with string literals and integer interpolation.
using System;

public static class StdioShowcase {
  public static int Run() {
    int sum = 0;
    for (int i = 1; i <= 3; ++i) {
      sum += i * i;
      Console.WriteLine($"step {i}: sum={sum}");
    }
    return sum;
  }
}
