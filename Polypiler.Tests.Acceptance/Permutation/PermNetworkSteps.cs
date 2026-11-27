using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Networks;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Permutation;

/// <summary>
/// The steps of <c>tests/features/permutation/perm-network.feature</c>: candidate networks built from their
/// text, evaluated, lowered and interpreted, verified, and assembled from terms.
/// </summary>
[Binding, Scope(Feature = "Typed candidate networks")]
public sealed class PermNetworkSteps {

  private BitOperationNetwork? _network;
  private BitMapping? _mapping;
  private PrimitiveCandidate? _term;
  private BigInteger _evaluated;
  private BigInteger _interpreted;
  private string? _diagnostic;

  private BitOperationNetwork Network => this._network ?? throw new AssertionException("no network was given");

  private static BigInteger Hex(string text) => BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

  /// <summary>A network from its text, input and output at one width.</summary>
  [Given("^the network \"(.*)\" at width ([0-9]+)$")]
  public void Network_(string text, int width) => this._network = NetworkText.Parse(text, width, width);

  /// <summary>A mapping and one term that emits a text over the input.</summary>
  [Given("^the mapping \"(.*)\" and one term emitting \"(.*)\" with Routed (0x[0-9A-Fa-f]+)$")]
  public void Term(string mapping, string text, string routed) {
    this._mapping = BitMapping.Parse(mapping);
    this._term = new PrimitiveCandidate("test", Hex(routed), 1, text, builder => NetworkText.Emit(builder, text));
  }

  /// <summary>Evaluates the network and interprets its lowered IR on one input.</summary>
  [When("^it is evaluated on (0x[0-9A-Fa-f]+) and its lowered IR is interpreted on (0x[0-9A-Fa-f]+)$")]
  public void Evaluate(string input, string again) {
    this._evaluated = NetworkEvaluator.Evaluate(this.Network, Hex(input));
    var module = new IrModule("Network", [NetworkLowering.ToFunction(this.Network, "F")], []);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    this._interpreted = new BigInteger((byte)IrInterpreter.Call(module, "F", [(byte)Hex(again)])!);
  }

  /// <summary>Runs the verifier and keeps its first diagnostic.</summary>
  [When("^the verifier runs$")]
  public void Verify() => this._diagnostic = NetworkVerifier.Check(this.Network).Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message).FirstOrDefault();

  /// <summary>Assembles the term, keeping the network or the refusal.</summary>
  [When("^the composite network is assembled$")]
  public void Assemble() {
    try {
      this._network = CompositeNetwork.Assemble(this._mapping!, [this._term!]);
    } catch (PermutationModelException refused) {
      this._diagnostic = refused.Message;
    }
  }

  /// <summary>Both evaluations give one value.</summary>
  [Then("^both give (0x[0-9A-Fa-f]+)$")]
  public void Both(string value) => Assert.That((this._evaluated, this._interpreted), Is.EqualTo((Hex(value), Hex(value))));

  /// <summary>The operation of each lowered step, a mask that only clears the bits above the step's width looked through.</summary>
  [Then("^the lowered steps are \"(.*)\" in that order$")]
  public void Lowered(string operations) {
    var lowered = NetworkLowering.Lower(this.Network, new IrLocalRef(new IrLocal("x", IrType.U8)));
    var names = lowered.Statements.Cast<IrDeclare>().Select((declare, index) => Operation(declare.Initializer!, this.Network.Steps[index].Width).ToString());
    Assert.That(string.Join(", ", names), Is.EqualTo(operations));
  }

  private static IrBinaryOp Operation(IrExpr expression, int width) => expression is IrBinary { Op: IrBinaryOp.And, Left: IrBinary inner, Right: IrConst mask } && mask.Value == (1L << width) - 1
    ? inner.Op : ((IrBinary)expression).Op;

  /// <summary>The network's cost under a cost table of the repository.</summary>
  [Then("^its cost under \"(.*)\" is ([0-9]+)$")]
  public void Cost(string table, long cost)
    => Assert.That(new NetworkStatistics(this.Network).Cost(PrimitiveCostTable.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "permutations", "costs", table + ".yaml"))), Is.EqualTo(cost));

  /// <summary>The network's text.</summary>
  [Then("^its steps are \"(.*)\"$")]
  public void StepsAre(string text) => Assert.That(NetworkText.Print(this.Network), Is.EqualTo(text));

  /// <summary>The network's value on one input.</summary>
  [Then("^evaluating it on (0x[0-9A-Fa-f]+) gives (0x[0-9A-Fa-f]+)$")]
  public void Gives(string input, string output) => Assert.That(NetworkEvaluator.Evaluate(this.Network, Hex(input)), Is.EqualTo(Hex(output)));

  /// <summary>The first diagnostic.</summary>
  [Then("^the diagnostic is \"(.*)\"$")]
  public void Is_(string text) => Assert.That(this._diagnostic, Is.EqualTo(text));

  /// <summary>The refusal names a code and a problem.</summary>
  [Then("^the diagnostic contains \"(.*)\" and \"(.*)\"$")]
  public void Contains(string code, string problem) => Assert.That(this._diagnostic, Does.Contain(code).And.Contain(problem));
}
