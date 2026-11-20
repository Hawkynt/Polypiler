using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// Checks actual tree-IR recognition candidates with the bitvector prover. Integer boundaries not
/// modeled by the encoder are independent inputs, so a proof holds for every value they can produce.
/// The bounded cache contains only proved obligations and belongs to this host instance.
/// </summary>
public sealed class SmtRecognitionProver : IRecognitionProver {

  private readonly ISmtSolver? _solver;
  private readonly ConcurrentDictionary<string, RecognitionProof> _proved = new(StringComparer.Ordinal);

  /// <summary>Creates a prover using Z3, or an injected solver.</summary>
  /// <param name="solver">The solver; null selects the pinned Z3 binding.</param>
  public SmtRecognitionProver(ISmtSolver? solver = null) => this._solver = solver;

  /// <summary>The per-candidate proof budget. Zero timeout always refuses, including cached candidates.</summary>
  public ProofBudget Budget { get; init; } = ProofBudget.Default;

  /// <inheritdoc/>
  public RecognitionProof Prove(string id, IrExpr before, IrExpr after, RuleContext context) {
    if (this.Budget.EffectiveSolverTimeout <= TimeSpan.Zero)
      return new(false, "proof timeout");

    var obligation = CreateObligation(id, before, after, context);
    if (obligation is null)
      return new(false, "unsupported proof expression");

    var key = id + "\n" + obligation.Before + "\n" + obligation.After + "\n" + string.Join(",", obligation.Inputs.Select(input => input.Type));
    if (this._proved.TryGetValue(key, out var cached))
      return cached;

    ProofResult result;
    try {
      result = new SmtBitvectorProver([before.Type.Bits], this._solver).Prove(obligation, this.Budget);
    } catch (ProverUnavailableException) {
      return new(false, "proof unavailable");
    }

    var proof = result switch {
      ProofResult.Proved => new RecognitionProof(true, SmtBitvectorProver.ProofClass),
      ProofResult.Inconclusive { Reason: SmtBitvectorProver.TimeoutReason } => new(false, "proof timeout"),
      ProofResult.Inconclusive => new(false, "proof unknown"),
      ProofResult.Refuted => new(false, "not equivalent"),
      ProofResult.Unsupported unsupported => new(false, $"unsupported proof expression: {unsupported.Operation} ({unsupported.Detail})"),
      _ => new(false, "unsupported proof expression")
    };
    if (proof.Proved && this._proved.Count < 256)
      this._proved.TryAdd(key, proof);

    return proof;
  }

  /// <summary>Exports the exact obligation used at runtime for independent exhaustive checks and evidence.</summary>
  /// <param name="id">The rule.</param>
  /// <param name="before">The candidate.</param>
  /// <param name="after">The replacement of the same type.</param>
  /// <param name="context">The stable definitions in scope.</param>
  /// <returns>The obligation, or null for an unsupported type or trapping expression.</returns>
  public static ProofObligation? CreateObligation(string id, IrExpr before, IrExpr after, RuleContext context) {
    if (before.Type != after.Type)
      return null;

    var atoms = new Dictionary<IrExpr, string>(IrShape.Comparer);
    var inputs = new List<ProofInput>();
    var left = new Fragment(atoms, inputs, context).Encode(before);
    var right = new Fragment(atoms, inputs, context).Encode(after);
    return left is null || right is null ? null : new(id, left, right, inputs);
  }

  /// <summary>One fragment, sharing its free inputs with the opposite side.</summary>
  private sealed class Fragment(Dictionary<IrExpr, string> atoms, List<ProofInput> inputs, RuleContext context) {

    private readonly List<string> _lines = [];
    private readonly Dictionary<IrExpr, string> _values = new(ReferenceEqualityComparer.Instance);

    public string? Encode(IrExpr expression) {
      if (!expression.Type.IsInteger || expression.Type.Bits > 64 || !IrShape.IsTrapFree(expression))
        return null;

      var result = this.Value(expression);
      this._lines.Add($"%r = Or.{Type(expression)} {result}, 0");
      return string.Join("\n", this._lines);
    }

    private static string Type(IrExpr expression) => expression.Type == IrType.Bool ? "bool" : "u" + expression.Type.Bits.ToString(CultureInfo.InvariantCulture);

    private string Emit(string op, IrExpr expression, params string[] arguments) {
      var name = "%v" + this._lines.Count.ToString(CultureInfo.InvariantCulture);
      this._lines.Add($"{name} = {op}.{Type(expression)} {string.Join(", ", arguments)}");
      return name;
    }

    private string Value(IrExpr expression) {
      if (this._values.TryGetValue(expression, out var known))
        return known;

      var result = expression switch {
        IrConst { Type.Kind: IrTypeKind.Bool } constant => constant.Value == 0 ? "false" : "true",
        IrConst constant => "0x" + ((ulong)constant.Value & Mask(constant.Type.Bits)).ToString("X", CultureInfo.InvariantCulture),
        IrLocalRef reference when context.Definition(reference.Local) is { } definition => this.Value(definition),
        IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true } convert => this.Convert(convert),
        IrUnary { Op: IrUnaryOp.Negate or IrUnaryOp.BitNot } unary => this.Emit(unary.Op == IrUnaryOp.Negate ? "Neg" : "Not", unary, this.Value(unary.Operand)),
        IrBinary binary when Operator(binary) is { } op => this.Binary(binary, op),
        IrBinary { Left.Type.IsInteger: true } comparison when Predicate(comparison) is { } predicate => this.Compare(comparison.Left, comparison.Right, predicate),
        IrConditional choice => this.Emit("Select", choice, this.Value(choice.Condition), this.Value(choice.WhenTrue), this.Value(choice.WhenFalse)),
        IrIntrinsic { Op: IrIntrinsicOp.AbsWrap } absolute => this.Absolute(absolute),
        IrIntrinsic { Op: IrIntrinsicOp.Sign } sign => this.Sign(sign),
        IrIntrinsic { Op: IrIntrinsicOp.BitReverse or IrIntrinsicOp.ByteSwap } intrinsic => this.Reverse(intrinsic),
        IrIntrinsic { Op: IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight } rotate
          => this.Emit(rotate.Op.ToString(), rotate, this.Value(rotate.Arguments[0]), this.Value(rotate.Arguments[1])),
        IrIntrinsic { Op: IrIntrinsicOp.ToInt32 or IrIntrinsicOp.ToUint32 } converted => this.JavaScript(converted),
        _ => this.Atom(expression)
      };
      return this._values[expression] = result;
    }

    private string Atom(IrExpr expression) {
      if (atoms.TryGetValue(expression, out var name))
        return "%" + name;

      name = "x" + atoms.Count.ToString(CultureInfo.InvariantCulture);
      atoms.Add(expression, name);
      inputs.Add(new(name, Type(expression)));
      return "%" + name;
    }

    private string JavaScript(IrIntrinsic converted) {
      if (converted.Arguments[0] is IrConvert { Operand.Type: { IsInteger: true, Bits: <= 32 } } exact)
        return this.Convert(new IrConvert(converted.Type, exact.Operand));

      return this.Atom(new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, converted.Arguments));
    }

    private string Convert(IrConvert convert) {
      var from = convert.Operand.Type;
      var value = this.Value(convert.Operand);
      return from.Bits == convert.Type.Bits ? value
        : this.Emit(from.Bits > convert.Type.Bits ? "Truncate" : from.IsSigned ? "SignExtend" : "ZeroExtend", convert, value);
    }

    private static string? Operator(IrBinary binary) => binary.Op switch {
      IrBinaryOp.Add => "Add",
      IrBinaryOp.Subtract => "Sub",
      IrBinaryOp.Multiply => "Mul",
      IrBinaryOp.And => "And",
      IrBinaryOp.Or => "Or",
      IrBinaryOp.Xor => "Xor",
      IrBinaryOp.ShiftLeft => "Shl",
      IrBinaryOp.ShiftRight => binary.Type.IsSigned ? "AShr" : "LShr",
      IrBinaryOp.ShiftRightLogical => "LShr",
      _ => null
    };

    private static string? Predicate(IrBinary comparison) {
      var sign = comparison.Left.Type.IsSigned ? "S" : "U";
      return comparison.Op switch {
        IrBinaryOp.Less => sign + "Lt",
        IrBinaryOp.LessOrEqual => sign + "Le",
        IrBinaryOp.Greater => sign + "Gt",
        IrBinaryOp.GreaterOrEqual => sign + "Ge",
        IrBinaryOp.Equal => "Eq",
        IrBinaryOp.NotEqual => "Ne",
        _ => null
      };
    }

    private string Compare(IrExpr left, IrExpr right, string predicate)
      => this.Emit(predicate, left, this.Value(left), this.Value(right));

    /// <summary>A signed magnitude by comparison and negation, independent of sign-mask matching.</summary>
    private string Absolute(IrIntrinsic absolute) {
      var input = absolute.Arguments[0];
      var value = this.Value(input);
      if (!input.Type.IsSigned)
        return value;

      var negative = this.Emit("SLt", input, value, "0");
      var negated = this.Emit("Neg", input, value);
      return this.Emit("Select", absolute, negative, negated, value);
    }

    /// <summary>
    /// The three-way sign by comparisons with zero, independent of the matched idioms; an unsigned value has no negative class.
    /// The sign of a float may trap on NaN, so <see cref="Encode"/> refuses it before it gets here.
    /// </summary>
    private string Sign(IrIntrinsic sign) {
      var input = sign.Arguments[0];
      var value = this.Value(input);
      var positive = this.Emit(input.Type.IsSigned ? "SGt" : "UGt", input, value, "0");
      var rest = "0";
      if (input.Type.IsSigned) {
        var negative = this.Emit("SLt", input, value, "0");
        rest = this.Emit("Select", sign, negative, "-1", "0");
      }

      return this.Emit("Select", sign, positive, "1", rest);
    }

    private string Binary(IrBinary binary, string op) {
      var left = this.Value(binary.Left);
      var right = this.Value(binary.Right);
      // Tree IR masks shifts to the operand width. Both values have unsigned proof types;
      // AShr itself carries signed shift semantics independently of that spelling.
      var contract = binary.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical ? "[mask]" : "";
      var name = "%v" + this._lines.Count.ToString(CultureInfo.InvariantCulture);
      this._lines.Add($"{name} = {op}.{Type(binary)}{contract} {left}, {right}");
      return name;
    }

    /// <summary>The intrinsic's bit definition, independent of the recognizer's weighted-bit algebra.</summary>
    private string Reverse(IrIntrinsic intrinsic) {
      var input = this.Value(intrinsic.Arguments[0]);
      var width = intrinsic.Type.Bits;
      var result = "0";
      for (var bit = 0; bit < width; ++bit) {
        var position = intrinsic.Op == IrIntrinsicOp.BitReverse ? width - 1 - bit : (width / 8 - 1 - bit / 8) * 8 + bit % 8;
        var shifted = this.Emit("LShr", intrinsic, input, bit.ToString(CultureInfo.InvariantCulture));
        var isolated = this.Emit("And", intrinsic, shifted, "1");
        var placed = this.Emit("Shl", intrinsic, isolated, position.ToString(CultureInfo.InvariantCulture));
        result = this.Emit("Or", intrinsic, result, placed);
      }

      return result;
    }

    private static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;
  }
}
