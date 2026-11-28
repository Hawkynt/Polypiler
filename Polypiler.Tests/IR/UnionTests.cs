using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.Batch;
using Polypiler.Core.Backends.Cpp;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Jvm;
using Polypiler.Core.Backends.Msil;
using Polypiler.Core.Backends.Perl;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.VBScript;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Tests.IR;

/// <summary>
/// The closed tagged unions of the IR (#1582, ADR 0300 level 5) on modules built by hand: the type and
/// its canonical order, the rules of the verifier, what the interpreter computes and traps with, the
/// value at the boundary, the module format, the walkers of the tree and the backends that refuse it.
/// </summary>
[TestFixture]
public sealed class UnionTests {

  private static readonly IrType Number = IrType.Union(IrType.String, IrType.I32);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrModule Module(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => new("M", [new IrFunction("F", true, result, parameters, [], new IrBlock([new IrReturn(value)]))]);

  private static IReadOnlyList<string> Problems(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => [.. IrVerifier.Verify(Module(result, parameters, value)).Select(diagnostic => diagnostic.Message)];

  private static object? Run(IrExpr value, IReadOnlyList<IrLocal> parameters, params object[] arguments)
    => IrInterpreter.Call(Module(value.Type, parameters, value), "F", arguments);

  [Test]
  public void Type_KeepsItsMembersInCanonicalOrder() {
    Assert.That(Number.ToString(), Is.EqualTo("union<i32, str>"));
    Assert.That(Number, Is.EqualTo(IrType.Union(IrType.I32, IrType.String)));
    Assert.That(Number.GetHashCode(), Is.EqualTo(IrType.Union(IrType.I32, IrType.String).GetHashCode()));
    Assert.That(Number, Is.Not.EqualTo(IrType.Union(IrType.I32, IrType.F64)));
    Assert.That(Number.IsUnion && Number.IsStorable, Is.True);
    Assert.That(Number.IsVariant || Number.IsNumeric || Number.IsNullable, Is.False);
    Assert.That(Number.UnionType!.IndexOf(IrType.String), Is.EqualTo(1));
    Assert.That(Number.UnionType.IndexOf(IrType.Bool), Is.EqualTo(-1));
    Assert.That(Number.UnionType.Equals((object)"union"), Is.False);
    Assert.That(Number.UnionType.Equals(null), Is.False);
    Assert.That(IrType.I32.UnionType, Is.Null);
    Assert.That(IrType.Union(IrType.I32, IrType.Array(IrType.Void)).IsStorable, Is.False);
    Assert.That(IrUnions.MemberType(Number, 0), Is.EqualTo(IrType.I32));
    Assert.That(IrUnions.MemberType(Number, 2), Is.EqualTo(IrType.Void));
    Assert.That(IrUnions.MemberType(Number, -1), Is.EqualTo(IrType.Void));
    Assert.That(IrUnions.MemberType(IrType.I32, 0), Is.EqualTo(IrType.Void));
    Assert.That(() => IrType.Union(null!), Throws.ArgumentNullException);
    Assert.That(() => IrType.Union(IrType.I32, null!), Throws.ArgumentNullException);
    Assert.That(() => IrUnions.MemberType(null!, 0), Throws.ArgumentNullException);
    Assert.That(() => IrUnions.IsMember(null!), Throws.ArgumentNullException);
    Assert.That(() => IrUnions.Problem(null!), Throws.ArgumentNullException);
    Assert.That(() => IrUnions.Problem(IrType.I32), Throws.ArgumentException);
  }

  [Test]
  public void Union_CarriesItsMember() {
    var made = new IrUnionOf(Number, 1, new IrStrConst("a"));
    Assert.That(Problems(IrType.I32, [], new IrUnionTagOf(made)), Is.Empty);
    Assert.That(Run(new IrUnionTagOf(made), []), Is.EqualTo(1));
    Assert.That(Run(new IrUnionValue(made, 1), []), Is.EqualTo("a"));
    Assert.That(Run(new IrUnionValue(made, 1) { IsTested = true }, []), Is.EqualTo("a"));
    Assert.That(new IrUnionValue(made, 1).Type, Is.EqualTo(IrType.String));
  }

  [Test]
  public void WrongMember_TrapsWithAnInvalidCast() {
    var exception = Assert.Throws<IrTrapException>(() => Run(new IrUnionValue(new IrUnionOf(Number, 0, new IrConst(IrType.I32, 7)), 1), []))!;
    Assert.That(exception.Trap, Is.EqualTo(IrTrap.InvalidCast));
  }

  [Test]
  public void Value_CrossesTheBoundaryWithItsMember() {
    var u = new IrLocal("u", Number);
    Assert.That(Run(R(u), [u], new IrUnion(0, 5)), Is.EqualTo(new IrUnion(0, 5)));
    Assert.That(Run(R(u), [u], new IrUnion(1, "b")), Is.EqualTo(new IrUnion(1, "b")));
    Assert.That(() => Run(R(u), [u], 5), Throws.ArgumentException);
    Assert.That(() => Run(R(u), [u], new IrUnion(2, 5)), Throws.ArgumentException);
    Assert.That(() => Run(R(u), [u], new IrUnion(0, "b")), Throws.ArgumentException);
  }

  private static IEnumerable<TestCaseData> MemberKinds() {
    var point = new IrStructType("Point");
    point.Define([new IrField("x", IrType.I32, false)]);
    var shape = new IrClassType("Shape");
    shape.Define([]);
    shape.Inherit(null, []);
    shape.DefineMembers([], [], [], []);
    yield return new TestCaseData(IrType.Bool, Array.Empty<IrTypeDeclaration>()).SetName("Member_Scalar");
    yield return new TestCaseData(IrType.String, Array.Empty<IrTypeDeclaration>()).SetName("Member_String");
    yield return new TestCaseData(IrType.Struct(point), new IrTypeDeclaration[] { point }).SetName("Member_Struct");
    yield return new TestCaseData(IrType.Object(shape), new IrTypeDeclaration[] { shape }).SetName("Member_Object");
    yield return new TestCaseData(IrType.Optional(IrType.F64), Array.Empty<IrTypeDeclaration>()).SetName("Member_Optional");
  }

  [TestCaseSource(nameof(MemberKinds))]
  public void EveryMemberKind_IsAccepted(IrType member, IrTypeDeclaration[] types) {
    var union = IrType.Union(IrType.I64, member);
    var u = new IrLocal("u", union);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [u], [], new IrBlock([new IrReturn(new IrUnionTagOf(R(u)))]))]) { Types = types };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void TwoAndFourMembers_AreUnions_OneAndFiveAreNot() {
    var four = IrType.Union(IrType.I32, IrType.String, IrType.Bool, IrType.F64);
    Assert.That(Problems(IrType.I32, [new("u", four)], new IrConst(IrType.I32, 0)), Is.Empty);
    Assert.That(Problems(IrType.I32, [new("u", Number)], new IrConst(IrType.I32, 0)), Is.Empty);
    Assert.That(Problems(IrType.I32, [new("u", IrType.Union(IrType.I32))], new IrConst(IrType.I32, 0)), Has.Some.Contains("union of 1 members"));
    var five = IrType.Union(IrType.I32, IrType.String, IrType.Bool, IrType.F64, IrType.I64);
    Assert.That(Problems(IrType.I32, [new("u", five)], new IrConst(IrType.I32, 0)), Has.Some.Contains("union of 5 members"));
  }

  [Test]
  public void ForbiddenMembers_AreRefused() {
    IReadOnlyList<string> Of(IrType union) => Problems(IrType.I32, [new("u", union)], new IrConst(IrType.I32, 0));
    Assert.That(Of(IrType.Union(IrType.I32, IrType.Variant)), Has.Some.Contains("union member variant not allowed"));
    Assert.That(Of(IrType.Union(IrType.I32, Number)), Has.Some.Contains("union member union<i32, str> not allowed"));
    Assert.That(Of(IrType.Union(IrType.I32, IrType.Void)), Has.Some.Contains("union member void not allowed"));
    Assert.That(Of(IrType.Union(IrType.I32, IrType.I32)), Has.Some.Contains("union member i32 not allowed twice"));
    Assert.That(Of(IrType.Union(IrType.I32, IrType.Array(IrType.I32))), Has.Some.Contains("union member i32[] not allowed"));
    var ghost = new IrStructType("Ghost");
    ghost.Define([new IrField("x", IrType.I32, false)]);
    Assert.That(Of(IrType.Union(IrType.I32, IrType.Struct(ghost))), Has.Some.Contains("not among the types of the module"));
  }

  [Test]
  public void Verifier_RefusesEveryIllFormedNode() {
    var i = new IrLocal("i", IrType.I32);
    var u = new IrLocal("u", Number);
    Assert.That(Problems(Number, [i], new IrUnionOf(Number, 1, R(i))), Has.Some.Contains("of the type i32, not str"));
    Assert.That(Problems(Number, [i], new IrUnionOf(Number, 2, R(i))), Has.Some.Contains("member 2 of union<i32, str>, which has 2"));
    Assert.That(Problems(IrType.I32, [i], new IrUnionOf(IrType.I32, 0, R(i))), Has.Some.Contains("which is no union"));
    Assert.That(Problems(IrType.Union(IrType.I32), [i], new IrUnionOf(IrType.Union(IrType.I32), 0, R(i))), Has.Some.Contains("union of 1 members"));
    Assert.That(Problems(IrType.I32, [i], new IrUnionTagOf(R(i))), Has.Some.Contains("the tag of a value of the type i32, which is no union"));
    Assert.That(Problems(IrType.Void, [i], new IrUnionValue(R(i), 0)), Has.Some.Contains("the member of a value of the type i32, which is no union"));
    Assert.That(Problems(IrType.Void, [u], new IrUnionValue(R(u), 3)), Has.Some.Contains("member 3 of union<i32, str>, which it does not have"));
    Assert.That(Problems(IrType.String, [u], new IrUnionValue(R(u), 1)), Is.Empty);
  }

  /// <summary>A module that has every node of the type.</summary>
  private static IrModule Sink() {
    var u = new IrLocal("u", Number);
    var t = new IrLocal("t", IrType.I32);
    var body = new IrBlock([
      new IrDeclare(t, new IrUnionTagOf(R(u))),
      new IrIf(new IrBinary(IrBinaryOp.Equal, R(t), new IrConst(IrType.I32, 0)),
        new IrBlock([new IrReturn(new IrUnionOf(Number, 0, new IrUnionValue(R(u), 0) { IsTested = true }))]), null),
      new IrReturn(new IrUnionOf(Number, 1, new IrUnionValue(R(u), 1)))
    ]);
    return new IrModule("M", [new IrFunction("F", true, Number, [u], [t], body)]);
  }

  [Test]
  public void Format_KeepsEveryNode() {
    var module = Sink();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
    var body = read.Functions[0].Body;
    Assert.That(IrTree.Nodes(body).OfType<IrUnionValue>().Select(value => (value.Member, value.IsTested)), Is.EqualTo(new[] { (0, true), (1, false) }));
    Assert.That(read.Functions[0].ReturnType, Is.EqualTo(Number));
    Assert.That(IrInterpreter.Call(read, "F", [new IrUnion(1, "z")]), Is.EqualTo(new IrUnion(1, "z")));
    Assert.That(IrInterpreter.Call(read, "F", [new IrUnion(0, 4)]), Is.EqualTo(new IrUnion(0, 4)));
  }

  [Test]
  public void Uses_FindsTheTypeInEveryPlace() {
    IrModule Of(IrType result, IrLocal[] parameters, IrLocal[] locals, IrStmt body, IrGlobal[]? globals = null)
      => new("M", [new IrFunction("F", true, result, parameters, locals, new IrBlock([body]))]) { Globals = globals ?? [] };

    var u = new IrLocal("u", Number);
    Assert.That(IrUnions.Uses(Of(IrType.I32, [], [], new IrReturn(new IrConst(IrType.I32, 0)))), Is.False);
    Assert.That(IrUnions.Uses(Sink()), Is.True);
    Assert.That(IrUnions.Uses(Of(IrType.Void, [u], [], new IrReturn(null))), Is.True);
    Assert.That(IrUnions.Uses(Of(IrType.Void, [], [u], new IrReturn(null))), Is.True);
    Assert.That(IrUnions.Uses(Of(IrType.Void, [], [], new IrExprStmt(new IrUnionOf(Number, 0, new IrConst(IrType.I32, 1))))), Is.True);
    Assert.That(IrUnions.Uses(Of(IrType.Void, [], [], new IrReturn(null), [new IrGlobal("g", Number, null)])), Is.True);
    Assert.That(() => IrUnions.Uses(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Walkers_SeeAndRebuildTheOperand() {
    var value = new IrUnionOf(Number, 0, new IrConst(IrType.I32, 1));
    IrExpr[] nodes = [value, new IrUnionTagOf(value), new IrUnionValue(value, 0) { IsTested = true }];
    var other = new IrUnionOf(Number, 0, new IrConst(IrType.I32, 2));
    foreach (var node in nodes) {
      var operands = IrTree.Operands(node);
      Assert.That(IrShape.Operands(node), Is.EqualTo(operands));
      var rebuilt = IrTree.With(node, [.. operands.Select(operand => operand is IrConst ? new IrConst(IrType.I32, 2) : (IrExpr)other)]);
      Assert.That(rebuilt.GetType(), Is.EqualTo(node.GetType()));
      Assert.That(IrShape.Same(node, node), Is.True);
      Assert.That(IrShape.Same(node, rebuilt), Is.False);
      Assert.That(IrShape.Same(node, IrShape.With(node, operands)), Is.True);
      Assert.That(IrShape.IsSideEffectFreeNode(node), Is.True);
    }

    Assert.That(IrShape.IsTrapFreeNode(new IrUnionValue(value, 0)), Is.False);
    Assert.That(IrShape.IsTrapFreeNode(new IrUnionValue(value, 0) { IsTested = true }), Is.True);
    Assert.That(IrShape.IsTrapFreeNode(value) && IrShape.IsTrapFreeNode(new IrUnionTagOf(value)), Is.True);
  }

  private static IEnumerable<TestCaseData> Backends() {
    yield return new TestCaseData("csharp", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => CSharpEmitter.Check(module, CSharpProfile.Default)));
    yield return new TestCaseData("javascript", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => JavaScriptEmitter.Check(module, JavaScriptProfile.Default)));
    yield return new TestCaseData("python", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => PythonEmitter.Check(module, PythonProfile.Default)));
    yield return new TestCaseData("visualbasic", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => VisualBasicEmitter.Check(module, VisualBasicProfile.Vba7)));
    yield return new TestCaseData("rust", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => RustEmitter.Check(module, RustProfile.Default)));
    yield return new TestCaseData("go", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => GoEmitter.Check(module, GoProfile.Default)));
    yield return new TestCaseData("cpp", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => CppEmitter.Check(module, CppProfile.Default)));
    yield return new TestCaseData("jvm", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => JvmEmitter.Check(module, JvmProfile.Default)));
    yield return new TestCaseData("msil", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => MsilEmitter.Check(module, MsilProfile.Default)));
    yield return new TestCaseData("perl", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => PerlEmitter.Check(module, PerlProfile.Default)));
    yield return new TestCaseData("batch", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => BatchEmitter.Check(module)));
    yield return new TestCaseData("vbscript", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => VBScriptEmitter.Check(module, VBScriptProfile.Default)));
  }

  [TestCaseSource(nameof(Backends))]
  public void EveryBackend_RefusesTheType(string backend, Func<IrModule, IReadOnlyList<Diagnostic>> check)
    => Assert.That(check(Sink()).Select(problem => problem.Message), Has.Some.Contains(backend is "batch" or "jvm" or "vbscript" or "perl" ? "IrUnion" : "union<i32, str>"), backend);

}
