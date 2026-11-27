using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Go;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Go;

/// <summary>
/// The Go frontend on the bound trees its helper printed for the fixtures of
/// <c>tests/fixtures/go/frontend</c>, recorded next to each source (the Go layer checks that the helper
/// still prints them): the imported module runs in the interpreter with Go's results - wrap-around at
/// the operand width, unmasked shifts, exact constants, the division that wraps the smallest value by
/// -1 - and every construct outside the subset is refused with the code that names it.
/// </summary>
[TestFixture]
public sealed class GoFrontendTests {

  /// <summary>The folder of the fixtures.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend");

  /// <summary>Imports a fixture from its recorded tree.</summary>
  public static GoImportResult Imported(string relative, int minor = 27) {
    var path = Path.Combine(Folder, relative);
    var dump = File.ReadAllText(Path.ChangeExtension(path, ".dump.json"));
    return GoFrontend.Import(Path.GetFileName(path), File.ReadAllText(path), new GoImportOptions { Dump = dump, LanguageMinor = minor });
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
    Assert.That(result.Go, Does.StartWith("go1."));
    Assert.That(GoFrontend.HelperSource, Does.Contain("package main").And.Contain("go/types"));
  }

  [Test]
  public void PerIterationLoopVariables_ComeWithGo122() {
    static bool PerIteration(IrModule module) => module.Functions.Single(function => function.Name == "SumTo").Body.Statements
      .SelectMany(Statements).OfType<IrFor>().Single().PerIteration.Count > 0;
    Assert.That(PerIteration(Imported("checksums.go", 22).Module!), Is.True);
    Assert.That(PerIteration(Imported("checksums.go", 21).Module!), Is.False);
  }

  private static System.Collections.Generic.IEnumerable<IrStmt> Statements(IrStmt statement)
    => statement is IrBlock block ? block.Statements.SelectMany(Statements).Prepend(statement) : [statement];

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

  [TestCase("not json", "wrote no tree")]
  [TestCase("{\"format\": 2}", "another wire format")]
  [TestCase("[]", "another wire format")]
  [TestCase("{\"format\": 1, \"go\": \"go1.27\"}", "neither a tree nor an error")]
  public void ABrokenDump_IsAnErrorOfTheHelper(string dump, string text) {
    var result = GoFrontend.Import("x.go", "package x\n", new GoImportOptions { Dump = dump });
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain("PLY2100").And.Contain(text));
  }

  [Test]
  public void AMissingToolchain_IsReportedWithTheVariableThatNamesOne() {
    var result = GoFrontend.Import("x.go", "package x\n", new GoImportOptions { Toolchain = Path.Combine(Path.GetTempPath(), "no-such-go-" + Guid.NewGuid().ToString("N")) });
    Assert.That(result.Diagnostics.Single().ToString(), Does.Contain("PLY2100").And.Contain("the Go helper failed").And.Contain("POLYPILER_GO"));
  }

  /// <summary>The helper itself, where Go is installed: what it prints for every fixture is what the fixture recorded, the version of Go aside.</summary>
  [Test]
  public void TheHelper_PrintsTheRecordedTrees() {
    var go = GoFrontend.Import("probe.go", "package probe\n");
    if (go.Module is null)
      Assert.Inconclusive($"no Go is installed: {string.Join("; ", go.Diagnostics)}");

    foreach (var source in Directory.EnumerateFiles(Folder, "*.go", SearchOption.AllDirectories)) {
      var printed = GoFrontend.Print(Path.GetFileName(source), File.ReadAllText(source));
      Assert.That(Unversioned(printed.Json ?? printed.Failure!), Is.EqualTo(Unversioned(File.ReadAllText(Path.ChangeExtension(source, ".dump.json")))), source);
    }

    var unchecked_ = GoFrontend.Import("checksums.go", File.ReadAllText(Path.Combine(Folder, "checksums.go")));
    Assert.That(unchecked_.Module!.Functions.Select(function => function.Name), Is.EqualTo(_checksums.Value.Functions.Select(function => function.Name)));
  }

  private static string Unversioned(string json) => System.Text.RegularExpressions.Regex.Replace(json, "\"go\":\"[^\"]*\"", "\"go\":\"\"");

}
