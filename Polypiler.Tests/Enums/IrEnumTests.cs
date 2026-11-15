using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Enums;

/// <summary>
/// The enums of the IR (#1318): the type, what the verifier takes, and what the interpreter computes
/// for each operation - against .NET 10 itself, which runs these tests and declares the same enums.
/// </summary>
[TestFixture]
public sealed class IrEnumTests {

  [Flags]
  private enum Perm { None = 0, Read = 1, Write = 2, Exec = 4, ReadWrite = 3 }

  [Flags]
  private enum NoZero { A = 1, B = 2, C = 8 }

  private enum Color : byte { Red, Green = 5, Blue }

  private enum Signed { B = 2, Neg = -1, A = 0, C = 1 }

  private enum Small : sbyte { Low = -128, Minus = -1, High = 127 }

  private enum Big : ulong { Max = ulong.MaxValue, One = 1 }

  [Flags]
  private enum Wide : long { Low = 1, High = long.MinValue }

  private enum Empty { }

  /// <summary>The IR enum of a .NET enum: its underlying type, its members in declaration order, and whether it has [Flags].</summary>
  internal static IrEnumType Of<TEnum>() where TEnum : struct, System.Enum {
    var underlying = Type.GetTypeCode(typeof(TEnum)) switch {
      TypeCode.SByte => IrType.I8,
      TypeCode.Byte => IrType.U8,
      TypeCode.Int16 => IrType.I16,
      TypeCode.UInt16 => IrType.U16,
      TypeCode.Int32 => IrType.I32,
      TypeCode.UInt32 => IrType.U32,
      TypeCode.Int64 => IrType.I64,
      _ => IrType.U64
    };
    var members = typeof(TEnum).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
      .OrderBy(field => field.MetadataToken)
      .Select(field => new IrEnumMember(field.Name, Carried(field.GetRawConstantValue()!)))
      .ToList();
    return new IrEnumType(typeof(TEnum).Name, underlying, members, typeof(TEnum).IsDefined(typeof(FlagsAttribute), false));
  }

  private static long Carried(object value) => value is ulong large ? unchecked((long)large) : System.Convert.ToInt64(value, CultureInfo.InvariantCulture);

  private static IrModule Module(IrEnumType type, IrExpr body, params IrLocal[] parameters)
    => new("M", [new IrFunction("F", true, body.Type, parameters, [], new IrBlock([new IrReturn(body)]))]) { Types = [type] };

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  private static object? Run(IrEnumType type, IrExpr body, IrLocal[] parameters, params object[] arguments) {
    var module = Module(type, body, parameters);
    Assert.That(Problems(module), Is.Empty);
    return IrInterpreter.Call(module, "F", arguments);
  }

  private static string Text(IrEnumType type, long value) {
    var x = new IrLocal("x", type.Type);
    return (string)Run(type, new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [new IrLocalRef(x)]), [x], Host(type, value))!;
  }

  /// <summary>A carried value as the .NET value of the underlying type the interpreter takes.</summary>
  private static object Host(IrEnumType type, long value) => type.Underlying.Kind switch {
    IrTypeKind.I8 => (sbyte)value,
    IrTypeKind.U8 => (byte)value,
    IrTypeKind.I16 => (short)value,
    IrTypeKind.U16 => (ushort)value,
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U32 => (uint)value,
    IrTypeKind.I64 => value,
    _ => unchecked((ulong)value)
  };

  [Test]
  public void Type_HasTheKindOfItsUnderlyingTypeAndItsOwnName() {
    var color = Of<Color>();
    Assert.That(color.Type.Kind, Is.EqualTo(IrTypeKind.U8));
    Assert.That(color.Type.IsEnum, Is.True);
    Assert.That(color.Type.IsInteger, Is.True);
    Assert.That(color.Type.Integral, Is.EqualTo(IrType.U8));
    Assert.That(IrType.U8.Integral, Is.EqualTo(IrType.U8));
    Assert.That(color.Type, Is.Not.EqualTo(IrType.U8));
    Assert.That(color.Type, Is.EqualTo(IrType.Enum(color)));
    Assert.That(color.Type.EnumType, Is.SameAs(color));
    Assert.That(color.Type.StructType, Is.Null);
    Assert.That(IrType.I32.EnumType, Is.Null);
    Assert.That(color.Type.ToString(), Is.EqualTo("Color"));
    Assert.That(color.IsDefined, Is.True);
    Assert.That(color.Fields, Is.Empty);
    Assert.That(color.IsFlags, Is.False);
    Assert.That(color.IsPublic, Is.False);
    Assert.That(color.Member("Green")!.Value, Is.EqualTo(5));
    Assert.That(color.Member("green"), Is.Null);
    Assert.That(color.MemberOf(6)!.Name, Is.EqualTo("Blue"));
    Assert.That(color.MemberOf(7), Is.Null);
    Assert.That(color.HasRepeatedValues, Is.False);
    Assert.That(Of<Perm>().IsFlags, Is.True);
    Assert.That(new IrEnumType("D", IrType.I32, [new("X", 1), new("Y", 1)]).HasRepeatedValues, Is.True);
  }

  /// <summary>Test design 5: the members by their unsigned values, ties in declaration order.</summary>
  [Test]
  public void Ordered_IsByUnsignedValue() {
    Assert.That(Of<Signed>().Ordered.Select(member => member.Name), Is.EqualTo(new[] { "A", "C", "B", "Neg" }));
    Assert.That(Of<Small>().Ordered.Select(member => member.Name), Is.EqualTo(new[] { "High", "Low", "Minus" }));
    Assert.That(Of<Big>().Ordered.Select(member => member.Name), Is.EqualTo(new[] { "One", "Max" }));
    Assert.That(new IrEnumType("D", IrType.I32, [new("X", 1), new("Z", 0), new("Y", 1)]).Ordered.Select(member => member.Name), Is.EqualTo(new[] { "Z", "X", "Y" }));
    Assert.That(Of<Small>().Unsigned(-1), Is.EqualTo(255));
    Assert.That(Of<Big>().Unsigned(-1), Is.EqualTo(ulong.MaxValue));
  }

  /// <summary>Test design 3: the text of every value 0…255 of a flags enum with and without a zero member, against .NET 10.</summary>
  [Test]
  public void Format_OfFlags_IsDotNets() {
    var perm = Of<Perm>();
    var noZero = Of<NoZero>();
    for (var value = 0; value <= 255; ++value) {
      Assert.That(EnumOperations.Format(perm, value), Is.EqualTo(((Perm)value).ToString()), $"Perm {value}");
      Assert.That(EnumOperations.Format(noZero, value), Is.EqualTo(((NoZero)value).ToString()), $"NoZero {value}");
    }

    Assert.That(EnumOperations.Format(perm, -1), Is.EqualTo("-1"));
    var wide = Of<Wide>();
    foreach (var value in new[] { 0L, 1L, long.MinValue, long.MinValue + 1, long.MinValue + 2, -1L })
      Assert.That(EnumOperations.Format(wide, value), Is.EqualTo(((Wide)value).ToString()), $"Wide {value}");
  }

  /// <summary>Test design 4: the boundaries of the underlying types, a value between members, and -1.</summary>
  [Test]
  public void Format_OfOrdinaryEnums_IsDotNets() {
    foreach (var value in new long[] { 0, 1, 2, 3, 4, 5, 6, 7, 255 })
      Assert.That(EnumOperations.Format(Of<Color>(), value), Is.EqualTo(((Color)value).ToString()));

    foreach (var value in new long[] { -2, -1, 0, 1, 2, 3, int.MinValue, int.MaxValue })
      Assert.That(EnumOperations.Format(Of<Signed>(), value), Is.EqualTo(((Signed)value).ToString()));

    foreach (var value in new long[] { -128, -127, -1, 0, 127 })
      Assert.That(EnumOperations.Format(Of<Small>(), value), Is.EqualTo(((Small)value).ToString()));

    foreach (var value in new[] { 0UL, 1UL, 2UL, ulong.MaxValue, ulong.MaxValue - 1 })
      Assert.That(EnumOperations.Format(Of<Big>(), unchecked((long)value)), Is.EqualTo(((Big)value).ToString()));

    Assert.That(EnumOperations.Format(Of<Empty>(), 3), Is.EqualTo("3"));
  }

  [Test]
  public void IsDefined_IsAMemberWithExactlyTheValue() {
    var perm = Of<Perm>();
    foreach (var value in Enumerable.Range(-2, 12))
      Assert.That(EnumOperations.IsDefined(perm, value), Is.EqualTo(System.Enum.IsDefined(typeof(Perm), value)), $"{value}");

    Assert.That(EnumOperations.IsDefined(Of<Big>(), -1), Is.True);
    Assert.That(EnumOperations.IsDefined(Of<Empty>(), 0), Is.False);
  }

  /// <summary>Test design 6: every class of text Enum.Parse distinguishes, with and without ignoring case.</summary>
  [TestCase(" Read ")]
  [TestCase("Read, Write")]
  [TestCase("Read , Exec")]
  [TestCase("read")]
  [TestCase("READ,exec")]
  [TestCase("")]
  [TestCase(" ")]
  [TestCase("5")]
  [TestCase(" -1 ")]
  [TestCase("+3")]
  [TestCase("-0")]
  [TestCase("Read,")]
  [TestCase(",Read")]
  [TestCase("Read,,Exec")]
  [TestCase("300")]
  [TestCase("1x")]
  [TestCase("\tRead\n")]
  [TestCase("\u00A0Read")]
  [TestCase("\u00A05")]
  [TestCase("5\u00A0")]
  [TestCase("5 ")]
  [TestCase("5\0")]
  [TestCase("-")]
  [TestCase("0x5")]
  [TestCase("None")]
  [TestCase("Read|Write")]
  [TestCase("99999999999")]
  [TestCase("1, 2")]
  [TestCase("Read, 4")]
  [TestCase("Unknown")]
  public void Parse_IsDotNets(string text) {
    foreach (var ignoreCase in new[] { false, true })
      Assert.That(Outcome(() => EnumOperations.Parse(Of<Perm>(), text, ignoreCase)), Is.EqualTo(Outcome(() => (long)(int)System.Enum.Parse<Perm>(text, ignoreCase))), $"'{text}', {ignoreCase}");
  }

  [TestCase("300")]
  [TestCase("-1")]
  [TestCase("255")]
  [TestCase("Red, Green")]
  [TestCase("Green,Blue")]
  [TestCase("256")]
  [TestCase("blue")]
  public void Parse_OfAByteEnum_IsDotNets(string text)
    => Assert.That(Outcome(() => EnumOperations.Parse(Of<Color>(), text, false)), Is.EqualTo(Outcome(() => (long)(byte)System.Enum.Parse<Color>(text))));

  [TestCase("18446744073709551615")]
  [TestCase("-1")]
  [TestCase("Max")]
  [TestCase("One, Max")]
  public void Parse_OfAnUnsignedLongEnum_IsDotNets(string text)
    => Assert.That(Outcome(() => EnumOperations.Parse(Of<Big>(), text, false)), Is.EqualTo(Outcome(() => unchecked((long)(ulong)System.Enum.Parse<Big>(text)))));

  [Test]
  public void Parse_OfASignedEnum_IsDotNets() {
    foreach (var text in new[] { "Neg", "-1", "Neg, A", "C, B", "-2147483649" })
      Assert.That(Outcome(() => EnumOperations.Parse(Of<Signed>(), text, false)), Is.EqualTo(Outcome(() => (long)(int)System.Enum.Parse<Signed>(text))), text);

    foreach (var text in new[] { "Minus", "Low, Minus", "-129" })
      Assert.That(Outcome(() => EnumOperations.Parse(Of<Small>(), text, false)), Is.EqualTo(Outcome(() => (long)(sbyte)System.Enum.Parse<Small>(text))), text);
  }

  /// <summary>The value, or the trap the IR names for .NET's exception.</summary>
  private static string Outcome(Func<long> parse) {
    try {
      return parse().ToString(CultureInfo.InvariantCulture);
    } catch (IrTrapException trap) {
      return trap.Trap.ToString();
    } catch (OverflowException) {
      return nameof(IrTrap.IntegerOverflow);
    } catch (ArgumentException) {
      return nameof(IrTrap.UnknownName);
    }
  }

  [Test]
  public void NamesAndValues_AreInDotNetsOrder() {
    Assert.That(EnumOperations.Names(Of<Signed>()), Is.EqualTo(System.Enum.GetNames<Signed>()));
    Assert.That(EnumOperations.Values(Of<Signed>()), Is.EqualTo(System.Enum.GetValues<Signed>().Select(value => (long)value)));
    Assert.That(EnumOperations.Names(Of<Big>()), Is.EqualTo(System.Enum.GetNames<Big>()));
    Assert.That(EnumOperations.Names(Of<Empty>()), Is.Empty);
    Assert.That(EnumOperations.Values(Of<Empty>()), Is.Empty);
  }

  [Test]
  public void Interpreter_RunsEveryOperation() {
    var perm = Of<Perm>();
    var type = perm.Type;
    var x = new IrLocal("x", type);
    var f = new IrLocal("f", type);
    var s = new IrLocal("s", IrType.String);
    Assert.That(Text(perm, 7), Is.EqualTo("ReadWrite, Exec"));
    Assert.That(Text(perm, 8), Is.EqualTo("8"));
    Assert.That(Run(perm, new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [new IrLocalRef(x)]), [x], 5), Is.False);
    Assert.That(Run(perm, new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [new IrLocalRef(x)]), [x], 3), Is.True);
    Assert.That(Run(perm, new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [new IrLocalRef(x), new IrLocalRef(f)]), [x, f], 7, 5), Is.True);
    Assert.That(Run(perm, new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [new IrLocalRef(x), new IrLocalRef(f)]), [x, f], 6, 5), Is.False);
    var parse = new IrIntrinsic(IrIntrinsicOp.EnumParse, type, [new IrLocalRef(s), new IrConst(IrType.Bool, 0)]);
    Assert.That(Run(perm, parse, [s], "Read, Exec"), Is.EqualTo(5));
    Assert.That(() => Run(perm, parse, [s], "read"), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.UnknownName));
    Assert.That(Run(perm, EnumOperations.Listing(IrIntrinsicOp.EnumNames, perm), []), Is.EqualTo(new[] { "None", "Read", "Write", "ReadWrite", "Exec" }));
    Assert.That(Run(perm, EnumOperations.Listing(IrIntrinsicOp.EnumValues, perm), []), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
    var color = Of<Color>();
    Assert.That(Run(color, EnumOperations.Listing(IrIntrinsicOp.EnumValues, color), []), Is.EqualTo(new byte[] { 0, 5, 6 }));
    var colors = new IrLocal("c", IrType.Array(color.Type));
    Assert.That(Run(color, new IrArrayLength(new IrLocalRef(colors)), [colors], new byte[] { 1, 2 }), Is.EqualTo(2));
  }

  /// <summary>Test design 4: arithmetic of the underlying type wraps, and the value outside the members is kept.</summary>
  [Test]
  public void Arithmetic_WrapsInTheUnderlyingType() {
    var color = Of<Color>();
    var c = new IrLocal("c", color.Type);
    var n = new IrLocal("n", IrType.U8);
    // (Color)(byte)((int)(byte)c + (int)n), as C# computes c + n of a byte enum.
    var sum = new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I32, new IrConvert(IrType.U8, new IrLocalRef(c))), new IrConvert(IrType.I32, new IrLocalRef(n)));
    var shifted = new IrConvert(color.Type, new IrConvert(IrType.U8, sum));
    Assert.That(Run(color, new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [shifted]), [c, n], (byte)6, (byte)250), Is.EqualTo("Red"));
    Assert.That(Run(color, new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [new IrLocalRef(c)]), [c], (byte)7), Is.EqualTo("7"));
    Assert.That(Run(color, new IrBinary(IrBinaryOp.Equal, new IrLocalRef(c), new IrConst(color.Type, 5)), [c], (byte)5), Is.True);
    var signed = Of<Signed>();
    var a = new IrLocal("a", signed.Type);
    var b = new IrLocal("b", signed.Type);
    Assert.That(Run(signed, new IrBinary(IrBinaryOp.Or, new IrLocalRef(a), new IrLocalRef(b)), [a, b], 1, 2), Is.EqualTo(3));
    Assert.That(Run(signed, new IrUnary(IrUnaryOp.BitNot, new IrLocalRef(a)), [a], 0), Is.EqualTo(-1));
  }

  [Test]
  public void Operations_AreTheSix() {
    Assert.That(EnumOperations.Operations, Has.Count.EqualTo(6));
    Assert.That(EnumOperations.Operations, Has.All.Matches<IrIntrinsicOp>(EnumOperations.IsEnum));
    Assert.That(EnumOperations.IsEnum(IrIntrinsicOp.StrConcat), Is.False);
    Assert.That(EnumOperations.Problem(new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrStrConst("a")])), Is.Null);
  }

  /// <summary>Test design 10: a member outside the underlying type, an enum over a floating-point type, an operation on what is no enum.</summary>
  [Test]
  public void Verifier_RefusesMalformedEnums() {
    var outside = new IrEnumType("E", IrType.U8, [new("A", 256)]);
    Assert.That(Problems(Module(outside, new IrConst(IrType.I32, 0))), Has.Some.Contains("the member 'A' has the value 256"));
    var floating = new IrEnumType("F", IrType.F64, [new("A", 0)]);
    Assert.That(Problems(Module(floating, new IrConst(IrType.I32, 0))), Has.Some.Contains("an enum over the type f64"));
    var twice = new IrEnumType("T", IrType.I32, [new("A", 0), new("A", 1)]);
    Assert.That(Problems(Module(twice, new IrConst(IrType.I32, 0))), Has.Some.Contains("the member 'A' is declared twice"));
    var perm = Of<Perm>();
    Assert.That(Problems(Module(perm, new IrIntrinsic(IrIntrinsicOp.EnumParse, IrType.I32, [new IrStrConst("A"), new IrConst(IrType.Bool, 0)]))), Has.Some.Contains("EnumParse with the type i32"));
    Assert.That(Problems(Module(perm, new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [new IrConst(IrType.I32, 0)]))), Has.Some.Contains("EnumToString"));
    Assert.That(Problems(Module(perm, new IrIntrinsic(IrIntrinsicOp.EnumHasFlag, IrType.Bool, [new IrConst(perm.Type, 0), new IrConst(IrType.I32, 0)]))), Has.Some.Contains("EnumHasFlag"));
    Assert.That(Problems(Module(perm, new IrIntrinsic(IrIntrinsicOp.EnumValues, IrType.Array(IrType.I32), [new IrConst(perm.Type, 0)]))), Has.Some.Contains("EnumValues"));
    Assert.That(Problems(Module(perm, new IrIntrinsic(IrIntrinsicOp.EnumNames, IrType.Array(IrType.String), [new IrConst(perm.Type, 0)]))), Is.Empty);
  }

  [Test]
  public void Verifier_RefusesTheTextOfAnEnumWhoseValuesRepeat() {
    var repeated = new IrEnumType("D", IrType.I32, [new("X", 1), new("Y", 1)]);
    Assert.That(Problems(Module(repeated, new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [new IrConst(repeated.Type, 1)]))), Has.Some.Contains("whose members repeat a value"));
    Assert.That(Problems(Module(repeated, EnumOperations.Listing(IrIntrinsicOp.EnumNames, repeated))), Has.Some.Contains("whose members repeat a value"));
    Assert.That(Problems(Module(repeated, new IrIntrinsic(IrIntrinsicOp.EnumIsDefined, IrType.Bool, [new IrConst(repeated.Type, 1)]))), Is.Empty);
  }

  [Test]
  public void Verifier_TakesOnlyTheOperatorsAndConversionsOfAnEnum() {
    var signed = Of<Signed>();
    var e = signed.Type;
    var one = new IrConst(e, 1);
    Assert.That(Problems(Module(signed, new IrBinary(IrBinaryOp.And, one, one))), Is.Empty);
    Assert.That(Problems(Module(signed, new IrBinary(IrBinaryOp.NotEqual, one, one))), Is.Empty);
    Assert.That(Problems(Module(signed, new IrBinary(IrBinaryOp.Add, one, one))), Has.Some.Contains("Add is applied to Signed and Signed"));
    Assert.That(Problems(Module(signed, new IrBinary(IrBinaryOp.Less, one, one))), Has.Some.Contains("Less"));
    Assert.That(Problems(Module(signed, new IrBinary(IrBinaryOp.Equal, one, new IrConst(IrType.I32, 1)))), Has.Some.Contains("Equal"));
    Assert.That(Problems(Module(signed, new IrUnary(IrUnaryOp.Negate, one))), Has.Some.Contains("Negate is applied to Signed"));
    var color = Of<Color>();
    var red = new IrConst(color.Type, 0);
    Assert.That(Problems(Module(color, new IrBinary(IrBinaryOp.Equal, red, red))), Is.Empty);
    Assert.That(Problems(Module(color, new IrBinary(IrBinaryOp.Or, red, red))), Has.Some.Contains("Or is applied to Color and Color"));
    Assert.That(Problems(Module(color, new IrUnary(IrUnaryOp.BitNot, red))), Has.Some.Contains("BitNot"));
    Assert.That(Problems(Module(signed, new IrConvert(IrType.I32, one))), Is.Empty);
    Assert.That(Problems(Module(signed, new IrConvert(e, new IrConst(IrType.I32, 7)))), Is.Empty);
    Assert.That(Problems(Module(signed, new IrConvert(IrType.I64, one))), Has.Some.Contains("a conversion from Signed to i64"));
    Assert.That(Problems(Module(signed, new IrConvert(IrType.I32, one, IrConversion.Trap))), Has.Some.Contains("a conversion from Signed to i32"));
    Assert.That(Problems(Module(signed, new IrConvert(e, one))), Has.Some.Contains("a conversion from Signed to Signed"));
  }

  [Test]
  public void Verifier_RefusesAnEnumTheModuleDoesNotDeclare() {
    var signed = Of<Signed>();
    var other = Of<Color>();
    Assert.That(Problems(Module(signed, new IrConst(other.Type, 0))), Has.Some.Contains("the type Color is not among the types of the module"));
    var module = new IrModule("M", [], [new IrModuleConst("K", other.Type, [0])]) { Types = [signed] };
    Assert.That(Problems(module), Has.Some.Contains("the type Color is not among the types of the module"));
    var global = new IrModule("M", []) { Types = [signed], Globals = [new IrGlobal("G", other.Type, null)] };
    Assert.That(Problems(global), Has.Some.Contains("the type Color is not among the types of the module"));
    var field = new IrStructType("S", [new IrField("C", other.Type, false)]);
    Assert.That(Problems(new IrModule("M", []) { Types = [field] }), Has.Some.Contains("the field 'C' has the type Color"));
  }

  [Test]
  public void Enum_InStructsGlobalsAndConstants_IsAccepted() {
    var color = Of<Color>();
    var field = new IrStructType("S", [new IrField("C", color.Type, false)]);
    var module = new IrModule("M", [], [new IrModuleConst("K", color.Type, [5])]) {
      Types = [color, field],
      Globals = [new IrGlobal("G", color.Type, new IrIntrinsic(IrIntrinsicOp.EnumParse, color.Type, [new IrStrConst("Blue"), new IrConst(IrType.Bool, 0)]))]
    };
    Assert.That(Problems(module), Is.Empty);
    Assert.That(field.HasDefault, Is.True);
  }

  [Test]
  public void ModuleFormat_KeepsTheEnums() {
    var perm = Of<Perm>();
    var big = new IrEnumType("Big", IrType.U64, [new("Max", -1)]) { IsPublic = true };
    var x = new IrLocal("x", perm.Type);
    var function = new IrFunction("F", true, IrType.String, [x], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.EnumToString, IrType.String, [new IrLocalRef(x)]))]));
    var record = new IrStructType("R", [new IrField("P", perm.Type, true)]);
    var module = new IrModule("M", [function]) { Types = [perm, big, record] };
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(Precomputed.Support.IrGraph.Difference(module, read), Is.Null);
    var named = (IrEnumType)read.Types[0];
    Assert.That(named.Name, Is.EqualTo("Perm"));
    Assert.That(named.IsFlags, Is.True);
    Assert.That(named.Underlying, Is.EqualTo(IrType.I32));
    Assert.That(named.Members, Is.EqualTo(perm.Members));
    Assert.That(((IrEnumType)read.Types[1]).IsPublic, Is.True);
    Assert.That(((IrEnumType)read.Types[1]).Members[0].Value, Is.EqualTo(-1));
    Assert.That(((IrStructType)read.Types[2]).Fields[0].Type.EnumType, Is.SameAs(named));
    Assert.That(read.Functions[0].Parameters[0].Type.EnumType, Is.SameAs(named));
    Assert.That(IrInterpreter.Call(read, "F", [5]), Is.EqualTo("Read, Exec"));
  }

  [Test]
  public void ModuleFormat_RefusesAnEnumOfAnotherModule() {
    var perm = Of<Perm>();
    var function = new IrFunction("F", true, perm.Type, [], [], new IrBlock([new IrReturn(new IrConst(perm.Type, 0))]));
    Assert.That(() => IrModuleFormat.Write(new IrModule("M", [function])), Throws.ArgumentException.With.Message.Contains("The enum 'Perm'"));
  }

}
