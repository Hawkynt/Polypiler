using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>Arithmetic recognition contracts on executable source imported into the current tree IR.</summary>
[Binding, Scope(Feature = "Proved wrapping absolute value recognition"), Scope(Feature = "Proved zero-count loop recognition"), Scope(Feature = "Proved SWAR population count recognition"), Scope(Feature = "Proved parity recognition"), Scope(Feature = "Proved shift-add multiplication recognition"), Scope(Feature = "Proved single-bit recognition"), Scope(Feature = "Proved minimum and maximum recognition"), Scope(Feature = "Proved signum recognition"), Scope(Feature = "Proved de Bruijn zero-count recognition")]
public sealed class ArithmeticRecognitionSteps {

  private IrModule _before = null!;
  private OptimizationResult _after = null!;

  /// <summary>Imports one named fixture function, retaining the actual frontend conversions.</summary>
  [Given("^the arithmetic recognition fixture \"([^\"]+)\" function \"([^\"]+)\"$")]
  public void Fixture(string file, string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", file);
    var module = PrototypeFixture.Import(file, File.ReadAllText(path));
    this._before = module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
    Assert.That(IrVerifier.Verify(this._before), Is.Empty);
  }

  /// <summary>Runs only the selected rule with the stated proof budget.</summary>
  [When("^only arithmetic rule (O[0-9]+) runs with proof budget ([0-9]+) seconds$")]
  public void Run(string id, int seconds) {
    this._after = Optimizer.Run(this._before, new OptimizerOptions {
      Prover = new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.FromSeconds(seconds)) },
      Disabled = RuleTable.Default.Ids.Where(other => other != id).ToHashSet(), Verify = true
    });
  }

  /// <summary>Disables the selected rule while all unrelated rules remain excluded from this fixture.</summary>
  [When("^arithmetic rule (O[0-9]+) is disabled$")]
  public void Disabled(string id) {
    Assert.That(RuleTable.Default.Ids, Does.Contain(id));
    this._after = Optimizer.Run(this._before, new OptimizerOptions {
      Prover = new SmtRecognitionProver(), Disabled = RuleTable.Default.Ids.ToHashSet(), Verify = true
    });
  }

  /// <summary>The committed rewrite contains the intrinsic and its proof evidence.</summary>
  [Then("^the arithmetic result contains intrinsic \"([^\"]+)\" and an SMT proof$")]
  public void Intrinsic(string operation) {
    Assert.That(IrText.Of(this._after.Module), Does.Contain(operation + "<"));
    Assert.That(this._after.Applications, Has.Count.EqualTo(1));
    Assert.That(this._after.Notes.Any(note => note.EndsWith("[proof: smt-bitvector]", StringComparison.Ordinal)), Is.True);
    Assert.That(IrVerifier.Verify(this._after.Module), Is.Empty);
  }

  /// <summary>A refused or disabled candidate preserves the entire imported IR.</summary>
  [Then("^the arithmetic IR is unchanged$")]
  public void Unchanged() {
    Assert.That(IrText.Of(this._after.Module), Is.EqualTo(IrText.Of(this._before)));
    Assert.That(this._after.Applications, Is.Empty);
  }

  /// <summary>The report contains the specified precondition or proof refusal.</summary>
  [Then("^the arithmetic report contains \"([^\"]+)\"$")]
  public void Report(string text) => Assert.That(this._after.Notes, Does.Contain(text));

  /// <summary>A disabled rule emits no provisional proof or refusal.</summary>
  [Then("^the arithmetic report is empty$")]
  public void NoReport() => Assert.That(this._after.Notes, Is.Empty);

  /// <summary>Both versions produce the specified bits, including the signed minimum.</summary>
  [Then("^both arithmetic versions map ([0-9A-Fa-f]+) to ([0-9A-Fa-f]+)$")]
  public void Value(string input, string output) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.I8 => (object)unchecked((sbyte)bits),
      IrTypeKind.I16 => (object)unchecked((short)bits),
      IrTypeKind.I32 => (object)unchecked((int)bits),
      IrTypeKind.U8 => unchecked((byte)bits),
      IrTypeKind.U16 => unchecked((ushort)bits),
      IrTypeKind.U32 => unchecked((uint)bits),
      IrTypeKind.U64 => bits,
      _ => unchecked((long)bits)
    };
    var before = IrInterpreter.Call(this._before, function.Name, [argument]);
    var after = IrInterpreter.Call(this._after.Module, function.Name, [argument]);
    Assert.That(after, Is.EqualTo(before));
    var width = function.ReturnType.Bits;
    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    var actual = unchecked((ulong)Convert.ToInt64(after, CultureInfo.InvariantCulture)) & mask;
    Assert.That(actual, Is.EqualTo(ulong.Parse(output, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }

  /// <summary>Both versions return the stated count for one value of any integer kind.</summary>
  [Then("^both arithmetic versions count ([0-9A-Fa-f]+) as ([0-9]+)$")]
  public void Count(string input, int count) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.I8 => unchecked((sbyte)bits),
      IrTypeKind.U8 => unchecked((byte)bits),
      IrTypeKind.I16 => unchecked((short)bits),
      IrTypeKind.U16 => unchecked((ushort)bits),
      IrTypeKind.I32 => unchecked((int)bits),
      IrTypeKind.U32 => unchecked((uint)bits),
      IrTypeKind.I64 => unchecked((long)bits),
      _ => bits
    };
    Assert.That(IrInterpreter.Call(this._before, function.Name, [argument]), Is.EqualTo(count));
    Assert.That(IrInterpreter.Call(this._after.Module, function.Name, [argument]), Is.EqualTo(count));
  }

  /// <summary>Both versions give the stated count for one unsigned value.</summary>
  [Then("^both popcount versions count ([0-9A-Fa-f]+) as ([0-9]+)$")]
  public void PopCount(string value, int count) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.U16 => (object)unchecked((ushort)bits),
      IrTypeKind.U32 => (object)unchecked((uint)bits),
      _ => (object)bits
    };
    var before = IrInterpreter.Call(this._before, function.Name, [argument]);
    var after = IrInterpreter.Call(this._after.Module, function.Name, [argument]);
    Assert.That(after, Is.EqualTo(before));
    Assert.That(Convert.ToUInt64(after, CultureInfo.InvariantCulture), Is.EqualTo((ulong)count));
  }

  /// <summary>The committed rewrite is one multiplication by the stated coefficient, with its proof evidence.</summary>
  [Then("^the arithmetic result contains a multiplication by ([0-9]+) and an SMT proof$")]
  public void Multiplication(string coefficient) {
    var products = IrTree.Nodes(this._after.Module.Functions.Single().Body).OfType<IrBinary>().Where(node => node.Op == IrBinaryOp.Multiply).ToList();
    Assert.That(products, Has.Count.EqualTo(1));
    Assert.That(products[0].Right, Is.InstanceOf<IrConst>());
    var constant = (IrConst)products[0].Right;
    Assert.That(IrOperators.Number(constant.Type, constant.Value).ToString(CultureInfo.InvariantCulture), Is.EqualTo(coefficient));
    Assert.That(this._after.Notes.Any(note => note.EndsWith("[proof: smt-bitvector]", StringComparison.Ordinal)), Is.True);
    Assert.That(IrVerifier.Verify(this._after.Module), Is.Empty);
  }

  /// <summary>Both versions map one value of any integer kind to the stated bits of the result.</summary>
  [Then("^both arithmetic versions multiply ([0-9A-Fa-f]+) to ([0-9A-Fa-f]+)$")]
  public void Multiplied(string input, string output) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.U8 => unchecked((byte)bits),
      IrTypeKind.I32 => unchecked((int)bits),
      IrTypeKind.U32 => unchecked((uint)bits),
      IrTypeKind.I64 => unchecked((long)bits),
      _ => bits
    };
    var before = IrInterpreter.Call(this._before, function.Name, [argument]);
    var after = IrInterpreter.Call(this._after.Module, function.Name, [argument]);
    Assert.That(after, Is.EqualTo(before));
    var mask = function.ReturnType.Bits == 64 ? ulong.MaxValue : (1UL << function.ReturnType.Bits) - 1;
    var actual = (after is ulong unsigned ? unsigned : unchecked((ulong)Convert.ToInt64(after, CultureInfo.InvariantCulture))) & mask;
    Assert.That(actual, Is.EqualTo(ulong.Parse(output, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }

  /// <summary>Both versions give the stated truth value or bits for a value and a bit position.</summary>
  [Then("^both arithmetic versions give (true|false|[0-9A-Fa-f]+) for ([0-9A-Fa-f]+) at bit ([0-9]+)$")]
  public void Bit(string expected, string value, int position) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.U8 => unchecked((byte)bits),
      IrTypeKind.I32 => unchecked((int)bits),
      IrTypeKind.U32 => unchecked((uint)bits),
      _ => bits
    };
    var before = IrInterpreter.Call(this._before, function.Name, [argument, position]);
    var after = IrInterpreter.Call(this._after.Module, function.Name, [argument, position]);
    Assert.That(after, Is.EqualTo(before));
    if (after is bool truth)
      Assert.That(truth, Is.EqualTo(expected == "true"));
    else
      Assert.That(Convert.ToUInt64(after, CultureInfo.InvariantCulture), Is.EqualTo(ulong.Parse(expected, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }

  /// <summary>Both versions produce the specified bits for two operands of any integer kind.</summary>
  [Then("^both arithmetic versions map ([0-9A-Fa-f]+) and ([0-9A-Fa-f]+) to ([0-9A-Fa-f]+)$")]
  public void Pair(string first, string second, string output) {
    var function = this._before.Functions.Single();
    object[] arguments = [Argument(function.Parameters[0].Type, first), Argument(function.Parameters[1].Type, second)];
    var before = IrInterpreter.Call(this._before, function.Name, arguments);
    var after = IrInterpreter.Call(this._after.Module, function.Name, arguments);
    Assert.That(after, Is.EqualTo(before));
    var width = function.ReturnType.Bits;
    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    var actual = (after is ulong unsigned ? unsigned : unchecked((ulong)Convert.ToInt64(after, CultureInfo.InvariantCulture))) & mask;
    Assert.That(actual, Is.EqualTo(ulong.Parse(output, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }

  private static object Argument(IrType type, string text) {
    var bits = ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    return type.Kind switch {
      IrTypeKind.I8 => unchecked((sbyte)bits),
      IrTypeKind.U8 => unchecked((byte)bits),
      IrTypeKind.I16 => unchecked((short)bits),
      IrTypeKind.U16 => unchecked((ushort)bits),
      IrTypeKind.I32 => unchecked((int)bits),
      IrTypeKind.U32 => unchecked((uint)bits),
      IrTypeKind.I64 => unchecked((long)bits),
      _ => bits
    };
  }
}
