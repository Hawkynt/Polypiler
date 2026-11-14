using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// Locals of one name whose C# declaration spaces nest. C# forbids a local named like one of an
/// enclosing block or like a parameter, whichever comes first in the text (ECMA-334, "Local variable
/// declaration spaces"; CS0136); JavaScript and the IR allow it. The emitter renames the later of two
/// such locals and leaves every other name as it is. Every file is built by the C# compiler against
/// the reference assemblies of its profile and called; the outcome is the interpreter's and the one
/// <c>node</c> printed for the original source, written down here.
/// </summary>
[TestFixture]
public sealed class CSharpScopeTests {

  private static readonly bool[] _overflowChecking = [false, true];

  /// <summary>The directory of the JavaScript fixtures of this test.</summary>
  private static readonly string _scripts = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "js", "scopes");

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrConst Double(double value) => new(IrType.F64, BitConverter.DoubleToInt64Bits(value));

  private static IrLocalRef Ref(IrLocal local) => new(local);

  private static IrBinary Op(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  /// <summary>A module of one public function <c>F</c>; its locals are those the body declares, in the order given.</summary>
  private static IrModule Module(IrType returns, IrLocal[] parameters, IrLocal[] locals, params IrStmt[] body)
    => new("Scopes", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]);

  /// <summary>The module's file for a profile, after the C# compiler built it for the reference assemblies of the profile without complaint.</summary>
  private static string Emitted(IrModule module, CSharpProfile profile) {
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var text = CSharpEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
    return text;
  }

  /// <summary>What a call does in the compiled text, which must not depend on the overflow setting.</summary>
  private static string Compiled(string text, PrototypeCall call) {
    var outcomes = _overflowChecking.Select(checking => GeneratedCSharp.Compile(text, checking).Outcome(call)).Distinct().ToList();
    Assert.That(outcomes, Has.Count.EqualTo(1), $"{call} depends on CheckOverflow");
    return outcomes[0];
  }

  /// <summary>Translates a fixture with the <c>translate</c> command inside the test process and returns the text written.</summary>
  private string Translate(string source, string from, string profile) {
    var output = Path.Combine(this._directory, "out-" + Guid.NewGuid().ToString("N"));
    using var printed = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", source, "--from", from, "--to", "csharp", "--profile", profile, "--output", output], printed, error);
    Assert.That((exitCode, error.ToString()), Is.EqualTo((Program.Success, "")));
    return File.ReadAllText(Directory.GetFiles(output).Single());
  }

  private static IEnumerable<TestCaseData> Profiles(string fixture, string function, double argument, double result)
    => CSharpProfile.All.Select(profile => new TestCaseData(fixture, function, argument, result, profile.Name).SetCategory("Slow")
      .SetArgDisplayNames(fixture, $"{function}({argument})", result.ToString(System.Globalization.CultureInfo.InvariantCulture), profile.Name));

  /// <summary>Test design 1, 2 and 4, and the same for a record-typed local: the values <c>node</c> prints for the sources.</summary>
  private static IEnumerable<TestCaseData> Scripts()
    => Profiles("later", "f", 3, 3.5).Concat(Profiles("later", "f", 0, 0.5))
      .Concat(Profiles("loop", "g", 4, 16)).Concat(Profiles("loop", "g", 0, 10))
      .Concat(Profiles("later2", "f", 3, 6)).Concat(Profiles("later2", "f", 1, 1))
      .Concat(Profiles("records", "f", 4, 14)).Concat(Profiles("records", "f", 0, 0));

  /// <summary>
  /// Test design 1, 2 and 4: a block-local <c>let</c> or a loop variable followed by a <c>let</c> of
  /// the same name in the enclosing block compiles in every profile and computes what <c>node</c>
  /// computes for the source.
  /// </summary>
  [TestCaseSource(nameof(Scripts))]
  public void ScriptWithALaterOuterLet_CompilesInEveryProfile(string fixture, string function, double argument, double result, string profile) {
    var source = Path.Combine(_scripts, fixture + ".js");
    var text = this.Translate(source, "javascript", profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile, CSharpProfile.Parse(profile, null).LanguageVersionName), Is.Empty, text);

    var call = new PrototypeCall(function, [argument]);
    var module = JavaScriptFixture.Import(source, File.ReadAllText(source));
    var expected = PrototypeFixture.Show(result);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(expected));
    Assert.That(Compiled(text, call), Is.EqualTo(expected));
  }

  /// <summary>The outer local of a later declaration is the one renamed; the block-local one keeps its name.</summary>
  [Test]
  public void LaterOuterLet_IsTheOneRenamed() {
    var text = this.Translate(Path.Combine(_scripts, "later.js"), "javascript", "net10.0");
    Assert.That(EmittedCSharp.Body(text, "f"), Is.EqualTo(new[] {
      "double s = 0.0;",
      "if (n > 1.0) {",
      "var t = ToInt32(n);",
      "s = s + (double)t;",
      "}",
      "double t_1 = 0.5;",
      "return s + t_1;"
    }));
  }

  /// <summary>A record-typed local is renamed like any other, and its fields are read through the new name.</summary>
  [Test]
  public void LaterOuterRecord_IsTheOneRenamed() {
    var text = this.Translate(Path.Combine(_scripts, "records.js"), "javascript", "net2.0");
    Assert.That(text, Does.Contain("      P p = new P(ToInt32(n), 3);\n").And.Contain("    P2 p_1 = new P2(0.5, n);\n").And.Contain("return s + (p_1.x * p_1.y);"));
  }

  /// <summary>The loop variable keeps its name and the later local of the enclosing block is renamed.</summary>
  [Test]
  public void LaterLetBehindALoop_IsTheOneRenamed() {
    var text = this.Translate(Path.Combine(_scripts, "loop.js"), "javascript", "net2.0");
    Assert.That(text, Does.Contain("      double i = 0.0;\n").And.Contain("    int i_1 = 10;\n").And.Contain(" : (double)i_1;"));
  }

  /// <summary>
  /// Test design 3: <c>t: f64</c> declared first, and inside <c>if (n &gt; 0)</c> a <c>t: i32</c> -
  /// another local to the IR, which the verifier accepts. The inner one is renamed; a use of either
  /// reads the right one.
  /// </summary>
  [TestCase(3, 7.0)]
  [TestCase(-1, 1.0)]
  public void NestedLocalOfAnotherType_IsRenamed(int argument, double result) {
    var outer = new IrLocal("t", IrType.F64);
    var inner = new IrLocal("t", IrType.I32);
    var n = new IrLocal("n", IrType.I32);
    var module = Module(IrType.F64, [n], [outer, inner],
      new IrDeclare(outer, Double(0.5)),
      new IrIf(Op(IrBinaryOp.Greater, Ref(n), Int(0)), Block(
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, Ref(n), Int(2))),
        new IrReturn(Op(IrBinaryOp.Add, new IrConvert(IrType.F64, Ref(inner)), Op(IrBinaryOp.Multiply, Ref(outer), Double(2))))), null),
      new IrReturn(Op(IrBinaryOp.Multiply, Ref(outer), Double(2))));

    var text = Emitted(module, CSharpProfile.Parse("net10.0", null));
    Assert.That(EmittedCSharp.Body(text), Is.EqualTo(new[] {
      "double t = 0.5;",
      "if (n > 0) {",
      "var t_1 = n * 2;",
      "return (double)t_1 + (t * 2.0);",
      "}",
      "return t * 2.0;"
    }));

    var call = new PrototypeCall("F", [argument]);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(PrototypeFixture.Show(result)));
    Assert.That(Compiled(text, call), Is.EqualTo(PrototypeFixture.Show(result)));
  }

  /// <summary>Test design 5: a nested local named like a parameter is renamed; the parameter keeps its name.</summary>
  [TestCase(2, 7)]
  [TestCase(-1, -1)]
  public void NestedLocalNamedLikeAParameter_IsRenamed(int argument, int result) {
    var n = new IrLocal("n", IrType.I32);
    var inner = new IrLocal("n", IrType.I64);
    var module = Module(IrType.I32, [n], [inner],
      new IrIf(Op(IrBinaryOp.Greater, Ref(n), Int(0)), Block(
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, new IrConvert(IrType.I64, Ref(n)), new IrConst(IrType.I64, 3))),
        new IrReturn(new IrConvert(IrType.I32, Op(IrBinaryOp.Add, Ref(inner), new IrConst(IrType.I64, 1))))), null),
      new IrReturn(Ref(n)));

    var text = Emitted(module, CSharpProfile.Parse("net10.0", null));
    Assert.That(text, Does.Contain("public static int F(int n) {").And.Contain("var n_1 = (long)n * 3L;").And.Contain("return (int)(n_1 + 1L);"));

    var call = new PrototypeCall("F", [argument]);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(result.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    Assert.That(Compiled(text, call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)));
  }

  /// <summary>Test design 6: two sibling blocks of a C# source keep the name they share; the text is the one written before renaming existed.</summary>
  [Test]
  public void SiblingBlocksOfACSharpSource_KeepTheirNames() {
    var text = this.Translate(PrototypeFixture.PathOf("scopes/Sibling"), "csharp", "net10.0");
    Assert.That(text, Is.EqualTo(string.Join('\n',
      "// Sibling: generated by polypiler. Change the source, not this file.",
      "// Profile: net10.0, C# 14.",
      "",
      "public static class Sibling {",
      "",
      "  public static int F(int n) {",
      "    unchecked {",
      "      int s = 0;",
      "      if (n > 0) {",
      "        var t = n * 2;",
      "        s = s + t;",
      "      } else {",
      "        var t = (double)n * 0.5;",
      "        s = s + ((t < -1.0) ? 1 : 0);",
      "      }",
      "      return s;",
      "    }",
      "  }",
      "",
      "}",
      "")));

    foreach (var argument in new[] { 3, -1, -3 }) {
      var call = new PrototypeCall("F", [argument]);
      Assert.That(Compiled(text, call), Is.EqualTo(PrototypeFixture.CompiledOutcome("scopes/Sibling", call)));
    }
  }

  /// <summary>Test design 6: two sibling blocks of a JavaScript source keep the name they share; the text is the one written before renaming existed.</summary>
  [Test]
  public void SiblingBlocksOfAScript_KeepTheirNames() {
    var text = this.Translate(Path.Combine(_scripts, "sib.js"), "javascript", "net10.0");
    Assert.That(text, Is.EqualTo(string.Join('\n',
      "// sib: generated by polypiler. Change the source, not this file.",
      "// Profile: net10.0, C# 14.",
      "",
      "public static class @sib {",
      "",
      "  public static double h(double n) {",
      "    double s = 0.0;",
      "    if (n > 0.0) {",
      "      var t = ToInt32(n);",
      "      s = s + (double)t;",
      "    } else {",
      "      var t = n * 0.5;",
      "      s = s + t;",
      "    }",
      "    return s;",
      "  }",
      "",
      "  private static int ToInt32(double value) {",
      "    return double.IsNaN(value) || double.IsInfinity(value) ? 0 : unchecked((int)(long)(global::System.Math.Truncate(value) % 4294967296.0));",
      "  }",
      "",
      "}",
      "")));

    Assert.That(Compiled(text, new PrototypeCall("h", [3.0])), Is.EqualTo("3"));
    Assert.That(Compiled(text, new PrototypeCall("h", [-3.0])), Is.EqualTo(PrototypeFixture.Show(-1.5)));
  }

  /// <summary>Test design 7: the first candidate is the name of another local, so the nested one takes the next.</summary>
  [TestCase(2, 6)]
  [TestCase(-2, -3)]
  public void TakenCandidate_IsPassedOver(int argument, int result) {
    var t = new IrLocal("t", IrType.I32);
    var t1 = new IrLocal("t_1", IrType.I32);
    var nested = new IrLocal("t", IrType.I64);
    var n = new IrLocal("n", IrType.I32);
    var module = Module(IrType.I32, [n], [t, t1, nested],
      new IrDeclare(t, Ref(n)),
      new IrDeclare(t1, Op(IrBinaryOp.Add, Ref(n), Int(1))),
      new IrIf(Op(IrBinaryOp.Greater, Ref(n), Int(0)), Block(
        new IrDeclare(nested, Op(IrBinaryOp.Multiply, new IrConvert(IrType.I64, Ref(t)), new IrConvert(IrType.I64, Ref(t1)))),
        new IrReturn(new IrConvert(IrType.I32, Ref(nested)))), null),
      new IrReturn(Op(IrBinaryOp.Add, Ref(t), Ref(t1))));

    var text = Emitted(module, CSharpProfile.Parse("net10.0", null));
    Assert.That(EmittedCSharp.Body(text), Is.EqualTo(new[] {
      "var t = n;",
      "var t_1 = n + 1;",
      "if (n > 0) {",
      "var t_2 = (long)t * (long)t_1;",
      "return (int)t_2;",
      "}",
      "return t + t_1;"
    }));
    Assert.That(Compiled(text, new PrototypeCall("F", [argument])), Is.EqualTo(result.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("F", [argument])), Is.EqualTo(result.ToString(System.Globalization.CultureInfo.InvariantCulture)));
  }

  /// <summary>Test design 8: one name at three depths of blocks becomes three names, in every profile.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void OneNameAtThreeDepths_BecomesThreeNames(CSharpProfile profile) {
    var outer = new IrLocal("t", IrType.I32);
    var middle = new IrLocal("t", IrType.I64);
    var inner = new IrLocal("t", IrType.F64);
    var n = new IrLocal("n", IrType.I32);
    var module = Module(IrType.F64, [n], [outer, middle, inner],
      new IrDeclare(outer, Op(IrBinaryOp.Add, Ref(n), Int(1))),
      Block(
        new IrDeclare(middle, Op(IrBinaryOp.Multiply, new IrConvert(IrType.I64, Ref(outer)), new IrConst(IrType.I64, 2))),
        Block(
          new IrDeclare(inner, Op(IrBinaryOp.Add, new IrConvert(IrType.F64, Ref(middle)), new IrConvert(IrType.F64, Ref(outer)))),
          new IrReturn(Ref(inner)))));

    var text = Emitted(module, profile);
    var declarations = EmittedCSharp.Body(text).Where(line => line.Contains(" = ", StringComparison.Ordinal) && !line.StartsWith("return", StringComparison.Ordinal))
      .Select(line => line.Split(' ')[1]).ToList();
    Assert.That(declarations, Is.EqualTo(new[] { "t", "t_1", "t_2" }));

    var call = new PrototypeCall("F", [4]);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(PrototypeFixture.Show(15.0)));
    Assert.That(Compiled(text, call), Is.EqualTo(PrototypeFixture.Show(15.0)));
  }

  /// <summary>Test design 9: a keyword keeps its <c>@</c> where it stays, and the renamed local, no keyword, needs none.</summary>
  [Test]
  public void KeywordName_IsRenamedWithoutTheAt() {
    var outer = new IrLocal("object", IrType.I32);
    var inner = new IrLocal("object", IrType.I64);
    var n = new IrLocal("n", IrType.I32);
    var module = Module(IrType.I64, [n], [outer, inner],
      new IrDeclare(outer, Op(IrBinaryOp.Add, Ref(n), Int(1))),
      Block(
        new IrDeclare(inner, Op(IrBinaryOp.Multiply, new IrConvert(IrType.I64, Ref(outer)), new IrConst(IrType.I64, 5))),
        new IrReturn(Ref(inner))));

    var text = Emitted(module, CSharpProfile.Parse("net10.0", null));
    Assert.That(EmittedCSharp.Body(text), Is.EqualTo(new[] { "var @object = n + 1;", "{", "var object_1 = (long)@object * 5L;", "return object_1;", "}" }));
    Assert.That(Compiled(text, new PrototypeCall("F", [1])), Is.EqualTo("10"));
  }

  /// <summary>Two locals of one name and two types in one block: the second is renamed, as C# also forbids that (CS0128).</summary>
  [Test]
  public void TwoLocalsOfOneNameInOneBlock_AreTwoNames() {
    var first = new IrLocal("x", IrType.I32);
    var second = new IrLocal("x", IrType.F64);
    var n = new IrLocal("n", IrType.I32);
    var module = Module(IrType.F64, [n], [first, second],
      new IrDeclare(first, Op(IrBinaryOp.Add, Ref(n), Int(1))),
      new IrDeclare(second, new IrConvert(IrType.F64, Ref(first))),
      new IrReturn(Op(IrBinaryOp.Add, Ref(second), new IrConvert(IrType.F64, Ref(first)))));

    var text = Emitted(module, CSharpProfile.Parse("net2.0", null));
    Assert.That(EmittedCSharp.Body(text), Is.EqualTo(new[] { "int x = n + 1;", "double x_1 = (double)x;", "return x_1 + (double)x;" }));
    Assert.That(Compiled(text, new PrototypeCall("F", [2])), Is.EqualTo("6"));
  }

}
