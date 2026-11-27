using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Frontends.Go.Support;

namespace Polypiler.Tests.Frontends.Go;

/// <summary>
/// The Go frontend on the fixtures of <c>tests/fixtures/go/frontend</c>, parsed and type-checked by the
/// frontend itself without any Go installed: the imported module runs in the interpreter with Go's results - wrap-around at
/// the operand width, unmasked shifts, exact constants, the division that wraps the smallest value by
/// -1 - and every construct outside the subset is refused with the code that names it.
/// </summary>
[TestFixture]
public sealed class GoFrontendTests {

  /// <summary>The folder of the fixtures.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend");

  /// <summary>Imports a fixture.</summary>
  public static GoImportResult Imported(string relative, int minor = 27) {
    var path = Path.Combine(Folder, relative);
    return GoFrontend.Import(Path.GetFileName(path), File.ReadAllText(path), new GoImportOptions { LanguageMinor = minor });
  }

  private static readonly Lazy<IrModule> _checksums = new(() => {
    var result = Imported("checksums.go");
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("\n", result.Diagnostics));
    return result.Module!;
  });

  private static object? Call(string function, params object[] arguments) => IrInterpreter.Call(_checksums.Value, function, arguments);

  private static IrTrap Trap(string function, params object[] arguments)
    => Assert.Throws<IrTrapException>(() => Call(function, arguments))!.Trap;

  private static string Thrown(string function, params object[] arguments)
    => Assert.Catch<IrException>(() => Call(function, arguments))!.Message;

  [Test]
  public void Checksums_HaveThePublishedVectors() {
    Assert.That(Call("Adler32", Encoding.ASCII.GetBytes("Wikipedia")), Is.EqualTo(0x11E60398u));
    Assert.That(Call("Fnv1a32", Encoding.ASCII.GetBytes("a")), Is.EqualTo(0xE40C292Cu));
    Assert.That(Call("Fnv1a32", Array.Empty<byte>()), Is.EqualTo(0x811C9DC5u));
  }

  [Test]
  public void SizedIntegers_WrapAtTheirOwnWidth_WithoutPromotion() {
    Assert.That(Call("AddWrap", int.MaxValue, 1), Is.EqualTo(int.MinValue));
    Assert.That(Call("ByteSum", (byte)200, (byte)100), Is.EqualTo((byte)44));
    Assert.That(Call("Promote"), Is.EqualTo((byte)44));
    Assert.That(Call("Complement", (byte)0x0F), Is.EqualTo((byte)240));
    Assert.That(Call("ClearBits", 0xFFu, 0x0Fu), Is.EqualTo(0xF0u));
  }

  [Test]
  public void Shifts_AreNotMasked_AndANegativeCountTraps() {
    Assert.That(Call("ShiftLeft", 1u, 31UL), Is.EqualTo(0x80000000u));
    Assert.That(Call("ShiftLeft", 1u, 32UL), Is.EqualTo(0u));
    Assert.That(Call("ShiftLeft", 1u, ulong.MaxValue), Is.EqualTo(0u));
    Assert.That(Call("ShiftRight", -8, 40UL), Is.EqualTo(-1));
    Assert.That(Call("ShiftRight", 8, 40UL), Is.EqualTo(0));
    Assert.That(Call("ShiftRight", -8, 1UL), Is.EqualTo(-4));
    Assert.That(Call("ShiftSigned", 1u, 3L), Is.EqualTo(8u));
    Assert.That(Thrown("ShiftSigned", 1u, -1L), Is.EqualTo("negative shift amount"));
  }

  [Test]
  public void UntypedConstants_AreExact() {
    Assert.That(Call("Quarter"), Is.EqualTo(4L));
    Assert.That(BitConverter.DoubleToInt64Bits((double)Call("ExactSum")!), Is.EqualTo(0x3FD3333333333333L));
    Assert.That(BitConverter.DoubleToInt64Bits((double)Call("VariableSum", 0.1, 0.2)!), Is.EqualTo(0x3FD3333333333334L));
  }

  [Test]
  public void Division_Truncates_WrapsTheSmallestValueByMinusOne_AndTrapsOnZero() {
    Assert.That(Call("Divide", -7, 2), Is.EqualTo(-3));
    Assert.That(Call("Remainder", -7, 3), Is.EqualTo(-1));
    Assert.That(Call("Divide", int.MinValue, -1), Is.EqualTo(int.MinValue));
    Assert.That(Call("Remainder", int.MinValue, -1), Is.EqualTo(0));
    Assert.That(Trap("Divide", 1, 0), Is.EqualTo(IrTrap.DivideByZero));
    Assert.That(Call("CountDown", 100L), Is.EqualTo(7L));
  }

  [Test]
  public void Library_IsTheExactOperations() {
    Assert.That(Call("Rotate", 0x80000001u, 1L), Is.EqualTo(3u));
    Assert.That(Call("Rotate", 3u, -1L), Is.EqualTo(0x80000001u));
    Assert.That(Call("Ones", 0xFFUL), Is.EqualTo(8L));
    Assert.That(Call("Truncate", -3.99), Is.EqualTo(-3));
    Assert.That(Call("Truncate", 3e10), Is.EqualTo(int.MaxValue));
    Assert.That(Call("Larger", 3L, -5L), Is.EqualTo(3L));
  }

  [Test]
  public void ControlFlow_IsGos() {
    Assert.That(new[] { 7, 3, 9, 1 }.Select(x => Call("Classify", x)), Is.EqualTo(new object[] { 0, 6, -1, 0 }));
    Assert.That(new[] { -5, 0, 5 }.Select(x => Call("Sign", x)), Is.EqualTo(new object[] { -1, 0, 1 }));
    Assert.That(Call("SumTo", 100), Is.EqualTo(5050));
    Assert.That(Call("Squares", 4L), Is.EqualTo(new long[] { 0, 1, 4, 9 }));
    Assert.That(Call("Fib", 10L), Is.EqualTo(55u));
    Assert.That(Call("FirstAbove", new[] { 1, 5, 9 }, 4), Is.EqualTo(1L));
    Assert.That(Call("FirstAbove", new[] { 1 }, 4), Is.EqualTo(-1L));
    Assert.That(Thrown("Fail", -1), Is.EqualTo("negative code"));
    Assert.That(Call("Fail", 2), Is.EqualTo(2));
  }

  [Test]
  public void Structs_AreValues() {
    Assert.That(Call("Dot", new object[] { 1, 2 }, new object[] { 3, 4 }), Is.EqualTo(11));
    Assert.That(Call("Moved", new object[] { 1, 2 }, 5), Is.EqualTo(new object[] { 6, 2 }));
  }

  [Test]
  public void Module_IsThePackage_WithItsCaveats() {
    var result = Imported("checksums.go");
    Assert.That(result.Module!.Name, Is.EqualTo("checksums"));
    Assert.That(result.Module.Functions.Where(function => function.IsPublic).Select(function => function.Name), Does.Contain("Adler32").And.Contain("Fail"));
    Assert.That(result.Caveats, Does.Contain(GoFrontend.FloatToInteger));
    Assert.That(result.Module.Caveats, Does.Contain("go-float-to-int"));
  }

  [Test]
  public void PerIterationLoopVariables_ComeWithGo122() {
    static bool PerIteration(IrModule module) => module.Functions.Single(function => function.Name == "SumTo").Body.Statements
      .SelectMany(Statements).OfType<IrFor>().Single().PerIteration.Count > 0;
    Assert.That(PerIteration(Imported("checksums.go", 22).Module!), Is.True);
    Assert.That(Imported("checksums.go", 21).Diagnostics.Single().ToString(), Does.Contain("PLY2100").And.Contain("requires go1.22 or later"));
  }

  private static System.Collections.Generic.IEnumerable<IrStmt> Statements(IrStmt statement)
    => statement is IrBlock block ? block.Statements.SelectMany(Statements).Prepend(statement) : [statement];

  /// <summary>Every construct of <c>constructs.go</c> imported, called in the interpreter with Go's own outcome as go recorded it.</summary>
  [Test]
  public void Constructs_HaveTheOutcomesGoRecorded() {
    var result = Imported("constructs.go");
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("\n", result.Diagnostics));
    var recorded = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(GoConstructCalls.Recorded))!;
    var calls = GoConstructCalls.All;
    Assert.That(recorded, Has.Length.EqualTo(calls.Count));
    var outcomes = calls.Select(call => {
      try {
        return PrototypeFixture.InterpretedOutcome(result.Module!, call);
      } catch (IrException exception) when (exception is not IrTrapException) {
        return "exception: " + exception.Message;
      }
    });
    Assert.That(outcomes, Is.EqualTo(recorded));
  }

  [TestCase("import_fmt", "PLY2102", "import \"fmt\"")]
  [TestCase("method", "PLY2102", "method Get")]
  [TestCase("generic", "PLY2102", "generic function Id")]
  [TestCase("two_results", "PLY2102", "with 2 results")]
  [TestCase("named_result", "PLY2102", "named result")]
  [TestCase("variadic", "PLY2102", "variadic parameter")]
  [TestCase("goroutine", "PLY2102", "go statement")]
  [TestCase("defer", "PLY2102", "defer")]
  [TestCase("pointer", "PLY2101", "*int32")]
  [TestCase("map", "PLY2101", "map[string]int32")]
  [TestCase("slice_expr", "PLY2102", "slice expression")]
  [TestCase("closure", "PLY2101", "func() int32")]
  [TestCase("fallthrough", "PLY2102", "fallthrough")]
  [TestCase("goto", "PLY2102", "goto")]
  [TestCase("outer_break", "PLY2102", "break outer")]
  [TestCase("outer_continue", "PLY2102", "continue outer")]
  [TestCase("append", "PLY2102", "built-in append")]
  [TestCase("string_order", "PLY2102", "ordering of strings")]
  [TestCase("complex", "PLY2101", "complex128")]
  [TestCase("range_string", "PLY2102", "range over string")]
  [TestCase("function_value", "PLY2101", "func()")]
  [TestCase("unused_variable", "PLY2100", "declared and not used")]
  [TestCase("syntax", "PLY2100", "syntax error")]
  [TestCase("constant_overflow", "PLY2100", "overflows")]
  [TestCase("panic_value", "PLY2102", "panic of a value")]
  [TestCase("struct_float_compare", "PLY2102", "comparison of struct")]
  [TestCase("embedded", "PLY2102", "embedded field")]
  [TestCase("local_type", "PLY2102", "local type declaration")]
  [TestCase("make_capacity", "PLY2102", "capacity")]
  [TestCase("invalid_utf8", "PLY2102", "no valid UTF-8")]
  [TestCase("alias", "PLY2102", "type alias T")]
  public void ConstructOutsideTheSubset_IsRefusedByName(string name, string code, string text) {
    var result = Imported(Path.Combine("refused", name + ".go"));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.Some.Contains(code).And.Some.Contains(text));
  }

  [Test]
  public void Forms_AreGos() {
    var result = Imported("forms.go");
    Assert.That(result.Diagnostics, Is.Empty, () => string.Join("\n", result.Diagnostics));
    object? Run(string function, params object[] arguments) => IrInterpreter.Call(result.Module!, function, arguments);
    Assert.That(Run("Ignore", 5, 6), Is.EqualTo(7));
    Assert.That(Run("Swap", [new object[] { 1, 2 }]), Is.EqualTo(new object[] { 2, 1 }));
    Assert.That(new[] { 6, 3, 1 }.Select(x => Run("Grade", x)), Is.EqualTo(new object[] { 3, 2, 1 }));
    Assert.That(Run("Calls", 4), Is.EqualTo(5));
    Assert.That(Run("Floats", 2.0, 3.0, 4.0), Is.EqualTo(2.0 * 3.0 + 4.0 - 2.0 * 3.0 * 4.0));
    Assert.That(Run("Pick", new[] { 1, 2, 3, 4 }, 0, (byte)1, (short)2, (ushort)3, 0u), Is.EqualTo(11));
    Assert.That(result.Caveats, Does.Contain(GoFrontend.Fusion));
  }

  [TestCase("func F(s []int32) bool { return s == nil }", "PLY2102", "'nil'")]
  [TestCase("func F(a, b []int32) []int32 { return append(a, b...) }", "PLY2102", "spread argument")]
  [TestCase("func F() { func() {}() }", "PLY2102", "call of a function value")]
  [TestCase("func F(s string) []byte { return []byte(s) }", "PLY2102", "conversion from string to []byte")]
  [TestCase("func F(s string) int { return len(s) }", "PLY2102", "len of string")]
  [TestCase("import \"math\"\n\nfunc F(x float64) float64 { return math.Gamma(x) }", "PLY2102", "math.Gamma")]
  [TestCase("func G() {}\n\nfunc F() { _ = G }", "PLY2101", "func()")]
  [TestCase("func F(s string) byte { return s[0] }", "PLY2102", "index of string")]
  [TestCase("func F() []int32 { return []int32{1: 2} }", "PLY2102", "slice literal with indices")]
  [TestCase("func F(m map[int]int) {}", "PLY2101", "map[int]int")]
  [TestCase("type P struct{ A int32 }\n\nvar origin P", "PLY2102", "global origin of a struct type")]
  public void WhatTheLowererRefuses_IsNamed(string declarations, string code, string text) {
    var result = GoFrontend.Import("x.go", "package x\n\n" + declarations + "\n");
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.ToString()), Has.One.Contains(code).And.Contains(text));
  }

  [Test]
  public void AFunctionWithoutABody_IsRefused() {
    var result = GoFrontend.Import("x.go", "package x\n\nfunc External(x int32) int32\n");
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain("PLY2102").And.Contain("function External without a body"));
  }

  [Test]
  public void ANestingDeeperThanTheParserFollows_IsRefused() {
    var source = "package x\n\nvar y = " + new string('(', 1200) + "1" + new string(')', 1200) + "\n";
    var result = GoFrontend.Check("x.go", source);
    Assert.That((result.Verdict, result.Parsed), Is.EqualTo((GoVerdict.Unsupported, false)));
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain("PLY2102").And.Contain("nesting deeper than 1000 levels"));
  }

  [Test]
  public void AnExcessivelyLongConstant_IsAnError() {
    var result = GoFrontend.Check("x.go", "package x\n\nconst c = 1" + new string('0', 10001) + "\n");
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain("PLY2100").And.Contain("excessively long constant"));
  }

  [Test]
  public void AConstantStringBeyondTheFrontendsLimit_IsRefused() {
    var lines = string.Join("\n", Enumerable.Range(1, 25).Select(index => $"\tc{index} = c{index - 1} + c{index - 1}"));
    var result = GoFrontend.Check("x.go", "package x\n\nconst (\n\tc0 = \"x\"\n" + lines + "\n)\n");
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain("PLY2102").And.Contain("longer than 16 MiB"));
  }

  [Test]
  public void TheCheck_ReportsThePackageConstants_OfAValidSource() {
    var result = GoFrontend.Check("x.go", "package x\n\nconst (\n\t_ = 1\n\tA = 1 << 70\n\tB float32 = 0.1\n)\n");
    Assert.That(result.Verdict, Is.EqualTo(GoVerdict.Valid));
    Assert.That(result.Constants, Is.EqualTo(new[] { new GoConstantValue("A", "untyped int", "1180591620717411303424"), new GoConstantValue("B", "float32", "13421773/134217728") }));
  }

  [Test]
  public void TheArguments_AreRequired() {
    Assert.That(() => GoFrontend.Import(null!, ""), Throws.ArgumentNullException);
    Assert.That(() => GoFrontend.Import("x.go", null!), Throws.ArgumentNullException);
    Assert.That(() => GoFrontend.Check(null!, ""), Throws.ArgumentNullException);
    Assert.That(() => GoFrontend.Check("x.go", null!), Throws.ArgumentNullException);
  }

  [Test]
  public void ABuildConstraint_SetsTheLanguageVersion() {
    static bool PerIteration(string header) {
      var source = header + "package x\n\nfunc S(n int32) int32 {\n\tvar s int32\n\tfor i := int32(0); i < n; i++ {\n\t\ts += i\n\t}\n\treturn s\n}\n";
      var module = GoFrontend.Import("x.go", source, new GoImportOptions { LanguageMinor = 27 }).Module!;
      return module.Functions.Single(function => function.Name == "S").Body.Statements.SelectMany(Statements).OfType<IrFor>().Single().PerIteration.Count > 0;
    }

    Assert.That(PerIteration(""), Is.True);
    Assert.That(PerIteration("//go:build go1.18\n\n"), Is.False, "go1.18 is raised to go1.21, before per-iteration variables");
    Assert.That(PerIteration("//go:build go1.22 && linux\n\n"), Is.True);
  }

  [Test]
  public void BeforeGo121_MinMaxAndClearAreUnknown() {
    var result = GoFrontend.Check("x.go", "package x\n\nvar y = min(1, 2)\n", new GoImportOptions { LanguageMinor = 20 });
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain("PLY2100").And.Contain("min requires go1.21 or later"));
  }

}
