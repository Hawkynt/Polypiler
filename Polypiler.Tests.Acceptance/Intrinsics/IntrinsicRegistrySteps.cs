using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.IR.Intrinsics;
using Polypiler.Core.Legalize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Spec;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Intrinsics;

/// <summary>
/// The steps of <c>tests/features/intrinsics/int-registry.feature</c> (#106): the contracts of
/// <c>tests/fixtures/intrinsics/registry/</c> loaded, registered, checked and expanded in process.
/// </summary>
[Binding, Scope(Feature = "Intrinsic registry")]
public sealed class IntrinsicRegistrySteps {

  private static readonly string _fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "intrinsics", "registry");

  private readonly List<(string File, IIntrinsicDecomposition? Decomposition)> _files = [];

  private IIntrinsicRegistry? _registry;
  private Diagnostic? _failure;
  private IrModule? _module;
  private IReadOnlyList<Diagnostic> _diagnostics = [];

  /// <summary>A fixture contract, checked against what the scenario says of it.</summary>
  [Given("^the contract file " + StepText.Quoted + " declaring intrinsic " + StepText.Quoted + " in group " + StepText.Quoted + " over unsigned types with exactness mode " + StepText.Quoted + "$")]
  public void Contract(string file, string name, string group, string mode) {
    var contract = Load(file);
    Assert.That((contract.Id.Name, contract.Group, contract.TypeParameters.Single().Constraint, contract.Exactness.ToString().ToLowerInvariant()),
      Is.EqualTo((name, group, IntrinsicConstraint.Unsigned, mode)));
    this._files.Add((file, null));
  }

  /// <summary>The decomposition <c>x | 0</c> for the last contract.</summary>
  [Given("^an evaluator and the decomposition \"x \\| 0\" for it$")]
  public void OrZero() => this._files[^1] = (this._files[^1].File, new OrZeroDecomposition());

  /// <summary>A contract that declares neither decomposition nor runtime.</summary>
  [Given("^the contract file " + StepText.Quoted + " with no decomposition and requiresRuntime false$")]
  public void NoFallback(string file) {
    Assert.That(Load(file).RequiresRuntime, Is.False);
    this._files.Add((file, null));
  }

  /// <summary>A contract whose exactness the schema refuses.</summary>
  [Given("^the contract file " + StepText.Quoted + " with exactness mode " + StepText.Quoted + "$")]
  public void BadExactness(string file, string mode) {
    Assert.That(File.ReadAllText(Path.Combine(_fixtures, file)), Does.Contain($"mode: {mode}"));
    this._files.Add((file, null));
  }

  /// <summary>Loads the contracts and builds the registry, keeping the diagnostic of a failure.</summary>
  [When("^the registry is built$")]
  public void Build() {
    try {
      var builder = new IntrinsicRegistryBuilder();
      foreach (var (file, decomposition) in this._files)
        builder.Add(Load(file), new InterpretedEvaluator(IrIntrinsicOp.AbsWrap), decomposition);
      this._registry = builder.Build();
    } catch (IntrinsicRegistryException failure) {
      this._failure = failure.Diagnostic;
    }
  }

  /// <summary>Expands every intrinsic of a function that returns the operation of its parameter.</summary>
  [When("^ExpandIntrinsics runs for every intrinsic of a function returning " + StepText.Quoted + " of a (\\w+)$")]
  public void Expand(string op, string type)
    => this._module = ExpandIntrinsics.Run(Module(op, type), this._registry!, _ => true);

  /// <summary>Checks a function that returns the operation of its parameter.</summary>
  [When("^the intrinsic verifier checks a function returning " + StepText.Quoted + " of a (\\w+)$")]
  public void Verify(string op, string type) => this._diagnostics = IntrinsicVerifier.Verify(Module(op, type), this._registry!);

  /// <summary>The group of a contract of the registry.</summary>
  [Then("^Get\\(" + StepText.Quoted + "\\)\\.Group is " + StepText.Quoted + "$")]
  public void Group(string name, string group) => Assert.That(this._registry!.Get(new(name)).Group, Is.EqualTo(group));

  /// <summary>The value the registry's evaluator gives for a <c>u32</c>.</summary>
  [Then("^evaluating " + StepText.Quoted + " on u32 (0x[0-9A-F]+) gives (0x[0-9A-F]+)$")]
  public void Evaluate(string name, string input, string output) {
    var id = new IntrinsicId(name);
    var result = this._registry!.Evaluator(id).Evaluate(new(id, IrType.U32, [IrType.U32], [Hex(input)]));
    Assert.That(result.Values.Single(), Is.EqualTo(Hex(output)));
  }

  /// <summary>What the expanded function returns.</summary>
  [Then("^the function returns \"x \\| 0\"$")]
  public void Returns() {
    var value = ((IrReturn)this._module!.Functions[0].Body.Statements[0]).Value;
    Assert.That(value, Is.TypeOf<IrBinary>().With.Property(nameof(IrBinary.Op)).EqualTo(IrBinaryOp.Or));
    Assert.That(((IrBinary)value!).Right, Is.EqualTo(new IrConst(value.Type, 0)));
  }

  /// <summary>The expanded function interpreted.</summary>
  [Then("^calling it with (0x[0-9A-F]+) gives (0x[0-9A-F]+)$")]
  public void Calls(string input, string output)
    => Assert.That(IrInterpreter.Call(this._module!, "F", [(byte)Hex(input)]), Is.EqualTo((byte)Hex(output)));

  /// <summary>The one diagnostic of the verifier.</summary>
  [Then("^it reports (PLY\\d{4}) " + StepText.Quoted + "$")]
  public void Reports(string code, string message)
    => Assert.That(this._diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Is.EqualTo(new[] { (code, message) }));

  /// <summary>The diagnostic of a failed build.</summary>
  [Then("^building fails with (PLY\\d{4}) " + StepText.Quoted + "$")]
  public void Fails(string code, string message) => Assert.That((this._failure?.Code, this._failure?.Message), Is.EqualTo(((string?)code, (string?)message)));

  /// <summary>The diagnostic of a refused document names the file and the property.</summary>
  [Then("^building fails with (PLY\\d{4}) naming " + StepText.Quoted + " and the property " + StepText.Quoted + "$")]
  public void FailsNaming(string code, string file, string property) {
    Assert.That(this._failure?.Code, Is.EqualTo(code));
    Assert.That(this._failure!.Message, Does.Contain(file).And.Contain($"property '{property}'"));
  }

  private static IntrinsicContract Load(string file)
    => ContractLoader.Load(file, SpecDocument.Parse("intrinsics/registry/" + file, File.ReadAllText(Path.Combine(_fixtures, file))).Root);

  private static uint Hex(string text) => uint.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

  private static IrModule Module(string op, string type) {
    var irType = type switch { "u8" => IrType.U8, "u32" => IrType.U32, _ => IrType.F32 };
    var operation = Enum.Parse<IrIntrinsicOp>(op);
    var resultType = operation == IrIntrinsicOp.Parity ? IrType.I32 : irType;
    var x = new IrLocal("x", irType);
    return new IrModule("M", [new IrFunction("F", true, resultType, [x], [], new IrBlock([new IrReturn(new IrIntrinsic(operation, resultType, [new IrLocalRef(x)]))]))]);
  }

  private sealed class OrZeroDecomposition : IIntrinsicDecomposition {

    public IrExpr? Expand(IrIntrinsic node, IIntrinsicLibrary library) => new IrBinary(IrBinaryOp.Or, node.Arguments[0], new IrConst(node.Type, 0));

  }

}
