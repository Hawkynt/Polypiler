using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Cpp.CppTestHarness;

namespace Polypiler.Tests.Backends.Cpp;

/// <summary>
/// Live <c>g++ -Wall -Wextra -Werror</c> tests for <see cref="IrGenericCompare"/> (<c>08-generics.cs</c>),
/// <see cref="IrWriteLine"/> + number/bool formatting (<c>17-stdio.cs</c>), and <see cref="IrForeignCall"/> /
/// <see cref="IrModule.ForeignFunctions"/> (<c>19-native-interop.cs</c>) across C++17, C++20, and C++23.
/// </summary>
[TestFixture]
public sealed class CppShowcaseAndInteropTests {

  [Test]
  public void Showcase08Generics_TranslatesCompilesAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("08-generics.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty);

    var r1 = (long)Eval(mod, "Run", 50, 20L)!;
    var r2 = (long)Eval(mod, "Run", -10, 100L)!;
    var r3 = (long)Eval(mod, "Run", 200, -99L)!;

    var driver = $$"""
      #include "generics_showcase.hpp"
      #include <cassert>
      using namespace generics_showcase;
      int main() {
        assert(run(50, 20LL) == {{r1}}LL);
        assert(run(-10, 100LL) == {{r2}}LL);
        assert(run(200, -99LL) == {{r3}}LL);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void GenericCompare_AllScalarFloatNaNAndNarrowTypes_ParityAcrossAllStandards() {
    var i8a = L("a", IrType.I8);
    var i8b = L("b", IrType.I8);
    var u16a = L("a", IrType.U16);
    var u16b = L("b", IrType.U16);
    var i32a = L("a", IrType.I32);
    var i32b = L("b", IrType.I32);
    var i64a = L("a", IrType.I64);
    var i64b = L("b", IrType.I64);
    var ba = L("a", IrType.Bool);
    var bb = L("b", IrType.Bool);
    var f32a = L("a", IrType.F32);
    var f32b = L("b", IrType.F32);
    var f64a = L("a", IrType.F64);
    var f64b = L("b", IrType.F64);

    var mod = new IrModule("GenCmp", [
      Function("cmpI8", IrType.I32, [i8a, i8b], new IrReturn(new IrGenericCompare(R(i8a), R(i8b)))),
      Function("cmpU16", IrType.I32, [u16a, u16b], new IrReturn(new IrGenericCompare(R(u16a), R(u16b)))),
      Function("cmpI32", IrType.I32, [i32a, i32b], new IrReturn(new IrGenericCompare(R(i32a), R(i32b)))),
      Function("cmpI64", IrType.I32, [i64a, i64b], new IrReturn(new IrGenericCompare(R(i64a), R(i64b)))),
      Function("cmpBool", IrType.I32, [ba, bb], new IrReturn(new IrGenericCompare(R(ba), R(bb)))),
      Function("cmpF32", IrType.I32, [f32a, f32b], new IrReturn(new IrGenericCompare(R(f32a), R(f32b)))),
      Function("cmpF64", IrType.I32, [f64a, f64b], new IrReturn(new IrGenericCompare(R(f64a), R(f64b))))
    ]);

    Assert.That(Eval(mod, "cmpI8", (sbyte)-10, (sbyte)5), Is.EqualTo(-15));
    Assert.That(Eval(mod, "cmpU16", (ushort)100, (ushort)25), Is.EqualTo(75));
    Assert.That(Eval(mod, "cmpI32", int.MinValue, int.MaxValue), Is.EqualTo(-1));
    Assert.That(Eval(mod, "cmpI64", 9L, 9L), Is.EqualTo(0));
    Assert.That(Eval(mod, "cmpBool", false, true), Is.EqualTo(-1));
    Assert.That(Eval(mod, "cmpF64", double.NaN, double.NaN), Is.EqualTo(0));
    Assert.That(Eval(mod, "cmpF64", double.NaN, -1.0), Is.EqualTo(-1));
    Assert.That(Eval(mod, "cmpF64", -1.0, double.NaN), Is.EqualTo(1));
    Assert.That(Eval(mod, "cmpF64", -0.0, 0.0), Is.EqualTo(0));

    var driver = """
      #include "gen_cmp.hpp"
      #include <cassert>
      #include <climits>
      #include <limits>
      using namespace gen_cmp;
      int main() {
        assert(cmp_i8(static_cast<std::int8_t>(-10), static_cast<std::int8_t>(5)) == -15);
        assert(cmp_u16(static_cast<std::uint16_t>(100), static_cast<std::uint16_t>(25)) == 75);
        assert(cmp_i32(INT32_MIN, INT32_MAX) == -1);
        assert(cmp_i32(5, 5) == 0);
        assert(cmp_i32(7, 2) == 1);
        assert(cmp_i64(9LL, 9LL) == 0);
        assert(cmp_bool(false, true) == -1);
        assert(cmp_bool(true, false) == 1);
        assert(cmp_bool(true, true) == 0);

        const float fnan = std::numeric_limits<float>::quiet_NaN();
        assert(cmp_f32(fnan, fnan) == 0);
        assert(cmp_f32(fnan, 0.0f) == -1);
        assert(cmp_f32(0.0f, fnan) == 1);
        assert(cmp_f32(-0.0f, 0.0f) == 0);

        const double dnan = std::numeric_limits<double>::quiet_NaN();
        const double dinf = std::numeric_limits<double>::infinity();
        assert(cmp_f64(dnan, dnan) == 0);
        assert(cmp_f64(dnan, -dinf) == -1);
        assert(cmp_f64(-dinf, dnan) == 1);
        assert(cmp_f64(-0.0, 0.0) == 0);
        assert(cmp_f64(-dinf, dinf) == -1);
        assert(cmp_f64(dinf, -dinf) == 1);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver);
  }

  [Test]
  public void Showcase17Stdio_AndFormatting_TranslatesCompilesAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("17-stdio.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty);

    var irOutput = new List<string>();
    var expectedRet = (int)IrInterpreter.Instantiate(mod, irOutput.Add).Call("Run")!;
    Assert.That(expectedRet, Is.EqualTo(14));
    Assert.That(irOutput, Is.EqualTo(new[] { "step 1: sum=1", "step 2: sum=5", "step 3: sum=14" }));

    var driver = """
      #include "stdio_showcase.hpp"
      #include <cassert>
      using namespace stdio_showcase;
      int main() {
        assert(run() == 14);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver, stdout => {
      var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
      Assert.That(lines, Is.EqualTo(new[] { "step 1: sum=1", "step 2: sum=5", "step 3: sum=14" }));
    });
  }

  [Test]
  public void WriteLineNullAndNumberFormatting_ParityAcrossAllStandards() {
    var i = L("i", IrType.I32);
    var d = L("d", IrType.F64);
    var f = L("f", IrType.F32);
    var b = L("b", IrType.Bool);

    var mod = new IrModule("StdioFmt", [
      Function("writeNullAndText", IrType.Void, [],
        new IrWriteLine(new IrStrConst("first")),
        new IrWriteLine(new IrNull(IrType.String)),
        new IrWriteLine(new IrCall("missing", IrType.String, [])),
        new IrWriteLine(new IrStrConst("last")),
        new IrReturn(null)),
      Function("missing", false, IrType.String, [], new IrReturn(new IrNull(IrType.String))),
      Function("fmtIntDotNet", IrType.String, [i], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(i)]))),
      Function("fmtIntEcma", IrType.String, [i], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatEcma, IrType.String, [R(i)]))),
      Function("fmtDoubleDotNet", IrType.String, [d], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(d)]))),
      Function("fmtDoubleEcma", IrType.String, [d], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatEcma, IrType.String, [R(d)]))),
      Function("fmtFloatDotNet", IrType.String, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [R(f)]))),
      Function("fmtBoolDotNet", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [R(b)]))),
      Function("fmtBoolEcma", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [R(b)])))
    ]);

    var driver = """
      #include "stdio_fmt.hpp"
      #include <cassert>
      #include <limits>
      using namespace stdio_fmt;
      int main() {
        write_null_and_text();
        assert(fmt_int_dot_net(-42) == "-42");
        assert(fmt_int_ecma(123) == "123");
        assert(fmt_double_dot_net(1.5) == "1.5");
        assert(fmt_double_dot_net(std::numeric_limits<double>::quiet_NaN()) == "NaN");
        assert(fmt_double_dot_net(std::numeric_limits<double>::infinity()) == "Infinity");
        assert(fmt_double_dot_net(-std::numeric_limits<double>::infinity()) == "-Infinity");
        assert(fmt_double_dot_net(-0.0) == "-0");
        assert(fmt_double_dot_net(0.0) == "0");
        assert(fmt_double_dot_net(1e20) == "1E+20");
        assert(fmt_double_ecma(-0.0) == "0");
        assert(fmt_double_ecma(2.25) == "2.25");
        assert(fmt_float_dot_net(3.5f) == "3.5");
        assert(fmt_bool_dot_net(true) == "True" && fmt_bool_dot_net(false) == "False");
        assert(fmt_bool_ecma(true) == "true" && fmt_bool_ecma(false) == "false");
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver, stdout => {
      var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
      Assert.That(lines, Is.EqualTo(new[] { "first", "", "", "last" }));
    });
  }

  [Test]
  public void Showcase19NativeInterop_CompilesLinksAndRunsAcrossAllStandards() {
    var mod = ImportShowcase("19-native-interop.cs");
    foreach (var profile in AllProfiles)
      Assert.That(CppEmitter.Check(Legalize(mod, profile), profile), Is.Empty);

    var companion = """
      #include <cstdint>
      struct CPoint {
        std::int32_t x;
        std::int32_t y;
      };
      extern "C" std::int32_t geom_norm(CPoint p) {
        return p.x * p.x + p.y * p.y;
      }
      """;

    var driver = """
      #include "native_interop_showcase.hpp"
      #include <cassert>
      #include <cstddef>
      #include <type_traits>
      using namespace native_interop_showcase;
      static_assert(std::is_standard_layout_v<NativePoint>);
      static_assert(std::is_trivially_copyable_v<NativePoint>);
      static_assert(sizeof(NativePoint) == 8);
      static_assert(offsetof(NativePoint, x) == 0);
      static_assert(offsetof(NativePoint, y) == 4);
      int main() {
        assert(measure(3, 4) == 25);
        assert(measure(-5, 12) == 169);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver, companionCpp: companion);
  }

  [Test]
  public void ForeignCalls_AllMarshalKindsAndPackedStructs_CompileLinkAndRunAcrossAllStandards() {
    var packed = new IrStructType("PackedPair", [new IrField("Tag", IrType.U8, false), new IrField("Val", IrType.I32, false)]) {
      Layout = new IrStructLayout(IrLayoutKind.Sequential, Pack: 1, Size: 0, Offsets: [])
    };
    var packedType = IrType.Struct(packed);

    var fPubBool = new IrForeignFunction("pub_bool", true, "native", IrType.Bool, [
      new("flag", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool }),
      new("u1", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.U1 }),
      new("i1", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.I1 })
    ]) { Result = new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool } };

    var fDupSymbol = new IrForeignFunction("pub_bool_alias", false, "native", IrType.Bool, [
      new("flag", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool }),
      new("u1", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.U1 }),
      new("i1", IrType.Bool, new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.I1 })
    ]) { EntryPoint = "pub_bool", Result = new(IrMarshalKind.Bool) { BoolForm = IrBoolForm.Bool } };

    var fStrings = new IrForeignFunction("str_lens", false, "native", IrType.I32, [
      new("utf8", IrType.String, new(IrMarshalKind.String) { Encoding = IrStringEncoding.Utf8 }),
      new("utf16", IrType.String, new(IrMarshalKind.String) { Encoding = IrStringEncoding.Unicode })
    ]);

    var fArraysAndRefs = new IrForeignFunction("arr_ref_ops", false, "native", IrType.I32, [
      new("inArr", IrType.Array(IrType.I32), new(IrMarshalKind.Array, IrMarshalDirection.In)),
      new("outArr", IrType.Array(IrType.I32), new(IrMarshalKind.Array, IrMarshalDirection.InOut)),
      new("refIn", IrType.Array(IrType.I32), new(IrMarshalKind.ByRef, IrMarshalDirection.In)),
      new("refOut", IrType.Array(IrType.I32), new(IrMarshalKind.ByRef, IrMarshalDirection.Out)),
      new("count", IrType.I32, IrMarshal.Blittable)
    ]);

    var fNativeAndHandle = new IrForeignFunction("native_handle", false, "native", IrType.I64, [
      new("h", IrType.I64, new(IrMarshalKind.Handle)),
      new("ni", IrType.I64, new(IrMarshalKind.Blittable) { NativeSize = true }),
      new("nu", IrType.U64, new(IrMarshalKind.Blittable) { NativeSize = true }),
      new("refNi", IrType.Array(IrType.I64), new(IrMarshalKind.ByRef, IrMarshalDirection.InOut) { NativeSize = true }),
      new("refNu", IrType.Array(IrType.U64), new(IrMarshalKind.ByRef, IrMarshalDirection.In) { NativeSize = true })
    ]) { Result = new(IrMarshalKind.Handle) };

    var fVoidNativeRef = new IrForeignFunction("bump_native_void", false, "native", IrType.Void, [
      new("refNi", IrType.Array(IrType.I64), new(IrMarshalKind.ByRef, IrMarshalDirection.InOut) { NativeSize = true })
    ]);

    var fNativeRet = new IrForeignFunction("ret_native", false, "native", IrType.U64, [
      new("p", packedType, new(IrMarshalKind.Struct))
    ]) { Result = new(IrMarshalKind.Blittable) { NativeSize = true } };

    var inArr = L("inArr", IrType.Array(IrType.I32));
    var outArr = L("outArr", IrType.Array(IrType.I32));
    var refIn = L("refIn", IrType.Array(IrType.I32));
    var refOut = L("refOut", IrType.Array(IrType.I32));
    var refNi = L("refNi", IrType.Array(IrType.I64));
    var refNu = L("refNu", IrType.Array(IrType.U64));

    var mod = new IrModule("InteropAll", [
      Function("testBool", IrType.Bool, [],
        new IrReturn(Bin(IrBinaryOp.LogicalAnd,
          new IrForeignCall("pub_bool", IrType.Bool, [B(true), B(false), B(true)]),
          new IrForeignCall("pub_bool_alias", IrType.Bool, [B(true), B(true), B(false)])))),
      Function("testStrings", IrType.I32, [L("s", IrType.String)],
        new IrReturn(new IrForeignCall("str_lens", IrType.I32, [new IrStrConst("hello"), R(L("s", IrType.String))]))),
      Function("testArrays", IrType.I32, [inArr, outArr],
        new IrDeclare(refIn, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(10)])),
        new IrDeclare(refOut, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(0)])),
        new IrDeclare(L("s", IrType.I32), new IrForeignCall("arr_ref_ops", IrType.I32, [R(inArr), R(outArr), R(refIn), R(refOut), new IrArrayLength(R(inArr))])),
        new IrReturn(Bin(IrBinaryOp.Add, R(L("s", IrType.I32)), new IrArrayLoad(R(refOut), I(0))))),
      Function("testNativeAndPacked", IrType.I64, [],
        new IrDeclare(refNi, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I64), [I64(5)])),
        new IrDeclare(refNu, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U64), [U64(7)])),
        new IrDeclare(L("r", IrType.I64), new IrForeignCall("native_handle", IrType.I64, [I64(100), I64(20), U64(3), R(refNi), R(refNu)])),
        new IrExprStmt(new IrForeignCall("bump_native_void", IrType.Void, [R(refNi)])),
        new IrDeclare(L("p", packedType), new IrStructNew(packedType, [C(IrType.U8, 4), I(11)])),
        new IrDeclare(L("u", IrType.U64), new IrForeignCall("ret_native", IrType.U64, [R(L("p", packedType))])),
        new IrReturn(Bin(IrBinaryOp.Add, R(L("r", IrType.I64)), Bin(IrBinaryOp.Add, new IrArrayLoad(R(refNi), I(0)), new IrConvert(IrType.I64, R(L("u", IrType.U64)))))))
    ]) {
      Types = [packed],
      ForeignFunctions = [fPubBool, fDupSymbol, fStrings, fArraysAndRefs, fNativeAndHandle, fVoidNativeRef, fNativeRet]
    };

    var companion = """
      #include <cstddef>
      #include <cstdint>
      #include <cstring>

      #pragma pack(push, 1)
      struct CPackedPair {
        std::uint8_t tag;
        std::int32_t val;
      };
      #pragma pack(pop)

      extern "C" {
        std::int32_t pub_bool(std::int32_t flag, std::uint8_t u1, std::int8_t i1) {
          return (flag != 0 && (u1 != 0 || i1 != 0)) ? 1 : 0;
        }
        std::int32_t str_lens(const char* utf8, const char16_t* utf16) {
          std::size_t n1 = std::strlen(utf8);
          std::size_t n2 = 0;
          while (utf16[n2] != u'\0') ++n2;
          return static_cast<std::int32_t>(n1 * 100 + n2);
        }
        std::int32_t arr_ref_ops(const std::int32_t* in_arr, std::int32_t* out_arr, const std::int32_t* ref_in, std::int32_t* ref_out, std::int32_t count) {
          std::int32_t sum = 0;
          for (std::int32_t i = 0; i < count; ++i) {
            out_arr[i] = in_arr[i] * 2;
            sum += in_arr[i];
          }
          *ref_out = *ref_in + 5;
          return sum;
        }
        std::intptr_t native_handle(std::intptr_t h, std::intptr_t ni, std::uintptr_t nu, std::intptr_t* ref_ni, const std::uintptr_t* ref_nu) {
          *ref_ni = *ref_ni + static_cast<std::intptr_t>(*ref_nu);
          return h + ni + static_cast<std::intptr_t>(nu);
        }
        void bump_native_void(std::intptr_t* ref_ni) {
          *ref_ni += 1;
        }
        std::uintptr_t ret_native(CPackedPair p) {
          return static_cast<std::uintptr_t>(p.tag) + static_cast<std::uintptr_t>(p.val);
        }
      }
      """;

    var driver = """
      #include "interop_all.hpp"
      #include <cassert>
      #include <vector>
      using namespace interop_all;
      static_assert(sizeof(PackedPair) == 5);
      int main() {
        assert(test_bool() == true);
        // "hello" has 5 bytes; UTF-8 "A\xC3\xA9\xE2\x98\x83\xF0\x9F\x98\x80" decodes to 1+1+1+2 = 5 UTF-16 code units
        assert(test_strings(std::string("A\xC3\xA9\xE2\x98\x83\xF0\x9F\x98\x80")) == 505);
        std::vector<std::int32_t> a{1, 2, 3};
        std::vector<std::int32_t> b{0, 0, 0};
        assert(test_arrays(a, b) == 21);
        assert(b[0] == 2 && b[1] == 4 && b[2] == 6);
        // r = 100+20+3 = 123; refNi = (5+7)+1 = 13; u = 4+11 = 15 => 151
        assert(test_native_and_packed() == 151LL);
        return 0;
      }
      """;
    CompileAndRunAllStandards(mod, driver, companionCpp: companion);

    // IrLastError remains cleanly refused with PLY1450.
    var lastErrMod = new IrModule("LastErr", [Function("err", IrType.I32, [], new IrReturn(new IrLastError(true)))]);
    Assert.That(CppEmitter.Check(lastErrMod).Any(d => d.Code == CppEmitter.UnsupportedConstructCode), Is.True);
  }

}
