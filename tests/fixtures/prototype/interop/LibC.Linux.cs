// Native calls into the C library of Linux (glibc, "libc.so.6") through P/Invoke, for #1332. The functions are
// those of ISO C: strlen (7.24.6.3), abs and div (7.22.6.1, 7.22.6.2), memset (7.24.6.1) and strtol (7.22.1.4).
// C's long is 64 bits on Linux, so strtol returns a long here; LibC.Windows.cs is the same class for msvcrt.
// Written for this repository.
using System;
using System.Runtime.InteropServices;

namespace Polypiler.Fixtures.Prototype.Interop.Linux;

[StructLayout(LayoutKind.Sequential)]
public struct DivT {
  public int Quot;
  public int Rem;
}

public static partial class LibC {

  [DllImport("libc.so.6", EntryPoint = "strlen", CharSet = CharSet.Ansi)]
  public static extern nuint StrLen(string s);

  [LibraryImport("libc.so.6", EntryPoint = "strlen", StringMarshalling = StringMarshalling.Utf8)]
  public static partial nuint StrLenUtf8(string s);

  [DllImport("libc.so.6", EntryPoint = "abs")]
  public static extern int Abs(int x);

  [DllImport("libc.so.6", EntryPoint = "div")]
  public static extern DivT Div(int numerator, int denominator);

  [DllImport("libc.so.6", EntryPoint = "memset")]
  public static extern void MemSet(byte[] buffer, int value, nuint count);

  [DllImport("libc.so.6", EntryPoint = "strtol", CharSet = CharSet.Ansi)]
  public static extern long StrToL(string text, out IntPtr end, int radix);

  public static int Length(string s) => (int)StrLen(s) + 1000 * (int)StrLenUtf8(s);

  public static int Quotient(int a, int b) => Div(a, b).Quot * 100 + Div(a, b).Rem;

  public static byte[] Filled(int count) {
    var buffer = new byte[4];
    MemSet(buffer, 0xAB, (nuint)count);
    return buffer;
  }

  public static long Parsed(string text, int radix) => StrToL(text, out _, radix) + Abs(-1);

}
