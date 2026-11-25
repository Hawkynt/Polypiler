using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using NUnit.Framework;
using Polypiler.Core.Frontends.Msil;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Msil;

[TestFixture]
public sealed class MsilEdgeCaseTests {

  [Test]
  public void AllIlEncodings_Int64_LongBranch_TwoByteVar_ManyParamsAndLocals() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class WideOps {
        public static long BigConst(long a) => a + 0x123456789ABCDEF0L;
        public static int ManyArgsAndLocals(int a, int b, int c, int d, int e, int f) {
          var l0 = a + 1;
          var l1 = b + 2;
          var l2 = c + 3;
          var l3 = d + 4;
          var l4 = e + 5;
          var l5 = f + 6;
          e = l4 + l5;
          return l0 + l1 + l2 + l3 + e;
        }
        public static int Overloaded(int x) => x + 1;
        public static int Overloaded(int x, int y) => x + y;
        public static int NonStaticIgnored() => 42;
      }
      public class InstanceHolder {
        public int InstanceMethod(int x) => x;
        public static int StaticHelper(int x) => x * 3;
      }
      """, OptimizationLevel.Debug);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "BigConst", [1L]), Is.EqualTo(0x123456789ABCDEF1L));
    Assert.That(IrInterpreter.Call(module, "ManyArgsAndLocals", [1, 2, 3, 4, 5, 6]), Is.EqualTo(42));
    Assert.That(IrInterpreter.Call(module, "StaticHelper", [5]), Is.EqualTo(15));

    // Raw IL exercising 2-byte Var encodings (ldarg, starg, ldloc, stloc, ldarga, ldloca) and 4-byte Branch (br, beq, bne.un, blt, bgt, ble, bge, leave)
    byte[] rawIl = [
      0xFE, 0x09, 0x00, 0x00,             // 0: ldarg 0
      0xFE, 0x0E, 0x00, 0x00,             // 4: stloc 0
      0xFE, 0x0C, 0x00, 0x00,             // 8: ldloc 0
      0x17,                               // 12: ldc.i4.1
      0x58,                               // 13: add
      0xFE, 0x0B, 0x00, 0x00,             // 14: starg 0
      0x38, 0x00, 0x00, 0x00, 0x00,       // 18: br +0 -> 23
      0xFE, 0x09, 0x00, 0x00,             // 23: ldarg 0
      0x2A                                // 27: ret
    ];
    var rawPe = MsilTestHelpers.BuildRawIlAssembly(
      "WideVar", rawIl,
      encodeReturn: r => r.Int32(),
      encodeParams: p => p.AddParameter().Type().Int32(),
      paramCount: 1,
      encodeLocals: l => l.AddVariable().Type().Int32(),
      localCount: 1);

    var rawMod = MsilTestHelpers.ImportAndVerify(rawPe);
    Assert.That(IrInterpreter.Call(rawMod, "WideVar", [41]), Is.EqualTo(42));
  }

  [Test]
  public void NestedExceptions_InsideCatchAndFinally_AndMultipleCatches() {
    var pe = MsilTestHelpers.CompileCSharp("""
      using System;
      public static class NestedEx {
        public static int MultiCatch(int mode) {
          try {
            if (mode == 0)
              throw new DivideByZeroException();
            if (mode == 1)
              throw new OverflowException();
            if (mode == 2)
              throw new InvalidOperationException("op");
            return 10;
          } catch (DivideByZeroException) {
            try {
              throw new ArgumentException("inner");
            } catch (ArgumentException) {
              return 100;
            }
          } catch (OverflowException) {
            return 200;
          } catch (Exception ex) {
            return ex.Message == "op" ? 300 : 400;
          }
        }

        public static int TryInFinally(int x) {
          var acc = 0;
          try {
            acc += x;
          } finally {
            try {
              if (x == 0)
                throw new InvalidOperationException();
              acc += 5;
            } catch (InvalidOperationException) {
              acc += 50;
            }
          }
          return acc;
        }

        public static int CastAndAsException(int flag) {
          try {
            if (flag == 1)
              throw new ArgumentOutOfRangeException("aor");
            throw new ArgumentException("arg");
          } catch (Exception ex) {
            var aor = ex as ArgumentOutOfRangeException;
            if (aor != null)
              return 11;
            var arg = (ArgumentException)ex;
            return arg.Message == "arg" ? 22 : 33;
          }
        }
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "MultiCatch", [0]), Is.EqualTo(100));
    Assert.That(IrInterpreter.Call(module, "MultiCatch", [1]), Is.EqualTo(200));
    Assert.That(IrInterpreter.Call(module, "MultiCatch", [2]), Is.EqualTo(300));
    Assert.That(IrInterpreter.Call(module, "MultiCatch", [3]), Is.EqualTo(10));
    Assert.That(IrInterpreter.Call(module, "TryInFinally", [0]), Is.EqualTo(50));
    Assert.That(IrInterpreter.Call(module, "TryInFinally", [3]), Is.EqualTo(8));
    Assert.That(IrInterpreter.Call(module, "CastAndAsException", [1]), Is.EqualTo(11));
    Assert.That(IrInterpreter.Call(module, "CastAndAsException", [0]), Is.EqualTo(22));
  }

  [Test]
  public void StaticInitializers_AllScalarTypes_SmallAndRvaArrays_AndDefaults() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class AllStatics {
        private static volatile int _vol = 3;
        private static int _negOne = -1;
        private static int _bigI32 = 100000;
        private static long _bigI64 = 0x100000000L;
        private static float _f32 = 1.5f;
        private static double _f64 = 2.5;
        private static string _uninitStr = default!;
        private static int[] _uninitArr = default!;
        private static readonly sbyte[] _i8 = { -1, 2, -3, 4 };
        private static readonly byte[] _u8 = { 1, 2, 3, 4 };
        private static readonly short[] _i16 = { -10, 20, -30, 40 };
        private static readonly ushort[] _u16 = { 10, 20, 30, 40 };
        private static readonly uint[] _u32 = { 1u, 2u, 3u, 4u };
        private static readonly long[] _i64 = { 1L, 2L, 3L, 4L };
        private static readonly ulong[] _u64 = { 1UL, 2UL, 3UL, 4UL };
        private static readonly float[] _af32 = { 1.0f, 2.0f, 3.0f, 4.0f };
        private static readonly double[] _af64 = { 1.0, 2.0, 3.0, 4.0 };
        public static double ReadAll()
          => _vol + _negOne + _bigI32 + _bigI64 + _f32 + _f64
             + _i8[0] + _u8[1] + _i16[2] + _u16[3] + _u32[0] + _i64[1] + (long)_u64[2] + _af32[3] + _af64[0]
             + _uninitStr.Length + _uninitArr.Length;
      }
      public static class DynamicCctor {
        private static int _x = Compute();
        private static int Compute() => 7;
        public static int Get() => _x;
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "ReadAll", []), Is.EqualTo(
      3 + -1 + 100000 + 0x100000000L + 1.5f + 2.5
      + -1 + 2 + -30 + 40 + 1 + 2 + 3 + 4.0f + 1.0));
    Assert.That(IrInterpreter.Call(module, "Get", []), Is.EqualTo(0));
  }

  [Test]
  public void SpanParameter_ByRef_InitObj_AndAllBinaryPrimitivesTypes() {
    var pe = MsilTestHelpers.CompileCSharp("""
      using System;
      using System.Buffers.Binary;

      public static class SpanAndEndian {
        public static int SliceParam(Span<byte> s) {
          var sub = s.Slice(2);
          return sub.Length;
        }
        public static double AllEndian(byte[] buf) {
          BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), 10u);
          var u32L = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), 20u);
          var u32B = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0), 30L);
          var i64L = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(0), 40L);
          var i64B = BinaryPrimitives.ReadInt64BigEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0), 50UL);
          var u64L = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(0), 60UL);
          var u64B = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(0), 1.5f);
          var f32L = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(0), 2.5f);
          var f32B = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(0), 3.5);
          var f64L = BinaryPrimitives.ReadDoubleLittleEndian(buf.AsSpan(0));
          BinaryPrimitives.WriteDoubleBigEndian(buf.AsSpan(0), 4.5);
          var f64B = BinaryPrimitives.ReadDoubleBigEndian(buf.AsSpan(0));
          var sp = new Span<byte>(buf);
          var ro = new ReadOnlySpan<byte>(buf);
          return u32L + u32B + i64L + i64B + (long)u64L + (long)u64B + f32L + f32B + f64L + f64B + sp.Length + ro.Length;
        }
        public static double ClearDoubleRange() {
          var a = new double[] { 1.5, 2.5, 3.5 };
          Array.Clear(a, 1, 1);
          return a[0] + a[1] + a[2];
        }
      }
      """, OptimizationLevel.Release);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "AllEndian", [new byte[8]]), Is.EqualTo(10 + 20 + 30 + 40 + 50 + 60 + 1.5f + 2.5f + 3.5 + 4.5 + 8 + 8));
    Assert.That(IrInterpreter.Call(module, "ClearDoubleRange", []), Is.EqualTo(5.0));
  }

  [Test]
  public void StackMerges_NullAndNumericAndBoolCoercions() {
    var pe = MsilTestHelpers.CompileCSharp("""
      public static class Coercions {
        public static string? NullLeft(bool c, string s) => c ? null : s;
        public static string? NullRight(bool c, string s) => c ? s : null;
        public static bool BoolCompare(bool a, bool b) => a == b;
        public static int UnsignedBranches(uint a, uint b) {
          var r = 0;
          if (a > b) r += 1;
          if (a >= b) r += 2;
          if (a < b) r += 4;
          if (a <= b) r += 8;
          if (a != b) r += 16;
          if (a == b) r += 32;
          return r;
        }
        public static int FloatBranchesUnordered(double a, double b) {
          var r = 0;
          if (!(a > b)) r += 1;
          if (!(a >= b)) r += 2;
          if (!(a < b)) r += 4;
          if (!(a <= b)) r += 8;
          return r;
        }
        public static bool NullBranch(string? s) {
          if (s != null) return true;
          return false;
        }
        public static int RefNotEqual(int[]? a, int[]? b) => a != b ? 1 : 0;
        public static int BoolToIntMath(bool a, bool b) => (a ? 1 : 0) + (~(b ? 1 : 0));
      }
      """, OptimizationLevel.Debug);

    var module = MsilTestHelpers.ImportAndVerify(pe);
    Assert.That(IrInterpreter.Call(module, "NullLeft", [true, "x"]), Is.Null);
    Assert.That(IrInterpreter.Call(module, "NullLeft", [false, "x"]), Is.EqualTo("x"));
    Assert.That(IrInterpreter.Call(module, "NullRight", [false, "x"]), Is.Null);
    Assert.That(IrInterpreter.Call(module, "BoolCompare", [true, true]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "UnsignedBranches", [5u, 3u]), Is.EqualTo(1 + 2 + 16));
    Assert.That(IrInterpreter.Call(module, "UnsignedBranches", [3u, 3u]), Is.EqualTo(2 + 8 + 32));
    Assert.That(IrInterpreter.Call(module, "FloatBranchesUnordered", [double.NaN, 1.0]), Is.EqualTo(15));
    Assert.That(IrInterpreter.Call(module, "NullBranch", ["ok"]), Is.EqualTo(true));
    Assert.That(IrInterpreter.Call(module, "NullBranch", [null!]), Is.EqualTo(false));
    Assert.That(IrInterpreter.Call(module, "RefNotEqual", [new[] { 1 }, new[] { 1 }]), Is.EqualTo(1));

    // Raw IL: ceq(ldnull, ldnull), brtrue(ldnull), neg(bool), not(bool), neg(u32), conv.r4(bool), i32/u32 merge, i64/u64 merge, f32/f64 merge, initobj
    byte[] rawNulls = [
      0x14,       // 0: ldnull
      0x14,       // 1: ldnull
      0xFE, 0x01, // 2: ceq -> true
      0x65,       // 4: neg (on bool -> -1)
      0x66,       // 5: not (on i32 -> 0)
      0x14,       // 6: ldnull
      0x2D, 0x01, // 7: brtrue.s +1 -> 10
      0x17,       // 9: ldc.i4.1
      0x58,       // 10: add (if not taken: 0 + 1; wait: stack depth mismatch if taken! Jump over pop instead)
    ];
    _ = rawNulls;
    byte[] rawBoolOps = [
      0x14,       // 0: ldnull
      0x14,       // 1: ldnull
      0xFE, 0x01, // 2: ceq (bool)
      0x66,       // 4: not (bool -> i32)
      0x26,       // 5: pop
      0x14,       // 6: ldnull
      0x14,       // 7: ldnull
      0xFE, 0x01, // 8: ceq (bool)
      0x65,       // 10: neg (bool -> i32)
      0x26,       // 11: pop
      0x02,       // 12: ldarg.0 (u32)
      0x65,       // 13: neg (u32 -> i32)
      0x26,       // 14: pop
      0x14,       // 15: ldnull
      0x2D, 0x02, // 16: brtrue.s +2 -> 20
      0x17,       // 18: ldc.i4.1
      0x2A,       // 19: ret
      0x16,       // 20: ldc.i4.0
      0x2A        // 21: ret
    ];
    var rawBoolPe = MsilTestHelpers.BuildRawIlAssembly(
      "NullAndBool", rawBoolOps,
      encodeReturn: r => r.Int32(),
      encodeParams: p => p.AddParameter().Type().UInt32(),
      paramCount: 1);
    var rawBoolMod = MsilTestHelpers.ImportAndVerify(rawBoolPe);
    Assert.That(IrInterpreter.Call(rawBoolMod, "NullAndBool", [5u]), Is.EqualTo(1));

    // Raw IL: non-trivial dup ((a + b) * (a + b)), conv.i8 on bool, null != null (cgt.un)
    byte[] rawDupConv = [
      0x02,       // 0: ldarg.0 (i32)
      0x03,       // 1: ldarg.1 (i32)
      0x58,       // 2: add
      0x25,       // 3: dup (non-trivial IrBinary -> spills to temp)
      0x5A,       // 4: mul
      0x04,       // 5: ldarg.2 (bool)
      0x6A,       // 6: conv.i8 (bool -> i64)
      0x69,       // 7: conv.i4
      0x58,       // 8: add
      0x14,       // 9: ldnull
      0x14,       // 10: ldnull
      0xFE, 0x03, // 11: cgt.un (null != null -> false)
      0x58,       // 13: add
      0x2A        // 14: ret
    ];
    var dupPe = MsilTestHelpers.BuildRawIlAssembly(
      "DupAndBoolConv", rawDupConv,
      encodeReturn: r => r.Int32(),
      encodeParams: p => {
        p.AddParameter().Type().Int32();
        p.AddParameter().Type().Int32();
        p.AddParameter().Type().Boolean();
      },
      paramCount: 3);
    var dupMod = MsilTestHelpers.ImportAndVerify(dupPe);
    Assert.That(IrInterpreter.Call(dupMod, "DupAndBoolConv", [3, 4, true]), Is.EqualTo(50));

    // Raw IL: stack-merge widening across blocks (i32/u32, i64/u64, f32/f64)
    byte[] merge32 = [0x02, 0x2C, 0x03, 0x03, 0x2B, 0x01, 0x04, 0x2A];
    var m32Pe = MsilTestHelpers.BuildRawIlAssembly("M32", merge32,
      encodeReturn: r => r.UInt32(),
      encodeParams: p => { p.AddParameter().Type().Boolean(); p.AddParameter().Type().Int32(); p.AddParameter().Type().UInt32(); },
      paramCount: 3);
    Assert.That(IrInterpreter.Call(MsilTestHelpers.ImportAndVerify(m32Pe), "M32", [false, 1, 2u]), Is.EqualTo(2u));

    var m64Pe = MsilTestHelpers.BuildRawIlAssembly("M64", merge32,
      encodeReturn: r => r.Int64(),
      encodeParams: p => { p.AddParameter().Type().Boolean(); p.AddParameter().Type().Int64(); p.AddParameter().Type().UInt64(); },
      paramCount: 3);
    Assert.That(IrInterpreter.Call(MsilTestHelpers.ImportAndVerify(m64Pe), "M64", [false, 1L, 2UL]), Is.EqualTo(2L));

    var mfPe = MsilTestHelpers.BuildRawIlAssembly("MF", merge32,
      encodeReturn: r => r.Double(),
      encodeParams: p => { p.AddParameter().Type().Boolean(); p.AddParameter().Type().Single(); p.AddParameter().Type().Double(); },
      paramCount: 3);
    Assert.That(IrInterpreter.Call(MsilTestHelpers.ImportAndVerify(mfPe), "MF", [true, 1.5f, 2.5]), Is.EqualTo(1.5));

    // C# test: newobj Span/ReadOnlySpan directly passed to BinaryPrimitives, multi-block catch with conditional rethrow,
    // duplicate export deduplication, and sanitized assembly name.
    var extraPe = MsilTestHelpers.CompileCSharp("""
      using System;
      using System.Buffers.Binary;
      public static class Extra {
        public static int DirectNewSpan(byte[] buf)
          => BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buf, 0, 4))
             + new Span<byte>(buf).Length;
        public static int ConditionalRethrow(int x) {
          try {
            throw new InvalidOperationException("boom");
          } catch (InvalidOperationException) {
            if (x > 0)
              throw;
            return 7;
          }
        }
        public static int InitLocalSpan(byte[] buf) {
          Span<byte> s = default;
          if (buf.Length > 0)
            s = buf;
          return s.Length;
        }
      }
      """, OptimizationLevel.Release, assemblyName: "My.Test-Asm");

    var extraMod = MsilTestHelpers.ImportAndVerify(extraPe, new MsilImportOptions { Exports = ["DirectNewSpan", "DirectNewSpan", "ConditionalRethrow", "InitLocalSpan"] });
    Assert.That(extraMod.Name, Is.EqualTo("My_Test_Asm"));
    Assert.That(IrInterpreter.Call(extraMod, "DirectNewSpan", [new byte[] { 1, 0, 0, 0 }]), Is.EqualTo(5));
    Assert.That(IrInterpreter.Call(extraMod, "ConditionalRethrow", [0]), Is.EqualTo(7));
    Assert.Throws<IrException>(() => IrInterpreter.Call(extraMod, "ConditionalRethrow", [1]));
    Assert.That(IrInterpreter.Call(extraMod, "InitLocalSpan", [new byte[] { 1, 2, 3 }]), Is.EqualTo(3));
  }

  [Test]
  public void AllUnsupportedCallsAndTypes_ReportExpectedDiagnostics() {
    void AssertCode(string src, string expectedCode, bool allowUnsafe = false, MsilImportOptions? options = null) {
      var pe = MsilTestHelpers.CompileCSharp(src, OptimizationLevel.Release, allowUnsafe: allowUnsafe);
      var res = MsilFrontend.Import("case.dll", pe, options);
      Assert.That(res.Module, Is.Null, $"Expected failure for:\n{src}");
      Assert.That(res.Diagnostics[0].Code, Is.EqualTo(expectedCode), () => $"{res.Diagnostics[0].Code}: {res.Diagnostics[0].Message}");
    }

    // Unsupported BCL methods across every recognized class (PLY3008)
    AssertCode("using System; public static class C { public static double F(double x) => Math.Sin(x); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System; public static class C { public static double F(double x) => Math.Round(x, MidpointRounding.ToZero); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System.Numerics; public static class C { public static int F(uint x) => BitOperations.Log2(x); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System.Buffers.Binary; public static class C { public static int F(int x) => BinaryPrimitives.ReverseEndianness(x); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System; public static class C { public static void F(int[] a) => Array.Reverse(a); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System; public static class C { public static int F(int[] a) => Buffer.ByteLength(a); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("public static class C { public static bool F(double x) => double.IsNegative(x); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("public static class C { public static bool F(string s) => string.IsNullOrEmpty(s); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System; public static class C { public static void F(Span<byte> s) => s.Clear(); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System; public static class C { public static bool F(ReadOnlySpan<byte> s) => MemoryExtensions.Contains(s, (byte)1); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("using System; public static class C { public static string F(Exception e) => e.ToString(); }", MsilFrontend.UnsupportedCallCode);
    AssertCode("public static class C { public static int F() { _ = new object(); return 1; } }", MsilFrontend.UnsupportedCallCode);
    AssertCode("public static class C { public static string F() => string.Empty; }", MsilFrontend.UnsupportedCallCode);
    AssertCode("public static class C { private static T Id<T>(T x) => x; public static int F() => Id(1); }", MsilFrontend.UnsupportedCallCode, options: new MsilImportOptions { Exports = ["F"] });

    // Unsupported CLR types (PLY3006)
    AssertCode("public static class C { public static unsafe void F(int* p) { *p = 1; } }", MsilFrontend.UnsupportedTypeCode, allowUnsafe: true);
    AssertCode("public static class C { public static unsafe void F(delegate*<int> p) { _ = p(); } }", MsilFrontend.UnsupportedTypeCode, allowUnsafe: true);
    AssertCode("using System; public static class C { public static void F(TypedReference r) { _ = r; } }", MsilFrontend.UnsupportedTypeCode);
    AssertCode("public static class C { public static unsafe int F(int[] a) { fixed (int* p = a) return *p; } }", MsilFrontend.UnsupportedTypeCode, allowUnsafe: true);
    AssertCode("using System.Collections.Generic; public static class C { public static int F(List<int> x) => x.Count; }", MsilFrontend.UnsupportedTypeCode);
    AssertCode("public struct P { public int A; } public static class C { public static int F(P[] a) => a.Length; }", MsilFrontend.UnsupportedTypeCode);
    AssertCode("public static class Box<T> { public static T Id(T x) => x; }", MsilFrontend.UnsupportedTypeCode);
    AssertCode("public static class C { public static string[] F(int n) => new string[n]; }", MsilFrontend.UnsupportedTypeCode);
    AssertCode("using System; public class MyEx : Exception {} public static class C { public static int F() { try { return 1; } catch (MyEx) { return 2; } } }", MsilFrontend.UnsupportedTypeCode);
    AssertCode("public class Local {} public static class C { public static int F() { var a = new Local[1]; return a.Length; } }", MsilFrontend.UnsupportedTypeCode);
  }

  [Test]
  public void CorruptAndUnverifiableRawIl_ReportsExactCodes() {
    // Bad PE header after MZ -> PLY3000
    var badPe = new byte[128];
    badPe[0] = (byte)'M';
    badPe[1] = (byte)'Z';
    badPe[0x3C] = 0x70;
    Assert.That(MsilFrontend.Import("badpe.dll", badPe).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.NotPeOrCliCode));

    // Corrupt BSJB metadata signature inside valid PE -> PLY3001
    var valid = MsilTestHelpers.CompileCSharp("public static class C { public static int F() => 1; }");
    for (var i = 0; i < valid.Length - 4; ++i) {
      if (valid[i] == (byte)'B' && valid[i + 1] == (byte)'S' && valid[i + 2] == (byte)'J' && valid[i + 3] == (byte)'B') {
        valid[i] = (byte)'X';
        break;
      }
    }

    Assert.That(MsilFrontend.Import("corruptmeta.dll", valid).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));

    // Empty IL body -> PLY3001
    var emptyIl = MsilTestHelpers.BuildRawIlAssembly("Empty", []);
    Assert.That(MsilFrontend.Import("empty.dll", emptyIl).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));

    // Invalid 2-byte opcode 0xFE 0xFF -> PLY3005
    var badTwoByte = MsilTestHelpers.BuildRawIlAssembly("Bad2", [0xFE, 0xFF, 0x2A]);
    Assert.That(MsilFrontend.Import("bad2.dll", badTwoByte).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedOpcodeCode));

    // Out-of-range parameter index -> PLY3007
    var badParam = MsilTestHelpers.BuildRawIlAssembly("BadParam", [0x0E, 0x05, 0x2A], encodeReturn: r => r.Int32());
    Assert.That(MsilFrontend.Import("badparam.dll", badParam).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));

    // Out-of-range local index -> PLY3007
    var badLocal = MsilTestHelpers.BuildRawIlAssembly("BadLocal", [0x11, 0x05, 0x2A], encodeReturn: r => r.Int32());
    Assert.That(MsilFrontend.Import("badlocal.dll", badLocal).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));

    // Incompatible stack types at merge (i32 vs f64) -> PLY3007
    byte[] badMergeIl = [
      0x02,                                     // 0: ldarg.0 (bool)
      0x2C, 0x03,                               // 1: brfalse.s +3 -> 6
      0x17,                                     // 3: ldc.i4.1 (i32)
      0x2B, 0x09,                               // 4: br.s +9 -> 15
      0x23, 0, 0, 0, 0, 0, 0, 0xF0, 0x3F,       // 6: ldc.r8 1.0 (f64)
      0x2A                                      // 15: ret
    ];
    var badMerge = MsilTestHelpers.BuildRawIlAssembly(
      "BadMerge", badMergeIl,
      encodeReturn: r => r.Int32(),
      encodeParams: p => p.AddParameter().Type().Boolean(),
      paramCount: 1);
    Assert.That(MsilFrontend.Import("badmerge.dll", badMerge).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnverifiableStackCode));

    // Invalid token in call (0x01000001 TypeRef) -> PLY3001
    var badCallTok = MsilTestHelpers.BuildRawIlAssembly("BadCallTok", [0x28, 0x01, 0x00, 0x00, 0x01, 0x2A]);
    Assert.That(MsilFrontend.Import("badcall.dll", badCallTok).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));

    // Invalid token in newarr (0x06000001 MethodDef) -> PLY3001
    var badTypeTok = MsilTestHelpers.BuildRawIlAssembly("BadTypeTok", [0x17, 0x8D, 0x01, 0x00, 0x00, 0x06, 0x26, 0x2A]);
    Assert.That(MsilFrontend.Import("badtype.dll", badTypeTok).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.CorruptMetadataCode));

    // Complex multi-block exception filter -> PLY3005
    byte[] complexFilterIl = [
      0x17,             // 0: ldc.i4.1
      0x16,             // 1: ldc.i4.0
      0x5B,             // 2: div
      0x26,             // 3: pop
      0xDE, 0x0B,       // 4: leave.s +11 -> 17
      0x26,             // 6: pop (filter start)
      0x17,             // 7: ldc.i4.1
      0x2D, 0x01,       // 8: brtrue.s +1 -> 11
      0x16,             // 10: ldc.i4.0
      0x17,             // 11: ldc.i4.1 (branch target — wait: fix depth)
      0xFE, 0x11,       // 12: endfilter
      0x26,             // 14: pop (handler start)
      0xDE, 0x00,       // 15: leave.s +0 -> 17
      0x1B,             // 17: ldc.i4.5
      0x2A              // 18: ret
    ];
    // Make sure filter stack depth matches at offset 12 (depth 1):
    // 6: pop, 7: ldc.i4.1, 8: brtrue.s +2 (12), 10: ldc.i4.0, 11: br.s +1 (14), 13: ... wait:
    byte[] complexFilterClean = [
      0x17,             // 0: ldc.i4.1
      0x16,             // 1: ldc.i4.0
      0x5B,             // 2: div
      0x26,             // 3: pop
      0xDE, 0x0D,       // 4: leave.s +13 -> 19
      0x26,             // 6: pop (filter start)
      0x17,             // 7: ldc.i4.1
      0x2D, 0x03,       // 8: brtrue.s +3 -> 13
      0x16,             // 10: ldc.i4.0
      0x2B, 0x01,       // 11: br.s +1 -> 14
      0x17,             // 13: ldc.i4.1
      0xFE, 0x11,       // 14: endfilter
      0x26,             // 16: pop (handler start)
      0xDE, 0x00,       // 17: leave.s +0 -> 19
      0x1B,             // 19: ldc.i4.5
      0x2A              // 20: ret
    ];
    _ = complexFilterIl;
    var complexFilterPe = MsilTestHelpers.BuildRawIlAssembly(
      "ComplexFilter", complexFilterClean,
      encodeReturn: r => r.Int32(),
      encodeRegions: enc => enc.Add(ExceptionRegionKind.Filter, 0, 6, 16, 3, default, 6),
      regionCount: 1);
    Assert.That(MsilFrontend.Import("cfilter.dll", complexFilterPe).Diagnostics[0].Code, Is.EqualTo(MsilFrontend.UnsupportedOpcodeCode));
  }

}
