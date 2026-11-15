using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Enums;

/// <summary>
/// The C# constructs on enums (#1318), each run by the interpreter against .NET running the original,
/// and the constructs the frontend refuses by name: every row of <see cref="CSharpEnumApi"/>.
/// </summary>
[TestFixture]
public sealed class CSharpEnumImportTests {

  /// <summary>The enums every case may use: one of each kind the issue names.</summary>
  internal const string Enums = """
    [System.Flags] public enum Perm { None = 0, Read = 1, Write = 2, Exec = 4, ReadWrite = 3 }
    public enum Color : byte { Red, Green = 5, Blue }
    public enum S { B = 2, Neg = -1, A = 0, C = 1 }
    public enum Big : ulong { Max = ulong.MaxValue, One = 1 }
    public enum Small : sbyte { Low = -128, High = 127 }
    public enum Half : short { Low = -1, High = 2 }
    public enum Word : ushort { A = 65535 }
    public enum Unsigned : uint { Top = 4294967295, One = 1 }
    [System.Flags] public enum Long : long { Low = 1, High = -9223372036854775808 }
    public enum Dup { X = 1, Y = 1 }
    public enum None { }
    """;

  private static IrModule Import(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static readonly (string Row, string Methods, object[][] Calls)[] _cases = [
    ("enum E [: integer type] { … } at the top of the file or in the static class, [Flags], explicit and implicit values",
      "public enum Inner : short { P = -3, Q, R = 10 } public static string F(int v) => ((Inner)v).ToString() + (int)Inner.Q + ((Long)v).ToString() + ((Half)v) + ((Word)v) + ((Unsigned)v) + ((Small)v);",
      [[-3], [-2], [10], [11], [1], [-1], [65535], [-128]]),
    ("E.A, locals, parameters, results, fields, constants and arrays of E, casts to and from integers",
      "const Color Fixed = Color.Blue; static readonly Color[] Table = { Color.Red, Color.Green }; static Color current = Color.Green; "
      + "static Color Pick(Color c, int i) { Color[] local = new Color[2]; local[1] = c; current = local[i]; return current; } "
      + "public static int F(int v, int i) { Color c = (Color)v; var picked = Pick(c, i); return (int)picked + 1000 * (int)Fixed + 100000 * (int)Table[1] + (int)(long)(Big)v; }",
      [[7, 1], [300, 1], [5, 0]]),
    ("&, |, ^, ~ of E; e + n, e - n, e - e; ==, !=, <, <=, >, >=; compound assignments, ++ and --",
      "public static string F(int a, int b, byte n) { var p = (Perm)a; var q = (Perm)b; var c = (Color)n; var s = (S)a; "
      + "var r = (p & q) | (p ^ Perm.Exec); r |= Perm.Read; r &= ~Perm.Write; c += n; c -= 1; c++; --c; var d = c - Color.Green; s++; "
      + "Color x = (Color)a, y = (Color)b; var e = x | y; e ^= Color.Blue; var f = ~x; var g = (Color)a | (Color)b; var h = (Color)(byte)x; "
      + "return (int)r + \",\" + (int)(~p) + \",\" + (int)c + \",\" + d + \",\" + (int)s + \",\" + (p == q) + (p != q) + (p < q) + (p <= q) + (p > q) + (p >= q) + (x == y) + \",\" + (int)e + \",\" + (int)f + \",\" + (int)(Color.Blue + n) + (int)(n + Color.Red) + \",\" + (int)g + (int)h; }",
      [[1, 2, (byte)250], [7, 7, (byte)3], [-1, 0, (byte)0]]),
    ("switch on E",
      "public static int F(int v) { switch ((Color)v) { case Color.Red: return 10; case Color.Green: case Color.Blue: return 20; default: return 30; } }",
      [[0], [5], [6], [7]]),
    ("e.ToString(), string + e, interpolation {e}",
      "public static string F(int v) => ((Perm)v).ToString() + \"|\" + (\"c=\" + (Color)v) + $\"<{(S)v}>\" + ((Big)v).ToString();",
      [[0], [3], [5], [7], [8], [-1], [6]]),
    ("Enum.IsDefined(typeof(E), v), Enum.IsDefined<E>(v)",
      "public static string F(int v, byte b) => System.Enum.IsDefined(typeof(Perm), v) + \"|\" + System.Enum.IsDefined(typeof(Color), b) + \"|\" + System.Enum.IsDefined(typeof(Color), (Color)b) + \"|\" + System.Enum.IsDefined<S>((S)v);",
      [[5, (byte)6], [3, (byte)7], [-1, (byte)0]]),
    ("(E)Enum.Parse(typeof(E), s[, ignoreCase]), Enum.Parse<E>(s[, ignoreCase])",
      "public static int F(string s) => (int)(Perm)System.Enum.Parse(typeof(Perm), s) + 1000 * (int)System.Enum.Parse<Perm>(s, true) + 1000000 * (int)((Perm)(System.Enum.Parse(typeof(Perm), s, false)));",
      [[" Read "], ["Read, Write"], ["read"], ["5"], [""], ["99999999999"]]),
    ("e.HasFlag(f)",
      "public static string F(int x, int f) => ((Perm)x).HasFlag((Perm)f) + \"|\" + ((Perm)x).HasFlag(Perm.Read) + \"|\" + ((Long)x).HasFlag(Long.High);",
      [[7, 5], [6, 5], [0, 0], [-1, 4]]),
    ("Enum.GetNames(typeof(E)), Enum.GetNames<E>()",
      "public static string F() => string.Join(\",\", System.Enum.GetNames(typeof(S))) + \"|\" + string.Join(\",\", System.Enum.GetNames<Big>()) + \"|\" + System.Enum.GetNames(typeof(None)).Length;",
      [[]]),
    ("(E[])Enum.GetValues(typeof(E)), Enum.GetValues<E>()",
      "public static string F() { var s = (S[])System.Enum.GetValues(typeof(S)); var text = \"\"; foreach (var v in s) text += (int)v + \",\"; foreach (var c in System.Enum.GetValues<Color>()) text += (int)c + \";\"; return text + System.Enum.GetValues<None>().Length; }",
      [[]])
  ];

  private static IEnumerable<TestCaseData> Cases() => _cases.Select(entry => new TestCaseData(entry.Methods, entry.Calls).SetName("Imports: " + entry.Row));

  /// <summary>Test design 1 and 2: every supported row, one enum per underlying type among them, interpreted and run by .NET with the same arguments.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_IsImported_AndComputesWhatDotNetComputes(string methods, object[][] calls) {
    var source = Enums + PrototypeFixture.Wrap(methods);
    var module = Import(source);
    var compiled = GeneratedCSharp.Compile(source, false).Method("F");
    foreach (var arguments in calls) {
      var expected = Outcome(() => compiled.Invoke(null, [.. arguments]));
      var actual = Outcome(() => IrInterpreter.Call(module, "F", [.. arguments]));
      Assert.That(actual, Is.EqualTo(expected), string.Join(", ", arguments.Select(PrototypeFixture.Show)));
    }
  }

  internal static string Outcome(Func<object?> call) {
    try {
      return PrototypeFixture.Show(call());
    } catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException or OverflowException) {
      return exception.InnerException is OverflowException ? "integer overflow" : "invalid format";
    } catch (IrTrapException trap) {
      return IrTrapException.Describe(trap.Trap);
    }
  }

  [Test]
  public void EverySupportedRow_HasACase()
    => Assert.That(_cases.Select(entry => entry.Row), Is.EquivalentTo(CSharpEnumApi.Supported.Select(member => member.Construct)));

  [Test]
  public void EveryRefusedRow_HasAReason() {
    Assert.That(CSharpEnumApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
    Assert.That(CSharpEnumApi.Members.Select(member => member.Construct), Is.Unique);
  }

  [Test]
  public void Declaration_IsATypeOfTheModule() {
    var module = Import(Enums + PrototypeFixture.Wrap("internal enum Hidden { A } internal static int F(Hidden h) => (int)h;"));
    var types = module.Types.OfType<IrEnumType>().ToDictionary(type => type.Name);
    Assert.That(types["Perm"].IsFlags, Is.True);
    Assert.That(types["Perm"].IsPublic, Is.True);
    Assert.That(types["Hidden"].IsPublic, Is.False);
    Assert.That(types["Color"].Underlying, Is.EqualTo(IrType.U8));
    Assert.That(types["Color"].Members.Select(member => (member.Name, member.Value)), Is.EqualTo(new[] { ("Red", 0L), ("Green", 5L), ("Blue", 6L) }));
    Assert.That(types["Big"].Members[0].Value, Is.EqualTo(-1));
    Assert.That(module.Functions[0].Parameters[0].Type.EnumType, Is.SameAs(types["Hidden"]));
  }

  /// <summary>Test design 7: every refused construct of the table, by name, with its reason.</summary>
  [TestCase("public static string F(Perm p) => p.ToString(\"X\");", "Enum.ToString(String) with a format")]
  [TestCase("public static string F(Perm p) => p.ToString(\"D\");", "Enum.ToString(String) with a format")]
  [TestCase("public static string F(Perm p) => $\"{p:G}\";", "string + Perm with a format")]
  [TestCase("public static bool F(string s) { return System.Enum.TryParse<Perm>(s, out _); }", "Enum.TryParse(String, Perm), whose result is an out parameter (#216)")]
  [TestCase("public static string F(Perm p) => System.Enum.Format(typeof(Perm), p, \"G\");", "Enum.Format(Type, Object, String)")]
  [TestCase("public static string F(Perm p) => System.Enum.GetName(p);", "Enum.GetName(Perm)")]
  [TestCase("public static string F(Dup d) => d.ToString();", "Enum.ToString() of the enum Dup, whose members repeat a value, for which .NET does not say which name it gives")]
  [TestCase("public static string F(Dup d) => \"\" + d;", "string + Dup of the enum Dup, whose members repeat a value, for which .NET does not say which name it gives")]
  [TestCase("public static int F() => System.Enum.GetNames<Dup>().Length;", "Enum.GetNames() of the enum Dup, whose members repeat a value, which .NET lists in no defined order")]
  [TestCase("public static int F() => ((Dup[])System.Enum.GetValues(typeof(Dup))).Length;", "Enum.GetValues(Type) of the enum Dup, whose members repeat a value, which .NET lists in no defined order")]
  [TestCase("public static bool F(int v) => System.Enum.IsDefined(typeof(Color), v);", "Enum.IsDefined(Type, Object) with a value of the type i32, which is neither Color nor its underlying type u8, for which .NET throws")]
  [TestCase("public static bool F(Perm p, Color c) => p.HasFlag(c);", "Enum.HasFlag(Enum) with a flag of the type Color, which is not the enum Perm, for which .NET throws")]
  [TestCase("public static int F(string s) => (int)System.Enum.Parse(typeof(Perm), s);", "Enum.Parse(Type, String) cast to i32, which is not the enum it parses")]
  [TestCase("public static int F() => ((int[])System.Enum.GetValues(typeof(Perm))).Length;", "Enum.GetValues(Type) cast to i32[], which is not the array of the enum it lists")]
  [TestCase("public static bool F(int v) => System.Enum.IsDefined(typeof(int), v);", "Enum.IsDefined(Type, Object) of another type than an enum of the file named by typeof")]
  [TestCase("public static string F(System.DayOfWeek d) => d.ToString();", "System.DayOfWeek")]
  public void Construct_IsRefusedByName(string method, string what) {
    var result = CSharpFrontend.Import("Snippet.cs", Enums + PrototypeFixture.Wrap(method));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((CSharpFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

  [TestCase("[System.Obsolete] public enum Old { A }", "the attribute [System.Obsolete] on an enum")]
  [TestCase("", "a second type named Perm", "public enum Perm { A }")]
  [TestCase("", "a second type named Color", "public struct Color { public int X; }")]
  [TestCase("file enum Local { A } ", "a file enum")]
  public void Declaration_IsRefusedByName(string declaration, string what, string nested = "") {
    var result = CSharpFrontend.Import("Snippet.cs", Enums + declaration + PrototypeFixture.Wrap(nested + " public static int F() => 1;"));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.EqualTo($"'{what}' is not supported yet"));
  }

}
