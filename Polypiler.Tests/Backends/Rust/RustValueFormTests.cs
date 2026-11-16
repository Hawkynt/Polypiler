using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Passes;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The forms of #460 and #462 the C# frontend does not import as such - steps of fields and elements
/// as expressions of their own, nested fields, a choice between a new array and another, string
/// constants and the first replacement - written as IR, and the forms the target passes and the tidy
/// leave behind.
/// </summary>
[TestFixture]
public sealed class RustValueFormTests {

  private static readonly IrField _x = new("X", IrType.I32, false);
  private static readonly IrField _y = new("Y", IrType.F64, false);
  private static readonly IrStructType _point = new("Point", [_x, _y]);
  private static readonly IrType _pointType = IrType.Struct(_point);
  private static readonly IrType _ints = IrType.Array(IrType.I32);

  private static IrModule Module(params IrFunction[] functions) => new("Snippet", functions) { Types = [_point] };

  private static IrFunction Function(string name, IrType result, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] body)
    => new(name, true, result, parameters, locals, new IrBlock(body));

  private static string Emitted(IrModule module) => RustCrates.ModuleText(RustCrates.Emit(module, level: OptimizationLevel.None));

  private static string EmittedSource(string methods) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None));

  [Test]
  public void StepsOfAnElement_AsStatements_EvaluateTheIndexOnce() {
    var a = new IrLocal("a", _ints);
    var f = new IrLocal("f", IrType.Array(IrType.F32));
    var i = new IrLocal("i", IrType.I32);
    var function = Function("F", IrType.Void, [a, f, i], [],
      new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(a), new IrLocalRef(i)), true, false, false)),
      new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(f), new IrConst(IrType.I32, 0)), false, true, false)),
      new IrExprStmt(new IrStep(new IrArrayLoad(new IrLocalRef(a), new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1))), true, true, true)));
    var text = Emitted(Module(function));
    Assert.That(text, Does.Contain("pub fn f(a: &mut [i32], f: &mut [f32], i: i32) {"));
    Assert.That(text, Does.Contain("a[i as usize] = a[i as usize].wrapping_add(1);"));
    Assert.That(text, Does.Contain("f[0] -= 1.0;"));
    Assert.That(text, Does.Contain("let index = i.wrapping_add(1) as usize;\n    a[index] = a[index].checked_add(1).expect(\"IntegerOverflow\");"));
  }

  private static readonly IrField _v = new("V", IrType.F64, false);
  private static readonly IrStructType _in = new("In", [_v]);
  private static readonly IrField _i = new("I", IrType.Struct(_in), false);
  private static readonly IrField _n = new("N", IrType.I32, false);
  private static readonly IrStructType _out = new("Out", [_i, _n]);

  /// <summary>A struct in a struct: its field written in place, the two compared.</summary>
  private static IrModule Nested() {
    var o = new IrLocal("o", IrType.Struct(_out));
    var d = new IrLocal("d", IrType.F64);
    IrExpr Inner() => new IrFieldLoad(new IrLocalRef(o), _i);
    var function = Function("F", IrType.Bool, [o, d], [],
      new IrFieldStore(Inner(), _v, new IrBinary(IrBinaryOp.Add, new IrFieldLoad(Inner(), _v), new IrLocalRef(d))),
      new IrReturn(new IrStructEquals(new IrLocalRef(o), new IrLocalRef(o))));
    return new IrModule("Snippet", [function]) { Types = [_in, _out] };
  }

  [Test]
  public void NestedFields_AreReadAndWrittenInPlace_AndCompared() {
    var text = Emitted(Nested());
    Assert.That(text, Does.Contain("o.i.v += d;"));
    Assert.That(text, Does.Contain("impl PartialEq for In {\n    fn eq(&self, other: &Self) -> bool {\n        self.v == other.v || (self.v.is_nan() && other.v.is_nan())\n    }\n}"));
    Assert.That(text, Does.Contain("self.i == other.i && self.n == other.n"));
  }

  [Test]
  public void ChoiceOfANewArrayAndAnother_OwnsTheVectorItBorrows() {
    var text = EmittedSource("public static int F(int[] a, bool c) => Sum(c ? a : new int[2]) + new int[3].Length; private static int Sum(int[] s) => s[0];");
    Assert.That(text, Does.Contain("sum(&(if c { a.to_vec() } else { vec![0; 2] }))"));
    Assert.That(text, Does.Contain("vec![0; 3].len() as i32"));
    var problems = RustCrates.Check(RustCrates.Import("public static void F(int[] a, bool c) => Set(c ? a : new int[2]); private static void Set(int[] s) { s[0] = 1; }"));
    Assert.That(problems.Select(problem => problem.Message), Has.Some.Contain("an exclusive borrow of a choice between a new array and another one"));
  }

  [Test]
  public void ArrayFromACallThatBorrows_IsCopiedWhereANewArrayMayStandInstead()
    => Assert.That(EmittedSource("public static int[] F(int[] a, bool c) => c ? Same(a) : new int[1]; private static int[] Same(int[] s) => s;"),
      Does.Contain("if c { same(a).to_vec() } else { vec![0; 1] }"));

  [Test]
  public void StringConstant_IsItsCodeUnits_AndASurrogatePairStaysInTheLiteral() {
    var digits = IrModuleConst.OfStrings("Digits", IrType.String, ["01"]);
    var i = new IrLocal("i", IrType.I32);
    var read = new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [new IrConstRef(digits), new IrLocalRef(i)]);
    var text = Emitted(new IrModule("Snippet", [Function("F", IrType.U16, [i], [], new IrReturn(read)), Function("G", IrType.String, [], [], new IrReturn(new IrStrConst("\uD83D\uDE00")))], [digits]));
    Assert.That(text, Does.Contain("static DIGITS: &str = \"01\";"));
    Assert.That(text, Does.Contain("ply_utf16(DIGITS)[i as usize]"));
    Assert.That(text, Does.Contain("ply_utf16(\"\U0001F600\")"));
  }

  [Test]
  public void FirstReplacement_IsItsHelper() {
    var s = new IrLocal("s", IrType.String);
    var replaced = new IrIntrinsic(IrIntrinsicOp.StrReplaceFirst, IrType.String, [new IrLocalRef(s), new IrStrConst("a"), new IrStrConst("b")]);
    var text = Emitted(Module(Function("F", IrType.String, [s], [], new IrReturn(replaced))));
    Assert.That(text, Does.Contain("ply_replace_first(s, &ply_utf16(\"a\"), &ply_utf16(\"b\"))"));
    Assert.That(text, Does.Contain("fn ply_replace_first(text: &[u16], needle: &[u16], replacement: &[u16]) -> Vec<u16> {"));
  }

  [Test]
  public void ReturnInABranchOfALateInitializedBinding_IsItsValue() {
    Assert.That(EmittedSource("public static int F(bool c, int x) { int r; if (c) return 1; else r = x; return r; }"), Does.Contain("if c { 1 } else { x }"));
    Assert.That(EmittedSource("public static int F(bool c, int x) { int r; if (c) r = x; else return 1; return r; }"), Does.Contain("if c { x } else { 1 }"));
  }

  [Test]
  public void InlinedLiteralForACastParameter_IsTheLiteralWhereItFits() {
    var helper = new RsFunction("h", "", [new RsParameter("e", "i64")], "u64", new RsBlock([], new RsCast(new RsPath("e"), "u64"))) { IsHelper = true };
    var caller = new RsFunction("f", "pub", [], "u64", new RsBlock([], new RsBinary(new RsBinary(new RsCall(new RsPath("h"), [new RsLiteral("0")]), "+", new RsCall(new RsPath("h"), [new RsLiteral("-1")])), "+",
      new RsCall(new RsPath("g"), [new RsLiteral("7")]))));
    var other = new RsFunction("g", "", [new RsParameter("e", "i64")], "f32", new RsBlock([], new RsCast(new RsPath("e"), "f32"))) { IsHelper = true };
    var crate = new RustCrate("p", "", new RsFile([], [], []), "m", new RsFile([], [], [helper, other, caller]));
    var inlined = new RustHelperInlining().Run(crate, RustProfile.Default, _ => { });
    var text = RustPrinter.Print(inlined.Module);
    Assert.That(text, Does.Contain("0 + (-1 as u64) + (7 as f32)").Or.Contain("0 + ((-1) as u64)"));
  }

}
