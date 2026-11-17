using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.IR.Intrinsics;
using Polypiler.Core.Legalize;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Intrinsics;

/// <summary>
/// The intrinsic registry (#106) on the fixtures of <c>tests/fixtures/intrinsics/registry/</c>: the loader,
/// the builder with its start-up checks, the check of a node against its contract, the verifier, the
/// evaluator and <see cref="ExpandIntrinsics"/>. The rows of the issue's test design are named where
/// they are tested; row 7 (the widths 65 and 7) waits for the arbitrary widths of #118.
/// </summary>
[TestFixture]
public sealed class RegistryTests {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "intrinsics", "registry");

  /// <summary>A fixture contract.</summary>
  internal static IntrinsicContract Fixture(string file)
    => ContractLoader.Load(file, SpecDocument.Parse("intrinsics/registry/" + file, File.ReadAllText(Path.Combine(_fixtures, file))).Root);

  /// <summary>The evaluator of the self-test: the value itself.</summary>
  internal sealed class Identity : IIntrinsicEvaluator {

    public EvaluationResult Evaluate(IntrinsicCall call) => EvaluationResult.Of(call.Arguments[0]);

  }

  /// <summary>The decomposition of the self-test: <c>x | 0</c>.</summary>
  internal sealed class OrZero : IIntrinsicDecomposition {

    public IrExpr? Expand(IrIntrinsic node, IIntrinsicLibrary library) => new IrBinary(IrBinaryOp.Or, node.Arguments[0], new IrConst(node.Type, 0));

  }

  /// <summary>The registry of the self-test with the contracts that need no decomposition.</summary>
  internal static IIntrinsicRegistry SelfTest() => new IntrinsicRegistryBuilder()
    .Add(Fixture("identity.yaml"), new Identity(), new OrZero())
    .Add(Fixture("runtime.yaml"), new InterpretedEvaluator(IrIntrinsicOp.PopCount), null)
    .Add(Fixture("attribute.yaml"), new InterpretedEvaluator(IrIntrinsicOp.RotateLeft), new OrZero())
    .Add(Fixture("pair.yaml"), new Identity(), null)
    .Add(Fixture("echo.yaml"), new Identity(), null)
    .Build();

  private static IrModule Module(IrIntrinsicOp op, IrType type, params IReadOnlyList<IrExpr> extra) {
    var x = new IrLocal("x", type);
    return new IrModule("M", [new IrFunction("F", true, type, [x], [], new IrBlock([new IrReturn(new IrIntrinsic(op, type, [new IrLocalRef(x), .. extra]))]))]);
  }

  // ---------------------------------------------------------------- rows 1, 2, 4: load, evaluate, expand

  [Test]
  public void AValidContract_IsLoadedAndQueried() {
    var registry = SelfTest();
    var contract = registry.Get(new("Abs"));
    Assert.That((contract.Group, contract.Exactness, contract.Spelling), Is.EqualTo(("IntMath", IntrinsicExactness.Exact, "Abs")));
    Assert.That(contract.TypeParameters, Is.EqualTo(new[] { new IntrinsicTypeParameter("T", IntrinsicConstraint.Unsigned) }));
    Assert.That((contract.Algebra.Single(), contract.LowersTo.Single(), contract.LaneLiftable, contract.RequiresRuntime), Is.EqualTo((IntrinsicAlgebra.Idempotent, "IR.op.or", true, false)));
    Assert.That(contract.Facts, Is.EqualTo(new IntrinsicFacts((0, 255), 0, 0)));
    Assert.That((contract.Algorithm, contract.Source, contract.Vectors.Count, contract.Effects.Single(), contract.TolerancePolicy), Is.EqualTo(("x", "registry self-test", 3, "pure", (string?)null)));
    var result = registry.Evaluator(contract.Id).Evaluate(new(contract.Id, IrType.U32, [IrType.U32], [0xDEADBEEFu]));
    Assert.That((result.Values.Single(), result.Trap), Is.EqualTo(((object)0xDEADBEEFu, (IrTrap?)null)));
  }

  [Test]
  public void Expansion_ReplacesTheIntrinsicByItsDecomposition() {
    var expanded = ExpandIntrinsics.Run(Module(IrIntrinsicOp.Abs, IrType.U8), SelfTest(), _ => true);
    var returned = ((IrReturn)expanded.Functions[0].Body.Statements[0]).Value;
    Assert.That(returned, Is.EqualTo(new IrBinary(IrBinaryOp.Or, new IrLocalRef(new IrLocal("x", IrType.U8)), new IrConst(IrType.U8, 0))));
    Assert.That(IrInterpreter.Call(expanded, "F", [(byte)0x7F]), Is.EqualTo((byte)0x7F));
  }

  [Test]
  public void Expansion_LeavesWhatThePredicateDoesNotSelect_TheRuntimeAndTheUnknown() {
    var registry = SelfTest();
    var abs = Module(IrIntrinsicOp.Abs, IrType.U8);
    Assert.That(ExpandIntrinsics.Run(abs, registry, _ => false), Is.SameAs(abs));
    var popCount = Module(IrIntrinsicOp.PopCount, IrType.I32);
    Assert.That(ExpandIntrinsics.Run(popCount, registry, _ => true), Is.SameAs(popCount), "row 4: requiresRuntime leaves the node");
    var unknown = Module(IrIntrinsicOp.Parity, IrType.I32);
    Assert.That(ExpandIntrinsics.Run(unknown, registry, _ => true), Is.SameAs(unknown));
    Assert.That(() => ExpandIntrinsics.Run(null!, registry, _ => true), Throws.ArgumentNullException);
    Assert.That(() => ExpandIntrinsics.Run(abs, null!, _ => true), Throws.ArgumentNullException);
    Assert.That(() => ExpandIntrinsics.Rewrite(registry, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void ARuntimeContract_WithoutDecomposition_IsAccepted() {
    var registry = SelfTest();
    Assert.That(registry.Decomposition(new("PopCount")), Is.Null);
    Assert.That(registry.Get(new("PopCount")).RequiresRuntime, Is.True);
  }

  // ---------------------------------------------------------------- row 3: the constraints

  private static readonly IrType[] _scalars = [IrType.Bool, IrType.I8, IrType.U8, IrType.I32, IrType.U64, IrType.F32, IrType.F64, IrType.String];

  [TestCase(IntrinsicConstraint.Integer, new[] { "i8", "u8", "i32", "u64" })]
  [TestCase(IntrinsicConstraint.Unsigned, new[] { "u8", "u64" })]
  [TestCase(IntrinsicConstraint.Signed, new[] { "i8", "i32" })]
  [TestCase(IntrinsicConstraint.Float, new[] { "f32", "f64" })]
  [TestCase(IntrinsicConstraint.Any, new[] { "bool", "i8", "u8", "i32", "u64", "f32", "f64" })]
  public void EachConstraint_AdmitsItsTypes(IntrinsicConstraint constraint, string[] admitted) {
    var parameter = new IntrinsicTypeParameter("T", constraint);
    Assert.That(_scalars.Where(parameter.Admits).Select(type => type.ToString()), Is.EqualTo(admitted));
    Assert.That(() => parameter.Admits(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void AnOperandTypeOutsideTheContract_IsPLY0252() {
    var registry = SelfTest();
    var diagnostics = IntrinsicVerifier.Verify(Module(IrIntrinsicOp.Abs, IrType.F32), registry);
    Assert.That(diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Is.EqualTo(new[] { ("PLY0252", "intrinsic 'Abs' does not admit f32 as operand 0") }));
    Assert.That(IntrinsicVerifier.Verify(Module(IrIntrinsicOp.Abs, IrType.I32), registry).Single().Message, Is.EqualTo("intrinsic 'Abs' does not admit i32 as operand 0"));
    Assert.That(IntrinsicVerifier.Verify(Module(IrIntrinsicOp.Abs, IrType.U32), registry), Is.Empty);
  }

  [Test]
  public void ANodeOfTheWrongArity_OrResult_IsPLY0252() {
    var contract = SelfTest().Get(new("Abs"));
    var x = new IrLocalRef(new IrLocal("x", IrType.U8));
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U8, []))!.Message, Is.EqualTo("intrinsic 'Abs' does not admit no value as operand 0"));
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U8, [x, x]))!.Message, Is.EqualTo("intrinsic 'Abs' does not admit u8 as operand 1"));
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.U16, [x]))!.Message, Is.EqualTo("intrinsic 'Abs' does not admit u16 as result"));
    Assert.That(() => contract.Check(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void AFixedTypeAndAnArray_AreCheckedAsTheyAreWritten() {
    var contract = ContractConformanceTests.Read(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "intrinsics", "endian", "loadlittle.yaml"));
    var bytes = new IrLocalRef(new IrLocal("b", IrType.Array(IrType.U8)));
    var index = new IrLocalRef(new IrLocal("i", IrType.I32));
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [bytes, index])), Is.Null);
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [index, index]))!.Message, Does.EndWith("i32 as operand 0"));
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [bytes, bytes]))!.Message, Does.EndWith("u8[] as operand 1"));
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.LoadLittle, IrType.U32, [new IrLocalRef(new IrLocal("c", IrType.Array(IrType.I8))), index]))!.Message, Does.EndWith("i8[] as operand 0"));
  }

  [Test]
  public void ATypeParameterStandsForOneType() {
    var contract = ContractConformanceTests.Read(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "intrinsics", "intmath", "min.yaml"));
    IrExpr Of(IrType type) => new IrLocalRef(new IrLocal("x", type));
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [Of(IrType.I32), Of(IrType.I32)])), Is.Null);
    Assert.That(contract.Check(new IrIntrinsic(IrIntrinsicOp.Min, IrType.I32, [Of(IrType.I32), Of(IrType.U32)]))!.Message, Does.EndWith("u32 as operand 1"));
  }

  // ---------------------------------------------------------------- row 5: the empty registry

  [Test]
  public void AnEmptyRegistry_HasNothing() {
    var registry = new IntrinsicRegistryBuilder().Build();
    Assert.That(registry.All, Is.Empty);
    Assert.That(registry.TryGet(new("Abs"), out var contract), Is.False);
    Assert.That(contract, Is.Null);
    Assert.That(registry.TryResolve("Abs", out _), Is.False);
    Assert.That(() => registry.Get(new("Abs")), Throws.TypeOf<IntrinsicRegistryException>().With.Property(nameof(IntrinsicRegistryException.Diagnostic)).Property(nameof(Diagnostic.Code)).EqualTo("PLY0250"));
    Assert.That(() => registry.Evaluator(new("Abs")), Throws.TypeOf<IntrinsicRegistryException>());
    Assert.That(() => registry.Decomposition(new("Abs")), Throws.TypeOf<IntrinsicRegistryException>());
    Assert.That(() => registry.TryResolve(null!, out _), Throws.ArgumentNullException);
  }

  // ---------------------------------------------------------------- row 6: attributes

  [TestCase(0, null)]
  [TestCase(7, null)]
  [TestCase(8, "intrinsic 'RotateLeft': attribute 'count' = 8 is outside [0, 7]")]
  [TestCase(-1, "intrinsic 'RotateLeft': attribute 'count' = -1 is outside [0, 7]")]
  public void AnAttributeOnInsideAndOutsideItsRange(int count, string? message) {
    var diagnostics = IntrinsicVerifier.Verify(Module(IrIntrinsicOp.RotateLeft, IrType.U32, new IrConst(IrType.I32, count)), SelfTest());
    Assert.That(diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Is.EqualTo(message is null ? [] : new[] { ("PLY0255", message) }));
  }

  [Test]
  public void AnAttributeThatIsNoConstantOfItsType_IsPLY0252() {
    var registry = SelfTest();
    Assert.That(IntrinsicVerifier.Verify(Module(IrIntrinsicOp.RotateLeft, IrType.U32, new IrLocalRef(new IrLocal("n", IrType.I32))), registry).Single().Message,
      Is.EqualTo("intrinsic 'RotateLeft' does not admit a non-constant i32 as attribute 'count'"));
    Assert.That(IntrinsicVerifier.Verify(Module(IrIntrinsicOp.RotateLeft, IrType.U32, new IrConst(IrType.I64, 1)), registry).Single().Message,
      Is.EqualTo("intrinsic 'RotateLeft' does not admit i64 as attribute 'count'"));
  }

  // ---------------------------------------------------------------- rows 8 to 13: the errors

  [Test]
  public void AnUnregisteredIntrinsic_IsPLY0250() {
    var global = new IrGlobal("G", IrType.I32, new IrIntrinsic(IrIntrinsicOp.Parity, IrType.I32, [new IrConst(IrType.U32, 3)]));
    var module = Module(IrIntrinsicOp.Parity, IrType.I32) with { Globals = [global] };
    var diagnostics = IntrinsicVerifier.Verify(module, SelfTest());
    Assert.That(diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Is.EqualTo(new[] { ("PLY0250", "unknown intrinsic 'Parity'"), ("PLY0250", "unknown intrinsic 'Parity'") }));
    Assert.That(() => IntrinsicVerifier.Verify(null!, SelfTest()), Throws.ArgumentNullException);
    Assert.That(() => IntrinsicVerifier.Verify(module, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void AContractThatViolatesTheSchema_IsPLY0251NamingFileAndProperty() {
    var failure = Assert.Throws<IntrinsicRegistryException>(() => Fixture("badexactness.yaml"))!;
    Assert.That(failure.Diagnostic.Code, Is.EqualTo("PLY0251"));
    Assert.That(failure.Diagnostic.Message, Does.Contain("badexactness.yaml").And.Contain("'exactness'").And.Contain("roughly"));
    Assert.That(failure.Message, Does.StartWith("PLY0251: "));
  }

  [Test]
  public void TheSameIdTwice_IsPLY0253() {
    var builder = new IntrinsicRegistryBuilder().Add(Fixture("identity.yaml"), new Identity(), new OrZero()).Add(Fixture("identity.yaml"), new Identity(), new OrZero());
    Assert.That(() => builder.Build(), Throws.TypeOf<IntrinsicRegistryException>().With.Message.EqualTo("PLY0253: intrinsic 'Abs' is registered twice"));
  }

  [Test]
  public void AContractWithoutFallback_IsPLY0254()
    => Assert.That(() => new IntrinsicRegistryBuilder().Add(Fixture("nofallback.yaml"), new Identity(), null).Build(),
      Throws.TypeOf<IntrinsicRegistryException>().With.Message.EqualTo("PLY0254: intrinsic 'NoFallback' has no fallback"));

  [Test]
  public void ADecompositionCycle_IsPLY0256NamingThePath() {
    var builder = new IntrinsicRegistryBuilder().Add(Fixture("cycle-b.yaml"), new Identity(), new OrZero()).Add(Fixture("cycle-a.yaml"), new Identity(), new OrZero());
    Assert.That(() => builder.Build(), Throws.TypeOf<IntrinsicRegistryException>().With.Message.EqualTo("PLY0256: intrinsic 'CycleA' lowers to itself through CycleA -> CycleB -> CycleA"));
    var alone = new IntrinsicRegistryBuilder().Add(Fixture("cycle-a.yaml"), new Identity(), new OrZero()).Build();
    Assert.That(alone.All.Single().LowersTo, Is.EqualTo(new[] { "IR.op.or", "INTRINSICS.CycleB" }), "a target outside the registry is no cycle");
  }

  [Test]
  public void ACycleThroughAnother_IsFoundFromEveryMember() {
    IntrinsicContract Lowering(string name, params string[] targets) => Fixture("identity.yaml") with { Id = new(name), LowersTo = [.. targets.Select(target => "INTRINSICS." + target)] };
    var builder = new IntrinsicRegistryBuilder()
      .Add(Lowering("A", "B", "C"), new Identity(), new OrZero())
      .Add(Lowering("B", "D"), new Identity(), new OrZero())
      .Add(Lowering("C", "D", "B"), new Identity(), new OrZero())
      .Add(Lowering("D", "C"), new Identity(), new OrZero());
    Assert.That(() => builder.Build(), Throws.TypeOf<IntrinsicRegistryException>().With.Message.EqualTo("PLY0256: intrinsic 'B' lowers to itself through B -> D -> C -> B"));
    var diamond = new IntrinsicRegistryBuilder()
      .Add(Lowering("A", "B", "C"), new Identity(), new OrZero())
      .Add(Lowering("B", "D"), new Identity(), new OrZero())
      .Add(Lowering("C", "D"), new Identity(), new OrZero())
      .Add(Lowering("D"), new Identity(), new OrZero());
    Assert.That(diamond.Build().All, Has.Count.EqualTo(4));
  }

  [Test]
  public void AMissingEvaluatorOrContract_IsRefused() {
    var builder = new IntrinsicRegistryBuilder();
    Assert.That(() => builder.Add(Fixture("identity.yaml"), null!, new OrZero()), Throws.ArgumentNullException);
    Assert.That(() => builder.Add(null!, new Identity(), new OrZero()), Throws.ArgumentNullException);
  }

  // ---------------------------------------------------------------- rows 14 to 16

  [Test]
  public void SeveralResults_AreOneTupleWithNamedProjections() {
    var registry = SelfTest();
    var pair = registry.Get(new("Pair"));
    Assert.That(pair.ResultNames, Is.EqualTo(new[] { "First", "Second" }));
    Assert.That(registry.Get(new("Abs")).ResultNames, Is.EqualTo(new[] { "Result" }));
    var evaluated = registry.Evaluator(pair.Id).Evaluate(new(pair.Id, IrType.I32, [IrType.I32, IrType.I32], [1, 2]));
    Assert.That(evaluated.Values, Is.EqualTo(new object[] { 1 }));
    Assert.That(pair.Check(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.Bool, [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2)])), Is.Null, "the type of a tuple node is #120's");
  }

  [Test]
  public void ADottedSpelling_ResolvesToItsContract() {
    var registry = SelfTest();
    Assert.That(registry.TryResolve("Test.Echo", out var echo), Is.True);
    Assert.That(echo!.Id, Is.EqualTo(new IntrinsicId("TestEcho")));
    Assert.That(registry.TryResolve("TestEcho", out var byName) && ReferenceEquals(byName, echo), Is.True);
    Assert.That(registry.TryResolve("Test.Other", out _), Is.False);
    Assert.That(new IntrinsicId("TestEcho").ToString(), Is.EqualTo("TestEcho"));
  }

  [Test]
  public void TheOrderOfAll_DoesNotDependOnTheOrderOfAdding() {
    var files = new[] { "identity.yaml", "runtime.yaml", "pair.yaml", "echo.yaml" };
    IReadOnlyList<string> Built(IEnumerable<string> order) {
      var builder = new IntrinsicRegistryBuilder();
      foreach (var file in order)
        builder.Add(Fixture(file), new Identity(), file == "identity.yaml" ? new OrZero() : null);
      return [.. builder.Build().All.Select(contract => contract.Id.Name)];
    }

    Assert.That(Built(files), Is.EqualTo(Built(files.Reverse())));
    Assert.That(Built(files), Is.EqualTo(new[] { "Abs", "Pair", "PopCount", "TestEcho" }));
  }

  // ---------------------------------------------------------------- the evaluator of the interpreter

  [Test]
  public void TheInterpretedEvaluator_GivesValuesAndTraps() {
    var evaluator = new InterpretedEvaluator(IrIntrinsicOp.DivFloor);
    var id = IntrinsicId.Of(IrIntrinsicOp.DivFloor);
    Assert.That(evaluator.Op, Is.EqualTo(IrIntrinsicOp.DivFloor));
    Assert.That(evaluator.Evaluate(new(id, IrType.I32, [IrType.I32, IrType.I32], [-7, 2])).Values, Is.EqualTo(new object[] { -4 }));
    Assert.That(evaluator.Evaluate(new(id, IrType.I32, [IrType.I32, IrType.I32], [-7, 0])), Is.EqualTo(EvaluationResult.Trapped(IrTrap.DivideByZero)).Using<EvaluationResult>((a, b) => a.Trap == b.Trap && a.Values.Count == b.Values.Count));
    Assert.That(() => evaluator.Evaluate(null!), Throws.ArgumentNullException);
  }

  // ---------------------------------------------------------------- the literals of the vectors

  [Test]
  public void Literals_AreValuesOfTheirTypes() {
    JsonNode? Json(string text) => JsonNode.Parse(text);
    Assert.That(ContractLiterals.Value(IrType.U32, Json("\"0xDEADBEEF\"")), Is.EqualTo(0xDEADBEEFu));
    Assert.That(ContractLiterals.Value(IrType.I8, Json("\"0x80\"")), Is.EqualTo((sbyte)-128));
    Assert.That(ContractLiterals.Value(IrType.I8, Json("-1")), Is.EqualTo((sbyte)-1));
    Assert.That(ContractLiterals.Value(IrType.U8, Json("255")), Is.EqualTo((byte)255));
    Assert.That(ContractLiterals.Value(IrType.I16, Json("-2")), Is.EqualTo((short)-2));
    Assert.That(ContractLiterals.Value(IrType.U16, Json("\"0xFFFF\"")), Is.EqualTo((ushort)0xFFFF));
    Assert.That(ContractLiterals.Value(IrType.I32, Json("-5")), Is.EqualTo(-5));
    Assert.That(ContractLiterals.Value(IrType.I64, Json("\"0xFFFFFFFFFFFFFFFF\"")), Is.EqualTo(-1L));
    Assert.That(ContractLiterals.Value(IrType.U64, Json("\"0xFFFFFFFFFFFFFFFF\"")), Is.EqualTo(ulong.MaxValue));
    Assert.That(ContractLiterals.Value(IrType.F32, Json("\"0x3F800000\"")), Is.EqualTo(1.0f));
    Assert.That(ContractLiterals.Value(IrType.F32, Json("2")), Is.EqualTo(2.0f));
    Assert.That(ContractLiterals.Value(IrType.F64, Json("\"0x3FF0000000000000\"")), Is.EqualTo(1.0));
    Assert.That(ContractLiterals.Value(IrType.F64, Json("-3")), Is.EqualTo(-3.0));
    Assert.That(ContractLiterals.Value(IrType.Bool, Json("1")), Is.True);
    Assert.That(ContractLiterals.Value(IrType.Bool, Json("0")), Is.False);
    Assert.That(ContractLiterals.Value(IrType.String, Json("[72, 105]")), Is.EqualTo("Hi"));
    Assert.That(ContractLiterals.Value(IrType.Array(IrType.U8), Json("[1, \"0xFF\"]")), Is.EqualTo(new byte[] { 1, 255 }));
    foreach (var element in new[] { IrType.I8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64, IrType.F32, IrType.F64 })
      Assert.That(ContractLiterals.Value(IrType.Array(element), Json("[1]")), Has.Length.EqualTo(1));
  }

  [Test]
  public void ALiteralOfAnotherKind_IsRefused() {
    JsonNode? Json(string text) => JsonNode.Parse(text);
    Assert.That(() => ContractLiterals.Value(IrType.Bool, Json("2")), Throws.ArgumentException);
    Assert.That(() => ContractLiterals.Value(IrType.String, Json("\"Hi\"")), Throws.ArgumentException);
    Assert.That(() => ContractLiterals.Value(IrType.Array(IrType.U8), Json("1")), Throws.ArgumentException);
    Assert.That(() => ContractLiterals.Value(IrType.U8, Json("\"twelve\"")), Throws.ArgumentException);
    Assert.That(() => ContractLiterals.Value(IrType.U8, Json("\"0x\"")), Throws.ArgumentException);
    Assert.That(() => ContractLiterals.Value(IrType.U8, null), Throws.ArgumentException.With.Message.Contains("null"));
    Assert.That(() => ContractLiterals.Value(IrType.Array(IrType.String), Json("[]")), Throws.ArgumentException);
    Assert.That(() => ContractLiterals.Value(null!, Json("1")), Throws.ArgumentNullException);
  }

  [Test]
  public void TheTypesOfTheDocuments_AreTheScalarsOfTheIR() {
    Assert.That(ContractTypes.Scalar("TYPES.u32"), Is.EqualTo(IrType.U32));
    Assert.That(ContractTypes.Scalar("TYPES.str"), Is.EqualTo(IrType.String));
    Assert.That(ContractTypes.Scalar("TYPES.u24"), Is.Null);
    Assert.That(ContractTypes.Scalar("T"), Is.Null);
  }

  // ---------------------------------------------------------------- the catalog of the repository

  [Test]
  public void TheCatalog_RefusesAContractOfNoOperation() {
    var failure = Assert.Throws<IntrinsicRegistryException>(() => IntrinsicCatalog.Build([Fixture("nofallback.yaml")]))!;
    Assert.That(failure.Diagnostic.Message, Is.EqualTo("unknown intrinsic 'NoFallback'"));
    Assert.That(() => IntrinsicCatalog.Build(null!), Throws.ArgumentNullException);
    Assert.That(() => IntrinsicCatalog.Operation(null!), Throws.ArgumentNullException);
    Assert.That(IntrinsicCatalog.Library(IrIntrinsicOp.StrLength), Is.Null);
    Assert.That(IntrinsicCatalog.Library(IrIntrinsicOp.PopCount), Is.Not.Null);
    Assert.That(IntrinsicCatalog.Library(IrIntrinsicOp.Exp), Is.Not.Null);
  }

}
