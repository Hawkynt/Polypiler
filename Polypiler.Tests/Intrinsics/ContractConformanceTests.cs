using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.IR.Intrinsics;
using Polypiler.Core.Legalize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Helpers;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Intrinsics;

/// <summary>
/// The contracts of <c>spec/intrinsics/</c> bound to the code (#106): every document is read by the
/// loader and names an operation of the IR; the registry of all of them builds; every reference vector
/// is what the evaluator - the interpreter's meaning of the operation - gives, and what the
/// decomposition of the library gives once the node is expanded; and every signature the IR takes for
/// an integer operation is one its contract admits.
/// </summary>
[TestFixture]
public sealed class ContractConformanceTests {

  private static readonly Lazy<IReadOnlyList<(string File, IntrinsicContract Contract)>> _contracts = new(() => [
    .. Directory.EnumerateFiles(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "intrinsics"), "*.yaml", SearchOption.AllDirectories)
      .Order(StringComparer.Ordinal)
      .Select(file => (Path.GetFileName(file), Read(file)))
  ]);

  private static readonly Lazy<IIntrinsicRegistry> _registry = new(() => IntrinsicCatalog.Build(_contracts.Value.Select(entry => entry.Contract)));

  /// <summary>Reads a contract document of the repository.</summary>
  internal static IntrinsicContract Read(string file) {
    var relative = Path.GetRelativePath(Path.Combine(PrototypeFixture.RepositoryRoot, "spec"), file).Replace('\\', '/');
    return ContractLoader.Load(Path.GetFileName(file), SpecDocument.Parse(relative, File.ReadAllText(file)).Root);
  }

  private static IEnumerable<TestCaseData> Contracts()
    => _contracts.Value.Select(entry => new TestCaseData(entry.Contract.Id.Name).SetName($"{{m}}({entry.Contract.Id.Name})"));

  [Test]
  public void EveryContract_IsReadAndNamesAnOperation() {
    Assert.That(_contracts.Value, Has.Count.GreaterThan(100));
    foreach (var (file, contract) in _contracts.Value) {
      Assert.That(file, Is.EqualTo(contract.Id.Name.ToLowerInvariant() + ".yaml"));
      Assert.That(IntrinsicCatalog.Operation(contract).ToString(), Is.EqualTo(contract.Id.Name));
    }
  }

  [Test]
  public void EveryContractWithoutALibraryFallback_RequiresTheRuntime()
    => Assert.That(_contracts.Value.Where(entry => IntrinsicCatalog.Library(IntrinsicCatalog.Operation(entry.Contract)) is null && !entry.Contract.RequiresRuntime)
      .Select(entry => entry.Contract.Id.Name), Is.Empty, string.Join(" ", _contracts.Value.Where(entry => IntrinsicCatalog.Library(IntrinsicCatalog.Operation(entry.Contract)) is null && !entry.Contract.RequiresRuntime).Select(entry => entry.File)));

  [Test]
  public void TheRegistryOfEveryContract_Builds() {
    var registry = _registry.Value;
    Assert.That(registry.All.Select(contract => contract.Id.Name), Is.Ordered.Using((IComparer)StringComparer.Ordinal));
    Assert.That(registry.All, Has.Count.EqualTo(_contracts.Value.Count));
    Assert.That(registry.All.Where(contract => registry.Decomposition(contract.Id) is null), Has.All.Matches<IntrinsicContract>(contract => contract.RequiresRuntime));
  }

  /// <summary>Every vector of a contract over numbers, truth values and strings: the evaluator and the expanded node both give its outputs.</summary>
  [TestCaseSource(nameof(Contracts))]
  public void EveryVector_IsWhatTheEvaluatorAndTheDecompositionGive(string name) {
    var contract = _registry.Value.Get(new(name));
    var evaluator = _registry.Value.Evaluator(contract.Id);
    var op = IntrinsicCatalog.Operation(contract);
    var checkedVectors = 0;
    foreach (var vector in contract.Vectors) {
      if (Typed(contract, vector) is not { } typed)
        continue;

      var (result, arguments) = typed;
      var inputs = arguments.Select((type, index) => ContractLiterals.Value(type, vector.Inputs[index])).ToArray();
      var expected = ContractLiterals.Value(result == IrType.Void ? arguments.First(type => type.IsArray) : result, vector.Outputs[0]);
      var evaluated = evaluator.Evaluate(new(contract.Id, result, arguments, Copies(inputs)));
      Assert.That(evaluated.Trap, Is.Null, $"{name} {Shown(vector)}");
      Assert.That(evaluated.Values.Single(), Is.EqualTo(expected), $"{name} {Shown(vector)}: evaluator");

      var call = new IntrinsicCall(contract.Id, result, arguments, Copies(inputs));
      var function = InterpretedEvaluator.Function(op, call, out var passed);
      var expanded = ExpandIntrinsics.Run(new IrModule("Vector", [function]), _registry.Value, _ => true, () => FloatSupport.Libraries);
      Assert.That(IrVerifier.Verify(expanded), Is.Empty);
      var actual = IrInterpreter.Call(expanded, function.Name, [.. passed.Select(index => call.Arguments[index]!)]);
      Assert.That(result == IrType.Void ? call.Arguments.First(argument => argument is Array) : actual, Is.EqualTo(expected), $"{name} {Shown(vector)}: decomposition");
      ++checkedVectors;
    }

    if (contract.Returns.Count == 1 && contract.Vectors.Count > 0 && contract.Group is not ("String" or "Enum"))
      Assert.That(checkedVectors, Is.GreaterThan(0), $"{name}: no vector could be checked");
  }

  /// <summary>Every signature the IR takes for an integer operation that has a contract is one its contract admits.</summary>
  [Test]
  public void EveryIntegerSignature_IsAdmittedByItsContract() {
    var refused = new List<string>();
    foreach (var op in IntegerOperations.Operations.Order()) {
      if (!_registry.Value.TryGet(IntrinsicId.Of(op), out var contract))
        continue;
      foreach (var (result, arguments) in IntegerOperations.Signatures(op)) {
        var node = new IrIntrinsic(op, result, [.. arguments.Select(type => (IrExpr)new IrLocalRef(new IrLocal("x", type)))]);
        if (contract.Check(node) is { } problem)
          refused.Add(problem.Message);
      }
    }

    Assert.That(refused, Is.Empty);
  }

  /// <summary>The types of result and operands a vector binds, or <see langword="null"/> for one this test cannot build: several results, attributes, or a value of another kind.</summary>
  private static (IrType Result, IrType[] Arguments)? Typed(IntrinsicContract contract, IntrinsicVector vector) {
    // The enum operations take declared enum types, which a contract names by their underlying integer.
    if (contract.Returns.Count != 1 || contract.Attributes.Count > 0 || contract.Group == "Enum")
      return null;

    IrType? Type(IntrinsicValue value) {
      var id = value.Type.StartsWith("TYPES.", StringComparison.Ordinal) ? value.Type : vector.Types.GetValueOrDefault(value.Type);
      if (id is null || ContractTypes.Scalar(id) is not { } scalar || (value.IsArray && !scalar.IsNumeric))
        return null;
      return value.IsArray ? IrType.Array(scalar) : scalar;
    }

    var types = contract.Parameters.Select(Type).ToList();
    // A store writes the array its contract returns in place: the node has no value.
    var op = IntrinsicCatalog.Operation(contract);
    if (contract.Returns[0] is { IsArray: true } written && IntegerOperations.Operations.Contains(op) && IntegerOperations.Signatures(op).All(signature => signature.Result == IrType.Void)
        && types.All(type => type is not null) && Type(written) is not null)
      return (IrType.Void, types.Select(type => type!).ToArray());
    return Type(contract.Returns[0]) is { } result && types.All(type => type is not null) ? (result, types.Select(type => type!).ToArray()) : null;
  }

  /// <summary>The inputs with every array copied, so that a store writes its own.</summary>
  private static object?[] Copies(object[] inputs) => [.. inputs.Select(input => input is Array array ? array.Clone() : input)];

  private static string Shown(IntrinsicVector vector)
    => $"[{string.Join(", ", vector.Types.Select(pair => $"{pair.Key}={pair.Value}"))}] ({string.Join(", ", vector.Inputs.Select(input => input?.ToJsonString()))})";

}
