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

namespace Polypiler.Tests.IR;

/// <summary>
/// The closed tagged unions of the IR (#1582): the type and its canonical member order, the verifier's
/// rules, the interpreter's make, tag, payload and equality, the module format, and every backend
/// refusing the type until it has a lowering.
/// </summary>
[TestFixture]
public sealed class UnionTests {

  private static readonly IrType _numberOrText = IrType.Union(IrType.I32, IrType.String);

  private static IrModule Module(IrType returns, IReadOnlyList<IrLocal> parameters, IReadOnlyList<IrLocal> locals, params IrStmt[] body)
    => new("Unions", [new IrFunction("F", true, returns, parameters, locals, new IrBlock(body))]);

  private static IReadOnlyList<string> Problems(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  [Test]
  public void AUnionCarriesItsMember_TheTagIsItsIndex_AndThePayloadComesBack() {
    var u = new IrLocal("u", _numberOrText);
    var made = Module(_numberOrText, [], [], new IrReturn(new IrUnionOf(_numberOrText, 1, new IrStrConst("a"))));
    var tag = Module(IrType.I32, [u], [], new IrReturn(new IrUnionTagOf(new IrLocalRef(u))));
    var payload = Module(IrType.String, [u], [], new IrReturn(new IrUnionValue(new IrLocalRef(u), 1)));

    Assert.That(Problems(made).Concat(Problems(tag)).Concat(Problems(payload)), Is.Empty);
    var union = (IrUnion)IrInterpreter.Call(made, "F", [])!;
    Assert.That(union, Is.EqualTo(new IrUnion(1, "a")));
    Assert.That(IrInterpreter.Call(tag, "F", [union]), Is.EqualTo(1));
    Assert.That(IrInterpreter.Call(payload, "F", [union]), Is.EqualTo("a"));
  }

  [Test]
  public void TakingAnotherMember_TrapsWithInvalidCast() {
    var module = Module(IrType.String, [], [], new IrReturn(new IrUnionValue(new IrUnionOf(_numberOrText, 0, new IrConst(IrType.I32, 7)), 1)));

    Assert.That(Problems(module), Is.Empty);
    Assert.That(() => IrInterpreter.Call(module, "F", []), Throws.TypeOf<IrTrapException>().With.Property(nameof(IrTrapException.Trap)).EqualTo(IrTrap.InvalidCast));
  }

  [Test]
  public void TheMembers_AreInTheirCanonicalOrder_AndTheTypeIsStructural() {
    Assert.That(IrType.Union(IrType.String, IrType.I32), Is.EqualTo(_numberOrText));
    Assert.That(IrType.Union(IrType.String, IrType.I32).GetHashCode(), Is.EqualTo(_numberOrText.GetHashCode()));
    Assert.That(_numberOrText.ToString(), Is.EqualTo("union<i32, str>"));
    Assert.That(_numberOrText.UnionType!.Members, Is.EqualTo(new[] { IrType.I32, IrType.String }));
    Assert.That(IrType.Union(IrType.I32, IrType.F64), Is.Not.EqualTo(_numberOrText));
    Assert.That(_numberOrText.IsUnion && _numberOrText.IsStorable, Is.True);
    Assert.That(IrType.I32.IsUnion, Is.False);
    Assert.That(_numberOrText.UnionType.Equals((object)IrType.Union(IrType.I32, IrType.String).UnionType), Is.True);
    Assert.That(_numberOrText.UnionType.Equals(null), Is.False);
  }

  [TestCase(1, "union of 1 members")]
  [TestCase(5, "union of 5 members")]
  public void AUnionOutsideTwoToFourMembers_IsRefused(int count, string reason) {
    IrType[] members = [IrType.I8, IrType.I16, IrType.I32, IrType.I64, IrType.F64];
    var local = new IrLocal("u", IrType.Union(members[..count]));

    Assert.That(Problems(Module(IrType.Void, [], [local])), Has.Some.Contains(reason));
  }

  [TestCase(2)]
  [TestCase(4)]
  public void AUnionOfTwoOrFourMembers_IsAccepted(int count) {
    IrType[] members = [IrType.I8, IrType.I16, IrType.I32, IrType.I64];

    Assert.That(Problems(Module(IrType.Void, [], [new IrLocal("u", IrType.Union(members[..count]))])), Is.Empty);
  }

  private static readonly IrStructType _point = new("Point", [new IrField("X", IrType.I32, false), new IrField("Y", IrType.F64, false)]);

  private static readonly IrClassType _box = Box();

  private static IrClassType Box() {
    var box = new IrClassType("Box");
    box.Define([]);
    box.DefineMembers([], [], [], []);
    return box;
  }

  [Test]
  public void EveryMemberKind_IsAccepted() {
    var union = IrType.Union(IrType.Bool, IrType.String, IrType.Struct(_point), IrType.Object(_box));
    var optional = IrType.Union(IrType.Optional(IrType.I32), IrType.F32);
    var module = Module(IrType.Void, [new IrLocal("u", union), new IrLocal("o", optional)], []) with { Types = [_point, _box] };

    Assert.That(Problems(module), Is.Empty);
  }

  [TestCase("variant")]
  [TestCase("union")]
  [TestCase("void")]
  [TestCase("array")]
  [TestCase("duplicate")]
  public void AForbiddenMember_IsRefused(string kind) {
    var member = kind switch {
      "variant" => IrType.Variant,
      "union" => _numberOrText,
      "void" => IrType.Void,
      "array" => IrType.Array(IrType.U8),
      _ => IrType.I32
    };
    var problems = Problems(Module(IrType.Union(IrType.I32, member), [], []));

    Assert.That(problems, Has.Some.Contains($"union member {member} not allowed"));
  }

  [Test]
  public void AnInvalidUnion_IsReportedWhereverItIsDeclared() {
    var bad = IrType.Union(IrType.I32);
    var parameter = Module(IrType.Void, [new IrLocal("p", bad)], []);
    var global = new IrModule("Unions", []) { Globals = [new IrGlobal("g", bad, null)] };

    Assert.That(Problems(parameter), Has.Some.Contains("union of 1 members"));
    Assert.That(Problems(global), Has.Some.Contains("union of 1 members"));
  }

  [Test]
  public void TheNodes_AreCheckedAgainstTheirTypes() {
    var u = new IrLocal("u", _numberOrText);
    var x = new IrLocal("x", IrType.I32);
    IrStmt[] wrong = [
      new IrExprStmt(new IrUnionOf(_numberOrText, 0, new IrStrConst("a"))),
      new IrExprStmt(new IrUnionOf(_numberOrText, 2, new IrConst(IrType.I32, 1))),
      new IrExprStmt(new IrUnionOf(IrType.I32, 0, new IrConst(IrType.I32, 1))),
      new IrExprStmt(new IrUnionOf(IrType.Union(IrType.I32), 0, new IrConst(IrType.I32, 1))),
      new IrExprStmt(new IrUnionTagOf(new IrLocalRef(x))),
      new IrExprStmt(new IrUnionValue(new IrLocalRef(x), 0)),
      new IrExprStmt(new IrUnionValue(new IrLocalRef(u), 5))
    ];
    var problems = Problems(Module(IrType.Void, [u, x], [], wrong));

    Assert.That(problems, Has.Some.Contains("a value of the type str for the member 0 of union<i32, str>, which is i32"));
    Assert.That(problems, Has.Some.Contains("a value of the member 2 of union<i32, str>, which has none"));
    Assert.That(problems, Has.Some.Contains("a union value of the type i32, which is no union"));
    Assert.That(problems, Has.Some.Contains("union of 1 members"));
    Assert.That(problems, Has.Some.Contains("the tag of a value of the type i32, which is no union"));
    Assert.That(problems, Has.Some.Contains("the payload of a value of the type i32, which is no union"));
    Assert.That(problems, Has.Some.Contains("the payload of the member 5 of union<i32, str>, which has none"));
  }

  private static object? Equal(IrType union, IrBinaryOp op, IrUnion left, IrUnion right) {
    var (a, b) = (new IrLocal("a", union), new IrLocal("b", union));
    var module = Module(IrType.Bool, [a, b], [], new IrReturn(new IrBinary(op, new IrLocalRef(a), new IrLocalRef(b)))) with { Types = [_point, _box] };
    Assert.That(Problems(module), Is.Empty);
    return IrInterpreter.Call(module, "F", [left, right]);
  }

  [TestCase(0, 5, 0, 5, true)]
  [TestCase(0, 5, 0, 6, false)]
  [TestCase(1, "a", 1, "a", true)]
  [TestCase(1, "a", 0, 1, false)]
  public void TwoUnions_AreEqualWithTheSameMemberAndAnEqualPayload(int leftMember, object left, int rightMember, object right, bool equal) {
    Assert.That(Equal(_numberOrText, IrBinaryOp.Equal, new(leftMember, left), new(rightMember, right)), Is.EqualTo(equal));
    Assert.That(Equal(_numberOrText, IrBinaryOp.NotEqual, new(leftMember, left), new(rightMember, right)), Is.EqualTo(!equal));
  }

  [Test]
  public void PayloadEquality_FollowsTheMembersOwnEquality() {
    var floats = IrType.Union(IrType.F32, IrType.F64);
    Assert.That(Equal(floats, IrBinaryOp.Equal, new(1, double.NaN), new(1, double.NaN)), Is.False);
    Assert.That(Equal(floats, IrBinaryOp.Equal, new(0, 0.5f), new(0, 0.5f)), Is.True);
    Assert.That(Equal(floats, IrBinaryOp.Equal, new(1, -0.0), new(1, 0.0)), Is.True);

    var optional = IrType.Union(IrType.Optional(IrType.I32), IrType.Bool);
    Assert.That(Equal(optional, IrBinaryOp.Equal, new(1, null), new(1, null)), Is.True);
    Assert.That(Equal(optional, IrBinaryOp.Equal, new(1, null), new(1, 3)), Is.False);
    Assert.That(Equal(optional, IrBinaryOp.Equal, new(1, 3), new(1, 3)), Is.True);
    Assert.That(Equal(optional, IrBinaryOp.Equal, new(0, true), new(0, false)), Is.False);

    var structs = IrType.Union(IrType.Struct(_point), IrType.I32);
    Assert.That(Equal(structs, IrBinaryOp.Equal, new(0, new object?[] { 1, 2.0 }), new(0, new object?[] { 1, 2.0 })), Is.True);
    Assert.That(Equal(structs, IrBinaryOp.Equal, new(0, new object?[] { 1, 2.0 }), new(0, new object?[] { 1, 3.0 })), Is.False);
    Assert.That(Equal(structs, IrBinaryOp.Equal, new(0, new object?[] { 1, 2.0 }), new(0, new object?[] { 2, 2.0 })), Is.False);
  }

  [Test]
  public void ObjectPayloads_AreEqualByIdentity() {
    var union = IrType.Union(IrType.Object(_box), IrType.I32);
    var (a, b) = (new IrLocal("a", union), new IrLocal("b", union));
    var same = Module(IrType.Bool, [a], [], new IrReturn(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(a), new IrLocalRef(a)))) with { Types = [_box] };
    var nulls = Module(IrType.Bool, [a, b], [], new IrReturn(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(a), new IrLocalRef(b)))) with { Types = [_box] };

    Assert.That(IrInterpreter.Call(nulls, "F", [new IrUnion(0, null), new IrUnion(0, null)]), Is.True);
    Assert.That(Problems(same), Is.Empty);
  }

  [Test]
  public void OnlyEqualityIsDefinedOnUnions() {
    Assert.That(IrOperators.Accepts(IrBinaryOp.Equal, _numberOrText, _numberOrText), Is.True);
    Assert.That(IrOperators.Accepts(IrBinaryOp.Add, _numberOrText, _numberOrText), Is.False);
    Assert.That(IrOperators.Accepts(IrBinaryOp.Equal, _numberOrText, IrType.I32), Is.False);
    Assert.That(IrOperators.Accepts(IrBinaryOp.Equal, IrType.I32, _numberOrText), Is.False);
  }

  [Test]
  public void AnArgumentThatIsNoValueOfTheUnion_IsRefusedAtTheBoundary() {
    var u = new IrLocal("u", _numberOrText);
    var module = Module(IrType.I32, [u], [], new IrReturn(new IrUnionTagOf(new IrLocalRef(u))));

    Assert.Throws<ArgumentException>(() => IrInterpreter.Call(module, "F", [new IrUnion(2, 1)]));
    Assert.Throws<ArgumentException>(() => IrInterpreter.Call(module, "F", [new IrUnion(0, "a")]));
    Assert.Throws<ArgumentException>(() => IrInterpreter.Call(module, "F", [5]));
  }

  /// <summary>A module with every node of the type, tested and untested, in a parameter, a local, a global and a result.</summary>
  private static IrModule Every() {
    var u = new IrLocal("u", _numberOrText);
    var v = new IrLocal("v", _numberOrText);
    var body = new IrBlock([
      new IrDeclare(v, new IrUnionOf(_numberOrText, 0, new IrConst(IrType.I32, 7))),
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrUnionTagOf(new IrLocalRef(u)), new IrConst(IrType.I32, 1)),
        new IrBlock([new IrReturn(new IrUnionOf(_numberOrText, 1, new IrUnionValue(new IrLocalRef(u), 1) { IsTested = true }))]), null),
      new IrIf(new IrBinary(IrBinaryOp.Equal, new IrLocalRef(u), new IrLocalRef(v)), new IrBlock([new IrReturn(new IrGlobalRef(Global))]), null),
      new IrReturn(new IrUnionOf(_numberOrText, 0, new IrUnionValue(new IrLocalRef(u), 0)))
    ]);
    return new IrModule("Every", [new IrFunction("Pass", true, _numberOrText, [u], [v], body)]) { Globals = [Global] };
  }

  private static readonly IrGlobal Global = new("g", _numberOrText, new IrUnionOf(_numberOrText, 1, new IrStrConst("g")));

  [Test]
  public void EveryNode_RunsAsTheTypeSays() {
    var module = Every();

    Assert.That(Problems(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "Pass", [new IrUnion(1, "x")]), Is.EqualTo(new IrUnion(1, "x")));
    Assert.That(IrInterpreter.Call(module, "Pass", [new IrUnion(0, 7)]), Is.EqualTo(new IrUnion(1, "g")));
    Assert.That(IrInterpreter.Call(module, "Pass", [new IrUnion(0, 3)]), Is.EqualTo(new IrUnion(0, 3)));
    Assert.That(IrUnions.Uses(module), Is.True);
  }

  [Test]
  public void TheModuleFormat_RoundTripsEveryNode() {
    var module = Every();
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));

    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
    Assert.That(read.Functions[0].ReturnType, Is.EqualTo(_numberOrText));
    var tested = IrTree.Nodes(read.Functions[0].Body).OfType<IrUnionValue>().ToList();
    Assert.That(tested.Select(node => (node.Member, node.IsTested)), Is.EqualTo(new[] { (1, true), (0, false) }));
    Assert.That(IrInterpreter.Call(read, "Pass", [new IrUnion(1, "x")]), Is.EqualTo(new IrUnion(1, "x")));
  }

  [Test]
  public void TheTreeWalkers_RebuildEveryNode() {
    var u = new IrLocal("u", _numberOrText);
    IrExpr[] nodes = [new IrUnionOf(_numberOrText, 0, new IrConst(IrType.I32, 1)), new IrUnionTagOf(new IrLocalRef(u)), new IrUnionValue(new IrLocalRef(u), 1) { IsTested = true }];
    foreach (var node in nodes) {
      var operands = IrTree.Operands(node);
      Assert.That(operands, Has.Count.EqualTo(1));
      Assert.That(IrTree.With(node, operands), Is.EqualTo(node));
    }
  }

  [Test]
  public void AModuleWithoutUnions_DoesNotUseThem() {
    var x = new IrLocal("x", IrType.I32);
    Assert.That(IrUnions.Uses(Module(IrType.I32, [x], [], new IrReturn(new IrLocalRef(x)))), Is.False);
    Assert.That(IrUnions.Uses(Module(IrType.I32, [new IrLocal("u", _numberOrText)], [], new IrReturn(new IrUnionTagOf(new IrLocalRef(new IrLocal("u", _numberOrText))))) with { }), Is.True);
    Assert.That(IrUnions.Uses(new IrModule("G", []) { Globals = [Global] }), Is.True);
    Assert.That(IrUnions.Uses(Module(IrType.Void, [], [new IrLocal("l", _numberOrText)])), Is.True);
  }

  [Test]
  public void TheRules_AnswerForEveryType() {
    Assert.That(IrUnions.MemberType(_numberOrText, 1), Is.EqualTo(IrType.String));
    Assert.That(IrUnions.MemberType(_numberOrText, -1), Is.Null);
    Assert.That(IrUnions.MemberType(_numberOrText, 2), Is.Null);
    Assert.That(IrUnions.MemberType(IrType.I32, 0), Is.Null);
    Assert.That(IrUnions.Problems(IrType.I32), Is.Empty);
    Assert.That(new IrUnionValue(new IrConst(IrType.I32, 0), 0).Type, Is.EqualTo(IrType.Void));
    Assert.Throws<ArgumentNullException>(() => IrUnions.IsMember(null!));
    Assert.Throws<ArgumentNullException>(() => IrUnions.Problems(null!));
    Assert.Throws<ArgumentNullException>(() => IrUnions.MemberType(null!, 0));
    Assert.Throws<ArgumentNullException>(() => IrUnions.Uses(null!));
    Assert.Throws<ArgumentNullException>(() => _ = new IrUnionType(null!));
    Assert.Throws<ArgumentNullException>(() => _ = new IrUnionType([IrType.I32, null!]));
  }

  [Test]
  public void TheJavaScriptBackend_RefusesToEmitAUnion() {
    Assert.That(() => JavaScriptEmitter.Emit(Every(), JavaScriptProfile.Default), Throws.ArgumentException.With.Message.Contains("a closed tagged union has no JavaScript form yet"));
    Assert.That(JavaScriptEmitter.Check(Every(), JavaScriptProfile.Default).Single().Code, Is.EqualTo(JavaScriptEmitter.Inexpressible));
  }

  [Test]
  public void AConstantOfAUnionType_IsRefused()
    => Assert.That(Problems(Module(_numberOrText, [], [], new IrReturn(new IrConst(_numberOrText, 0)))), Is.Not.Empty);

  [Test]
  public void AnAbsentOptional_EqualsOnlyAnAbsentOne_OnEitherSide() {
    var optional = IrType.Union(IrType.Optional(IrType.I32), IrType.Bool);

    Assert.That(Equal(optional, IrBinaryOp.Equal, new(1, 3), new(1, null)), Is.False);
  }

  [Test]
  public void Legalization_KeepsEveryNode() {
    var legalized = Polypiler.Tests.Helpers.Support.HelperHarness.Legalized(Every());

    Assert.That(Problems(legalized), Is.Empty);
    Assert.That(IrTree.Nodes(legalized.Functions[0].Body).OfType<IrUnionValue>().Select(node => node.IsTested), Is.EqualTo(new[] { true, false }));
    Assert.That(IrInterpreter.Call(legalized, "Pass", [new IrUnion(0, 3)]), Is.EqualTo(new IrUnion(0, 3)));
  }

  [Test]
  public void AGenericInstantiation_ReplacesTheParametersInTheMembers() {
    var standIn = new IrInterfaceType("T");
    standIn.DefineMembers([], [], []);
    var open = IrType.Union(IrType.Object(standIn), IrType.String);
    var u = new IrLocal("u", open);
    var tag = new IrLocal("t", IrType.I32);
    var body = new IrBlock([
      new IrDeclare(tag, new IrUnionTagOf(new IrLocalRef(u))),
      new IrReturn(new IrUnionOf(open, 1, new IrUnionValue(new IrLocalRef(u), 1) { IsTested = true }))
    ]);
    var template = new IrGenericTemplate(new IrFunction("F<T>", true, open, [u], [tag], body), [IrType.Object(standIn)]);
    var instance = IrGenerics.Instantiate(template, [IrType.I32], "F<i32>", new IrModule("M", []));
    var closed = IrType.Union(IrType.I32, IrType.String);

    Assert.That((instance.ReturnType, instance.Parameters[0].Type), Is.EqualTo((closed, closed)));
    var made = (IrUnionOf)((IrReturn)instance.Body.Statements[1]).Value!;
    Assert.That((made.Type, made.Union, ((IrUnionValue)made.Value).IsTested), Is.EqualTo((closed, closed, true)));
  }

  private static IEnumerable<TestCaseData> Backends() {
    yield return new TestCaseData("csharp", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => CSharpEmitter.Check(module, CSharpProfile.Default)));
    yield return new TestCaseData("javascript", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => JavaScriptEmitter.Check(module, JavaScriptProfile.Default)));
    yield return new TestCaseData("python", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => PythonEmitter.Check(module, PythonProfile.Default)));
    yield return new TestCaseData("visualbasic", (Func<IrModule, IReadOnlyList<Diagnostic>>)(module => VisualBasicEmitter.Check(module, VisualBasicProfile.Vb6)));
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
  public void EveryBackend_RefusesTheTypeUntilItHasALowering(string backend, Func<IrModule, IReadOnlyList<Diagnostic>> check)
    => Assert.That(check(Every()), Is.Not.Empty, backend);

}
