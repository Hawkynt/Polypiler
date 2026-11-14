using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Records;

/// <summary>
/// The passes between the frontends and the backends on value records: the optimizer and its
/// rules see into a field store, count it as a write of its variable and keep the types of the
/// module; legalization keeps them too.
/// </summary>
[TestFixture]
public sealed class RecordPassTests {

  private const string Holder = "public struct H { public uint V; public uint W; } ";

  /// <summary>A rotate spelled out as the value of a field store is recognized there, and the store is rebuilt around it.</summary>
  [Test]
  public void Optimizer_RewritesInsideAFieldStore() {
    var module = PrototypeFixture.Import("C.cs", Holder + "public static class C { public static uint F(uint x) { var h = new H(); h.V = (x << 3) | (x >> 29); return h.V; } }");
    var result = Optimizer.Run(module, new OptimizerOptions { Verify = true });
    Assert.That(result.Applications.Select(application => application.Id), Is.EqualTo(new[] { "O0100" }));
    Assert.That(result.Module.Types, Is.EqualTo(module.Types));
    var store = result.Module.Functions[0].Body.Statements.OfType<IrFieldStore>().Single();
    Assert.That(store.Value, Is.TypeOf<IrIntrinsic>().With.Property("Op").EqualTo(IrIntrinsicOp.RotateLeft));
    Assert.That(IrInterpreter.Call(result.Module, "F", [1u]), Is.EqualTo(8u));
  }

  /// <summary>A variable whose field is written is no longer stable, nor are the variables whose fields hold the place; an array element belongs to no variable.</summary>
  [Test]
  public void FieldStore_IsAWriteOfItsVariable() {
    var module = PrototypeFixture.Import("C.cs", Holder + "public struct O { public H In; } "
      + "public static class C { public static uint F(H[] a) { var h = new H(); var o = new O(); var k = h; o.In.W = 1; a[0].V = 2; return h.V + k.V + o.In.W; } }");
    var function = module.Functions[0];
    var context = new RuleContext(function, RuleTarget.Neutral);
    var locals = function.Locals.ToDictionary(local => local.Name);
    Assert.That((context.IsStable(locals["h"]), context.IsStable(locals["k"]), context.IsStable(locals["o"])), Is.EqualTo((true, true, false)));
    Assert.That(IrStatements.Writes(function.Body).Select(local => local.Name), Is.EquivalentTo(new[] { "h", "o", "k" }));
    var stores = function.Body.Statements.OfType<IrFieldStore>().ToList();
    Assert.That(stores.Select(store => IrStatements.Root(store.Target)?.Name), Is.EqualTo(new[] { "o", null }));
    Assert.That(IrStatements.Expressions(stores[0]), Has.Count.EqualTo(2));
  }

  /// <summary>Trees of record nodes compare node by node and are rebuilt with new operands.</summary>
  [Test]
  public void Shapes_OfRecordNodes_CompareAndRebuild() {
    var type = RecordModules.InnerType;
    var v = new IrLocal("v", type);
    IrExpr A(long value) => new IrConst(IrType.I64, value);
    IrExpr New(long value) => new IrStructNew(type, [A(value), new IrConst(IrType.Bool, 0)]);
    var load = new IrFieldLoad(new IrLocalRef(v), RecordModules.Inner.Fields[0]);
    var equals = new IrStructEquals(New(1), new IrLocalRef(v));
    Assert.That(IrShape.Same(New(1), New(1)), Is.True);
    Assert.That(IrShape.Same(New(1), New(2)), Is.False);
    Assert.That(IrShape.Same(new IrStructDefault(type), new IrStructDefault(type)), Is.True);
    Assert.That(IrShape.Same(load, new IrFieldLoad(new IrLocalRef(v), RecordModules.Inner.Fields[0])), Is.True);
    Assert.That(IrShape.Same(load, new IrFieldLoad(new IrLocalRef(v), new IrField("A", IrType.I64, false))), Is.False);
    Assert.That(IrShape.Same(equals, new IrStructEquals(New(1), new IrLocalRef(v))), Is.True);
    Assert.That(IrShape.Same(equals, new IrStructEquals(New(2), new IrLocalRef(v))), Is.False);
    Assert.That(IrShape.Operands(equals), Has.Count.EqualTo(2));
    Assert.That(IrShape.Nodes(New(3)).Count(), Is.EqualTo(3));
    var w = new IrLocal("w", type);
    Assert.That(IrShape.Same(IrShape.Substitute(load, v, new IrLocalRef(w)), new IrFieldLoad(new IrLocalRef(w), RecordModules.Inner.Fields[0])), Is.True);
    Assert.That(IrShape.Same(IrShape.Substitute(equals, v, new IrLocalRef(w)), new IrStructEquals(New(1), new IrLocalRef(w))), Is.True);
    Assert.That(IrShape.With(New(1), [A(5), new IrConst(IrType.Bool, 1)]), Is.TypeOf<IrStructNew>());
    Assert.That(IrShape.IsSideEffectFree(New(1)), Is.True);
    Assert.That(IrShape.IsTrapFree(load), Is.False);
  }

  /// <summary>Legalization keeps the types of a module and walks into every record node, also where it replaces an operation.</summary>
  [Test]
  public void Legalization_KeepsTheTypes() {
    var module = RecordModules.Everything();
    Assert.That(FloatLegalization.Lower(module, () => throw new AssertionException("nothing to replace"), _ => true), Is.SameAs(module).Or.Property("Types").EqualTo(module.Types));
    var sine = PrototypeFixture.Import("C.cs", "public record struct P(double X); public static class C { public static double F(double x) { var p = new P(); p.X = System.Math.Sin(x); "
      + "return new P { X = System.Math.Sin(p.X) }.X + (p == new P(1) ? 0 : 1); } }");
    var lowered = CSharpEmitter.Legalize(sine, () => Polypiler.Tests.Float.Support.FloatImplementations.Module, CSharpProfile.Net100, FloatMode.Strict);
    Assert.That(lowered.Types, Is.EqualTo(sine.Types));
    Assert.That(lowered.Functions.Select(function => function.Name), Has.Some.StartsWith("Float"));
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    Assert.That(JavaScriptEmitter.Check(JavaScriptEmitter.Legalize(sine, () => Polypiler.Tests.Float.Support.FloatImplementations.Module, JavaScriptProfile.Default, FloatMode.Strict), JavaScriptProfile.Default), Is.Empty);
  }

}
