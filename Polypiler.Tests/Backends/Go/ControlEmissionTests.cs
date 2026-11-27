using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.IR;
using static Polypiler.Tests.Backends.Go.GoIr;

namespace Polypiler.Tests.Backends.Go;

/// <summary>
/// The statements of the control issue (#689) and the functions, structs, globals and layout of the
/// module issue (#690) as text: loops in the forms of <c>for</c>, a counted loop with its counter in
/// the clause, <c>if</c> chains outdented after a branch that cannot fall through, <c>switch</c>
/// without <c>fallthrough</c>, values Go has no expression for in statements before their use,
/// nothing declared that nothing reads, a terminating statement at the end of every function with a
/// result, and the files of the module.
/// </summary>
[TestFixture]
public sealed class ControlEmissionTests {

  private static readonly IrLocal _n = L("n", IrType.I32), _i = L("i", IrType.I32), _sum = L("sum", IrType.I32);
  private static readonly IrLocal _c = L("c", IrType.Bool), _a = L("a", IrType.I32), _b = L("b", IrType.I32);

  private static IrStmt Inc(IrLocal local) => new IrAssign(local, Bin(IrBinaryOp.Add, R(local), I(1)));

  private static string Text(IrType result, IrLocal[] parameters, params IrStmt[] body) {
    var text = Emit(new IrModule("M", [Function("f", result, parameters, body)]));
    return text[text.IndexOf("func F(", System.StringComparison.Ordinal)..];
  }

  [Test]
  public void CountedLoop_TakesItsCounterIntoTheClause() {
    var text = Text(IrType.I32, [_n],
      new IrDeclare(_sum, I(0)),
      new IrDeclare(_i, I(1)),
      new IrFor(Bin(IrBinaryOp.LessOrEqual, R(_i), R(_n)), Block(new IrAssign(_sum, Bin(IrBinaryOp.Add, R(_sum), R(_i)))), Block(Inc(_i))),
      new IrReturn(R(_sum)));
    Assert.That(text, Does.Contain("\tvar sum int32\n\tfor i := int32(1); i <= n; i++ {\n\t\tsum += i\n\t}\n\treturn sum\n"));
  }

  [Test]
  public void CountedWhile_WithTheStepAtTheEnd_IsAThreeClauseFor_AndAnAssignedCounterStaysOutside() {
    var text = Text(IrType.I32, [_n],
      new IrDeclare(_i, null),
      new IrAssign(_i, I(0)),
      new IrWhile(Bin(IrBinaryOp.Less, R(_i), R(_n)), Block(new IrExprStmt(new IrStep(R(_i), true, false, false)))),
      new IrReturn(R(_i)));
    Assert.That(text, Does.Contain("\tvar i int32\n\tfor i = 0; i < n; i++ {\n\t}\n\treturn i\n"));
  }

  [Test]
  public void Loops_TakeEveryFormOfFor() {
    Assert.That(Text(IrType.Void, [_c], new IrWhile(R(_c), Block())), Does.Contain("\tfor c {\n\t}\n"));
    Assert.That(Text(IrType.I32, [], new IrWhile(C(IrType.Bool, 1), Block(new IrReturn(I(1))))), Does.Contain("\tfor {\n\t\treturn 1\n\t}\n}"));
    Assert.That(Text(IrType.Void, [_a], new IrDoWhile(Block(new IrAssign(_a, Bin(IrBinaryOp.Add, R(_a), I(2)))), Bin(IrBinaryOp.Less, R(_a), I(10)))),
      Does.Contain("\tfor {\n\t\ta += 2\n\t\tif a >= 10 {\n\t\t\tbreak\n\t\t}\n\t}\n"));
    Assert.That(Text(IrType.Void, [_a, _c], new IrDoWhile(Block(new IrIf(R(_c), Block(new IrContinue()), null)), Bin(IrBinaryOp.Less, R(_a), I(10)))),
      Does.Contain("for t := true; ; t = false {\n\t\tif !t {\n\t\t\tif a >= 10 {\n\t\t\t\tbreak\n\t\t\t}\n\t\t}\n"));
    Assert.That(Text(IrType.Void, [_a, _c], new IrFor(R(_c), Block(new IrIf(R(_c), Block(new IrContinue()), null)), Block(Inc(_a), Inc(_a)))),
      Does.Contain("for t := true; ; t = false {\n\t\tif !t {\n\t\t\ta++\n\t\t\ta++\n\t\t}\n\t\tif !c {\n\t\t\tbreak\n\t\t}\n"));
    Assert.That(Text(IrType.Void, [_a, _c], new IrFor(R(_c), Block(), Block(Inc(_a), Inc(_a)))), Does.Contain("\tfor c {\n\t\ta++\n\t\ta++\n\t}\n"));
    Assert.That(Text(IrType.Void, [_a, _c], new IrFor(R(_c), Block(), Block(Inc(_a)))), Does.Contain("\tfor ; c; a++ {\n\t}\n"));
  }

  [Test]
  public void LoopCondition_ThatNeedsStatements_IsTestedInTheBody() {
    var condition = new IrConditional(R(_c), Bin(IrBinaryOp.Less, R(_a), I(3)), C(IrType.Bool, 0));
    var text = Text(IrType.Void, [_a, _c], new IrWhile(condition, Block(Inc(_a))));
    Assert.That(text, Does.Contain("\tfor {\n\t\tvar t bool = false\n\t\tif c {\n\t\t\tt = a < 3\n\t\t}\n\t\tif !t {\n\t\t\tbreak\n\t\t}\n\t\ta++\n\t}\n"));
  }

  [Test]
  public void If_ChainsWithElseIf_AndOutdentsAfterABranchThatEnds() {
    var chain = new IrIf(Bin(IrBinaryOp.Less, R(_a), I(0)), Block(new IrAssign(_b, I(1))),
      Block(new IrIf(Bin(IrBinaryOp.Equal, R(_a), I(0)), Block(new IrAssign(_b, I(2))), Block(new IrAssign(_b, I(3))))));
    Assert.That(Text(IrType.I32, [_a, _b], chain, new IrReturn(R(_b))), Does.Contain("\tif a < 0 {\n\t\tb = 1\n\t} else if a == 0 {\n\t\tb = 2\n\t} else {\n\t\tb = 3\n\t}\n"));
    var returns = new IrIf(Bin(IrBinaryOp.Less, R(_a), I(0)), Block(new IrReturn(I(-1))), Block(new IrReturn(I(1))));
    Assert.That(Text(IrType.I32, [_a], returns), Does.Contain("\tif a < 0 {\n\t\treturn -1\n\t}\n\treturn 1\n}"));
    var declares = new IrIf(R(_c), Block(new IrReturn(I(-1))), Block(new IrDeclare(_i, R(_a)), new IrReturn(R(_i))));
    Assert.That(Text(IrType.I32, [_a, _c], declares), Does.Contain("\t} else {\n\t\ti := a\n\t\treturn i\n\t}\n}"));
  }

  [Test]
  public void Switch_HasNoFallthrough_AndLeavesOutTheBreakThatEndsASection() {
    var choice = new IrSwitch(R(_a), [
      new IrSwitchSection([1, 7], false, Block(new IrAssign(_b, I(0)), new IrBreak())),
      new IrSwitchSection([2], false, Block(new IrReturn(I(4)))),
      new IrSwitchSection([3], true, Block(new IrAssign(_b, I(-1))))
    ]);
    var text = Text(IrType.I32, [_a, _b], choice, new IrReturn(R(_b)));
    Assert.That(text, Does.Contain("\tswitch a {\n\tcase 1, 7:\n\t\tb = 0\n\tcase 2:\n\t\treturn 4\n\tdefault:\n\t\tb = -1\n\t}\n").And.Not.Contains("fallthrough"));
    var total = new IrSwitch(R(_a), [new IrSwitchSection([1], false, Block(new IrReturn(I(1)))), new IrSwitchSection([], true, Block(new IrReturn(I(2))))]);
    Assert.That(Text(IrType.I32, [_a], total), Does.Not.Contain("unreachable"));
  }

  [Test]
  public void FunctionsThatGoDoesNotSeeEnd_EndInAPanic() {
    var open = new IrIf(R(_c), Block(new IrReturn(I(1))), null);
    var loop = new IrWhile(R(_c), Block(new IrReturn(I(1))));
    Assert.That(Text(IrType.I32, [_c], open, new IrWhile(C(IrType.Bool, 1), Block(new IrBreak())), new IrReturn(I(0))), Does.Contain("\treturn 0\n}"));
    Assert.That(Text(IrType.I32, [_c], loop), Does.EndWith("\tpanic(\"unreachable\")\n}\n"));
    var blocked = Text(IrType.I32, [_c], Block(new IrDeclare(_i, I(1)), new IrReturn(R(_i))));
    Assert.That(blocked, Does.Not.Contain("unreachable"));
    var thrown = Text(IrType.I32, [_c], new IrThrow(new IrNew(IrType.Object(IrExceptionClass.OverflowException), IrExceptionClass.Constructor(IrExceptionClass.OverflowException, 1), [new IrStrConst("integer overflow")])));
    Assert.That(thrown, Does.Contain("\tpanic(\"integer overflow\")\n}").And.Not.Contain("unreachable"));
    var plain = Text(IrType.Void, [], new IrThrow(new IrNew(IrType.Object(IrExceptionClass.DivideByZeroException), IrExceptionClass.Constructor(IrExceptionClass.DivideByZeroException, 0), [])));
    Assert.That(plain, Does.Contain("panic(\""));
  }

  [Test]
  public void ValuesGoHasNoExpressionFor_AreStatementsBeforeTheirUse() {
    var choice = new IrConditional(R(_c), Bin(IrBinaryOp.Add, R(_a), I(1)), R(_b));
    Assert.That(Text(IrType.I32, [_c, _a, _b], new IrReturn(Bin(IrBinaryOp.Multiply, choice, I(2)))),
      Does.Contain("\tvar t int32\n\tif c {\n\t\tt = a + 1\n\t} else {\n\t\tt = b\n\t}\n\treturn t * 2\n"));
    Assert.That(Text(IrType.I32, [_c, _a, _b], new IrReturn(choice)), Does.Contain("\tif c {\n\t\treturn a + 1\n\t}\n\treturn b\n"));
    Assert.That(Text(IrType.I32, [_c, _a, _b], new IrDeclare(_i, choice), new IrReturn(R(_i))), Does.Contain("\tvar i int32\n\tif c {\n\t\ti = a + 1\n\t} else {\n\t\ti = b\n\t}\n"));
    var step = new IrStep(R(_a), true, true, false);
    Assert.That(Text(IrType.I32, [_a], new IrReturn(Bin(IrBinaryOp.Add, step, R(_a)))), Does.Contain("\tt := a\n\ta++\n\treturn t + a\n"));
    var trapping = new IrStep(R(_a), false, false, true);
    Assert.That(Text(IrType.I32, [_a], new IrReturn(trapping)), Does.Contain("\ta = plySubCheckedInt32(a, 1)\n\treturn a\n"));
    var rightNeedsStatements = Bin(IrBinaryOp.LogicalAnd, R(_c), Bin(IrBinaryOp.Less, new IrStep(R(_a), true, false, false), I(3)));
    Assert.That(Text(IrType.Bool, [_c, _a], new IrReturn(rightNeedsStatements)), Does.Contain("\tt := c\n\tif t {\n\t\ta++\n\t\tt = a < 3\n\t}\n\treturn t\n"));
    var thrown = new IrThrowExpression(IrType.I32, new IrNew(IrType.Object(IrExceptionClass.Exception), IrExceptionClass.Constructor(IrExceptionClass.Exception, 1), [new IrStrConst("no")]));
    Assert.That(Text(IrType.I32, [], new IrReturn(thrown)), Does.Contain("return func() int32 {\n\t\tpanic(\"no\")\n\t}()"));
  }

  [Test]
  public void OperandsBeforeACall_ThatReadState_AreEvaluatedFirst() {
    var log = L("log", IrType.Array(IrType.I32));
    var callee = Function("g", IrType.I32, [log], new IrReturn(I(1)));
    var call = new IrCall("g", IrType.I32, [R(log)]);
    var module = new IrModule("M", [Function("f", IrType.I32, [log], new IrReturn(Bin(IrBinaryOp.Add, new IrArrayLoad(R(log), I(0)), call))), callee]);
    Assert.That(Emit(module), Does.Contain("\tt := log[0]\n\treturn t + G(log)\n"));
    var calls = new IrModule("M", [Function("f", IrType.I32, [log], new IrReturn(Bin(IrBinaryOp.Subtract, call, call))), callee]);
    Assert.That(Emit(calls), Does.Contain("\treturn G(log) - G(log)\n"));
  }

  [Test]
  public void NothingIsDeclaredThatNothingReads_AndEffectsStay() {
    var unused = L("unused", IrType.I32);
    var log = L("log", IrType.Array(IrType.I32));
    var callee = Function("g", IrType.I32, [], new IrReturn(I(1)));
    var module = new IrModule("M", [Function("f", IrType.Void, [log],
      new IrDeclare(unused, new IrCall("g", IrType.I32, [])),
      new IrAssign(unused, new IrArrayLoad(R(log), I(1))),
      new IrAssign(unused, I(3)),
      new IrExprStmt(Bin(IrBinaryOp.Add, I(1), I(2))),
      new IrExprStmt(new IrArrayLength(R(log))),
      new IrExprStmt(R(log))), callee]);
    var text = Emit(module);
    Assert.That(text, Does.Contain("func F(log []int32) {\n\tG()\n\t_ = log[1]\n\t_ = int32(len(log))\n}").And.Not.Contains("unused"));
  }

  [Test]
  public void Stores_AreAssignmentsToElements_AndStepsOfThem() {
    var data = L("data", IrType.Array(IrType.U8));
    Assert.That(Text(IrType.Void, [data], new IrStore(R(data), I(0), C(IrType.U8, 7))), Does.Contain("\tdata[0] = 7\n"));
    Assert.That(Text(IrType.Void, [data], new IrExprStmt(new IrStep(new IrArrayLoad(R(data), I(1)), true, true, false))), Does.Contain("\tdata[1]++\n"));
    var counts = L("counts", IrType.Array(IrType.I32));
    Assert.That(Text(IrType.Void, [counts], new IrStore(R(counts), I(2), Bin(IrBinaryOp.Add, new IrArrayLoad(R(counts), I(2)), I(5)))), Does.Contain("\tcounts[2] += 5\n"));
    Assert.That(Text(IrType.Void, [counts], new IrStore(R(counts), I(2), Bin(IrBinaryOp.Subtract, new IrArrayLoad(R(counts), I(2)), I(1)))), Does.Contain("\tcounts[2]--\n"));
    Assert.That(Text(IrType.I32, [counts], new IrReturn(new IrStep(new IrArrayLoad(R(counts), I(0)), false, true, false))), Does.Contain("\tt := counts[0]\n\tcounts[0]--\n\treturn t\n"));
    Assert.That(Text(IrType.Array(IrType.I64), [_n], new IrReturn(new IrNewArray(IrType.Array(IrType.I64), R(_n)))), Does.Contain("return make([]int64, n)"));
    Assert.That(Text(IrType.Array(IrType.I32), [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [I(1), I(2)]))), Does.Contain("return []int32{1, 2}"));
    var index64 = L("i", IrType.I64);
    var clamped = new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.Clamp, IrType.I64, [R(index64), C(IrType.I64, -1), C(IrType.I64, int.MaxValue)]));
    Assert.That(Text(IrType.I32, [counts, index64], new IrReturn(new IrArrayLoad(R(counts), clamped))), Does.Contain("return counts[i]"));
  }

  [Test]
  public void StructsAndGlobals_AreGoTypesAndVariables() {
    var point = new IrStructType("Point");
    point.Define([new IrField("x", IrType.I32, false), new IrField("Weight", IrType.F64, false)]);
    var plain = new IrStructType("Pair");
    plain.Define([new IrField("first", IrType.I32, false), new IrField("second", IrType.I32, false)]);
    var p = L("p", IrType.Struct(point));
    var q = L("q", IrType.Struct(plain));
    var counter = new IrGlobal("Counter", IrType.I32, I(5));
    var table = new IrModuleConst("Table", IrType.Array(IrType.U32), [1, 2, 3]);
    var prime = new IrModuleConst("Prime", IrType.U32, [16777619]);
    var module = new IrModule("Shapes", [
      Function("make", IrType.Struct(point), [], new IrReturn(new IrStructNew(IrType.Struct(point), [I(1), F64(2.5)]))),
      Function("same", IrType.Bool, [p], new IrReturn(new IrStructEquals(R(p), R(p)))),
      Function("pair", IrType.Bool, [q], new IrReturn(new IrStructEquals(R(q), new IrStructDefault(IrType.Struct(plain))))),
      Function("move", IrType.Struct(point), [p], new IrFieldStore(R(p), point.Fields[0], I(3)), new IrReturn(R(p))),
      Function("count", IrType.I32, [], new IrGlobalAssign(counter, Bin(IrBinaryOp.Add, new IrGlobalRef(counter), I(1))), new IrReturn(new IrGlobalRef(counter))),
      Function("look", IrType.U32, [_i], new IrReturn(Bin(IrBinaryOp.Multiply, new IrArrayLoad(new IrConstRef(table), R(_i)), new IrConstRef(prime))))
    ], [table, prime]) { Types = [point, plain], Globals = [counter] };
    var text = Emit(module);
    Assert.That(text, Does.Contain("type Point struct {\n\tX      int32\n\tWeight float64\n}")
      .And.Contain("return Point{X: 1, Weight: 2.5}")
      .And.Contain("return plyEqualPoint(p, p)")
      .And.Contain("return q == Pair{}")
      .And.Contain("p.X = 3")
      .And.Contain("var counter int32 = 5")
      .And.Contain("counter = counter + 1")
      .And.Contain("var table = []uint32{\n\t1, 2, 3,\n}")
      .And.Contain("const prime uint32 = 0x1000193")
      .And.Contain("return table[i] * prime")
      .And.Contain("func plyEqualPoint(a, b Point) bool {\n\treturn a.X == b.X && (a.Weight == b.Weight || a.Weight != a.Weight && b.Weight != b.Weight)\n}"));
  }

  [Test]
  public void TheModule_IsGoModThePackageAndTheHelpers() {
    var module = new IrModule("Checksums.Adler32", [Function("f", IrType.I32, [_a, _b], new IrReturn(Bin(IrBinaryOp.Divide, R(_a), R(_b))))]);
    var files = GoEmitter.Emit(module, GoProfile.Parse("go1.22"));
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "go.mod", "adler32.go", "polyrt.go" }));
    Assert.That(files[0].Content, Is.EqualTo("module adler32\n\ngo 1.22\n"));
    Assert.That(files[1].Content, Does.StartWith("// Code generated by polypiler. DO NOT EDIT.\n//\n// Checksums.Adler32: change the source, not this file.\n// Go profile: go1.22.\n\npackage adler32\n"));
    Assert.That(files[2].Content, Does.StartWith("// Code generated by polypiler. DO NOT EDIT.\n").And.Contain("Helpers added by polypiler: MIT"));
    Assert.That(files[2].Helpers.Single().Symbol, Is.EqualTo("plyDivInt32"));
    var options = new GoEmitOptions { ModulePath = "example.test/polychecksums/v2" };
    var placed = GoEmitter.Emit(module, GoProfile.Default, FloatMode.Accurate, null, Polypiler.Core.Optimize.Target.TargetPassOptions.None, options).Files;
    Assert.That(placed[0].Content, Does.StartWith("module example.test/polychecksums/v2\n"));
    Assert.That(placed[1].Content, Does.Contain("package polychecksums\n"));
    Assert.That(GoEmitter.PackageName(module, options), Is.EqualTo("polychecksums"));
    Assert.That(GoEmitter.ModulePath(module, null), Is.EqualTo("adler32"));
    Assert.That(Problems(module, null, new GoEmitOptions { ModulePath = "Example Test/x" }), Has.Some.Contains("PLY2173"));
    Assert.That(() => GoEmitter.Emit(module, GoProfile.Default, FloatMode.Accurate, null, Polypiler.Core.Optimize.Target.TargetPassOptions.None, new GoEmitOptions { ModulePath = "" }), Throws.ArgumentException);
    var first = GoEmitter.Emit(module, GoProfile.Default).Select(file => file.Content).ToList();
    Assert.That(GoEmitter.Emit(module, GoProfile.Default).Select(file => file.Content), Is.EqualTo(first));
  }

  [Test]
  public void Names_AreGoNames_AndCollisionsAreRefused() {
    var module = new IrModule("M", [Function("get_id", IrType.I32, [L("len", IrType.I32), L("func", IrType.I32)], new IrReturn(R(L("len", IrType.I32)))),
      new IrFunction("plyHidden", false, IrType.Void, [], [], Block())]);
    var names = GoEmitter.FunctionNames(module);
    Assert.That(names["get_id"], Is.EqualTo("GetID"));
    Assert.That(names["plyHidden"], Is.EqualTo("xPlyHidden"));
    Assert.That(Emit(module), Does.Contain("func GetID(len_, func_ int32) int32 {"));
    var collision = new IrModule("M", [Function("get_id", IrType.I32, [], new IrReturn(I(1))), Function("GetID", IrType.I32, [], new IrReturn(I(2)))]);
    Assert.That(Problems(collision), Has.Some.Contains("PLY2172").And.Some.Contains("'GetID'"));
  }

  [Test]
  public void ConstructsOutsideTheSubset_AreRefusedByName() {
    var async = new IrFunction("run", true, IrType.Async(IrType.I32), [], [], Block(new IrReturn(I(1))));
    Assert.That(Problems(new IrModule("M", [async])), Has.Some.Contains("PLY2170"));
    var list = L("xs", IrType.List(IrType.I32));
    Assert.That(Problems(new IrModule("M", [Function("f", IrType.Void, [list])])), Has.Some.Contains("PLY2150"));
    var element = L("e", IrType.I32);
    var each = new IrForEachCollection(R(list), default, element, null, Block());
    Assert.That(Problems(new IrModule("M", [new IrFunction("f", true, IrType.Void, [list], [element], Block(each))])), Has.Some.Contains("PLY2160"));
  }

}
