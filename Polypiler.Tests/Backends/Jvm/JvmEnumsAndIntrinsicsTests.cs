using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tooling.Report;
using static Polypiler.Tests.Backends.Jvm.JvmTestRunner;

namespace Polypiler.Tests.Backends.Jvm;

/// <summary>
/// Live <c>java -Xverify:all</c> execution, interpreter-parity, and disassembly tests for all 6 enum intrinsics,
/// showcase 11 (<c>11-enums-and-optionals.cs</c>), and standard JDK string/math/bit/format intrinsics across
/// <c>jvm/8</c>, <c>jvm/17</c>, and <c>jvm/21</c>.
/// </summary>
[TestFixture]
public sealed class JvmEnumsAndIntrinsicsTests {

  private static string ShowcasePath(string fileName) {
    var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (dir is not null) {
      var candidate = Path.Combine(dir.FullName, "docs", "showcase", fileName);
      if (File.Exists(candidate))
        return candidate;
      dir = dir.Parent;
    }

    throw new FileNotFoundException($"Could not locate showcase file '{fileName}'.");
  }

  [Test]
  public void Showcase11_EnumsAndOptionals_ExecutesLiveAcrossAllJvmProfiles() {
    var source = File.ReadAllText(ShowcasePath("11-enums-and-optionals.cs"));
    var imported = CSharpFrontend.Import("11-enums-and-optionals.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);

    var expected1 = IrInterpreter.Call(imported.Module!, "Resolve", [3, 5]);
    var expected2 = IrInterpreter.Call(imported.Module!, "Resolve", [3, null!]);
    var expected3 = IrInterpreter.Call(imported.Module!, "Resolve", [1, 5]);
    var expected4 = IrInterpreter.Call(imported.Module!, "Resolve", [1, null!]);

    foreach (var profile in new[] { JvmProfile.Java8, JvmProfile.Java17, JvmProfile.Java21 }) {
      var legalized = JvmEmitter.Legalize(imported.Module!, () => PrecomputedImplementations.Module, profile);
      var diagnostics = JvmEmitter.Check(legalized, profile);
      Assert.That(diagnostics, Is.Empty, $"Check failed ({profile}): {string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");

      var files = JvmEmitter.Emit(legalized, profile);
      var disasm = JvmDisassembler.DisassembleJar(files[0].GetBytes());
      Assert.That(disasm, Does.Contain("Resolve"));

      var output = RunHarness(files, $$"""
        public class Harness {
          public static void main(String[] args) {
            System.out.println(EnumsAndOptionalsShowcase.Resolve(3, Integer.valueOf(5)) == {{expected1}});
            System.out.println(EnumsAndOptionalsShowcase.Resolve(3, null) == {{expected2}});
            System.out.println(EnumsAndOptionalsShowcase.Resolve(1, Integer.valueOf(5)) == {{expected3}});
            System.out.println(EnumsAndOptionalsShowcase.Resolve(1, null) == {{expected4}});
          }
        }
        """);
      foreach (var line in output.Split('\n'))
        Assert.That(line, Is.EqualTo("true"), $"{profile}:\n{output}");
    }
  }

  [Test]
  public void EnumIntrinsics_AllSixOperations_ExecuteLiveMatchingEnumOperations() {
    var perms = new IrEnumType("Perms", IrType.I32, [
      new("None", 0), new("Read", 1), new("Write", 2), new("Execute", 4), new("All", 7)
    ], isFlags: true);
    var color = new IrEnumType("Color", IrType.U8, [
      new("Red", 1), new("Green", 2), new("Blue", 200)
    ], isFlags: false);
    var smallFlags = new IrEnumType("SmallFlags", IrType.I8, [
      new("Bit0", 1), new("Bit7", -128)
    ], isFlags: true);
    var midFlags = new IrEnumType("MidFlags", IrType.I16, [
      new("Low", 1), new("High", -32768)
    ], isFlags: true);
    var u16Enum = new IrEnumType("U16Enum", IrType.U16, [
      new("One", 1), new("Top", 65535)
    ], isFlags: false);
    var u32Enum = new IrEnumType("U32Enum", IrType.U32, [
      new("Lo", 1), new("Hi", unchecked((long)0x8000_0000U))
    ], isFlags: true);
    var i64Enum = new IrEnumType("I64Enum", IrType.I64, [
      new("Neg", -5), new("Pos", 5)
    ], isFlags: false);
    var bigFlags = new IrEnumType("BigFlags", IrType.U64, [
      new("Empty", 0), new("Lo", 1), new("Hi", unchecked((long)0x8000_0000_0000_0000UL))
    ], isFlags: true);
    var dupEnum = new IrEnumType("DupEnum", IrType.I32, [
      new("A", 1), new("B", 1), new("C", 2)
    ], isFlags: false);

    var p1 = L("a", perms.Type);
    var p2 = L("b", perms.Type);
    var c1 = L("c", color.Type);
    var sf1 = L("sf", smallFlags.Type);
    var mf1 = L("mf", midFlags.Type);
    var u16p = L("u16", u16Enum.Type);
    var u32p = L("u32", u32Enum.Type);
    var i64p = L("i64", i64Enum.Type);
    var bf1 = L("bf", bigFlags.Type);
    var bf2 = L("bf2", bigFlags.Type);
    var de1 = L("de", dupEnum.Type);
    var txt = L("txt", IrType.String);
    var ic = L("ic", IrType.Bool);

    var mod = new IrModule("JvmEnumMod", [
      Function("HasFlag32", IrType.Bool, [p1, p2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [R(p1), R(p2)]))),
      Function("HasFlag64", IrType.Bool, [bf1, bf2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [R(bf1), R(bf2)]))),
      Function("IsDefPerms", IrType.Bool, [p1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(p1)]))),
      Function("IsDefColor", IrType.Bool, [c1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(c1)]))),
      Function("IsDefDup", IrType.Bool, [de1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(de1)]))),
      Function("IsDefBig", IrType.Bool, [bf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(bf1)]))),
      Function("ToStrPerms", IrType.String, [p1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(p1)]))),
      Function("ToStrColor", IrType.String, [c1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(c1)]))),
      Function("ToStrSmall", IrType.String, [sf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(sf1)]))),
      Function("ToStrMid", IrType.String, [mf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(mf1)]))),
      Function("ToStrU32", IrType.String, [u32p], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(u32p)]))),
      Function("ToStrI64", IrType.String, [i64p], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(i64p)]))),
      Function("ToStrBig", IrType.String, [bf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(bf1)]))),
      Function("ParsePerms", perms.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, perms.Type, [R(txt), R(ic)]))),
      Function("ParseColor", color.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, color.Type, [R(txt), R(ic)]))),
      Function("ParseSmall", smallFlags.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, smallFlags.Type, [R(txt), R(ic)]))),
      Function("ParseMid", midFlags.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, midFlags.Type, [R(txt), R(ic)]))),
      Function("ParseU16", u16Enum.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, u16Enum.Type, [R(txt), R(ic)]))),
      Function("ParseU32", u32Enum.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, u32Enum.Type, [R(txt), R(ic)]))),
      Function("ParseI64", i64Enum.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, i64Enum.Type, [R(txt), R(ic)]))),
      Function("ParseBig", bigFlags.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, bigFlags.Type, [R(txt), R(ic)]))),
      Function("NamesPerms", IrType.Array(IrType.String), [], new IrReturn(EnumOperations.Listing(IrIntrinsicOp.EnumNames, perms))),
      Function("ValuesPerms", IrType.Array(perms.Type), [], new IrReturn(EnumOperations.Listing(IrIntrinsicOp.EnumValues, perms))),
      Function("ValuesBig", IrType.Array(bigFlags.Type), [], new IrReturn(EnumOperations.Listing(IrIntrinsicOp.EnumValues, bigFlags)))
    ]) {
      Types = [perms, color, smallFlags, midFlags, u16Enum, u32Enum, i64Enum, bigFlags, dupEnum]
    };

    foreach (var profile in new[] { JvmProfile.Java8, JvmProfile.Java21 }) {
      var files = JvmEmitter.Emit(mod, profile);
      var disasm = JvmDisassembler.DisassembleJar(files[0].GetBytes());
      Assert.That(disasm, Does.Contain("$enumToString_"));

      var output = RunHarness(files, """
        import java.util.Arrays;
        public class Harness {
          static boolean throwsIae(Runnable r) {
            try { r.run(); return false; } catch (IllegalArgumentException e) { return true; }
          }
          static boolean throwsArith(Runnable r) {
            try { r.run(); return false; } catch (ArithmeticException e) { return true; }
          }
          public static void main(String[] args) {
            System.out.println(JvmEnumMod.HasFlag32(5, 4) && !JvmEnumMod.HasFlag32(5, 2));
            System.out.println(JvmEnumMod.HasFlag64(0x8000000000000001L, 0x8000000000000000L) && !JvmEnumMod.HasFlag64(1L, 0x8000000000000000L));
            System.out.println(JvmEnumMod.IsDefPerms(2) && !JvmEnumMod.IsDefPerms(3));
            System.out.println(JvmEnumMod.IsDefColor((byte)200) && !JvmEnumMod.IsDefColor((byte)99));
            System.out.println(JvmEnumMod.IsDefDup(1) && !JvmEnumMod.IsDefDup(99));
            System.out.println(JvmEnumMod.IsDefBig(0x8000000000000000L) && !JvmEnumMod.IsDefBig(99L));
            System.out.println(JvmEnumMod.ToStrPerms(0).equals("None"));
            System.out.println(JvmEnumMod.ToStrPerms(5).equals("Read, Execute"));
            System.out.println(JvmEnumMod.ToStrPerms(7).equals("All"));
            System.out.println(JvmEnumMod.ToStrPerms(9).equals("9"));
            System.out.println(JvmEnumMod.ToStrColor((byte)200).equals("Blue") && JvmEnumMod.ToStrColor((byte)255).equals("255"));
            System.out.println(JvmEnumMod.ToStrSmall((byte)-127).equals("Bit0, Bit7") && JvmEnumMod.ToStrSmall((byte)0).equals("0"));
            System.out.println(JvmEnumMod.ToStrMid((short)-32767).equals("Low, High"));
            System.out.println(JvmEnumMod.ToStrU32(0x80000002).equals("2147483650"));
            System.out.println(JvmEnumMod.ToStrI64(-99L).equals("-99"));
            System.out.println(JvmEnumMod.ToStrBig(0x8000000000000001L).equals("Lo, Hi") && JvmEnumMod.ToStrBig(-2L).equals("18446744073709551614"));
            System.out.println(JvmEnumMod.ParsePerms("Read, Execute", false) == 5);
            System.out.println(JvmEnumMod.ParsePerms(" read , write ", true) == 3);
            System.out.println(JvmEnumMod.ParsePerms(" +6 ", false) == 6 && JvmEnumMod.ParsePerms("-1", false) == -1);
            System.out.println((JvmEnumMod.ParseColor("Blue", false) & 0xFF) == 200);
            System.out.println(JvmEnumMod.ParseSmall("Bit0, Bit7", false) == (byte)-127);
            System.out.println(JvmEnumMod.ParseMid("Low, High", false) == (short)-32767);
            System.out.println(JvmEnumMod.ParseU16("Top", false) == (char)65535);
            System.out.println(JvmEnumMod.ParseU32("2147483648", false) == 0x80000000);
            System.out.println(JvmEnumMod.ParseI64("-5", false) == -5L && JvmEnumMod.ParseI64("Pos", false) == 5L);
            System.out.println(JvmEnumMod.ParseBig("lo, hi", true) == 0x8000000000000001L && JvmEnumMod.ParseBig("42", false) == 42L);
            System.out.println(throwsIae(() -> JvmEnumMod.ParsePerms("read", false)));
            System.out.println(throwsIae(() -> JvmEnumMod.ParsePerms("   ", false)));
            System.out.println(throwsIae(() -> JvmEnumMod.ParsePerms("+", false)));
            System.out.println(throwsIae(() -> JvmEnumMod.ParsePerms("+abc", false)));
            System.out.println(throwsIae(() -> JvmEnumMod.ParsePerms("+1/", false)));
            System.out.println(throwsArith(() -> JvmEnumMod.ParsePerms("9999999999999", false)));
            System.out.println(throwsArith(() -> JvmEnumMod.ParsePerms("99999999999999999999999", false)));
            System.out.println(throwsArith(() -> JvmEnumMod.ParseColor("-1", false)));
            System.out.println(throwsArith(() -> JvmEnumMod.ParseColor("300", false)));
            System.out.println(Arrays.equals(JvmEnumMod.NamesPerms(), new String[] { "None", "Read", "Write", "Execute", "All" }));
            System.out.println(Arrays.equals(JvmEnumMod.ValuesPerms(), new int[] { 0, 1, 2, 4, 7 }));
            System.out.println(Arrays.equals(JvmEnumMod.ValuesBig(), new long[] { 0L, 1L, 0x8000000000000000L }));
          }
        }
        """);
      foreach (var line in output.Split('\n'))
        Assert.That(line, Is.EqualTo("true"), $"{profile}:\n{output}");
    }
  }

  [Test]
  public void ExtendedJdkIntrinsics_FormatStringsMathFmaAndBits_ExecuteAcrossJvm8And21() {
    var b = L("b", IrType.Bool);
    var i8 = L("i8", IrType.I8);
    var u8 = L("u8", IrType.U8);
    var u16 = L("u16", IrType.U16);
    var u16b = L("u16b", IrType.U16);
    var i32 = L("i32", IrType.I32);
    var i32b = L("i32b", IrType.I32);
    var i32c = L("i32c", IrType.I32);
    var u32 = L("u32", IrType.U32);
    var u32b = L("u32b", IrType.U32);
    var u32c = L("u32c", IrType.U32);
    var i64 = L("i64", IrType.I64);
    var i64b = L("i64b", IrType.I64);
    var i64c = L("i64c", IrType.I64);
    var u64 = L("u64", IrType.U64);
    var u64b = L("u64b", IrType.U64);
    var u64c = L("u64c", IrType.U64);
    var f32 = L("f32", IrType.F32);
    var f32b = L("f32b", IrType.F32);
    var f32c = L("f32c", IrType.F32);
    var f64 = L("f64", IrType.F64);
    var f64b = L("f64b", IrType.F64);
    var f64c = L("f64c", IrType.F64);
    var s1 = L("s1", IrType.String);
    var s2 = L("s2", IrType.String);
    var s3 = L("s3", IrType.String);
    var sa = L("sa", IrType.Array(IrType.String));

    var mod = new IrModule("JvmExtIntr", [
      // Bool formatting
      Function("FmtBoolNet", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [R(b)]))),
      Function("FmtBoolEcma", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [R(b)]))),
      // Strings
      Function("FromUnit", IrType.String, [u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [R(u16)]))),
      Function("StartsS", IrType.Bool, [s1, s2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(s1), R(s2)]))),
      Function("StartsC", IrType.Bool, [s1, u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrStartsWith, IrType.Bool, [R(s1), R(u16)]))),
      Function("EndsS", IrType.Bool, [s1, s2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(s1), R(s2)]))),
      Function("EndsC", IrType.Bool, [s1, u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrEndsWith, IrType.Bool, [R(s1), R(u16)]))),
      Function("ContainsS", IrType.Bool, [s1, s2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(s1), R(s2)]))),
      Function("ContainsC", IrType.Bool, [s1, u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [R(s1), R(u16)]))),
      Function("Idx2S", IrType.I32, [s1, s2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s1), R(s2)]))),
      Function("Idx3S", IrType.I32, [s1, s2, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s1), R(s2), R(i32)]))),
      Function("Idx2C", IrType.I32, [s1, u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s1), R(u16)]))),
      Function("Idx3C", IrType.I32, [s1, u16, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [R(s1), R(u16), R(i32)]))),
      Function("LastIdx2S", IrType.I32, [s1, s2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s1), R(s2)]))),
      Function("LastIdx3S", IrType.I32, [s1, s2, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s1), R(s2), R(i32)]))),
      Function("LastIdx3C", IrType.I32, [s1, u16, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s1), R(u16), R(i32)]))),
      Function("Join", IrType.String, [s1, sa], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [R(s1), R(sa)]))),
      Function("ReplAllS", IrType.String, [s1, s2, s3], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(s1), R(s2), R(s3)]))),
      Function("ReplAllC", IrType.String, [s1, u16, u16b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(s1), R(u16), R(u16b)]))),
      Function("ReplFirst", IrType.String, [s1, s2, s3], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceFirst, IrType.String, [R(s1), R(s2), R(s3)]))),
      Function("TrimNet", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("TrimStartNet", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimStart, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("TrimEndEcma", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimEnd, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.Ecma)]))),
      Function("TrimEcma", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.Ecma)]))),
      Function("SplitS", IrType.Array(IrType.String), [s1, s2, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s1), R(s2), R(i32)]))),
      Function("SplitC", IrType.Array(IrType.String), [s1, u16, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s1), R(u16), R(i32)]))),
      // Bit / Int intrinsics
      Function("RevI8", IrType.I8, [i8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.I8, [R(i8)]))),
      Function("RevU8", IrType.U8, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U8, [R(u8)]))),
      Function("RevU32", IrType.U32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U32, [R(u32)]))),
      Function("RevU64", IrType.U64, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U64, [R(u64)]))),
      Function("IsPow2I32", IrType.Bool, [i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(i32)]))),
      Function("IsPow2U32", IrType.Bool, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(u32)]))),
      Function("IsPow2I64", IrType.Bool, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(i64)]))),
      Function("IsPow2U64", IrType.Bool, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(u64)]))),
      Function("BigMulS", IrType.I64, [i32, i32b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BigMul, IrType.I64, [R(i32), R(i32b)]))),
      Function("BigMulU", IrType.U64, [u32, u32b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BigMul, IrType.U64, [R(u32), R(u32b)]))),
      Function("ClampI32", IrType.I32, [i32, i32b, i32c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.I32, [R(i32), R(i32b), R(i32c)]))),
      Function("ClampU32", IrType.U32, [u32, u32b, u32c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.U32, [R(u32), R(u32b), R(u32c)]))),
      Function("ClampI64", IrType.I64, [i64, i64b, i64c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.I64, [R(i64), R(i64b), R(i64c)]))),
      Function("ClampU64", IrType.U64, [u64, u64b, u64c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.U64, [R(u64), R(u64b), R(u64c)]))),
      Function("ClampF32", IrType.F32, [f32, f32b, f32c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.F32, [R(f32), R(f32b), R(f32c)]))),
      Function("ClampF64", IrType.F64, [f64, f64b, f64c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.F64, [R(f64), R(f64b), R(f64c)]))),
      // Math / FMA
      Function("FmaF32", IrType.F32, [f32, f32b, f32c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FusedMultiplyAdd, IrType.F32, [R(f32), R(f32b), R(f32c)]))),
      Function("FmaF64", IrType.F64, [f64, f64b, f64c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FusedMultiplyAdd, IrType.F64, [R(f64), R(f64b), R(f64c)]))),
      Function("MathF32", IrType.F32, [f32, f32b, f32c], new IrReturn(
        Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F32, [R(f32)]),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.Cos, IrType.F32, [R(f32)]),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.Tan, IrType.F32, [R(f32)]),
              Bin(IrBinaryOp.Add,
                new IrIntrinsic(IrIntrinsicOp.Asin, IrType.F32, [R(f32)]),
                Bin(IrBinaryOp.Add,
                  new IrIntrinsic(IrIntrinsicOp.Acos, IrType.F32, [R(f32)]),
                  Bin(IrBinaryOp.Add,
                    new IrIntrinsic(IrIntrinsicOp.Atan, IrType.F32, [R(f32)]),
                    Bin(IrBinaryOp.Add,
                      new IrIntrinsic(IrIntrinsicOp.Atan2, IrType.F32, [R(f32), R(f32b)]),
                      Bin(IrBinaryOp.Add,
                        new IrIntrinsic(IrIntrinsicOp.Sinh, IrType.F32, [R(f32)]),
                        Bin(IrBinaryOp.Add,
                          new IrIntrinsic(IrIntrinsicOp.Cosh, IrType.F32, [R(f32)]),
                          Bin(IrBinaryOp.Add,
                            new IrIntrinsic(IrIntrinsicOp.Tanh, IrType.F32, [R(f32)]),
                            Bin(IrBinaryOp.Add,
                              new IrIntrinsic(IrIntrinsicOp.Exp, IrType.F32, [R(f32)]),
                              Bin(IrBinaryOp.Add,
                                new IrIntrinsic(IrIntrinsicOp.Log, IrType.F32, [R(f32b)]),
                                Bin(IrBinaryOp.Add,
                                  new IrIntrinsic(IrIntrinsicOp.Log10, IrType.F32, [R(f32b)]),
                                  Bin(IrBinaryOp.Add,
                                    new IrIntrinsic(IrIntrinsicOp.Pow, IrType.F32, [R(f32b), R(f32c)]),
                                    new IrIntrinsic(IrIntrinsicOp.Cbrt, IrType.F32, [R(f32c)]))))))))))))))))),
      Function("MathF64", IrType.F64, [f64, f64b, f64c], new IrReturn(
        Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [R(f64)]),
          Bin(IrBinaryOp.Add,
            new IrIntrinsic(IrIntrinsicOp.Cos, IrType.F64, [R(f64)]),
            Bin(IrBinaryOp.Add,
              new IrIntrinsic(IrIntrinsicOp.Tan, IrType.F64, [R(f64)]),
              Bin(IrBinaryOp.Add,
                new IrIntrinsic(IrIntrinsicOp.Asin, IrType.F64, [R(f64)]),
                Bin(IrBinaryOp.Add,
                  new IrIntrinsic(IrIntrinsicOp.Acos, IrType.F64, [R(f64)]),
                  Bin(IrBinaryOp.Add,
                    new IrIntrinsic(IrIntrinsicOp.Atan, IrType.F64, [R(f64)]),
                    Bin(IrBinaryOp.Add,
                      new IrIntrinsic(IrIntrinsicOp.Atan2, IrType.F64, [R(f64), R(f64b)]),
                      Bin(IrBinaryOp.Add,
                        new IrIntrinsic(IrIntrinsicOp.Sinh, IrType.F64, [R(f64)]),
                        Bin(IrBinaryOp.Add,
                          new IrIntrinsic(IrIntrinsicOp.Cosh, IrType.F64, [R(f64)]),
                          Bin(IrBinaryOp.Add,
                            new IrIntrinsic(IrIntrinsicOp.Tanh, IrType.F64, [R(f64)]),
                            Bin(IrBinaryOp.Add,
                              new IrIntrinsic(IrIntrinsicOp.Exp, IrType.F64, [R(f64)]),
                              Bin(IrBinaryOp.Add,
                                new IrIntrinsic(IrIntrinsicOp.Log, IrType.F64, [R(f64b)]),
                                Bin(IrBinaryOp.Add,
                                  new IrIntrinsic(IrIntrinsicOp.Log10, IrType.F64, [R(f64b)]),
                                  Bin(IrBinaryOp.Add,
                                    new IrIntrinsic(IrIntrinsicOp.Pow, IrType.F64, [R(f64b), R(f64c)]),
                                    new IrIntrinsic(IrIntrinsicOp.Cbrt, IrType.F64, [R(f64c)])))))))))))))))))
    ]);

    var expectedMathF32 = (float)IrInterpreter.Call(mod, "MathF32", [0.5f, 2.0f, 8.0f])!;
    var expectedMathF64 = (double)IrInterpreter.Call(mod, "MathF64", [0.5, 2.0, 8.0])!;

    foreach (var profile in new[] { JvmProfile.Java8, JvmProfile.Java17, JvmProfile.Java21 }) {
      var files = JvmEmitter.Emit(mod, profile);
      var disasm = JvmDisassembler.DisassembleJar(files[0].GetBytes());
      if (profile == JvmProfile.Java8)
        Assert.That(disasm, Does.Contain("$fmaD"));
      else
        Assert.That(disasm, Does.Contain("java/lang/Math.fma"));

      var output = RunHarness(files, $$"""
        import java.util.Arrays;
        public class Harness {
          static boolean throwsOob(Runnable r) {
            try { r.run(); return false; } catch (IndexOutOfBoundsException e) { return true; }
          }
          static boolean throwsIse(Runnable r) {
            try { r.run(); return false; } catch (IllegalStateException e) { return true; }
          }
          public static void main(String[] args) {
            System.out.println(JvmExtIntr.FmtBoolNet(true).equals("True") && JvmExtIntr.FmtBoolNet(false).equals("False"));
            System.out.println(JvmExtIntr.FmtBoolEcma(true).equals("true") && JvmExtIntr.FmtBoolEcma(false).equals("false"));
            System.out.println(JvmExtIntr.FromUnit('Z').equals("Z"));
            System.out.println(JvmExtIntr.StartsS("hello", "he") && JvmExtIntr.StartsC("hello", 'h') && !JvmExtIntr.StartsC("", 'h'));
            System.out.println(JvmExtIntr.EndsS("hello", "lo") && JvmExtIntr.EndsC("hello", 'o') && !JvmExtIntr.EndsC("", 'o'));
            System.out.println(JvmExtIntr.ContainsS("hello", "ell") && JvmExtIntr.ContainsC("hello", 'e') && !JvmExtIntr.ContainsC("hello", 'z'));
            System.out.println(JvmExtIntr.Idx2S("ababa", "ba") == 1 && JvmExtIntr.Idx3S("ababa", "ba", 2) == 3);
            System.out.println(JvmExtIntr.Idx2C("ababa", 'b') == 1 && JvmExtIntr.Idx3C("ababa", 'b', 2) == 3);
            System.out.println(JvmExtIntr.LastIdx2S("ababa", "ba") == 3 && JvmExtIntr.LastIdx3S("abc", "bc", 1) == 1);
            System.out.println(JvmExtIntr.LastIdx3C("ababa", 'a', 3) == 2);
            System.out.println(throwsOob(() -> JvmExtIntr.Idx3S("abc", "a", -1)) && throwsOob(() -> JvmExtIntr.LastIdx3S("abc", "a", 4)));
            System.out.println(JvmExtIntr.Join("-", new String[] { "a", "b", "c" }).equals("a-b-c"));
            System.out.println(JvmExtIntr.ReplAllS("a-b-a", "a", "XX").equals("XX-b-XX") && JvmExtIntr.ReplAllC("a-b-a", '-', '_').equals("a_b_a"));
            System.out.println(throwsIse(() -> JvmExtIntr.ReplAllS("abc", "", "x")));
            System.out.println(JvmExtIntr.ReplFirst("a-b-a", "a", "XX").equals("XX-b-a") && JvmExtIntr.ReplFirst("abc", "z", "XX").equals("abc"));
            System.out.println(JvmExtIntr.TrimNet("\u0085 hi \uFEFF").equals("hi \uFEFF"));
            System.out.println(JvmExtIntr.TrimStartNet("  hi  ").equals("hi  "));
            System.out.println(JvmExtIntr.TrimEndEcma("\uFEFFhi\u2000\uFEFF").equals("\uFEFFhi"));
            System.out.println(JvmExtIntr.TrimEcma("\uFEFF\u2000 hi \u0085\uFEFF").equals("hi \u0085"));
            System.out.println(Arrays.equals(JvmExtIntr.SplitS("a,b,c", ",", -1), new String[] { "a", "b", "c" }));
            System.out.println(Arrays.equals(JvmExtIntr.SplitS("a,b,c", ",", 2), new String[] { "a", "b" }));
            System.out.println(Arrays.equals(JvmExtIntr.SplitS("a,b,c", ",", 10), new String[] { "a", "b", "c" }));
            System.out.println(Arrays.equals(JvmExtIntr.SplitS("abc", "", -1), new String[] { "a", "b", "c" }));
            System.out.println(Arrays.equals(JvmExtIntr.SplitC("x:y:z", ':', 2), new String[] { "x", "y" }));
            System.out.println(JvmExtIntr.RevI8((byte)1) == (byte)-128 && (JvmExtIntr.RevU8((byte)0x81) & 0xFF) == 0x81);
            System.out.println(JvmExtIntr.RevU32(0x80000001) == 0x80000001 && JvmExtIntr.RevU64(0x8000000000000001L) == 0x8000000000000001L);
            System.out.println(JvmExtIntr.IsPow2I32(16) && !JvmExtIntr.IsPow2I32(-16) && !JvmExtIntr.IsPow2I32(0));
            System.out.println(JvmExtIntr.IsPow2U32(0x80000000) && !JvmExtIntr.IsPow2U32(0) && !JvmExtIntr.IsPow2U32(3));
            System.out.println(JvmExtIntr.IsPow2I64(1L << 40) && !JvmExtIntr.IsPow2I64(Long.MIN_VALUE) && JvmExtIntr.IsPow2U64(Long.MIN_VALUE) && !JvmExtIntr.IsPow2U64(0L));
            System.out.println(JvmExtIntr.BigMulS(-100000, 200000) == -20000000000L && JvmExtIntr.BigMulU(0x80000000, 3) == 0x180000000L);
            System.out.println(JvmExtIntr.ClampI32(15, 0, 10) == 10 && JvmExtIntr.ClampI32(-5, 0, 10) == 0 && JvmExtIntr.ClampI32(5, 0, 10) == 5 && throwsIse(() -> JvmExtIntr.ClampI32(5, 10, 0)));
            System.out.println(JvmExtIntr.ClampU32(-1, 0, 10) == 10 && JvmExtIntr.ClampU32(1, 5, -1) == 5 && throwsIse(() -> JvmExtIntr.ClampU32(5, -1, 10)));
            System.out.println(JvmExtIntr.ClampI64(15L, 0L, 10L) == 10L && JvmExtIntr.ClampI64(-5L, 0L, 10L) == 0L && throwsIse(() -> JvmExtIntr.ClampI64(5L, 10L, 0L)));
            System.out.println(JvmExtIntr.ClampU64(-1L, 0L, 10L) == 10L && JvmExtIntr.ClampU64(1L, 5L, -1L) == 5L && throwsIse(() -> JvmExtIntr.ClampU64(5L, -1L, 10L)));
            System.out.println(JvmExtIntr.ClampF32(1.5f, 0.0f, 1.0f) == 1.0f && JvmExtIntr.ClampF32(-1.0f, 0.0f, 1.0f) == 0.0f && Float.isNaN(JvmExtIntr.ClampF32(Float.NaN, 0.0f, 1.0f)) && throwsIse(() -> JvmExtIntr.ClampF32(0.5f, 2.0f, 1.0f)));
            System.out.println(JvmExtIntr.ClampF64(1.5, 0.0, 1.0) == 1.0 && JvmExtIntr.ClampF64(-1.0, 0.0, 1.0) == 0.0 && Double.isNaN(JvmExtIntr.ClampF64(Double.NaN, 0.0, 1.0)) && throwsIse(() -> JvmExtIntr.ClampF64(0.5, 2.0, 1.0)));
            System.out.println(JvmExtIntr.FmaF64(1.0 + Math.scalb(1.0, -52), 1.0 + Math.scalb(1.0, -52), -1.0 - Math.scalb(2.0, -52)) == Math.scalb(1.0, -104));
            System.out.println(JvmExtIntr.FmaF64(0.0, 5.0, -0.0) == 0.0 && Double.isNaN(JvmExtIntr.FmaF64(Double.NaN, 1.0, 2.0)));
            System.out.println(JvmExtIntr.FmaF32(2.0f, 3.0f, 4.0f) == 10.0f && JvmExtIntr.FmaF32(0.0f, 5.0f, 1.0f) == 1.0f && Float.isInfinite(JvmExtIntr.FmaF32(Float.POSITIVE_INFINITY, 1.0f, 0.0f)));
            System.out.println(Math.abs(JvmExtIntr.MathF32(0.5f, 2.0f, 8.0f) - {{expectedMathF32.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}f) < 1e-4f);
            System.out.println(Math.abs(JvmExtIntr.MathF64(0.5, 2.0, 8.0) - {{expectedMathF64.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}) < 1e-12);
          }
        }
        """);
      foreach (var line in output.Split('\n'))
        Assert.That(line, Is.EqualTo("true"), $"{profile}:\n{output}");
    }
  }

}
