using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Tooling.Report;
using static Polypiler.Tests.Backends.Msil.MsilTestHarness;

namespace Polypiler.Tests.Backends.Msil;

/// <summary>
/// Live execution, interpreter-parity, and disassembly tests for all 6 enum intrinsics,
/// showcase 11 (<c>11-enums-and-optionals.cs</c>), and standard BCL bit/math/string/format intrinsics in MSIL.
/// </summary>
[TestFixture]
public sealed class MsilEnumsAndIntrinsicsTests {

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

  private static object? InvokeMethod(Assembly assembly, IrModule module, string methodName, params object?[] args) {
    var type = assembly.GetType(module.Name) ?? assembly.GetTypes().Last();
    var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    Assert.That(method, Is.Not.Null, $"Method '{methodName}' not found on '{type.FullName}'.");
    try {
      return method!.Invoke(null, args);
    } catch (TargetInvocationException ex) when (ex.InnerException is not null) {
      throw ex.InnerException;
    }
  }

  [Test]
  public void Showcase11_EnumsAndOptionals_LiveExecutionAndDisassembly() {
    var source = File.ReadAllText(ShowcasePath("11-enums-and-optionals.cs"));
    var imported = CSharpFrontend.Import("11-enums-and-optionals.cs", source);
    Assert.That(imported.Diagnostics, Is.Empty);
    var module = Legalize(imported.Module!);

    foreach (var profile in new[] { MsilProfile.Net10, MsilProfile.Net8 }) {
      var diagnostics = MsilEmitter.Check(module, profile);
      Assert.That(diagnostics, Is.Empty, $"Check failed ({profile}): {string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");

      var files = MsilEmitter.Emit(module, profile);
      var il = MsilDisassembler.Disassemble(files[0].BinaryContent!);
      Assert.That(il, Does.Contain(".assembly"));

      WithLoadedAssembly(module, asm => {
        var permType = asm.GetType("Permissions")!;
        Assert.That(permType, Is.Not.Null);
        var readWrite = Enum.ToObject(permType, 3);
        var readOnly = Enum.ToObject(permType, 1);

        Assert.That(InvokeMethod(asm, module, "Resolve", readWrite, 5), Is.EqualTo(IrInterpreter.Call(module, "Resolve", [3, 5])));
        Assert.That(InvokeMethod(asm, module, "Resolve", readWrite, null), Is.EqualTo(IrInterpreter.Call(module, "Resolve", [3, null!])));
        Assert.That(InvokeMethod(asm, module, "Resolve", readOnly, 5), Is.EqualTo(IrInterpreter.Call(module, "Resolve", [1, 5])));
        Assert.That(InvokeMethod(asm, module, "Resolve", readOnly, null), Is.EqualTo(IrInterpreter.Call(module, "Resolve", [1, null!])));
      }, profile);
    }
  }

  [Test]
  public void EnumIntrinsics_AllSixOperations_AcrossWidthsFlagsAndEdgeCases() {
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
    var bf1 = L("bf", bigFlags.Type);
    var bf2 = L("bf2", bigFlags.Type);
    var de1 = L("de", dupEnum.Type);
    var txt = L("txt", IrType.String);
    var ic = L("ic", IrType.Bool);

    var mod = new IrModule("EnumMod", [
      Function("HasFlag32", IrType.Bool, [p1, p2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [R(p1), R(p2)]))),
      Function("HasFlag64", IrType.Bool, [bf1, bf2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [R(bf1), R(bf2)]))),
      Function("IsDefPerms", IrType.Bool, [p1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(p1)]))),
      Function("IsDefDup", IrType.Bool, [de1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(de1)]))),
      Function("IsDefBig", IrType.Bool, [bf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [R(bf1)]))),
      Function("ToStrPerms", IrType.String, [p1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(p1)]))),
      Function("ToStrColor", IrType.String, [c1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(c1)]))),
      Function("ToStrSmall", IrType.String, [sf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(sf1)]))),
      Function("ToStrMid", IrType.String, [mf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(mf1)]))),
      Function("ToStrBig", IrType.String, [bf1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [R(bf1)]))),
      Function("ParsePerms", perms.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, perms.Type, [R(txt), R(ic)]))),
      Function("ParseColor", color.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, color.Type, [R(txt), R(ic)]))),
      Function("ParseSmall", smallFlags.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, smallFlags.Type, [R(txt), R(ic)]))),
      Function("ParseMid", midFlags.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, midFlags.Type, [R(txt), R(ic)]))),
      Function("ParseBig", bigFlags.Type, [txt, ic], new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumParse, bigFlags.Type, [R(txt), R(ic)]))),
      Function("NamesPerms", IrType.Array(IrType.String), [], new IrReturn(EnumOperations.Listing(IrIntrinsicOp.EnumNames, perms))),
      Function("NamesSmall", IrType.Array(IrType.String), [], new IrReturn(EnumOperations.Listing(IrIntrinsicOp.EnumNames, smallFlags))),
      Function("ValuesPerms", IrType.Array(perms.Type), [], new IrReturn(EnumOperations.Listing(IrIntrinsicOp.EnumValues, perms))),
      Function("ValuesBig", IrType.Array(bigFlags.Type), [], new IrReturn(EnumOperations.Listing(IrIntrinsicOp.EnumValues, bigFlags)))
    ]) {
      Types = [perms, color, smallFlags, midFlags, bigFlags, dupEnum]
    };

    var files = MsilEmitter.Emit(mod);
    Assert.That(MsilDisassembler.Disassemble(files[0].BinaryContent!), Does.Contain("$EnumToString_"));

    WithLoadedAssembly(mod, asm => {
      var permsT = asm.GetType("Perms")!;
      var colorT = asm.GetType("Color")!;
      var smallT = asm.GetType("SmallFlags")!;
      var midT = asm.GetType("MidFlags")!;
      var bigT = asm.GetType("BigFlags")!;
      var dupT = asm.GetType("DupEnum")!;

      // EnumHasFlag (32-bit and 64-bit)
      Assert.That(InvokeMethod(asm, mod, "HasFlag32", Enum.ToObject(permsT, 5), Enum.ToObject(permsT, 4)), Is.True);
      Assert.That(InvokeMethod(asm, mod, "HasFlag32", Enum.ToObject(permsT, 5), Enum.ToObject(permsT, 2)), Is.False);
      Assert.That(InvokeMethod(asm, mod, "HasFlag64", Enum.ToObject(bigT, 0x8000_0000_0000_0001UL), Enum.ToObject(bigT, 0x8000_0000_0000_0000UL)), Is.True);
      Assert.That(InvokeMethod(asm, mod, "HasFlag64", Enum.ToObject(bigT, 1UL), Enum.ToObject(bigT, 0x8000_0000_0000_0000UL)), Is.False);

      // EnumIsDefined
      Assert.That(InvokeMethod(asm, mod, "IsDefPerms", Enum.ToObject(permsT, 2)), Is.True);
      Assert.That(InvokeMethod(asm, mod, "IsDefPerms", Enum.ToObject(permsT, 3)), Is.False);
      Assert.That(InvokeMethod(asm, mod, "IsDefDup", Enum.ToObject(dupT, 1)), Is.True);
      Assert.That(InvokeMethod(asm, mod, "IsDefDup", Enum.ToObject(dupT, 99)), Is.False);
      Assert.That(InvokeMethod(asm, mod, "IsDefBig", Enum.ToObject(bigT, 0x8000_0000_0000_0000UL)), Is.True);
      Assert.That(InvokeMethod(asm, mod, "IsDefBig", Enum.ToObject(bigT, 99UL)), Is.False);

      // EnumToString (exact, [Flags] combinations, zero without 0-member, undefined decimal fallback)
      Assert.That(InvokeMethod(asm, mod, "ToStrPerms", Enum.ToObject(permsT, 0)), Is.EqualTo(EnumOperations.Format(perms, 0)));
      Assert.That(InvokeMethod(asm, mod, "ToStrPerms", Enum.ToObject(permsT, 5)), Is.EqualTo(EnumOperations.Format(perms, 5)));
      Assert.That(InvokeMethod(asm, mod, "ToStrPerms", Enum.ToObject(permsT, 7)), Is.EqualTo(EnumOperations.Format(perms, 7)));
      Assert.That(InvokeMethod(asm, mod, "ToStrPerms", Enum.ToObject(permsT, 9)), Is.EqualTo(EnumOperations.Format(perms, 9)));
      Assert.That(InvokeMethod(asm, mod, "ToStrColor", Enum.ToObject(colorT, (byte)200)), Is.EqualTo(EnumOperations.Format(color, 200)));
      Assert.That(InvokeMethod(asm, mod, "ToStrColor", Enum.ToObject(colorT, (byte)99)), Is.EqualTo(EnumOperations.Format(color, 99)));
      Assert.That(InvokeMethod(asm, mod, "ToStrSmall", Enum.ToObject(smallT, (sbyte)-127)), Is.EqualTo(EnumOperations.Format(smallFlags, -127)));
      Assert.That(InvokeMethod(asm, mod, "ToStrSmall", Enum.ToObject(smallT, (sbyte)0)), Is.EqualTo(EnumOperations.Format(smallFlags, 0)));
      Assert.That(InvokeMethod(asm, mod, "ToStrMid", Enum.ToObject(midT, (short)-32767)), Is.EqualTo(EnumOperations.Format(midFlags, -32767)));
      Assert.That(InvokeMethod(asm, mod, "ToStrBig", Enum.ToObject(bigT, 0x8000_0000_0000_0001UL)), Is.EqualTo(EnumOperations.Format(bigFlags, unchecked((long)0x8000_0000_0000_0001UL))));
      Assert.That(InvokeMethod(asm, mod, "ToStrBig", Enum.ToObject(bigT, 123UL)), Is.EqualTo(EnumOperations.Format(bigFlags, 123)));

      // EnumParse (names, comma-separated, ignoreCase true/false, numeric + sign, overflow, invalid/empty)
      Assert.That(Convert.ToInt32(InvokeMethod(asm, mod, "ParsePerms", "Read, Execute", false)), Is.EqualTo(5));
      Assert.That(Convert.ToInt32(InvokeMethod(asm, mod, "ParsePerms", " read , write ", true)), Is.EqualTo(3));
      Assert.That(Convert.ToInt32(InvokeMethod(asm, mod, "ParsePerms", " +6 ", false)), Is.EqualTo(6));
      Assert.That(Convert.ToInt32(InvokeMethod(asm, mod, "ParsePerms", "-1", false)), Is.EqualTo(-1));
      Assert.That(Convert.ToByte(InvokeMethod(asm, mod, "ParseColor", "Blue", false)), Is.EqualTo((byte)200));
      Assert.That(Convert.ToSByte(InvokeMethod(asm, mod, "ParseSmall", "Bit0, Bit7", false)), Is.EqualTo((sbyte)-127));
      Assert.That(Convert.ToInt16(InvokeMethod(asm, mod, "ParseMid", "Low, High", false)), Is.EqualTo((short)-32767));
      Assert.That(Convert.ToUInt64(InvokeMethod(asm, mod, "ParseBig", "lo, hi", true)), Is.EqualTo(0x8000_0000_0000_0001UL));
      Assert.That(Convert.ToUInt64(InvokeMethod(asm, mod, "ParseBig", "42", false)), Is.EqualTo(42UL));

      Assert.That(() => InvokeMethod(asm, mod, "ParsePerms", "read", false), Throws.TypeOf<ArgumentException>());
      Assert.That(() => InvokeMethod(asm, mod, "ParsePerms", "   ", false), Throws.TypeOf<ArgumentException>());
      Assert.That(() => InvokeMethod(asm, mod, "ParsePerms", "+notanumber", false), Throws.TypeOf<ArgumentException>());
      Assert.That(() => InvokeMethod(asm, mod, "ParsePerms", "9999999999999", false), Throws.TypeOf<OverflowException>());

      // EnumNames & EnumValues (ordered by unsigned value)
      Assert.That(InvokeMethod(asm, mod, "NamesPerms"), Is.EqualTo(EnumOperations.Names(perms)));
      Assert.That(InvokeMethod(asm, mod, "NamesSmall"), Is.EqualTo(EnumOperations.Names(smallFlags)));
      var valsPerms = ((Array)InvokeMethod(asm, mod, "ValuesPerms")!).Cast<object>().Select(Convert.ToInt64).ToArray();
      Assert.That(valsPerms, Is.EqualTo(EnumOperations.Values(perms)));
      var valsBig = ((Array)InvokeMethod(asm, mod, "ValuesBig")!).Cast<object>().Select(v => unchecked((long)Convert.ToUInt64(v))).ToArray();
      Assert.That(valsBig, Is.EqualTo(EnumOperations.Values(bigFlags)));
    });
  }

  [Test]
  public void StandardBclIntrinsics_BoolFormatBitMathAndString_MatchInterpreter() {
    var b = L("b", IrType.Bool);
    var i8 = L("i8", IrType.I8);
    var u8 = L("u8", IrType.U8);
    var i16 = L("i16", IrType.I16);
    var u16 = L("u16", IrType.U16);
    var u16b = L("u16b", IrType.U16);
    var i32 = L("i32", IrType.I32);
    var i32b = L("i32b", IrType.I32);
    var i32c = L("i32c", IrType.I32);
    var u32 = L("u32", IrType.U32);
    var u32b = L("u32b", IrType.U32);
    var i64 = L("i64", IrType.I64);
    var u64 = L("u64", IrType.U64);
    var f32 = L("f32", IrType.F32);
    var f32b = L("f32b", IrType.F32);
    var f32c = L("f32c", IrType.F32);
    var f64 = L("f64", IrType.F64);
    var f64b = L("f64b", IrType.F64);
    var f64c = L("f64c", IrType.F64);
    var s1 = L("s1", IrType.String);
    var s2 = L("s2", IrType.String);
    var s3 = L("s3", IrType.String);

    var mod = new IrModule("BclIntr", [
      // Bool format
      Function("FmtBoolNet", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [R(b)]))),
      Function("FmtBoolEcma", IrType.String, [b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.FormatBoolEcma, IrType.String, [R(b)]))),
      // ByteSwap / HighWord / JoinWords / BigMul
      Function("SwapU8", IrType.U8, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U8, [R(u8)]))),
      Function("SwapI16", IrType.I16, [i16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.I16, [R(i16)]))),
      Function("SwapU32", IrType.U32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.U32, [R(u32)]))),
      Function("SwapI64", IrType.I64, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ByteSwap, IrType.I64, [R(i64)]))),
      Function("HighJoin", IrType.I64, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.JoinWords, IrType.I64, [
        new IrIntrinsic(IrIntrinsicOp.HighWord, IrType.U32, [R(i64)]), new IrConvert(IrType.U32, R(i64))
      ]))),
      Function("BigMulS", IrType.I64, [i32, i32b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BigMul, IrType.I64, [R(i32), R(i32b)]))),
      Function("BigMulU", IrType.U64, [u32, u32b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BigMul, IrType.U64, [R(u32), R(u32b)]))),
      // Clamp (int + f32 + f64)
      Function("ClampI32", IrType.I32, [i32, i32b, i32c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.I32, [R(i32), R(i32b), R(i32c)]))),
      Function("ClampF32", IrType.F32, [f32, f32b, f32c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.F32, [R(f32), R(f32b), R(f32c)]))),
      Function("ClampF64", IrType.F64, [f64, f64b, f64c], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.F64, [R(f64), R(f64b), R(f64c)]))),
      // BitReverse / IsPow2 / RoundUpPow2 / Log2 (int + float)
      Function("BitRevI8", IrType.I8, [i8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.I8, [R(i8)]))),
      Function("BitRevU8", IrType.U8, [u8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U8, [R(u8)]))),
      Function("BitRevI16", IrType.I16, [i16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.I16, [R(i16)]))),
      Function("BitRevU16", IrType.U16, [u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U16, [R(u16)]))),
      Function("BitRevU32", IrType.U32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U32, [R(u32)]))),
      Function("BitRevU64", IrType.U64, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitReverse, IrType.U64, [R(u64)]))),
      Function("IsPow2I8", IrType.Bool, [i8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(i8)]))),
      Function("IsPow2U32", IrType.Bool, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(u32)]))),
      Function("IsPow2I64", IrType.Bool, [i64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(i64)]))),
      Function("IsPow2U64", IrType.Bool, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.IsPow2, IrType.Bool, [R(u64)]))),
      Function("RoundPow2I8", IrType.I8, [i8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundUpPow2, IrType.I8, [R(i8)]))),
      Function("RoundPow2I16", IrType.I16, [i16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundUpPow2, IrType.I16, [R(i16)]))),
      Function("RoundPow2U32", IrType.U32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundUpPow2, IrType.U32, [R(u32)]))),
      Function("RoundPow2U64", IrType.U64, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundUpPow2, IrType.U64, [R(u64)]))),
      Function("Log2I8", IrType.I32, [i8], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Log2, IrType.I32, [R(i8)]))),
      Function("Log2I16", IrType.I32, [i16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Log2, IrType.I32, [R(i16)]))),
      Function("Log2U32", IrType.I32, [u32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Log2, IrType.I32, [R(u32)]))),
      Function("Log2U64", IrType.I32, [u64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Log2, IrType.I32, [R(u64)]))),
      Function("Log2F32", IrType.F32, [f32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Log2, IrType.F32, [R(f32)]))),
      Function("Log2F64", IrType.F64, [f64], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Log2, IrType.F64, [R(f64)]))),
      // Math / MathF transcendentals & FMA
      Function("MathF32", IrType.F32, [f32, f32b, f32c], new IrReturn(
        Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.FusedMultiplyAdd, IrType.F32, [R(f32), R(f32b), R(f32c)]),
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
                                      new IrIntrinsic(IrIntrinsicOp.Cbrt, IrType.F32, [R(f32c)])))))))))))))))))),
      Function("MathF64", IrType.F64, [f64, f64b, f64c], new IrReturn(
        Bin(IrBinaryOp.Add,
          new IrIntrinsic(IrIntrinsicOp.FusedMultiplyAdd, IrType.F64, [R(f64), R(f64b), R(f64c)]),
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
                                      new IrIntrinsic(IrIntrinsicOp.Cbrt, IrType.F64, [R(f64c)])))))))))))))))))),
      // String intrinsics
      Function("FromUnit", IrType.String, [u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [R(u16)]))),
      Function("LastIdx2", IrType.I32, [s1, s2], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s1), R(s2)]))),
      Function("LastIdx3", IrType.I32, [s1, s2, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s1), R(s2), R(i32)]))),
      Function("LastIdxChar", IrType.I32, [s1, u16], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrLastIndexOf, IrType.I32, [R(s1), R(u16)]))),
      Function("ReplAllStr", IrType.String, [s1, s2, s3], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(s1), R(s2), R(s3)]))),
      Function("ReplAllChr", IrType.String, [s1, u16, u16b], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [R(s1), R(u16), R(u16b)]))),
      Function("ReplFirst", IrType.String, [s1, s2, s3], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceFirst, IrType.String, [R(s1), R(s2), R(s3)]))),
      Function("TrimNet", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("TrimStartNet", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimStart, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("TrimEndNet", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimEnd, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.DotNet)]))),
      Function("TrimEcma", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.Ecma)]))),
      Function("TrimStartEcma", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimStart, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.Ecma)]))),
      Function("TrimEndEcma", IrType.String, [s1], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrTrimEnd, IrType.String, [R(s1), StringOperations.Set(WhitespaceSet.Ecma)]))),
      Function("SplitStr", IrType.Array(IrType.String), [s1, s2, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s1), R(s2), R(i32)]))),
      Function("SplitChar", IrType.Array(IrType.String), [s1, u16, i32], new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSplit, IrType.Array(IrType.String), [R(s1), R(u16), R(i32)])))
    ]);

    AssertMatchesInterpreter(mod, "FmtBoolNet", true);
    AssertMatchesInterpreter(mod, "FmtBoolNet", false);
    AssertMatchesInterpreter(mod, "FmtBoolEcma", true);
    AssertMatchesInterpreter(mod, "FmtBoolEcma", false);

    AssertMatchesInterpreter(mod, "SwapU8", (byte)0xAB);
    AssertMatchesInterpreter(mod, "SwapI16", (short)0x1234);
    AssertMatchesInterpreter(mod, "SwapU32", 0x12345678u);
    AssertMatchesInterpreter(mod, "SwapI64", 0x0102030405060708L);
    AssertMatchesInterpreter(mod, "HighJoin", 0x123456789ABCDEF0L);
    AssertMatchesInterpreter(mod, "BigMulS", -100000, 200000);
    AssertMatchesInterpreter(mod, "BigMulU", 0x80000000u, 3u);

    AssertMatchesInterpreter(mod, "ClampI32", 15, 0, 10);
    AssertMatchesInterpreter(mod, "ClampI32", -5, 0, 10);
    AssertMatchesInterpreter(mod, "ClampF32", 1.5f, 0.0f, 1.0f);
    AssertMatchesInterpreter(mod, "ClampF64", double.NaN, 0.0, 1.0);
    Assert.That(() => Invoke(mod, "ClampI32", 5, 10, 0), Throws.TypeOf<ArgumentException>());

    AssertMatchesInterpreter(mod, "BitRevI8", (sbyte)0x01);
    AssertMatchesInterpreter(mod, "BitRevU8", (byte)0x81);
    AssertMatchesInterpreter(mod, "BitRevI16", (short)0x0003);
    AssertMatchesInterpreter(mod, "BitRevU16", (ushort)0x1234);
    AssertMatchesInterpreter(mod, "BitRevU32", 0x80000001u);
    AssertMatchesInterpreter(mod, "BitRevU64", 0x8000000000000003UL);

    AssertMatchesInterpreter(mod, "IsPow2I8", (sbyte)64);
    AssertMatchesInterpreter(mod, "IsPow2I8", (sbyte)-128);
    AssertMatchesInterpreter(mod, "IsPow2U32", 0x80000000u);
    AssertMatchesInterpreter(mod, "IsPow2U32", 0u);
    AssertMatchesInterpreter(mod, "IsPow2I64", 1L << 40);
    AssertMatchesInterpreter(mod, "IsPow2U64", 1UL << 63);

    AssertMatchesInterpreter(mod, "RoundPow2I8", (sbyte)5);
    AssertMatchesInterpreter(mod, "RoundPow2I8", (sbyte)-128);
    AssertMatchesInterpreter(mod, "RoundPow2I8", (sbyte)-127);
    AssertMatchesInterpreter(mod, "RoundPow2I16", (short)1000);
    AssertMatchesInterpreter(mod, "RoundPow2U32", 0u);
    AssertMatchesInterpreter(mod, "RoundPow2U32", 33u);
    AssertMatchesInterpreter(mod, "RoundPow2U64", 1UL);
    AssertMatchesInterpreter(mod, "RoundPow2U64", 1000UL);

    AssertMatchesInterpreter(mod, "Log2I8", (sbyte)-1);
    AssertMatchesInterpreter(mod, "Log2I16", (short)256);
    AssertMatchesInterpreter(mod, "Log2U32", 0u);
    AssertMatchesInterpreter(mod, "Log2U32", 1024u);
    AssertMatchesInterpreter(mod, "Log2U64", 1UL << 50);
    AssertMatchesInterpreter(mod, "Log2F32", 8.0f);
    AssertMatchesInterpreter(mod, "Log2F64", 1024.0);

    var actualF32 = (float)Invoke(mod, "MathF32", 0.5f, 2.0f, 8.0f)!;
    var expectedF32 = (float)IrInterpreter.Call(mod, "MathF32", [0.5f, 2.0f, 8.0f])!;
    Assert.That(actualF32, Is.EqualTo(expectedF32).Within(1e-5f));
    var actualF64 = (double)Invoke(mod, "MathF64", 0.5, 2.0, 8.0)!;
    var expectedF64 = (double)IrInterpreter.Call(mod, "MathF64", [0.5, 2.0, 8.0])!;
    Assert.That(actualF64, Is.EqualTo(expectedF64).Within(1e-12));

    AssertMatchesInterpreter(mod, "FromUnit", (ushort)'Z');
    AssertMatchesInterpreter(mod, "LastIdx2", "ababa", "ba");
    AssertMatchesInterpreter(mod, "LastIdx3", "abc", "bc", 1);
    AssertMatchesInterpreter(mod, "LastIdx3", "abc", "z", 3);
    AssertMatchesInterpreter(mod, "LastIdxChar", "banana", (ushort)'a');
    Assert.That(() => Invoke(mod, "LastIdx3", "abc", "a", -1), Throws.TypeOf<IndexOutOfRangeException>());
    Assert.That(() => Invoke(mod, "LastIdx3", "abc", "a", 4), Throws.TypeOf<IndexOutOfRangeException>());

    AssertMatchesInterpreter(mod, "ReplAllStr", "a-b-a", "a", "XX");
    AssertMatchesInterpreter(mod, "ReplAllChr", "a-b-a", (ushort)'-', (ushort)'_');
    Assert.That(() => Invoke(mod, "ReplAllStr", "abc", "", "x"), Throws.TypeOf<InvalidOperationException>());
    AssertMatchesInterpreter(mod, "ReplFirst", "a-b-a", "a", "XX");
    AssertMatchesInterpreter(mod, "ReplFirst", "abc", "z", "XX");

    AssertMatchesInterpreter(mod, "TrimNet", "\u0085 hi \uFEFF");
    AssertMatchesInterpreter(mod, "TrimStartNet", "  hi  ");
    AssertMatchesInterpreter(mod, "TrimEndNet", "  hi  ");
    AssertMatchesInterpreter(mod, "TrimEcma", "\uFEFF\u2000\t hi \u0085\uFEFF");
    AssertMatchesInterpreter(mod, "TrimStartEcma", "\uFEFF\u0020hi\uFEFF");
    AssertMatchesInterpreter(mod, "TrimEndEcma", "\uFEFFhi\u200A\u000D");

    AssertMatchesInterpreter(mod, "SplitStr", "a,b,c", ",", -1);
    AssertMatchesInterpreter(mod, "SplitStr", "a,b,c", ",", 2);
    AssertMatchesInterpreter(mod, "SplitStr", "a,b,c", ",", 10);
    AssertMatchesInterpreter(mod, "SplitStr", "abc", "", -1);
    AssertMatchesInterpreter(mod, "SplitChar", "x:y:z", (ushort)':', 2);

    // Also verify Legalize preserves native integer/float intrinsics cleanly.
    var legalized = Legalize(mod);
    Assert.That(MsilEmitter.Check(legalized), Is.Empty);
  }

}
