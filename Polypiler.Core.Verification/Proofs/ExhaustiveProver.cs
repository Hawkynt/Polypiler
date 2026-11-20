using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polypiler.Core.Verification.Proofs.Obligations;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// Proves an obligation by trying every input: each combination of input bit patterns is evaluated on
/// both sides by the reference interpreter (<see cref="Polypiler.Core.IR.IrInterpreter"/>) and the bits
/// compared. A width-generic obligation is taken at 8 bits.
/// <para>
/// What equivalent means (the refinement rule): a combination is excluded when a precondition is
/// false or the before side is invalid for it - a strict shift by the width or more, a division by
/// zero, any trap. For every other combination the after side must be valid and give the same bits.
/// Inputs are enumerated as unsigned bit patterns from 0, the first input varying slowest, so the
/// counterexample reported is always the same one.
/// </para>
/// </summary>
public sealed class ExhaustiveProver : IEquivalenceProver {

  /// <summary>The width a width-generic obligation is proved at.</summary>
  public const int GenericWidth = 8;

  /// <summary>The reason of the verdict on an obligation whose preconditions exclude every input.</summary>
  public const string VacuousReason = "vacuous: no input satisfies the preconditions";

  /// <summary>The most input bits any budget can let the prover enumerate: the combinations are counted in a <see cref="long"/>.</summary>
  public const int MaxEnumerableBits = 62;

  /// <summary>How many combinations one parallel task enumerates.</summary>
  private const long BlockSize = 1024;

  /// <inheritdoc/>
  public string Id => "exhaustive";

  /// <inheritdoc/>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public ProofResult Prove(ProofObligation obligation, ProofBudget budget) {
    ArgumentNullException.ThrowIfNull(obligation);
    ArgumentNullException.ThrowIfNull(budget);

    ObligationProgram program;
    IrObligation lowered;
    try {
      program = ObligationProgram.Parse(obligation, GenericWidth);
      lowered = IrObligation.Lower(program);
    } catch (ObligationUnsupportedException exception) {
      return new ProofResult.Unsupported(this.Id, exception.Operation) { Detail = exception.Detail };
    }

    var widths = program.Inputs.Select(input => input.Type.Width).ToArray();
    var proofClass = $"exhaustive-{(widths.Length == 0 ? 0 : widths.Max()).ToString(CultureInfo.InvariantCulture)}bit";
    var bits = widths.Sum();
    if (bits > budget.MaxInputBits || bits > MaxEnumerableBits)
      return new ProofResult.BudgetExceeded(proofClass, bits) { Budget = budget.MaxInputBits };

    // The combinations in order are the numbers below 2^bits, the first input in the highest bits.
    // Blocks of them run in parallel; a block that finds a counterexample breaks the loop, which
    // still finishes every block before it, so the first counterexample in order is the one reported.
    var total = 1L << bits;
    var blocks = (total + BlockSize - 1) / BlockSize;
    var found = new ProofResult.Refuted?[blocks];
    long evaluated = 0;
    Parallel.For(0L, blocks, (block, state) => {
      long count = 0;
      for (var combination = block * BlockSize; combination < Math.Min(total, (block + 1) * BlockSize); ++combination) {
        if (state.LowestBreakIteration < block)
          return;

        var values = Values(combination, widths);
        var arguments = lowered.Arguments(values);
        if (!Holds(lowered, arguments))
          continue;

        var before = lowered.Evaluate(IrObligation.BeforeFunction, arguments);
        if (!before.IsValid)
          continue;

        var after = lowered.Evaluate(IrObligation.AfterFunction, arguments);
        if (!after.IsValid || after.Value != before.Value) {
          found[block] = new ProofResult.Refuted(proofClass, Inputs(program, values), before.Value, after.Value) { AfterFault = after.Fault };
          state.Break();
          return;
        }

        ++count;
      }

      Interlocked.Add(ref evaluated, count);
    });

    var refuted = found.FirstOrDefault(result => result is not null);
    return refuted ?? (evaluated == 0 ? new ProofResult.Inconclusive(proofClass, VacuousReason) : (ProofResult)new ProofResult.Proved(proofClass, evaluated));
  }

  /// <summary>Whether every precondition is valid and true for the arguments.</summary>
  private static bool Holds(IrObligation lowered, object[] arguments) {
    for (var index = 0; index < lowered.PreconditionCount; ++index) {
      var holds = lowered.Evaluate(IrObligation.PreconditionFunction(index), arguments);
      if (!holds.IsValid || holds.Value == 0)
        return false;
    }

    return true;
  }

  /// <summary>The input values of a combination: the last input in its lowest bits.</summary>
  private static ulong[] Values(long combination, int[] widths) {
    var values = new ulong[widths.Length];
    var rest = (ulong)combination;
    for (var index = widths.Length - 1; index >= 0; --index) {
      values[index] = rest & ((1UL << widths[index]) - 1);
      rest >>= widths[index];
    }

    return values;
  }

  private static Dictionary<string, ulong> Inputs(ObligationProgram program, ulong[] values)
    => program.Inputs.Select((input, index) => (input.Name, Value: values[index])).ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

}
