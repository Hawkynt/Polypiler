using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Python.Passes;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Python.PythonIr;

namespace Polypiler.Tests.Backends.Python;

/// <summary>
/// The boundaries of the rules the other tests of the backend state by their main cases: what a
/// counted loop's counter may meet after the loop, which bounds stay the same, which values Python may
/// compute out of the IR's order, how far a switch's <c>break</c> is taken out, and the corners of the
/// reader, the rewriter and the O0501 pass.
/// </summary>
[TestFixture]
public sealed class PythonBoundaryTests {

  private static readonly IrLocal _n = L("n", IrType.I32), _s = L("s", IrType.I32), _i = L("i", IrType.I32), _x = L("x", IrType.I32);

  private static IrStmt Assign(IrLocal local, IrExpr value) => new IrAssign(local, value);

  private static IrStmt Increment => new IrExprStmt(new IrStep(R(_i), true, false, false));

  private static string Text(params IrStmt[] body) => Emit(new IrModule("M", [Function("f", IrType.I32, [_n], body)]));

  private static string Loop(IrExpr bound, IrStmt[] body, params IrStmt[] after)
    => Text([new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Less, R(_i), bound), Block(body), Block(Increment)), .. after, new IrReturn(R(_s))]);

  [Test]
  public void TheCounterAfterTheLoop_MayBeWrittenFirst_ButNotReadFirst() {
    IrStmt[] fold = [Assign(_s, R(_i))];
    Assert.That(Loop(R(_n), fold, Assign(_i, I(3))), Does.Contain("for i in range(n):"));
    Assert.That(Loop(R(_n), fold, Assign(_i, Bin(IrBinaryOp.Add, R(_i), I(1)))), Does.Contain("while i < n:"));
    // A loop in a block of its own, as a for of C# is, followed by a block that declares the counter again.
    var first = Block(new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(fold), Block(Increment)));
    foreach (var again in new IrStmt[] { new IrDeclare(_i, I(4)), new IrDeclare(_i, null) })
      Assert.That(Text(new IrDeclare(_s, I(0)), first, Block(again, Assign(_s, R(_i))), new IrReturn(R(_s))), Does.Contain("for i in range(n):"));

    Assert.That(Loop(R(_i), fold), Does.Contain("while i < i:"));
    var data = L("data", IrType.Array(IrType.I32));
    var element = new IrModule("M", [Function("f", IrType.I32, [data, _n], new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)),
      new IrFor(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(new IrExprStmt(new IrStep(new IrArrayLoad(R(data), R(_i)), true, false, false))), Block(Increment)), new IrReturn(R(_s)))]);
    Assert.That(Emit(element), Does.Contain("for i in range(n):\n        data[i] = "));
    foreach (var reader in new IrStmt[] {
      new IrWhile(Bin(IrBinaryOp.Less, R(_s), R(_i)), Block(Assign(_s, I(9)))),
      new IrWhile(Bin(IrBinaryOp.Less, R(_s), I(1)), Block(Assign(_s, R(_i)))),
      new IrDoWhile(Block(Assign(_s, I(1))), Bin(IrBinaryOp.Less, R(_s), R(_i))),
      new IrDoWhile(Block(Assign(_s, R(_i))), C(IrType.Bool, 0)),
      new IrFor(Bin(IrBinaryOp.Less, R(_s), I(3)), Block(), Block(Assign(_s, R(_i)))),
      new IrFor(Bin(IrBinaryOp.Less, R(_s), I(3)), Block(Assign(_s, R(_i))), Block(Assign(_s, I(3)))),
      new IrFor(Bin(IrBinaryOp.Less, R(_s), R(_i)), Block(), Block(Assign(_s, I(3)))),
      new IrSwitch(R(_i), [new IrSwitchSection([1], false, Block())]),
      new IrSwitch(R(_s), [new IrSwitchSection([1], false, Block(Assign(_s, R(_i))))]),
      new IrIf(Bin(IrBinaryOp.Less, R(_s), I(1)), Block(), Block(Assign(_s, R(_i)))),
      new IrIf(Bin(IrBinaryOp.Less, R(_s), I(1)), Block(Assign(_s, R(_i))), null),
      new IrIf(Bin(IrBinaryOp.Less, R(_s), R(_i)), Block(), null),
      Block(Assign(_s, R(_i))),
      new IrExprStmt(R(_i))
    })
      Assert.That(Loop(R(_n), fold, reader), Does.Contain("while i < n:"), reader.ToString());

    Assert.That(Loop(R(_n), fold, new IrIf(Bin(IrBinaryOp.Less, R(_s), I(1)), Block(), Block(Assign(_s, I(2))))), Does.Contain("for i in range(n):"));
  }

  [Test]
  public void WritesInsideNestedLoopsOfTheBody_AreSeen() {
    foreach (var nested in new IrStmt[] {
      new IrWhile(Bin(IrBinaryOp.Less, R(_s), I(3)), Block(Assign(_n, I(1)))),
      new IrDoWhile(Block(Assign(_n, I(1))), C(IrType.Bool, 0)),
      new IrFor(C(IrType.Bool, 0), Block(), Block(Assign(_n, I(1)))),
      new IrSwitch(R(_s), [new IrSwitchSection([1], false, Block(Assign(_n, I(1))))]),
      new IrIf(C(IrType.Bool, 1), Block(), Block(new IrExprStmt(new IrStep(R(_n), true, false, false)))),
      new IrExprStmt(new IrStep(R(_n), true, false, false))
    })
      Assert.That(Loop(R(_n), [nested]), Does.Contain("while i < n:"), nested.ToString());

    Assert.That(Loop(R(_n), [new IrWhile(Bin(IrBinaryOp.Less, R(_s), I(3)), Block(Assign(_s, I(3))))]), Does.Contain("for i in range(n):"));
  }

  [Test]
  public void ABound_ThatDoesNotChange_IsAConstantAVariableOrALength() {
    var limit = new IrModuleConst("limit", IrType.I32, [5]);
    var table = new IrModuleConst("table", IrType.Array(IrType.I32), [1, 2]);
    var data = L("data", IrType.Array(IrType.I32));
    IrModule Module(IrExpr bound, params IrStmt[] body)
      => new("M", [Function("f", IrType.I32, [data], [new IrDeclare(_s, I(0)), new IrDeclare(_i, I(0)), new IrFor(Bin(IrBinaryOp.Less, R(_i), bound), Block(body), Block(Increment)), new IrReturn(R(_s))])], [limit, table]);
    Assert.That(Emit(Module(new IrConstRef(limit))), Does.Contain("for i in range(_LIMIT):"));
    Assert.That(Emit(Module(new IrArrayLength(new IrConstRef(table)))), Does.Contain("for i in range(len(_TABLE)):"));
    Assert.That(Emit(Module(new IrArrayLength(R(data)), Assign(data, new IrNewArray(IrType.Array(IrType.I32), I(1))))), Does.Contain("while i < len(data):"));
    Assert.That(Emit(Module(new IrCall("f", IrType.I32, [R(data)]))), Does.Contain("while i < f(data):"));
  }

  [Test]
  public void ACounterThatCountsDownToANonNegativeBound_IndexesWithoutACheck() {
    var data = L("data", IrType.Array(IrType.I32));
    IrModule Module(IrBinaryOp compare, IrExpr bound) => new("M", [Function("f", IrType.I32, [data, _n], [new IrDeclare(_s, I(0)), new IrDeclare(_i, R(_n)),
      new IrFor(Bin(compare, R(_i), bound), Block(Assign(_s, new IrArrayLoad(R(data), R(_i)))), Block(new IrExprStmt(new IrStep(R(_i), false, false, false)))), new IrReturn(R(_s))])]);
    Assert.That(Emit(Module(IrBinaryOp.GreaterOrEqual, I(0))), Does.Contain("for i in range(n, -1, -1):\n        s = data[i]\n"));
    Assert.That(Emit(Module(IrBinaryOp.Greater, I(-1))), Does.Contain("for i in range(n, -1, -1):\n        s = data[i]\n"));
    Assert.That(Emit(Module(IrBinaryOp.Greater, I(-2))), Does.Contain("s = data[_ply_index(i)]"));
    Assert.That(Emit(Module(IrBinaryOp.GreaterOrEqual, R(_s))), Does.Contain("while i >= s:"));
    var free = new IrModule("M", [Function("f", IrType.I32, [data, _n, _x], [new IrDeclare(_s, I(0)), new IrDeclare(_i, R(_n)),
      new IrFor(Bin(IrBinaryOp.GreaterOrEqual, R(_i), R(_x)), Block(Assign(_s, new IrArrayLoad(R(data), R(_i)))), Block(new IrExprStmt(new IrStep(R(_i), false, false, false)))), new IrReturn(R(_s))])]);
    Assert.That(Emit(free), Does.Contain("for i in range(n, x - 1, -1):\n        s = data[_ply_index(i)]\n"));
  }

  [Test]
  public void TakingABreakOutOfANestedBranch_CanCopyTooMuch() {
    var section = Block(
      new IrIf(Bin(IrBinaryOp.Equal, R(_x), I(1)), Block(new IrIf(Bin(IrBinaryOp.Equal, R(_s), I(2)), Block(new IrIf(Bin(IrBinaryOp.Equal, R(_s), I(3)), Block(new IrBreak()), null)), null),
        Assign(_s, I(1)), Assign(_s, I(2)), Assign(_s, I(3)), Assign(_s, I(4))), null),
      new IrBreak());
    var text = Emit(new IrModule("M", [Function("f", IrType.I32, [_x], new IrDeclare(_s, I(0)), new IrSwitch(R(_x), [new IrSwitchSection([1], false, section)]), new IrReturn(R(_s)))]));
    Assert.That(text, Does.Contain("    while True:\n        match x:\n"));
    var ending = Block(new IrIf(Bin(IrBinaryOp.Equal, R(_x), I(1)), Block(new IrIf(C(IrType.Bool, 1), Block(new IrBreak()), Block(new IrReturn(I(1))))),
      Block(Assign(_s, I(1)))), Assign(_s, I(1)), Assign(_s, I(2)), Assign(_s, I(3)), Assign(_s, I(4)), new IrBreak());
    var copied = Emit(new IrModule("M", [Function("f", IrType.I32, [_x], new IrDeclare(_s, I(0)), new IrSwitch(R(_x), [new IrSwitchSection([1], false, ending)]), new IrReturn(R(_s)))]));
    Assert.That(copied, Does.Not.Contain("while True"));
  }

  [Test]
  public void AValueThatCannotTrapOrAct_IsComputedWherePythonPutsIt() {
    var data = L("data", IrType.Array(IrType.F64));
    var d = L("d", IrType.F64);
    var g = new IrGlobal("g", IrType.I32, null);
    IrExpr Place() => new IrConditional(Bin(IrBinaryOp.Less, R(_x), I(0)), R(data), R(data));
    var quiet = new IrExpr[] {
      Bin(IrBinaryOp.Divide, R(d), R(d)), new IrUnary(IrUnaryOp.Negate, R(d)), new IrConvert(IrType.F64, new IrGlobalRef(g)),
      new IrConditional(C(IrType.Bool, 1), R(d), new IrConvert(IrType.F64, new IrConvert(IrType.I64, R(_x))))
    };
    var loud = new IrExpr[] {
      new IrConvert(IrType.F64, new IrUnary(IrUnaryOp.NegateChecked, R(_x))), new IrConvert(IrType.F64, Bin(IrBinaryOp.Remainder, R(_x), R(_x))),
      new IrConvert(IrType.F64, new IrConvert(IrType.I32, R(d), IrConversion.Saturate)), new IrConvert(IrType.F64, new IrConvert(IrType.U8, R(_x), IrConversion.Trap)),
      new IrConvert(IrType.F64, Bin(IrBinaryOp.AddChecked, R(_x), R(_x))), new IrConditional(C(IrType.Bool, 1), R(d), new IrArrayLoad(R(data), I(0))),
      new IrConditional(C(IrType.Bool, 1), new IrArrayLoad(R(data), I(0)), R(d)), new IrConditional(Bin(IrBinaryOp.Less, new IrArrayLoad(R(data), I(0)), R(d)), R(d), R(d)),
      new IrUnary(IrUnaryOp.Negate, new IrArrayLoad(R(data), I(0))), Bin(IrBinaryOp.Add, new IrArrayLoad(R(data), I(0)), R(d)), Bin(IrBinaryOp.Add, R(d), new IrArrayLoad(R(data), I(0)))
    };
    foreach (var (value, hoisted) in quiet.Select(value => (value, false)).Concat(loud.Select(value => (value, true)))) {
      var module = new IrModule("M", [Function("f", IrType.Void, [data, _x, d], new IrStore(Place(), I(0), value), new IrReturn(null))]) { Globals = [g] };
      Assert.That(Emit(module), hoisted ? Does.Contain("_t1 = data if x < 0 else data") : Does.Not.Contain("_t1"), value.ToString());
    }
  }

  [Test]
  public void ConstantsAreWrappedAtTheirWidth() {
    var text = Emit(Returning(IrType.I8, [], new IrConvert(IrType.I8, I(200))));
    Assert.That(text, Does.Contain("return -56\n"));
  }

  [Test]
  public void AThrowOfAnythingButANewLibraryException_IsRefused() {
    var thrown = L("e", IrExceptionClass.Reference);
    var module = new IrModule("M", [Function("f", IrType.Void, [thrown], new IrThrow(R(thrown)))]);
    Assert.That(Problems(module), Has.Some.Contains("'Throw' cannot be expressed"));
  }

  [Test]
  public void NegatingANegativeLiteral_IsAPositiveOne() => Assert.That(Emit(Returning(IrType.I32, [], new IrUnary(IrUnaryOp.Negate, I(-5)))), Does.Contain("return 5\n"));

  [Test]
  public void Usable_OfAnEmptyName_IsEmpty() => Assert.That(PythonNaming.Usable(""), Is.Empty);

  [Test]
  public void Reader_SkipsATrailingComment_ReadsDottedImportsAndEveryEscape() {
    var module = PythonSyntaxReader.ReadModule("import os.path  # a comment\nx = \"\\'\" # another\n");
    Assert.That(module.Body[0], Is.EqualTo(new PyImport("os.path")));
    Assert.That(((PyString)((PyAssign)module.Body[1]).Value).Value, Is.EqualTo("'"));
    Assert.That(() => PythonSyntaxReader.ReadModule("x = \"\\u12\"\n"), Throws.TypeOf<FormatException>().With.Message.Contains("broken"));
    Assert.That(() => PythonSyntaxReader.ReadModule("x = \"\\u12zz\"\n"), Throws.TypeOf<FormatException>().With.Message.Contains("broken"));
    Assert.That(() => PythonSyntaxReader.ReadModule("x = while\n"), Throws.TypeOf<FormatException>().With.Message.Contains("keyword"));
  }

  [Test]
  public void Rewrite_ReachesEveryKindOfNode() {
    const string source = """
      import math
      from typing import List


      def f(a: int, b: List[int]) -> int:
          global a
          x: a
          b[a] = a
          if a:
              raise a
          else:
              pass
          while a:
              a
          for i in a:
              break
          match a:
              case 1 if a:
                  return a
              case 2:
                  return a + a
          return (a := -a if a < a else a and a)


      t = (a, [a], f(a).a)
      """;
    var module = PythonSyntaxReader.ReadModule(source);
    var rewritten = PythonSyntaxRewriter.Rewrite(module, node => node is PyName { Name: "a" } ? new PyName("z") : node);
    var printed = PythonPrinter.Print(rewritten);
    Assert.That(PythonSyntaxRewriter.DescendantsAndSelf(rewritten).OfType<PyName>().Select(name => name.Name), Does.Not.Contain("a"));
    Assert.That(printed, Does.Contain("x: z").And.Contain("b[z] = z").And.Contain("raise z").And.Contain("case 1 if z:").And.Contain("(a := -z if z < z else z and z)"));
    Assert.That(PythonSyntaxRewriter.Rewrite(module, node => node), Is.SameAs(module));
  }

  [Test]
  public void O0501_HandsTheBlankLinesOfARemovedHelperOn() {
    var helper = new PyFunctionDef("_h", [], new PyName("int"), [new PyReturn(new PyInt(1))]) { IsHelper = true, Leading = PyTrivia.TwoBlank };
    var first = new PyAssign(new PyName("a"), new PyInt(1));
    var next = new PyAssign(new PyName("b"), new PyInt(2)) { Leading = PyTrivia.Blank };
    var applied = new List<string>();
    var middle = new PythonUnusedDeclarations().Run(new PyModule([], [first, helper, next]), PythonProfile.Default, applied.Add);
    Assert.That(middle.Body[1].Leading, Is.EqualTo(PyTrivia.TwoBlank));
    var front = new PythonUnusedDeclarations().Run(new PyModule([], [helper, next]), PythonProfile.Default, applied.Add);
    Assert.That(front.Body.Single().Leading, Is.EqualTo(PyTrivia.Blank));
    var last = new PythonUnusedDeclarations().Run(new PyModule([], [first, helper]), PythonProfile.Default, applied.Add);
    Assert.That(last.Body.Single(), Is.SameAs(first));
    var kept = new PythonUnusedDeclarations().Run(new PyModule([], [first, helper, next with { Leading = new PyTrivia(3, []) }]), PythonProfile.Default, applied.Add);
    Assert.That(kept.Body[1].Leading.BlankLines, Is.EqualTo(3));
  }

}
