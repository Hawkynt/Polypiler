using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using NUnit.Framework;
using Polypiler.Core.Frontends.Msil;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Msil;

[TestFixture]
public sealed class MsilFrontendTests {

  // ------------------------------------------------------------------ PE32 / PE32+ & Debug / Release

  [TestCase(OptimizationLevel.Debug, Platform.X86)]
  [TestCase(OptimizationLevel.Release, Platform.X86)]
  [TestCase(OptimizationLevel.Debug, Platform.X64)]
  [TestCase(OptimizationLevel.Release, Platform.X64)]
  public void ArithmeticAndControlFlow_DebugRelease_Pe32AndPe32Plus(OptimizationLevel opt, Platform platform) {
    var pe = MsilTestHelpers.CompileCSharp("""
      namespace Demo;
      public static class MathOps {
        public static int Add(int a, int b) => a + b;
        public static int Sub(int a, int b) => a - b;
        public static int Mul(int a, int b) => a * b;
        public static int Div(int a, int b) => a / b;
        public static int Rem(int a, int b) => a % b;
        public static int Neg(int a) => -a;
        public static int Bitwise(int a, int b) => (~a & b) | (a ^ b);
        public static int Shifts(int a, int n) => (a << n) + (a >> n);
        public static uint UnsignedOps(uint a, uint b, int s) => (a / b) + (a % b) + (a >> s);
        public static ulong Unsigned64(ulong a, ulong b, int s) => (a / b) + (a % b) + (a >> s) + (a << s);
        public static long Signed64(long a, long b) => (a + b) * (a - b) / (b == 0 ? 1 : b);
        public static int SumLoop(int n) {
          var s = 0;
          for (var i = 1; i <= n; ++i)
            s += i;
          return s;
        }
        public static int WhileLoop(int n) {
          var s = 0;
          while (n > 0) {
            if (n == 3) { n--; continue; }
            if (n == 1) break;
            s += n;
            n--;
          }
          return s;
        }
        public static int DoWhileLoop(int n) {
          var s = 0;
          do {
            s += n;
            n--;
          } while (n > 0);
          return s;
        }
        public static int Ternary(bool c, int a, int b) => c ? a : b;
        public static bool ShortCircuit(int a, int b) => a > 0 && b > 0 || a == 42;
      }
      """, opt, platform);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "Add", [3, 4]), Is.EqualTo(7));
    Assert.That(IrInterpreter.Call(module, "Sub", [10, 3]), Is.EqualTo(7));
    Assert.That(IrInterpreter.Call(module, "Mul", [6, 7]), Is.EqualTo(42));
    Assert.That(IrInterpreter.Call(module, "Div", [20, 3]), Is.EqualTo(6));
    Assert.That(IrInterpreter.Call(module, "Rem", [20, 3]), Is.EqualTo(2));
    Assert.That(IrInterpreter.Call(module, "Neg", [5]), Is.EqualTo(-5));
    Assert.That(IrInterpreter.Call(module, "Bitwise", [0x0F, 0x33]), Is.EqualTo((~0x0F & 0x33) | (0x0F ^ 0x33)));
    Assert.That(IrInterpreter.Call(module, "Shifts", [-16, 2]), Is.EqualTo((-16 << 2) + (-16 >> 2)));
    Assert.That(IrInterpreter.Call(module, "UnsignedOps", [0xF0000000u, 3u, 4]), Is.EqualTo((0xF0000000u / 3u) + (0xF0000000u % 3u) + (0xF0000000u >> 4)));
    Assert.That(IrInterpreter.Call(module, "Unsigned64", [0xF000000000000000UL, 5UL, 4]), Is.EqualTo((0xF000000000000000UL / 5UL) + (0xF000000000000000UL % 5UL) + (0xF000000000000000UL >> 4) + (0xF000000000000000UL << 4)));
    Assert.That(IrInterpreter.Call(module, "Signed64", [10L, 4L]), Is.EqualTo((10L + 4L) * (10L - 4L) / 4L));
    Assert.That(IrInterpreter.Call(module, "SumLoop", [10]), Is.EqualTo(55));
    Assert.That(IrInterpreter.Call(module, "WhileLoop", [5]), Is.EqualTo(5 + 4 + 2));
    Assert.That(IrInterpreter.Call(module, "DoWhileLoop", [4]), Is.EqualTo(10));
    Assert.That(IrInterpreter.Call(module, "Ternary", [true, 11, 22]), Is.EqualTo(11));
    Assert.That(IrInterpreter.Call(module, "Ternary", [false, 11, 22]), Is.EqualTo(22));
    Assert.That(IrInterpreter.Call(module, "ShortCircuit", [42, -1]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "ShortCircuit", [-1, 5]), Is.EqualTo(false));
  }

  [Test]
  public void CheckedArithmeticAndConversions_WrapAndTrap() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class CheckedOps {
        public static int AddOvf(int a, int b) => checked(a + b);
        public static int SubOvf(int a, int b) => checked(a - b);
        public static int MulOvf(int a, int b) => checked(a * b);
        public static uint AddOvfU(uint a, uint b) => checked(a + b);
        public static uint SubOvfU(uint a, uint b) => checked(a - b);
        public static uint MulOvfU(uint a, uint b) => checked(a * b);
        public static sbyte ConvI1(int x) => (sbyte)x;
        public static byte ConvU1(int x) => (byte)x;
        public static short ConvI2(int x) => (short)x;
        public static ushort ConvU2(int x) => (ushort)x;
        public static long ConvI8(int x) => (long)x;
        public static ulong ConvU8FromU32(uint x) => (ulong)x;
        public static float ConvR4(int x) => (float)x;
        public static double ConvR8(long x) => (double)x;
        public static double ConvRUn(uint x) => (double)x;
        public static int ConvFromDouble(double x) => (int)x;
        public static byte CheckedConvU1(int x) => checked((byte)x);
        public static sbyte CheckedConvI1U(uint x) => checked((sbyte)x);
        public static int CheckedConvI4FromLong(long x) => checked((int)x);
        public static uint CheckedConvU4FromULong(ulong x) => checked((uint)x);
        public static short CheckedConvI2(int x) => checked((short)x);
        public static ushort CheckedConvU2(int x) => checked((ushort)x);
        public static long CheckedConvI8FromU64(ulong x) => checked((long)x);
        public static ulong CheckedConvU8FromI64(long x) => checked((ulong)x);
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "AddOvf", [10, 20]), Is.EqualTo(30));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "AddOvf", [int.MaxValue, 1]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "SubOvf", [int.MinValue, 1]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "MulOvf", [int.MaxValue, 2]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "AddOvfU", [uint.MaxValue, 1u]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "SubOvfU", [0u, 1u]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "MulOvfU", [uint.MaxValue, 2u]));
    Assert.That(IrInterpreter.Call(module, "ConvI1", [255]), Is.EqualTo((sbyte)-1));
    Assert.That(IrInterpreter.Call(module, "ConvU1", [-1]), Is.EqualTo((byte)255));
    Assert.That(IrInterpreter.Call(module, "ConvI2", [65535]), Is.EqualTo((short)-1));
    Assert.That(IrInterpreter.Call(module, "ConvU2", [-1]), Is.EqualTo((ushort)65535));
    Assert.That(IrInterpreter.Call(module, "ConvI8", [-5]), Is.EqualTo(-5L));
    Assert.That(IrInterpreter.Call(module, "ConvU8FromU32", [0x80000000u]), Is.EqualTo(0x80000000UL));
    Assert.That(IrInterpreter.Call(module, "ConvR4", [42]), Is.EqualTo(42.0f));
    Assert.That(IrInterpreter.Call(module, "ConvR8", [123456L]), Is.EqualTo(123456.0));
    Assert.That(IrInterpreter.Call(module, "ConvRUn", [0xFFFFFFFFu]), Is.EqualTo((double)0xFFFFFFFFu));
    Assert.That(IrInterpreter.Call(module, "ConvFromDouble", [3.9]), Is.EqualTo(3));
    Assert.That(IrInterpreter.Call(module, "CheckedConvU1", [200]), Is.EqualTo((byte)200));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "CheckedConvU1", [300]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "CheckedConvI1U", [200u]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "CheckedConvI4FromLong", [long.MaxValue]));
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "CheckedConvU4FromULong", [ulong.MaxValue]));
    Assert.That(IrInterpreter.Call(module, "CheckedConvI2", [100]), Is.EqualTo((short)100));
    Assert.That(IrInterpreter.Call(module, "CheckedConvU2", [100]), Is.EqualTo((ushort)100));
    Assert.That(IrInterpreter.Call(module, "CheckedConvI8FromU64", [5UL]), Is.EqualTo(5L));
    Assert.That(IrInterpreter.Call(module, "CheckedConvU8FromI64", [5L]), Is.EqualTo(5UL));
  }

  [Test]
  public void FloatArithmeticAndNaNComparisons_OrderedAndUnordered() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class FloatOps {
        public static double Calc(double a, double b) => -(a + b) * (a - b) / b;
        public static float CalcF(float a, float b) => a + b * 2.5f - a / b;
        public static bool Gt(double a, double b) => a > b;
        public static bool Lt(double a, double b) => a < b;
        public static bool Ge(double a, double b) => a >= b;
        public static bool Le(double a, double b) => a <= b;
        public static bool Eq(double a, double b) => a == b;
        public static bool Ne(double a, double b) => a != b;
        public static int BranchFloat(double a, double b) {
          if (a > b) return 1;
          if (a < b) return -1;
          if (a == b) return 0;
          return 99;
        }
        public static int BranchFloatGeLe(double a, double b) {
          if (a >= b) return 1;
          if (a <= b) return -1;
          return 42;
        }
        public static int BranchFloatNe(double a, double b) {
          if (a != b) return 1;
          return 0;
        }
      }
      """, OptimizationLevel.Debug);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "Calc", [5.0, 2.0]), Is.EqualTo(-10.5));
    Assert.That(IrInterpreter.Call(module, "CalcF", [4.0f, 2.0f]), Is.EqualTo(7.0f));
    Assert.That(IrInterpreter.Call(module, "Gt", [double.NaN, 1.0]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Lt", [double.NaN, 1.0]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Ge", [double.NaN, 1.0]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Le", [double.NaN, 1.0]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Eq", [double.NaN, double.NaN]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "Ne", [double.NaN, 1.0]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "BranchFloat", [double.NaN, 1.0]), Is.EqualTo(99));
    Assert.That(IrInterpreter.Call(module, "BranchFloat", [2.0, 1.0]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(module, "BranchFloat", [1.0, 2.0]), Is.EqualTo(-1));
    Assert.That(IrInterpreter.Call(module, "BranchFloat", [2.0, 2.0]), Is.EqualTo(0));
    Assert.That(IrInterpreter.Call(module, "BranchFloatGeLe", [double.NaN, 1.0]), Is.EqualTo(42));
    Assert.That(IrInterpreter.Call(module, "BranchFloatNe", [double.NaN, double.NaN]), Is.EqualTo(1));
  }

  [TestCase(OptimizationLevel.Debug)]
  [TestCase(OptimizationLevel.Release)]
  public void SwitchTable_DenseAndDefault(OptimizationLevel opt) {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class SwitchOps {
        public static int Classify(int x) {
          switch (x) {
            case 0: return 100;
            case 1: return 200;
            case 2: return 300;
            case 3: return 400;
            case 4: return 500;
            default: return -1;
          }
        }
      }
      """, opt);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "Classify", [0]), Is.EqualTo(100));
    Assert.That(IrInterpreter.Call(module, "Classify", [3]), Is.EqualTo(400));
    Assert.That(IrInterpreter.Call(module, "Classify", [99]), Is.EqualTo(-1));
  }

  [Test]
  public void Arrays_AllElementTypes_AndRvaInitializer() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class ArrayOps {
        public static int Sum(int[] a) {
          var s = 0;
          for (var i = 0; i < a.Length; ++i)
            s += a[i];
          return s;
        }
        public static int[] MakeAndModify(int n) {
          var a = new int[n];
          for (var i = 0; i < n; ++i)
            a[i] = i * 3;
          return a;
        }
        public static int ConstantArrayLookup(int i) {
          int[] table = { 10, 20, 30, 40, 50 };
          return table[i];
        }
        public static double TypedElements() {
          sbyte[] sb = { -1, 2, -3 };
          byte[] b = { 10, 20, 30 };
          short[] sh = { -100, 200, -300 };
          ushort[] ush = { 100, 200, 300 };
          uint[] ui = { 1u, 2u, 3u };
          long[] l = { 10L, 20L, 30L };
          ulong[] ul = { 1UL, 2UL, 3UL };
          float[] f = { 1.5f, 2.5f, 3.5f };
          double[] d = { 0.5, 1.5, 2.5 };
          sb[0] = 5; b[0] = 1; sh[0] = 2; ush[0] = 3; ui[0] = 4u; l[0] = 5L; ul[0] = 6UL; f[0] = 0.5f; d[0] = 0.25;
          return sb[0] + b[0] + sh[0] + ush[0] + (int)ui[0] + l[0] + (long)ul[0] + f[0] + d[0];
        }
        public static string StringArray(string s) {
          var arr = new string[2];
          arr[0] = s;
          arr[1] = "!";
          return arr[0] + arr[1];
        }
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "Sum", [new[] { 1, 2, 3, 4 }]), Is.EqualTo(10));
    Assert.That(IrInterpreter.Call(module, "MakeAndModify", [4]), Is.EqualTo(new[] { 0, 3, 6, 9 }));
    Assert.That(IrInterpreter.Call(module, "ConstantArrayLookup", [3]), Is.EqualTo(40));
    Assert.That(IrInterpreter.Call(module, "TypedElements", []), Is.EqualTo(5 + 1 + 2 + 3 + 4 + 5 + 6 + 0.5 + 0.25));
    Assert.That(IrInterpreter.Call(module, "StringArray", ["hi"]), Is.EqualTo("hi!"));
  }

  [Test]
  public void StaticFields_AndStaticConstructorInitializers() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class State {
        private static int _counter = 10;
        private static int _defaultZero;
        private static readonly int[] _table = { 3, 6, 9, 12 };
        private static readonly int[] _small = { 7, 8 };
        private static string _tag = "v1";
        public static int Bump(int delta) {
          _counter += delta + _defaultZero;
          return _counter;
        }
        public static int Lookup(int idx) => _table[idx] + _small[0];
        public static string GetTag() => _tag;
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "Bump", [5]), Is.EqualTo(15));
    Assert.That(IrInterpreter.Call(module, "Lookup", [2]), Is.EqualTo(16));
    Assert.That(IrInterpreter.Call(module, "GetTag", []), Is.EqualTo("v1"));
  }

  [Test]
  public void IntraAssemblyCalls_Recursion_AndExportFiltering() {
    var pe = MsilTestHelpers.CompileCSharp("""
      namespace App;
      public static class A {
        private static int Helper(int x) => x * 2;
        public static int Compute(int n) => Helper(Fact(n));
        public static int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
        public static int Dup() => 1;
      }
      public static class B {
        public static int Dup() => 2;
        public static void SideEffect(int[] a, int v) { a[0] = v; }
      }
      """, OptimizationLevel.Release);

    // Export specific method by Namespace.Type.Method: private Helper & Fact are pulled in transitively.
    var mod1 = MsilTestHelpers.ImportAndVerify(pe, new MsilImportOptions { Exports = ["App.A.Compute"] });
    Assert.That(IrInterpreter.Call(mod1, "Compute", [5]), Is.EqualTo(240));

    // Export by Type.Method to disambiguate Dup.
    var mod2 = MsilTestHelpers.ImportAndVerify(pe, new MsilImportOptions { Exports = ["A.Dup", "B.Dup"] });
    Assert.That(mod2.Functions, Has.Count.EqualTo(2));

    // Export by simple MethodName.
    var mod3 = MsilTestHelpers.ImportAndVerify(pe, new MsilImportOptions { Exports = ["SideEffect"] });
    var arr = new int[1];
    IrInterpreter.Call(mod3, "SideEffect", [arr, 99]);
    Assert.That(arr[0], Is.EqualTo(99));
  }

  [Test]
  public void BclIntrinsics_Math_BitOps_BinaryPrimitives_Array_FloatClass_String() {
    var pe = MsilTestHelpers.CompileCSharp("""
      using System;
      using System.Buffers.Binary;
      using System.Numerics;

      public static class Intrinsics {
        public static double MathOps(double x, double y)
          => Math.Abs(x) + Math.Min(x, y) + Math.Max(x, y) + Math.Sqrt(Math.Abs(y))
             + Math.Floor(x) + Math.Ceiling(x) + Math.Truncate(x)
             + Math.Round(x) + Math.Round(x, MidpointRounding.ToEven) + Math.Round(x, MidpointRounding.AwayFromZero)
             + Math.CopySign(x, y);

        public static float MathFOps(float x, float y)
          => MathF.Abs(x) + MathF.Min(x, y) + MathF.Max(x, y) + MathF.Sqrt(MathF.Abs(y))
             + MathF.Floor(x) + MathF.Ceiling(x) + MathF.Truncate(x) + MathF.Round(x) + MathF.CopySign(x, y);

        public static int IntMath(int a, int b) => Math.Abs(a) + Math.Min(a, b) + Math.Max(a, b);

        public static uint BitOps32(uint x, int r)
          => BitOperations.RotateLeft(x, r) ^ BitOperations.RotateRight(x, r)
             ^ (uint)BitOperations.LeadingZeroCount(x)
             ^ (uint)BitOperations.TrailingZeroCount(x)
             ^ (uint)BitOperations.PopCount(x);

        public static ulong BitOps64(ulong x, int r)
          => BitOperations.RotateLeft(x, r) ^ BitOperations.RotateRight(x, r)
             ^ (ulong)BitOperations.LeadingZeroCount(x)
             ^ (ulong)BitOperations.TrailingZeroCount(x)
             ^ (ulong)BitOperations.PopCount(x);

        public static int BinaryRoundTrip(int v) {
          var buf = new byte[16];
          BinaryPrimitives.WriteInt32LittleEndian(buf, v);
          var a = BinaryPrimitives.ReadInt32LittleEndian(buf);
          BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(4), v);
          var span = new ReadOnlySpan<byte>(buf, 4, 4);
          var b = BinaryPrimitives.ReadInt32BigEndian(span);
          BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(8, 2), (short)v);
          var c = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(8));
          BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan().Slice(10, 2), (ushort)v);
          var d = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan().Slice(10));
          return a + b + c + d + span.Length;
        }

        public static int ArrayAndBufferOps() {
          var src = new int[] { 1, 2, 3, 4 };
          var dst = new int[4];
          Array.Copy(src, dst, 4);
          Array.Copy(src, 1, dst, 0, 2);
          Array.Fill(src, 9);
          Array.Clear(dst, 2, 2);
          var b1 = new byte[] { 5, 6, 7, 8 };
          var b2 = new byte[4];
          Buffer.BlockCopy(b1, 0, b2, 0, 4);
          var i2 = new int[4];
          Buffer.BlockCopy(src, 0, i2, 0, 16);
          Array.Clear(b1);
          return dst[0] + dst[1] + dst[2] + src[0] + b2[3] + i2[1] + b1[0];
        }

        public static int FloatClass(double d, float f) {
          var mask = 0;
          if (double.IsNaN(d)) mask |= 1;
          if (double.IsInfinity(d)) mask |= 2;
          if (double.IsFinite(d)) mask |= 4;
          if (double.IsNormal(d)) mask |= 8;
          if (double.IsSubnormal(d)) mask |= 16;
          if (float.IsNaN(f)) mask |= 32;
          if (float.IsFinite(f)) mask |= 64;
          return mask;
        }

        public static int StringOps(string a, string b) {
          var c = string.Concat(a, b);
          var d = string.Concat(a, "-", b);
          var eq = a == b ? 1 : 0;
          var ne = a != b ? 10 : 0;
          return c.Length + d.Length + eq + ne + a[0];
        }
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "MathOps", [-2.5, 4.0]), Is.EqualTo(
      Math.Abs(-2.5) + Math.Min(-2.5, 4.0) + Math.Max(-2.5, 4.0) + Math.Sqrt(4.0)
      + Math.Floor(-2.5) + Math.Ceiling(-2.5) + Math.Truncate(-2.5)
      + Math.Round(-2.5) + Math.Round(-2.5, MidpointRounding.ToEven) + Math.Round(-2.5, MidpointRounding.AwayFromZero)
      + Math.CopySign(-2.5, 4.0)));
    Assert.That(IrInterpreter.Call(module, "MathFOps", [-1.5f, 9.0f]), Is.EqualTo(
      MathF.Abs(-1.5f) + MathF.Min(-1.5f, 9.0f) + MathF.Max(-1.5f, 9.0f) + MathF.Sqrt(9.0f)
      + MathF.Floor(-1.5f) + MathF.Ceiling(-1.5f) + MathF.Truncate(-1.5f) + MathF.Round(-1.5f) + MathF.CopySign(-1.5f, 9.0f)));
    Assert.That(IrInterpreter.Call(module, "IntMath", [-7, 3]), Is.EqualTo(7 + -7 + 3));
    Assert.That(IrInterpreter.Call(module, "BitOps32", [0x12345678u, 5]), Is.EqualTo(
      System.Numerics.BitOperations.RotateLeft(0x12345678u, 5) ^ System.Numerics.BitOperations.RotateRight(0x12345678u, 5)
      ^ (uint)System.Numerics.BitOperations.LeadingZeroCount(0x12345678u)
      ^ (uint)System.Numerics.BitOperations.TrailingZeroCount(0x12345678u)
      ^ (uint)System.Numerics.BitOperations.PopCount(0x12345678u)));
    Assert.That(IrInterpreter.Call(module, "BitOps64", [0x123456789ABCDEF0UL, 7]), Is.EqualTo(
      System.Numerics.BitOperations.RotateLeft(0x123456789ABCDEF0UL, 7) ^ System.Numerics.BitOperations.RotateRight(0x123456789ABCDEF0UL, 7)
      ^ (ulong)System.Numerics.BitOperations.LeadingZeroCount(0x123456789ABCDEF0UL)
      ^ (ulong)System.Numerics.BitOperations.TrailingZeroCount(0x123456789ABCDEF0UL)
      ^ (ulong)System.Numerics.BitOperations.PopCount(0x123456789ABCDEF0UL)));
    Assert.That(IrInterpreter.Call(module, "BinaryRoundTrip", [0x1234]), Is.EqualTo(0x1234 * 4 + 4));
    Assert.That(IrInterpreter.Call(module, "ArrayAndBufferOps", []), Is.EqualTo(2 + 3 + 0 + 9 + 8 + 9 + 0));
    Assert.That(IrInterpreter.Call(module, "FloatClass", [double.NaN, 1.0f]), Is.EqualTo(1 | 64));
    Assert.That(IrInterpreter.Call(module, "FloatClass", [double.PositiveInfinity, float.NaN]), Is.EqualTo(2 | 32));
    Assert.That(IrInterpreter.Call(module, "FloatClass", [1.5, 1.0f]), Is.EqualTo(4 | 8 | 64));
    Assert.That(IrInterpreter.Call(module, "FloatClass", [double.Epsilon, 1.0f]), Is.EqualTo(4 | 16 | 64));
    Assert.That(IrInterpreter.Call(module, "StringOps", ["ab", "cd"]), Is.EqualTo(4 + 5 + 0 + 10 + 'a'));
    Assert.That(IrInterpreter.Call(module, "StringOps", ["ab", "ab"]), Is.EqualTo(4 + 5 + 1 + 0 + 'a'));
  }

  [TestCase(OptimizationLevel.Debug)]
  [TestCase(OptimizationLevel.Release)]
  public void Exceptions_TryCatchFinally_Throw_Rethrow_AndNested(OptimizationLevel opt) {
    var pe = MsilTestHelpers.CompileCSharp("""
      using System;
      public static class ExOps {
        public static int SafeDiv(int a, int b) {
          try {
            return a / b;
          } catch (DivideByZeroException) {
            return -1;
          }
        }
        public static int ThrowAndCatch(int x) {
          var log = 0;
          try {
            if (x < 0)
              throw new InvalidOperationException("neg");
            log += 10;
          } catch (InvalidOperationException ex) {
            log += ex.Message == "neg" ? 100 : 200;
          } finally {
            log += 1;
          }
          return log;
        }
        public static int RethrowInner(int x) {
          try {
            try {
              if (x == 0)
                throw new ArgumentException("bad");
              return 5;
            } catch (ArgumentException) {
              throw;
            }
          } catch (Exception) {
            return 99;
          }
        }
        public static int NestedFinally(int x) {
          var acc = 0;
          try {
            try {
              if (x > 0)
                return acc + 10;
              acc += 1;
            } finally {
              acc += 2;
            }
            acc += 4;
          } finally {
            acc += 8;
          }
          return acc;
        }
      }
      """, opt);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "SafeDiv", [10, 2]), Is.EqualTo(5));
    Assert.That(IrInterpreter.Call(module, "SafeDiv", [10, 0]), Is.EqualTo(-1));
    Assert.That(IrInterpreter.Call(module, "ThrowAndCatch", [1]), Is.EqualTo(11));
    Assert.That(IrInterpreter.Call(module, "ThrowAndCatch", [-1]), Is.EqualTo(101));
    Assert.That(IrInterpreter.Call(module, "RethrowInner", [0]), Is.EqualTo(99));
    Assert.That(IrInterpreter.Call(module, "RethrowInner", [1]), Is.EqualTo(5));
    Assert.That(IrInterpreter.Call(module, "NestedFinally", [0]), Is.EqualTo(15));
    Assert.That(IrInterpreter.Call(module, "NestedFinally", [1]), Is.EqualTo(10));
  }

  [Test]
  public void NullComparison_AndByRefArgAndLocal() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class NullOps {
        public static bool IsNullArr(int[]? a) => a == null;
        public static bool IsNotNullStr(string? s) => s != null;
        public static bool SameArr(int[]? a, int[]? b) => a == b;
        public static string? ReturnNull() => null;
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "IsNullArr", [null!]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "IsNullArr", [new[] { 1 }]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "IsNotNullStr", ["hi"]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "IsNotNullStr", [null!]), Is.EqualTo(false));
    var a = new[] { 1 };
    Assert.That(IrInterpreter.Call(module, "SameArr", [a, a]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "ReturnNull", []), Is.Null);
  }

  [Test]
  public void RawIl_FaultAndFilterExceptionRegions() {
    // Fault region: try { ldc.i4.1; ldc.i4.0; div; pop; leave.s end; } fault { nop; endfinally; } end: ldc.i4.7; ret
    byte[] faultIl = [
      0x17,       // 0: ldc.i4.1
      0x16,       // 1: ldc.i4.0
      0x5B,       // 2: div
      0x26,       // 3: pop
      0xDE, 0x02, // 4: leave.s +2 -> 8
      0x00,       // 6: nop
      0xDC,       // 7: endfinally (endfault)
      0x1D,       // 8: ldc.i4.7
      0x2A        // 9: ret
    ];
    var faultPe = MsilTestHelpers.BuildRawIlAssembly(
      "FaultTest", faultIl,
      encodeReturn: r => r.Int32(),
      encodeRegions: enc => enc.Add(ExceptionRegionKind.Fault, 0, 6, 6, 2, default, 0),
      regionCount: 1);

    var faultMod = MsilTestHelpers.ImportAndVerify(faultPe);
    Assert.Throws<IrTrapException>(() => IrInterpreter.Call(faultMod, "FaultTest", []));

    // Filter region: try { ldc.i4.1; ldc.i4.0; div; pop; leave.s end; } filter { pop; ldc.i4.1; endfilter; } handler { pop; leave.s end; } end: ldc.i4.5; ret
    byte[] filterIl = [
      0x17,             // 0: ldc.i4.1
      0x16,             // 1: ldc.i4.0
      0x5B,             // 2: div
      0x26,             // 3: pop
      0xDE, 0x07,       // 4: leave.s +7 -> 13
      0x26,             // 6: pop (filter start)
      0x17,             // 7: ldc.i4.1
      0xFE, 0x11,       // 8: endfilter
      0x26,             // 10: pop (handler start)
      0xDE, 0x00,       // 11: leave.s +0 -> 13
      0x1B,             // 13: ldc.i4.5
      0x2A              // 14: ret
    ];
    var filterPe = MsilTestHelpers.BuildRawIlAssembly(
      "FilterTest", filterIl,
      encodeReturn: r => r.Int32(),
      encodeRegions: enc => enc.Add(ExceptionRegionKind.Filter, 0, 6, 10, 3, default, 6),
      regionCount: 1);

    var filterMod = MsilTestHelpers.ImportAndVerify(filterPe);
    Assert.That(IrInterpreter.Call(filterMod, "FilterTest", []), Is.EqualTo(5));
  }

  // ------------------------------------------------------------------ Error Paths (PLY3000 - PLY3008)

  [Test]
  public void Import_NullArguments_ThrowArgumentNullException() {
    Assert.Throws<ArgumentNullException>(() => MsilFrontend.Import(null!, []));
    Assert.Throws<ArgumentNullException>(() => MsilFrontend.Import("a.dll", null!));
  }

  [Test]
  public void Import_NotPeOrCli_ReportsPLY3000() {
    var empty = MsilFrontend.Import("empty.dll", []);
    Assert.That(empty.Module, Is.Null);
    Assert.That(empty.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.NotPeOrCliCode));

    var garbage = MsilFrontend.Import("junk.dll", new byte[128]);
    Assert.That(garbage.Module, Is.Null);
    Assert.That(garbage.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.NotPeOrCliCode));

    var nativePe = MsilTestHelpers.BuildNativePeWithoutCli();
    var nativeRes = MsilFrontend.Import("native.dll", nativePe);
    Assert.That(nativeRes.Module, Is.Null);
    Assert.That(nativeRes.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.NotPeOrCliCode));
  }

  [Test]
  public void Import_CorruptMetadata_ReportsPLY3001() {
    // Non-assembly metadata module
    var nonAsm = MsilTestHelpers.BuildRawIlAssembly("F", [0x2A], isAssembly: false);
    var res1 = MsilFrontend.Import("mod.netmodule", nonAsm);
    Assert.That(res1.Module, Is.Null);
    Assert.That(res1.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));

    // Truncated 2-byte opcode (trailing 0xFE)
    var trunc2 = MsilTestHelpers.BuildRawIlAssembly("F", [0xFE]);
    var res2 = MsilFrontend.Import("trunc2.dll", trunc2);
    Assert.That(res2.Module, Is.Null);
    Assert.That(res2.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));

    // Truncated operand (ldc.i4 with only 2 bytes)
    var truncOp = MsilTestHelpers.BuildRawIlAssembly("F", [0x20, 0x01, 0x02]);
    var res3 = MsilFrontend.Import("truncop.dll", truncOp);
    Assert.That(res3.Module, Is.Null);
    Assert.That(res3.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));

    // Truncated switch table
    var truncSw = MsilTestHelpers.BuildRawIlAssembly("F", [0x16, 0x45, 0x05, 0x00, 0x00, 0x00]);
    var res4 = MsilFrontend.Import("truncsw.dll", truncSw);
    Assert.That(res4.Module, Is.Null);
    Assert.That(res4.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));
  }

  [Test]
  public void Import_ExportNotFoundOrAmbiguous_ReportsPLY3002() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class A { public static int X() => 1; }
      public static class B { public static int X() => 2; }
      """);

    var missing = MsilFrontend.Import("test.dll", pe, new MsilImportOptions { Exports = ["NoSuchMethod"] });
    Assert.That(missing.Module, Is.Null);
    Assert.That(missing.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.ExportNotFoundCode));

    var ambiguous = MsilFrontend.Import("test.dll", pe, new MsilImportOptions { Exports = ["X"] });
    Assert.That(ambiguous.Module, Is.Null);
    Assert.That(ambiguous.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.ExportNotFoundCode));
  }

  [Test]
  public void Import_MixedModeAssembly_ReportsPLY3003() {
    var mixed = MsilTestHelpers.BuildRawIlAssembly("F", [0x2A], corFlags: (CorFlags)0);
    var res = MsilFrontend.Import("mixed.dll", mixed);
    Assert.That(res.Module, Is.Null);
    Assert.That(res.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.MixedModeUnsupportedCode));
  }

  [Test]
  public void Import_ExternOrNoBodyMethod_ReportsPLY3004() {
    var pe = MsilTestHelpers.CompileCSharp("""
      using System.Runtime.InteropServices;
      public static class Native {
        [DllImport("libc")]
        public static extern int getpid();
      }
      """);
    var res = MsilFrontend.Import("native_import.dll", pe);
    Assert.That(res.Module, Is.Null);
    Assert.That(res.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedMethodBodyCode));
  }

  [Test]
  public void Import_UnsupportedOpcode_ReportsPLY3005() {
    // Unknown opcode byte 0xFF
    var unknownOp = MsilTestHelpers.BuildRawIlAssembly("F", [0xFF]);
    var res1 = MsilFrontend.Import("unk.dll", unknownOp);
    Assert.That(res1.Module, Is.Null);
    Assert.That(res1.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedOpcodeCode));

    // Valid ECMA-335 opcode outside supported subset: arglist (0xFE 0x00)
    var arglist = MsilTestHelpers.BuildRawIlAssembly("F", [0xFE, 0x00, 0x26, 0x2A]);
    var res2 = MsilFrontend.Import("arglist.dll", arglist);
    Assert.That(res2.Module, Is.Null);
    Assert.That(res2.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedOpcodeCode));
  }

  [Test]
  public void Import_UnsupportedType_ReportsPLY3006() {
    var multiDim = MsilTestHelpers.CompileCSharp("public static class C { public static int F(int[,] m) => m[0, 0]; }");
    var res1 = MsilFrontend.Import("md.dll", multiDim);
    Assert.That(res1.Module, Is.Null);
    Assert.That(res1.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedTypeCode));

    var genericMethod = MsilTestHelpers.CompileCSharp("public static class C { public static T Id<T>(T x) => x; }");
    var res2 = MsilFrontend.Import("gen.dll", genericMethod);
    Assert.That(res2.Module, Is.Null);
    Assert.That(res2.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedTypeCode));

    var customType = MsilTestHelpers.CompileCSharp("public struct Pair { public int X; } public static class C { public static int F(Pair p) => p.X; }");
    var res3 = MsilFrontend.Import("pair.dll", customType);
    Assert.That(res3.Module, Is.Null);
    Assert.That(res3.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedTypeCode));
  }

  [Test]
  public void Import_UnverifiableStack_ReportsPLY3007() {
    // Stack underflow: pop on empty stack
    var underflow = MsilTestHelpers.BuildRawIlAssembly("F", [0x26, 0x2A]);
    var res1 = MsilFrontend.Import("uf.dll", underflow);
    Assert.That(res1.Module, Is.Null);
    Assert.That(res1.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));

    // Void method returning with value on stack
    var extraOnRet = MsilTestHelpers.BuildRawIlAssembly("F", [0x17, 0x2A]);
    var res2 = MsilFrontend.Import("ret.dll", extraOnRet);
    Assert.That(res2.Module, Is.Null);
    Assert.That(res2.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));

    // Stack depth mismatch across branch merge: brtrue +1 -> ldc.i4.1; ret (depth 0 vs 1)
    var mismatch = MsilTestHelpers.BuildRawIlAssembly(
      "F",
      [0x16, 0x2D, 0x01, 0x17, 0x2A],
      encodeReturn: r => r.Int32());
    var res3 = MsilFrontend.Import("mismatch.dll", mismatch);
    Assert.That(res3.Module, Is.Null);
    Assert.That(res3.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));

    // Invalid branch target (jumps into middle of instruction / outside IL)
    var badTarget = MsilTestHelpers.BuildRawIlAssembly("F", [0x2B, 0x20, 0x2A]);
    var res4 = MsilFrontend.Import("badtarget.dll", badTarget);
    Assert.That(res4.Module, Is.Null);
    Assert.That(res4.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));

    // Control falls off end without ret
    var falloff = MsilTestHelpers.BuildRawIlAssembly("F", [0x00]);
    var res5 = MsilFrontend.Import("falloff.dll", falloff);
    Assert.That(res5.Module, Is.Null);
    Assert.That(res5.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));
  }

  [Test]
  public void Import_UnsupportedCall_ReportsPLY3008() {
    var pe = MsilTestHelpers.CompileCSharp("""
      using System;
      public static class C {
        public static void Speak() => Console.WriteLine("hello");
      }
      """);
    var res = MsilFrontend.Import("call.dll", pe);
    Assert.That(res.Module, Is.Null);
    Assert.That(res.Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedCallCode));
  }

}
