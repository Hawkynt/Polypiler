using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Recognize;

namespace Polypiler.Tests.Variant;

/// <summary>
/// The dynamic type of the IR on modules built by hand: the type and its tags, the rules of the
/// verifier, what the interpreter computes and traps with, the value at the boundary, the module
/// format and the walkers of the tree.
/// </summary>
[TestFixture]
public sealed class IrVariantTests {

  private static object? Call(IrModule module, string function, params object[] arguments) => IrInterpreter.Call(module, function, arguments);

  private static readonly IrLocal V = new("v", IrType.Variant);

  private static IrLocalRef R(IrLocal local) => new(local);

  private static IrModule Module(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => new("M", [new IrFunction("F", true, result, parameters, [], new IrBlock([new IrReturn(value)]))]);

  private static IReadOnlyList<string> Problems(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => [.. IrVerifier.Verify(Module(result, parameters, value)).Select(diagnostic => diagnostic.Message)];

  private static object? Run(IrExpr value, IReadOnlyList<IrLocal> parameters, params object[] arguments)
    => IrInterpreter.Call(Module(value.Type, parameters, value), "F", arguments);

  private static IrVariantOf Boxed(IrVariantTag tag, IrExpr? value = null) => new(tag, value);

  [Test]
  public void Type_IsADynamicScalarOfItsOwn() {
    Assert.That(IrType.Variant.ToString(), Is.EqualTo("variant"));
    Assert.That(IrType.Variant.IsVariant && IrType.Variant.IsStorable, Is.True);
    Assert.That(IrType.Variant.IsNumeric || IrType.Variant.IsNullable || IrType.Variant.IsOptionalValue, Is.False);
    Assert.That(IrType.Array(IrType.Variant).IsStorable, Is.True);
    Assert.That(IrType.I32.IsVariant, Is.False);
  }

  [TestCase(IrVariantTag.Empty, 0)]
  [TestCase(IrVariantTag.Null, 1)]
  [TestCase(IrVariantTag.I16, 2)]
  [TestCase(IrVariantTag.I32, 3)]
  [TestCase(IrVariantTag.F32, 4)]
  [TestCase(IrVariantTag.F64, 5)]
  [TestCase(IrVariantTag.Currency, 6)]
  [TestCase(IrVariantTag.Date, 7)]
  [TestCase(IrVariantTag.String, 8)]
  [TestCase(IrVariantTag.Bool, 11)]
  [TestCase(IrVariantTag.U8, 17)]
  [TestCase(IrVariantTag.I64, 20)]
  public void Tags_AreTheComVarEnumNumbers(IrVariantTag tag, int number) => Assert.That((int)tag, Is.EqualTo(number));

  [Test]
  public void Tags_HavePayloadTypes_AndTypesHaveTags() {
    Assert.That(IrVariants.Tags, Has.Count.EqualTo(12));
    Assert.That(IrVariants.Tags, Is.Ordered);
    Assert.That(IrVariants.PayloadType(IrVariantTag.Empty), Is.Null);
    Assert.That(IrVariants.PayloadType(IrVariantTag.Null), Is.Null);
    Assert.That(IrVariants.PayloadType(IrVariantTag.Currency), Is.EqualTo(IrType.I64));
    Assert.That(IrVariants.PayloadType(IrVariantTag.Date), Is.EqualTo(IrType.F64));
    foreach (var tag in IrVariants.Tags.Where(tag => tag is not (IrVariantTag.Empty or IrVariantTag.Null or IrVariantTag.Currency or IrVariantTag.Date)))
      Assert.That(IrVariants.TagOf(IrVariants.PayloadType(tag)!), Is.EqualTo(tag), tag.ToString());

    var colour = new IrEnumType("Colour", IrType.I32, [new("Red", 0)]);
    Assert.That(IrVariants.TagOf(IrType.Enum(colour)), Is.Null);
    Assert.That(IrVariants.TagOf(IrType.U32), Is.Null);
    Assert.That(IrVariants.TagOf(IrType.Array(IrType.I32)), Is.Null);
    Assert.That(() => IrVariants.TagOf(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Uses_FindsTheTypeInEveryPlace() {
    IrModule Of(IrType result, IrLocal[] parameters, IrLocal[] locals, IrStmt body, IrGlobal[]? globals = null)
      => new("M", [new IrFunction("F", true, result, parameters, locals, new IrBlock([body]))]) { Globals = globals ?? [] };

    var i = new IrLocal("i", IrType.I32);
    Assert.That(IrVariants.Uses(Of(IrType.I32, [i], [], new IrReturn(R(i)))), Is.False);
    Assert.That(IrVariants.Uses(Of(IrType.Variant, [], [], new IrReturn(Boxed(IrVariantTag.Empty)))), Is.True);
    Assert.That(IrVariants.Uses(Of(IrType.I32, [V], [], new IrReturn(new IrConst(IrType.I32, 0)))), Is.True);
    Assert.That(IrVariants.Uses(Of(IrType.Void, [], [V], new IrReturn(null))), Is.True);
    Assert.That(IrVariants.Uses(Of(IrType.Void, [], [], new IrExprStmt(Boxed(IrVariantTag.Null)))), Is.True);
    Assert.That(IrVariants.Uses(Of(IrType.Void, [], [], new IrReturn(null), [new IrGlobal("g", IrType.Variant, null)])), Is.True);
    Assert.That(() => IrVariants.Uses(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Value_CrossesTheBoundaryWithItsTag() {
    Assert.That(IrVariant.Empty.ToString(), Is.EqualTo("Empty"));
    Assert.That(IrVariant.Null, Is.EqualTo(new IrVariant(IrVariantTag.Null, null)));
    Assert.That(new IrVariant(IrVariantTag.F64, 1.5).ToString(), Is.EqualTo("F64 1.5"));
    Assert.That(Run(R(V), [V], new IrVariant(IrVariantTag.I16, (short)5)), Is.EqualTo(new IrVariant(IrVariantTag.I16, (short)5)));
    Assert.That(Run(R(V), [V], new IrVariant(IrVariantTag.String, "a")), Is.EqualTo(new IrVariant(IrVariantTag.String, "a")));
    Assert.That(Run(R(V), [V], IrVariant.Empty), Is.EqualTo(IrVariant.Empty));
  }

  [Test]
  public void Value_OfTheWrongShape_IsRefusedAtTheBoundary() {
    Assert.That(() => Run(R(V), [V], 5), Throws.ArgumentException);
    Assert.That(() => Run(R(V), [V], new IrVariant(IrVariantTag.I16, 5)), Throws.ArgumentException);
    Assert.That(() => Run(R(V), [V], new IrVariant(IrVariantTag.Empty, 5)), Throws.ArgumentException);
  }

  [TestCase(IrVariantTag.Bool, "bool")]
  [TestCase(IrVariantTag.U8, "u8")]
  [TestCase(IrVariantTag.I16, "i16")]
  [TestCase(IrVariantTag.I32, "i32")]
  [TestCase(IrVariantTag.I64, "i64")]
  [TestCase(IrVariantTag.F32, "f32")]
  [TestCase(IrVariantTag.F64, "f64")]
  [TestCase(IrVariantTag.Currency, "i64")]
  [TestCase(IrVariantTag.Date, "f64")]
  public void EveryTag_BoxesAndUnboxesItsPayload(IrVariantTag tag, string payload) {
    var type = IrVariants.PayloadType(tag)!;
    Assert.That(type.ToString(), Is.EqualTo(payload));
    var x = new IrLocal("x", type);
    var round = new IrVariantValue(Boxed(tag, R(x)), tag);
    Assert.That(Problems(type, [x], round), Is.Empty);
    var value = type.Kind switch {
      IrTypeKind.Bool => true,
      IrTypeKind.U8 => (byte)200,
      IrTypeKind.I16 => (short)-5,
      IrTypeKind.I32 => int.MinValue,
      IrTypeKind.I64 => long.MaxValue,
      IrTypeKind.F32 => 1.5f,
      _ => (object)-0.25
    };
    Assert.That(Run(round, [x], value), Is.EqualTo(value));
    Assert.That(Run(new IrVariantTagOf(Boxed(tag, R(x))), [x], value), Is.EqualTo((int)tag));
  }

  [Test]
  public void String_BoxesAndUnboxes_NullIncluded() {
    var s = new IrLocal("s", IrType.String);
    var round = new IrVariantValue(Boxed(IrVariantTag.String, R(s)), IrVariantTag.String);
    Assert.That(Run(round, [s], "text"), Is.EqualTo("text"));
    Assert.That(Run(round, [s], [null!]), Is.Null);
  }

  [TestCase(IrVariantTag.Empty)]
  [TestCase(IrVariantTag.Null)]
  public void TagsWithoutPayload_HaveTheirTag(IrVariantTag tag)
    => Assert.That(Run(new IrVariantTagOf(Boxed(tag)), []), Is.EqualTo((int)tag));

  [TestCase(IrVariantTag.Empty)]
  [TestCase(IrVariantTag.Null)]
  [TestCase(IrVariantTag.I32)]
  public void Payload_OfAnotherTag_TrapsWithAnInvalidCast(IrVariantTag tag) {
    IrExpr value = tag == IrVariantTag.I32 ? Boxed(tag, new IrConst(IrType.I32, 7)) : Boxed(tag);
    var exception = Assert.Throws<IrTrapException>(() => Run(new IrVariantValue(value, IrVariantTag.I16), []))!;
    Assert.That(exception.Trap, Is.EqualTo(IrTrap.InvalidCast));
  }

  [Test]
  public void Payload_OfItsTag_IsTheValue_TestedOrNot() {
    var boxed = Boxed(IrVariantTag.I16, new IrConst(IrType.I16, -3));
    Assert.That(Run(new IrVariantValue(boxed, IrVariantTag.I16), []), Is.EqualTo((short)-3));
    Assert.That(Run(new IrVariantValue(boxed, IrVariantTag.I16) { IsTested = true }, []), Is.EqualTo((short)-3));
    Assert.That(new IrVariantValue(boxed, IrVariantTag.Empty).Type, Is.EqualTo(IrType.Void));
  }

  [Test]
  public void Verifier_RefusesEveryIllFormedNode() {
    var i = new IrLocal("i", IrType.I32);
    Assert.That(Problems(IrType.Variant, [i], Boxed(IrVariantTag.I16, R(i))), Has.Some.Contains("with a payload of the type i32, not i16"));
    Assert.That(Problems(IrType.Variant, [i], Boxed(IrVariantTag.Empty, R(i))), Has.Some.Contains("which has no payload, with one of the type i32"));
    Assert.That(Problems(IrType.Variant, [], Boxed(IrVariantTag.I32)), Has.Some.Contains("without its payload"));
    Assert.That(Problems(IrType.Variant, [], Boxed((IrVariantTag)99)), Has.Some.Contains("tag 99, which is none"));
    Assert.That(Problems(IrType.I32, [i], new IrVariantTagOf(R(i))), Has.Some.Contains("which is no variant"));
    Assert.That(Problems(IrType.I16, [i], new IrVariantValue(R(i), IrVariantTag.I16)), Has.Some.Contains("payload of a value of the type i32, which is no variant"));
    Assert.That(Problems(IrType.Void, [V], new IrVariantValue(R(V), IrVariantTag.Null)), Has.Some.Contains("the payload of the tag Null, which has none"));
    Assert.That(Problems(IrType.Void, [V], new IrVariantValue(R(V), (IrVariantTag)99)), Has.Some.Contains("which has none"));
    Assert.That(Problems(IrType.I16, [V], new IrVariantValue(R(V), IrVariantTag.I16)), Is.Empty);
  }

  [Test]
  public void Verifier_KeepsTheTypeOutOfFields_AndLetsArraysHoldIt() {
    var array = new IrLocal("a", IrType.Array(IrType.Variant));
    Assert.That(IrVerifier.Verify(new IrModule("M", [new IrFunction("F", true, IrType.Void, [array], [], new IrBlock([new IrReturn(null)]))])), Is.Empty, "an array of the dynamic type (#1567)");
    var record = new IrStructType("R");
    record.Define([new IrField("v", IrType.Variant, false)]);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Void, [], [], new IrBlock([new IrReturn(null)]))]) { Types = [record] };
    Assert.That(IrVerifier.Verify(module).Select(problem => problem.Message), Has.Some.Contains("has the type variant"));
    Assert.That(Problems(IrType.Variant, [], new IrConst(IrType.Variant, 0)), Is.Not.Empty);
  }

  /// <summary>A module that has every node of the type, in a function and in a global's initializer.</summary>
  private static IrModule Sink() {
    var x = new IrLocal("x", IrType.I16);
    var tag = new IrLocal("t", IrType.I32);
    var global = new IrGlobal("g", IrType.Variant, Boxed(IrVariantTag.Null));
    var body = new IrBlock([
      new IrDeclare(tag, new IrVariantTagOf(R(V))),
      new IrIf(new IrBinary(IrBinaryOp.Equal, R(tag), new IrConst(IrType.I32, 2)),
        new IrBlock([new IrReturn(Boxed(IrVariantTag.I16, new IrVariantValue(R(V), IrVariantTag.I16) { IsTested = true }))]), null),
      new IrReturn(new IrConditional(new IrBinary(IrBinaryOp.Equal, R(tag), new IrConst(IrType.I32, 0)), Boxed(IrVariantTag.I16, R(x)), new IrGlobalRef(global)))
    ]);
    return new IrModule("M", [new IrFunction("F", true, IrType.Variant, [V, x], [tag], body)]) { Globals = [global] };
  }

  [Test]
  public void Sink_IsWellTyped_AndRuns() {
    var module = Sink();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(Call(module, "F", new IrVariant(IrVariantTag.I16, (short)4), (short)0), Is.EqualTo(new IrVariant(IrVariantTag.I16, (short)4)));
    Assert.That(Call(module, "F", IrVariant.Empty, (short)9), Is.EqualTo(new IrVariant(IrVariantTag.I16, (short)9)));
    Assert.That(Call(module, "F", new IrVariant(IrVariantTag.String, "a"), (short)9), Is.EqualTo(IrVariant.Null));
  }

  [Test]
  public void ModuleFormat_KeepsEveryNode() {
    var module = Sink();
    var bytes = IrModuleFormat.Write(module);
    var read = IrModuleFormat.Read(bytes);
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(bytes));
    Assert.That(IrVerifier.Verify(read), Is.Empty);
    var nodes = IrTree.Nodes(read.Functions[0].Body).ToList();
    Assert.That(nodes.OfType<IrVariantValue>().Single().IsTested, Is.True);
    Assert.That(nodes.OfType<IrVariantOf>().Select(node => node.Tag), Is.EquivalentTo(new[] { IrVariantTag.I16, IrVariantTag.I16 }));
    Assert.That(read.Globals[0].Type, Is.EqualTo(IrType.Variant));
  }

  [Test]
  public void Tree_ListsAndReplacesTheOperands() {
    var boxed = Boxed(IrVariantTag.I32, new IrConst(IrType.I32, 1));
    var replacement = new IrConst(IrType.I32, 2);
    foreach (var (node, operands) in new (IrExpr, int)[] { (boxed, 1), (Boxed(IrVariantTag.Empty), 0), (new IrVariantTagOf(R(V)), 1), (new IrVariantValue(R(V), IrVariantTag.I32) { IsTested = true }, 1) }) {
      Assert.That(IrTree.Operands(node), Has.Count.EqualTo(operands));
      Assert.That(IrShape.Operands(node), Has.Count.EqualTo(operands));
    }

    Assert.That(IrTree.With(boxed, [replacement]), Is.EqualTo(Boxed(IrVariantTag.I32, replacement)).Using<IrExpr>(IrShape.Same));
    Assert.That(IrShape.With(boxed, [replacement]), Is.EqualTo(Boxed(IrVariantTag.I32, replacement)).Using<IrExpr>(IrShape.Same));
    var other = new IrLocal("w", IrType.Variant);
    Assert.That(IrShape.Same(IrTree.With(new IrVariantTagOf(R(V)), [R(other)]), new IrVariantTagOf(R(other))), Is.True);
    Assert.That(IrShape.Same(IrShape.With(new IrVariantTagOf(R(V)), [R(other)]), new IrVariantTagOf(R(other))), Is.True);
    var tested = new IrVariantValue(R(V), IrVariantTag.I32) { IsTested = true };
    Assert.That(((IrVariantValue)IrTree.With(tested, [R(other)])).IsTested, Is.True);
    Assert.That(((IrVariantValue)IrShape.With(tested, [R(other)])).Value, Is.EqualTo(R(other)));
    Assert.That(IrTree.With(Boxed(IrVariantTag.Empty), []), Is.EqualTo(Boxed(IrVariantTag.Empty)));
  }

  [Test]
  public void Shape_ComparesTagsPayloadsAndTests() {
    var one = new IrConst(IrType.I32, 1);
    Assert.That(IrShape.Same(Boxed(IrVariantTag.I32, one), Boxed(IrVariantTag.I32, new IrConst(IrType.I32, 1))), Is.True);
    Assert.That(IrShape.Same(Boxed(IrVariantTag.I32, one), Boxed(IrVariantTag.I32, new IrConst(IrType.I32, 2))), Is.False);
    Assert.That(IrShape.Same(Boxed(IrVariantTag.Empty), Boxed(IrVariantTag.Empty)), Is.True);
    Assert.That(IrShape.Same(Boxed(IrVariantTag.Empty), Boxed(IrVariantTag.Null)), Is.False);
    Assert.That(IrShape.Same(Boxed(IrVariantTag.Empty), Boxed(IrVariantTag.Empty, one)), Is.False);
    Assert.That(IrShape.Same(Boxed(IrVariantTag.Empty, one), Boxed(IrVariantTag.Empty)), Is.False);
    Assert.That(IrShape.Same(new IrVariantTagOf(R(V)), new IrVariantTagOf(R(V))), Is.True);
    var value = new IrVariantValue(R(V), IrVariantTag.I32);
    Assert.That(IrShape.Same(value, new IrVariantValue(R(V), IrVariantTag.I32)), Is.True);
    Assert.That(IrShape.Same(value, value with { IsTested = true }), Is.False);
    Assert.That(IrShape.Same(value, new IrVariantValue(R(V), IrVariantTag.I16)), Is.False);
  }

  [Test]
  public void Shape_ATestedPayloadNeverTraps_AnUntestedOneMay() {
    Assert.That(IrShape.IsTrapFreeNode(Boxed(IrVariantTag.Empty)), Is.True);
    Assert.That(IrShape.IsTrapFreeNode(new IrVariantTagOf(R(V))), Is.True);
    Assert.That(IrShape.IsTrapFreeNode(new IrVariantValue(R(V), IrVariantTag.I32) { IsTested = true }), Is.True);
    Assert.That(IrShape.IsTrapFreeNode(new IrVariantValue(R(V), IrVariantTag.I32)), Is.False);
    var trapping = new IrModule("M", [new IrFunction("F", true, IrType.I32, [V], [], new IrBlock([new IrReturn(new IrVariantValue(R(V), IrVariantTag.I32))]))]);
    var total = new IrModule("M", [new IrFunction("F", true, IrType.I32, [V], [], new IrBlock([new IrReturn(new IrVariantTagOf(R(V)))]))]);
    Assert.That(IrEffects.Of(trapping, "F").MayTrap, Is.True);
    Assert.That(IrEffects.Of(total, "F").MayTrap, Is.False);
  }

  [Test]
  public void Rewriters_RebuildTheOperands() {
    var module = Sink();
    var renamed = IrGenerics.Rename(module, new Dictionary<string, string> { ["F"] = "G" });
    Assert.That(renamed.Functions.Single().Name, Is.EqualTo("G"));
    Assert.That(IrVerifier.Verify(renamed), Is.Empty);
    Assert.That(Call(renamed, "G", IrVariant.Empty, (short)1), Is.EqualTo(new IrVariant(IrVariantTag.I16, (short)1)));
    var lowered = LibraryLegalization.Lower(module, () => new IrModule("Lib", []), (_, _) => null);
    Assert.That(IrVerifier.Verify(lowered), Is.Empty);
    Assert.That(Call(lowered, "F", IrVariant.Empty, (short)1), Is.EqualTo(new IrVariant(IrVariantTag.I16, (short)1)));
    Assert.That(FloatManifest.Operations(module), Is.Empty);
  }

}
