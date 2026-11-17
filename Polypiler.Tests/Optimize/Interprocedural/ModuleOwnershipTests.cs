using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Interprocedural;

namespace Polypiler.Tests.Optimize.Interprocedural;

/// <summary>
/// The census the interprocedural passes share (#1151): which functions the module owns - every call a
/// direct call it contains - and why the others are not owned, in every combination of export, member,
/// generic instantiation, module initializer and a reference in code, in a lambda or in the initializer
/// of a global; the call sites; and recursion and code the module cannot name, which may re-enter a function.
/// </summary>
[TestFixture]
public sealed class ModuleOwnershipTests {

  private static readonly IrLocal _p = new("p", IrType.I32);
  private static readonly IrType _unary = IrType.Function(IrType.I32, IrType.I32);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  private static IrCall Call(string function, params IrExpr[] arguments) => new(function, IrType.I32, arguments);

  private static IrFunction F(string name, bool isPublic = false, params IrStmt[] body)
    => new(name, isPublic, IrType.I32, [_p], [], Block(body.Length == 0 ? [new IrReturn(new IrLocalRef(_p))] : body));

  [Test]
  public void APrivateFunctionCalledDirectly_IsOwned() {
    var module = new IrModule("M", [F("Main", true, new IrReturn(Call("G", new IrConst(IrType.I32, 1)))), F("G")]);
    var ownership = ModuleOwnership.Of(module);
    Assert.That(ownership.IsOwned("G"), Is.True);
    Assert.That(ownership.WhyNotOwned("G"), Is.Null);
    Assert.That(ownership.CallSitesOf("G"), Has.Count.EqualTo(1));
    Assert.That(ownership.CallSitesOf("G")[0], Is.EqualTo(new IrCallSite("Main", (IrCall)((IrReturn)module.Functions[0].Body.Statements[0]).Value!, false)));
    Assert.That(ownership.CallSitesOf("Main"), Is.Empty);
  }

  [Test]
  public void TheReasons_AreGivenInOrder() {
    var type = new IrClassType("C");
    type.DefineMembers([new IrConstructor("Ctor")], [new IrMethod("M", "Method", false)],
      [new IrProperty("P", IrType.I32) { Getter = new IrMethod("get_P", "Getter", false) }, new IrProperty("Q", IrType.I32)], []);
    var face = new IrInterfaceType("I");
    face.DefineMembers([new IrMethod("N", "Signature", false)], [new IrProperty("R", IrType.I32) { Setter = new IrMethod("set_R", "Setter", false) }], []);
    var generic = F("Generic") with { Origin = new IrGenericOrigin("Generic", ["T"], [IrType.I32], []) };
    var module = new IrModule("M", [
      F("Main", true), F("Init"), F("Ctor"), F("Method"), F("Getter"), F("Signature"), F("Setter"), generic, F("Public", true)
    ]) { Types = [type, face], Initializer = "Init" };
    var ownership = ModuleOwnership.Of(module);
    Assert.That(ownership.WhyNotOwned("Public"), Is.EqualTo(ModuleOwnership.Exported));
    Assert.That(ownership.WhyNotOwned("Init"), Is.EqualTo(ModuleOwnership.ModuleInitializer));
    foreach (var member in new[] { "Ctor", "Method", "Getter", "Signature", "Setter" })
      Assert.That(ownership.WhyNotOwned(member), Is.EqualTo(ModuleOwnership.Member), member);

    Assert.That(ownership.WhyNotOwned("Generic"), Is.EqualTo(ModuleOwnership.Generic));
    Assert.That(ownership.WhyNotOwned("Missing"), Is.EqualTo(ModuleOwnership.Unknown));
    Assert.That(ownership.IsOwned("Missing"), Is.False);
    Assert.Throws<ArgumentNullException>(() => ownership.WhyNotOwned(null!));
    Assert.Throws<ArgumentNullException>(() => ModuleOwnership.Of(null!));
  }

  /// <summary>A reference in code, in the body of a lambda and in the initializer of a global each takes the address; an exported function says so first.</summary>
  [TestCase("code", false)]
  [TestCase("lambda", false)]
  [TestCase("global", false)]
  [TestCase("code", true)]
  [TestCase("global", true)]
  public void AReference_TakesTheAddress(string where, bool exported) {
    var reference = new IrFunctionRef(_unary, "G");
    var holder = new IrLocal("h", _unary);
    IrStmt[] body = where switch {
      "code" => [new IrDeclare(holder, reference), new IrReturn(new IrConst(IrType.I32, 0))],
      "lambda" => [new IrReturn(new IrInvoke(IrType.I32, new IrLambda(IrType.Function(IrType.I32), [], [], [], Block(new IrReturn(new IrInvoke(IrType.I32, reference, [new IrConst(IrType.I32, 1)])))), []))],
      _ => [new IrReturn(new IrConst(IrType.I32, 0))]
    };
    var module = new IrModule("M", [F("Main", true, body), F("G", exported)]) {
      Globals = where == "global" ? [new IrGlobal("table", _unary, reference)] : []
    };
    var ownership = ModuleOwnership.Of(module);
    Assert.That(ownership.IsAddressTaken("G"), Is.True);
    Assert.That(ownership.WhyNotOwned("G"), Is.EqualTo(exported ? ModuleOwnership.Exported : ModuleOwnership.AddressTaken));
  }

  [Test]
  public void CallSites_InLambdasAndInitializers_AreCounted() {
    var lambda = new IrLambda(IrType.Function(IrType.I32), [], [], [], Block(new IrReturn(Call("G", new IrConst(IrType.I32, 2)))));
    var module = new IrModule("M", [F("Main", true, new IrReturn(new IrInvoke(IrType.I32, lambda, []))), F("G")]) {
      Globals = [new IrGlobal("seed", IrType.I32, Call("G", new IrConst(IrType.I32, 3)))]
    };
    var sites = ModuleOwnership.Of(module).CallSitesOf("G");
    Assert.That(sites.Select(site => (site.Caller, site.InLambda)), Is.EqualTo(new (string?, bool)[] { ("Main", true), (null, false) }));
  }

  [Test]
  public void Recursion_DirectAndThroughOtherFunctions() {
    var module = new IrModule("M", [
      F("Self", false, new IrReturn(Call("Self", new IrLocalRef(_p)))),
      F("A", false, new IrReturn(Call("B", new IrLocalRef(_p)))),
      F("B", false, new IrReturn(Call("A", new IrLocalRef(_p)))),
      F("Caller", false, new IrReturn(Call("A", new IrLocalRef(_p)))),
      F("Leaf")
    ]);
    var ownership = ModuleOwnership.Of(module);
    Assert.That(ownership.IsRecursive("Self"), Is.True);
    Assert.That(ownership.IsRecursive("A"), Is.True);
    Assert.That(ownership.IsRecursive("Caller"), Is.False, "it reaches a cycle, but not itself");
    Assert.That(ownership.IsRecursive("Leaf"), Is.False);
  }

  /// <summary>What the module cannot name: a call through a value, a call of a name it has no function of, a statement that runs a resource's code; a construction of a class of the module runs its constructor, which is followed.</summary>
  [Test]
  public void UnknownCode_IsFoundWhereverItIsReached() {
    var callback = new IrLocal("c", _unary);
    var invoking = new IrFunction("Invoking", false, IrType.I32, [callback], [], Block(new IrReturn(new IrInvoke(IrType.I32, new IrLocalRef(callback), [new IrConst(IrType.I32, 1)]))));
    var type = new IrClassType("K");
    type.Define([]);
    type.DefineMembers([new IrConstructor("Make")], [], [], []);
    var make = new IrFunction("Make", false, IrType.Void, [], [], Block(new IrExprStmt(Call("Missing"))));
    var constructing = new IrFunction("Constructing", false, IrType.I32, [], [], Block(
      new IrExprStmt(new IrNew(IrType.Object(type), type.Constructors[0], [])), new IrReturn(new IrConst(IrType.I32, 0))));
    var resource = new IrLocal("r", IrType.Object(type));
    var disposing = new IrFunction("Disposing", false, IrType.I32, [], [resource], Block(
      new IrUsing(resource, new IrNull(IrType.Object(type)), new IrMethod("Dispose", "Make", false), Block()), new IrReturn(new IrConst(IrType.I32, 0))));
    var lambda = new IrLambda(IrType.Function(IrType.I32), [], [], [], Block(new IrReturn(new IrInvoke(IrType.I32, new IrLocalRef(callback), []))));
    var making = new IrFunction("Making", false, IrType.I32, [callback], [], Block(new IrExprStmt(lambda), new IrReturn(new IrConst(IrType.I32, 0))));
    var module = new IrModule("M", [invoking, make, constructing, disposing, making, F("Plain")]) { Types = [type] };
    var ownership = ModuleOwnership.Of(module);
    Assert.That(ownership.UnknownCodeReachedFrom("Invoking"), Is.EqualTo("Invoke in @Invoking"));
    Assert.That(ownership.UnknownCodeReachedFrom("Constructing"), Is.EqualTo("call of @Missing"));
    Assert.That(ownership.UnknownCodeReachedFrom("Disposing"), Is.EqualTo("Using in @Disposing"));
    Assert.That(ownership.UnknownCodeReachedFrom("Making"), Is.Null, "a lambda made but not run runs nothing");
    Assert.That(ownership.UnknownCodeReachedFrom("Plain"), Is.Null);
  }

}
