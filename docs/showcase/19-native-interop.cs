// Showcase: Native Interop (P/Invoke)
// Category: I/O & Interop
// Summary: Sequential StructLayout ABI records and DllImport native library declarations.
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct NativePoint {
  public int X;
  public int Y;
}

public static class NativeInteropShowcase {
  [DllImport("geom")]
  private static extern int geom_norm(NativePoint point);

  public static int Measure(int x, int y)
    => geom_norm(new NativePoint { X = x, Y = y });
}
