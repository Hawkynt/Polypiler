using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Optional;

/// <summary>
/// Optional values written by the JavaScript backend in every profile: nothing inexpressible, the
/// text parsed at the level it claims, and the forms of the lowering table on both sides of each
/// first level. What the code computes is decided by node in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptOptionalEmissionTests {

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "G" : null, typed)
  ];

  private static string Emit(JavaScriptProfile profile) {
    var module = OptionalFixtures.Imported();
    Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty);
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  private static string Emit(string level) => Emit(JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm"));

  /// <summary>Test design 12: every node of optional values in every profile and module format; nothing is inexpressible.</summary>
  [Test]
  public void EveryOptionalNode_IsWrittenInEveryProfile() {
    foreach (var profile in _profiles)
      Assert.That(Emit(profile), Does.Contain("function Find(a, v)"), profile.ToString());
  }

  /// <summary>Test design 7: ?? and ?. from ECMAScript 2020, ??= from 2021 (the level es2022); a conditional on == null below, never ||.</summary>
  [TestCase("es2017", "??", false)]
  [TestCase("es2017", "?.", false)]
  [TestCase("es2017", "z != null ? z : 5", true)]
  [TestCase("es2017", "($c = Find(a, v)) != null ? $c : (-1)", true)]
  [TestCase("es2020", "return z ?? 5;", true)]
  [TestCase("es2020", "a?.length ?? (-1)", true)]
  [TestCase("es2020", "??=", false)]
  [TestCase("es2022", "q ??= 7;", true)]
  [TestCase("es5", "(a == null) ? false : (a < 1)", true)]
  [TestCase("es5", "(a == null) ? null : ((a + 1) | 0)", true)]
  [TestCase("es5", "function $unwrap(value)", true)]
  [TestCase("es5", "return z != null ? z : 5;", true)]
  public void Form_IsTheOneOfTheLevel(string level, string form, bool present) {
    var text = Emit(level);
    var code = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));
    Assert.That(code, present ? Does.Contain(form) : Does.Not.Contain(form), text);
  }

  /// <summary>An exported result spelled undefined in the source is turned from null into undefined at the boundary, none written as void 0.</summary>
  [Test]
  public void UndefinedResult_IsWrittenAtTheBoundary() {
    var x = new IrLocal("x", IrType.Optional(IrType.F64));
    var type = IrType.Optional(IrType.F64);
    var g = new IrFunction("g", true, type, [x], [], new IrBlock([
      new IrIf(new IrIsNone(new IrLocalRef(x)), new IrBlock([new IrReturn(new IrNone(type))]), null),
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrUnwrap(new IrLocalRef(x)), new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 0))), new IrBlock([new IrReturn(new IrSome(new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1))))]), null),
      new IrReturn(new IrLocalRef(x))
    ])) { Absent = IrAbsent.Undefined };
    var text = LegalizedJavaScript.Emit(new IrModule("M", [g]), JavaScriptProfile.Parse("es2022", "esm")).Single().Content;
    Assert.That(text, Does.Contain("return void 0;").And.Contain("return 1;").And.Contain("return $undef(x);").And.Contain("function $undef(value)"));
    var internalOnly = LegalizedJavaScript.Emit(new IrModule("M", [g with { Absent = IrAbsent.Null }]), JavaScriptProfile.Parse("es2022", "esm")).Single().Content;
    Assert.That(internalOnly, Does.Not.Contain("$undef").And.Contain("return null;"));
  }

  [TestCase("a?.length")]
  [TestCase("a?.length ?? -1")]
  [TestCase("(a?.b).c")]
  [TestCase("(a?.b)[0]")]
  [TestCase("(a?.b)()")]
  [TestCase("(a || b) ?? c")]
  [TestCase("a ?? (b && c)")]
  [TestCase("(a ?? b) || c")]
  [TestCase("x ??= 1")]
  public void Syntax_ReadsAndPrintsBack(string source) {
    var statement = (JsExpressionStatement)JavaScriptSyntaxReader.Statements(source + ";").Single();
    Assert.That(JavaScriptPrinter.Print(Unwrapped(statement.Expression)), Is.EqualTo(source));
  }

  /// <summary>The tree without the parentheses the source wrote, so that the printer has to put them back.</summary>
  private static JsExpression Unwrapped(JsExpression expression) => expression switch {
    JsParenthesized parenthesized => Unwrapped(parenthesized.Inner),
    JsMember member => member with { Target = Unwrapped(member.Target) },
    JsIndex index => index with { Target = Unwrapped(index.Target) },
    JsCall call => call with { Callee = Unwrapped(call.Callee) },
    JsBinary binary => binary with { Left = Unwrapped(binary.Left), Right = Unwrapped(binary.Right) },
    _ => expression
  };

}
