using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Semantics;

/// <summary>
/// A Boolean bit transform (PRD §20.2; Knuth, TAOCP 4A §7.1.1): every output bit is a
/// <see cref="BoolExpr"/> over the input bits. It is represented and classified, never synthesized.
/// Output bits the <see cref="Condition"/> does not require are don't-cares whatever their expression.
/// </summary>
public sealed class BitBooleanTransform : IBitSemantics {

  private readonly BoolExpr[] _outputs;

  private BitBooleanTransform(int inputWidth, BoolExpr[] outputs, BitCondition condition, BitLayout layout) {
    this.InputWidth = inputWidth;
    this._outputs = outputs;
    this.Outputs = Array.AsReadOnly(outputs);
    this.Condition = condition;
    this.Layout = layout;
  }

  /// <summary>The expression of each output bit; index = output bit, 0 = least significant.</summary>
  public IReadOnlyList<BoolExpr> Outputs { get; }

  /// <summary>The input domain and the required outputs.</summary>
  public BitCondition Condition { get; }

  /// <inheritdoc/>
  public int InputWidth { get; }

  /// <inheritdoc/>
  public int OutputWidth => this._outputs.Length;

  /// <inheritdoc/>
  public BitLayout Layout { get; }

  BitSemanticClass IBitSemantics.DeclaredClass => BitSemanticClass.BooleanTransform;

  /// <summary>Creates a transform. Expressions that read past the input are kept; rule H4 of <see cref="BitSemanticsVerifier"/> reports them.</summary>
  /// <param name="inputWidth">The input width, 1 to 4096.</param>
  /// <param name="outputs">The expression of each output bit, least significant first; 1 to 4096 of them.</param>
  /// <param name="condition">The condition; <see cref="BitCondition.Unconstrained"/> when <see langword="null"/>.</param>
  /// <param name="layout">The lanes; <see cref="BitLayout.Default"/> when <see langword="null"/>.</param>
  /// <returns>The transform.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="outputs"/> or one of its expressions is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">A width is out of range (<c>PLY0800</c>).</exception>
  public static BitBooleanTransform Create(int inputWidth, IReadOnlyList<BoolExpr> outputs, BitCondition? condition = null, BitLayout? layout = null) {
    ArgumentNullException.ThrowIfNull(outputs);
    var expressions = new BoolExpr[outputs.Count];
    for (var bit = 0; bit < expressions.Length; ++bit)
      expressions[bit] = outputs[bit] ?? throw new ArgumentNullException(nameof(outputs), $"output bit {bit} has no expression");

    var unconstrained = BitCondition.Unconstrained(inputWidth, expressions.Length);
    return new(inputWidth, expressions, condition ?? unconstrained, layout ?? BitLayout.Default(inputWidth, expressions.Length));
  }

  /// <summary>
  /// Widens a routing, losslessly: a source becomes <see cref="BoolExpr.Input"/>, an inverted source its
  /// <see cref="BoolExpr.Not"/>, a constant <see cref="BoolExpr.Const"/>; a don't-care becomes
  /// <c>Const 0</c> and stays a don't-care because the routing's condition, carried over with its lanes,
  /// does not require it.
  /// </summary>
  /// <param name="routing">The routing.</param>
  /// <returns>The transform.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="routing"/> is <see langword="null"/>.</exception>
  public static BitBooleanTransform FromRouting(BitRouting routing) {
    ArgumentNullException.ThrowIfNull(routing);
    var outputs = new BoolExpr[routing.OutputWidth];
    for (var bit = 0; bit < outputs.Length; ++bit) {
      var entry = routing.Mapping.Outputs[bit];
      outputs[bit] = entry.Kind switch {
        BitSourceKind.Source => new BoolExpr.Input(entry.Index),
        BitSourceKind.InvertedSource => new BoolExpr.Not(new BoolExpr.Input(entry.Index)),
        _ => new BoolExpr.Const(entry.Kind == BitSourceKind.One)
      };
    }
    return new(routing.InputWidth, outputs, routing.Condition, routing.Layout);
  }

  /// <summary>
  /// The linear transform of a matrix over GF(2): output bit <c>i</c> is the exclusive or of the input
  /// bits set in <c>rows[i]</c>, in ascending order; a zero row is <c>Const 0</c>. Bits at or above the
  /// input width are kept as inputs for rule H4 to report.
  /// </summary>
  /// <param name="inputWidth">The input width, 1 to 4096.</param>
  /// <param name="rows">One row per output bit.</param>
  /// <returns>The transform.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException">A row is negative.</exception>
  /// <exception cref="PermutationModelException">A width is out of range (<c>PLY0800</c>).</exception>
  public static BitBooleanTransform FromGf2Matrix(int inputWidth, IReadOnlyList<BigInteger> rows) {
    ArgumentNullException.ThrowIfNull(rows);
    var outputs = new BoolExpr[rows.Count];
    for (var bit = 0; bit < outputs.Length; ++bit) {
      var row = rows[bit];
      if (row.Sign < 0)
        throw new ArgumentOutOfRangeException(nameof(rows), row, $"row {bit} is negative");

      BoolExpr? sum = null;
      for (var input = 0; input < (int)row.GetBitLength(); ++input)
        if (Hex.Has(row, input))
          sum = sum is null ? new BoolExpr.Input(input) : new BoolExpr.Xor(sum, new BoolExpr.Input(input));
      outputs[bit] = sum ?? new BoolExpr.Const(false);
    }
    return Create(inputWidth, outputs);
  }

  /// <summary>
  /// Narrows the transform to a routing (rule H6). Every required output is folded by
  /// <see cref="BoolExpr.Fold"/> and must become an input, an inverted input or a constant; outputs the
  /// condition does not require become don't-cares. Up to 16 inputs each folded output is held to the
  /// truth table of its expression. The condition and the lanes are carried over.
  /// </summary>
  /// <param name="routing">The routing, or <see langword="null"/>.</param>
  /// <param name="reason">Why the transform is no routing, for example <c>output bit 0 depends on 2 input bits</c>; <see langword="null"/> on success.</param>
  /// <returns>Whether the transform is a routing.</returns>
  /// <exception cref="PermutationModelException">A folded output disagrees with its truth table (<c>PLY0001</c>); that is a defect of the folding.</exception>
  public bool TryAsRouting([NotNullWhen(true)] out BitRouting? routing, [NotNullWhen(false)] out string? reason) {
    var entries = new BitSource[this._outputs.Length];
    for (var bit = 0; bit < entries.Length; ++bit) {
      if (!this.Condition.Requires(bit)) {
        entries[bit] = BitSource.DontCare;
        continue;
      }

      var folded = this._outputs[bit].Fold();
      BitSource? entry = folded switch {
        BoolExpr.Input input => BitSource.From(input.Index),
        BoolExpr.Not { Operand: BoolExpr.Input input } => BitSource.Not(input.Index),
        BoolExpr.Const constant => constant.Value ? BitSource.One : BitSource.Zero,
        _ => null
      };
      reason = entry switch {
        null => DependsOn(bit, folded.Inputs().Count),
        { ReadsInput: true } when entry.Value.Index >= this.InputWidth => $"output bit {bit} reads input {entry.Value.Index}, input width is {this.InputWidth}",
        _ => null
      };
      if (reason is not null) {
        routing = null;
        return false;
      }

      if (this.InputWidth <= BitSemanticsDiagnostics.TruthTableLimit)
        TruthTable.CrossCheck(this._outputs[bit], folded, this.InputWidth, bit);
      entries[bit] = entry!.Value;
    }

    routing = BitRouting.From(BitMapping.Create(this.InputWidth, entries), this.Condition, this.Layout);
    reason = null;
    return true;
  }

  private static string DependsOn(int bit, int inputs) => $"output bit {bit} depends on {inputs} input bit{(inputs == 1 ? "" : "s")}";

  /// <summary>
  /// The full truth table, when it is requested explicitly: entry <c>x</c> holds the outputs for input
  /// <c>x</c>, output bit <c>i</c> in bit <c>i</c>, for every <c>x</c> below 2^<see cref="InputWidth"/>.
  /// Don't-care outputs hold the value of their expression.
  /// </summary>
  /// <param name="diagnostics">Receives <c>PLY0807</c> when the transform has more than 16 inputs.</param>
  /// <param name="table">The table, or <see langword="null"/>.</param>
  /// <returns>Whether the table was built.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
  public bool TryGetTruthTable(DiagnosticBag diagnostics, [NotNullWhen(true)] out BigInteger[]? table) {
    ArgumentNullException.ThrowIfNull(diagnostics);
    if (this.InputWidth > BitSemanticsDiagnostics.TruthTableLimit) {
      diagnostics.Report(Diagnostic.Create(BitSemanticsDiagnostics.TruthTableTooLarge, null, this.InputWidth, BitSemanticsDiagnostics.TruthTableLimit));
      table = null;
      return false;
    }

    var columns = Array.ConvertAll(this._outputs, output => TruthTable.Evaluate(output, this.InputWidth));
    table = new BigInteger[1 << this.InputWidth];
    for (var input = 0; input < table.Length; ++input) {
      var value = BigInteger.Zero;
      for (var bit = 0; bit < columns.Length; ++bit)
        if ((columns[bit][input >> 6] >> (input & 63) & 1) != 0)
          value |= BigInteger.One << bit;
      table[input] = value;
    }
    return true;
  }

  /// <summary>The expressions, least significant output first, as <c>[e0, e1, …]</c> over the input width.</summary>
  public override string ToString() => $"{this.InputWidth}:[{string.Join(", ", this._outputs)}]";

}
