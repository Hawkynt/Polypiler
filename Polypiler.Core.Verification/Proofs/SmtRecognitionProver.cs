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
      // Every obligation names its own types; the width only matters to width-generic ones, so a truth value takes 1.
      result = new SmtBitvectorProver([Math.Max(1, before.Type.Bits)], this._solver).Prove(obligation, this.Budget);
    } catch (ProverUnavailableException) {
      return new(false, "proof unavailable");
    }

    var proof = result switch {
      ProofResult.Proved => new RecognitionProof(true, SmtBitvectorProver.ProofClass),
      ProofResult.Inconclusive { Reason: SmtBitvectorProver.TimeoutReason } => new(false, "proof timeout"),
      ProofResult.Inconclusive => new(false, "proof unknown"),
      ProofResult.Refuted => new(false, RuleContext.NotEquivalent),
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
      if (!(expression.Type.IsInteger || expression.Type == IrType.Bool) || expression.Type.Bits > 64 || !IrShape.IsTrapFree(expression))
        return null;

      var result = this.Value(expression);
      this._lines.Add($"%r = Or.{Type(expression)} {result}, {(expression.Type == IrType.Bool ? "false" : "0")}");
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
        IrUnary { Op: IrUnaryOp.LogicalNot, Operand.Type.Kind: IrTypeKind.Bool } not => this.Emit("Not", not, this.Value(not.Operand)),
        IrBinary binary when Operator(binary) is { } op => this.Binary(binary, op),
        IrBinary { Left.Type.IsInteger: true } comparison when Predicate(comparison) is { } predicate => this.Compare(comparison.Left, comparison.Right, predicate),
        IrConditional choice => this.Emit("Select", choice, this.Value(choice.Condition), this.Value(choice.WhenTrue), this.Value(choice.WhenFalse)),
        IrArrayLoad { Array: IrConstRef { Constant: var table }, Type.IsInteger: true, Index.Type.IsInteger: true } load when IrShape.IsTableRead(load) => this.Table(load, table),
        IrIntrinsic { Op: IrIntrinsicOp.AbsWrap } absolute => this.Absolute(absolute),
        IrIntrinsic { Op: IrIntrinsicOp.MulHigh, Type.Bits: <= 32, Arguments: [var left, var right] } high => this.High(high, left, right),
        IrIntrinsic { Op: IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.LeadingZeroCount, Arguments: [{ Type.IsInteger: true } input] } count => this.ZeroCount(count, input),
        IrIntrinsic { Op: IrIntrinsicOp.Parity or IrIntrinsicOp.PopCount, Arguments: [{ Type.IsInteger: true } counted] } count => this.Count(count, counted),
        IrIntrinsic { Op: IrIntrinsicOp.TestBit or IrIntrinsicOp.SetBit or IrIntrinsicOp.ClearBit or IrIntrinsicOp.ToggleBit, Arguments: [var value, var position] } bit
          => this.Bit(bit, value, position),
        IrIntrinsic { Op: IrIntrinsicOp.Min or IrIntrinsicOp.Max, Arguments: [var first, var second] } extreme => this.Extreme(extreme, first, second),
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
      IrBinaryOp.Divide => binary.Type.IsSigned ? "SDiv" : "UDiv",
      IrBinaryOp.Remainder => binary.Type.IsSigned ? "SRem" : "URem",
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

    /// <summary>The upper half of the full product, by its definition at twice the width; a 64-bit one would need 128 bits and stays an input.</summary>
    private string High(IrIntrinsic high, IrExpr left, IrExpr right) {
      var wide = new IrConvert(high.Type.IsSigned ? IrType.I64 : IrType.U64, left);
      var extend = high.Type.IsSigned ? "SignExtend" : "ZeroExtend";
      var a = this.Emit(extend, wide, this.Value(left));
      var b = this.Emit(extend, wide, this.Value(right));
      var product = this.Emit("Mul", wide, a, b);
      var shifted = this.Emit(high.Type.IsSigned ? "AShr" : "LShr", wide, product, high.Type.Bits.ToString(CultureInfo.InvariantCulture));
      return this.Emit("Truncate", high, shifted);
    }

    /// <summary>
    /// The number of zero bits below the lowest (or above the highest) set bit, by testing every bit
    /// position in turn, independent of the matched loops; the width for zero.
    /// </summary>
    private string ZeroCount(IrIntrinsic count, IrExpr input) {
      var value = this.Value(input);
      var width = input.Type.Bits;
      var result = width.ToString(CultureInfo.InvariantCulture);
      for (var step = 0; step < width; ++step) {
        // The trailing count tests from the top down, so that the lowest set bit is chosen last; the leading count the other way.
        var bit = count.Op == IrIntrinsicOp.TrailingZeroCount ? width - 1 - step : step;
        var shifted = this.Emit("LShr", input, value, bit.ToString(CultureInfo.InvariantCulture));
        var low = this.Emit("And", input, shifted, "1");
        var set = this.Emit("Ne", input, low, "0");
        var zeros = count.Op == IrIntrinsicOp.TrailingZeroCount ? bit : width - 1 - bit;
        result = this.Emit("Select", count, set, zeros.ToString(CultureInfo.InvariantCulture), result);
      }

      return result;
    }

    /// <summary>
    /// The population count, or its parity, by their textbook definitions (Hacker's Delight §5-1, §5-2),
    /// which share no code with the recognizers: the parity as bit 0 of the halving fold with logical
    /// shifts, the count as a balanced tree of sums of the isolated bits, both kept at the input's width.
    /// A rule's arithmetic shifts, tables and masks are checked against these spellings; flat xor or
    /// sum chains of every bit leave the solver without a proof of 64-bit candidates within budget.
    /// </summary>
    private string Count(IrIntrinsic count, IrExpr counted) {
      var input = this.Value(counted);
      var width = counted.Type.Bits;
      var resultBits = count.Type.Bits;
      string Fit(string value) => width == resultBits ? value : this.Emit(width > resultBits ? "Truncate" : "ZeroExtend", count, value);
      if (count.Op == IrIntrinsicOp.Parity) {
        var folded = input;
        for (var step = width / 2; step >= 1; step /= 2)
          folded = this.Emit("Xor", counted, folded, this.Emit("LShr", counted, folded, step.ToString(CultureInfo.InvariantCulture)));

        return Fit(this.Emit("And", counted, folded, "1"));
      }

      var terms = Enumerable.Range(0, width)
        .Select(bit => this.Emit("And", counted, this.Emit("LShr", counted, input, bit.ToString(CultureInfo.InvariantCulture)), "1")).ToList();
      while (terms.Count > 1)
        terms = [.. terms.Chunk(2).Select(pair => pair.Length == 1 ? pair[0] : this.Emit("Add", counted, pair[0], pair[1]))];

      return Fit(terms[0]);
    }

    /// <summary>
    /// A single-bit operation by its definition on the single bit <c>1 &lt;&lt; p</c>, independent of the
    /// matched idioms. The position is within the width, which the trap check before encoding requires.
    /// </summary>
    private string Bit(IrIntrinsic bit, IrExpr value, IrExpr position) {
      var bits = this.Value(value);
      var where = this.Value(position);
      if (bit.Op == IrIntrinsicOp.TestBit) {
        var shifted = this.Emit("LShr", value, bits, where);
        var low = this.Emit("And", value, shifted, "1");
        return this.Emit("Ne", value, low, "0");
      }

      var single = this.Emit("Shl", value, "1", where);
      return bit.Op switch {
        IrIntrinsicOp.SetBit => this.Emit("Or", bit, bits, single),
        IrIntrinsicOp.ClearBit => this.Emit("And", bit, bits, this.Emit("Not", bit, single)),
        _ => this.Emit("Xor", bit, bits, single)
      };
    }

    /// <summary>The smaller or larger operand by a comparison in the operands' signedness, independent of the matched forms.</summary>
    private string Extreme(IrIntrinsic extreme, IrExpr first, IrExpr second) {
      var a = this.Value(first);
      var b = this.Value(second);
      var predicate = (first.Type.IsSigned ? "S" : "U") + (extreme.Op == IrIntrinsicOp.Min ? "Lt" : "Gt");
      var ordered = this.Emit(predicate, first, a, b);
      return this.Emit("Select", extreme, ordered, a, b);
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

    /// <summary>
    /// A load from a constant table as the choice among its entries by the index, element by element.
    /// The index is always inside the table, which the trap check before encoding requires, so the
    /// last entry needs no comparison.
    /// </summary>
    private string Table(IrArrayLoad load, IrModuleConst table) {
      var index = this.Value(load.Index);
      string Entry(int position) => "0x" + ((ulong)table.Values[position] & Mask(load.Type.Bits)).ToString("X", CultureInfo.InvariantCulture);
      var result = Entry(table.Values.Count - 1);
      for (var position = table.Values.Count - 2; position >= 0; --position) {
        var chosen = this.Emit("Eq", load.Index, index, position.ToString(CultureInfo.InvariantCulture));
        result = this.Emit("Select", load, chosen, Entry(position), result);
      }

      return result;
    }

    private string Binary(IrBinary binary, string op) {
      if (binary.Op == IrBinaryOp.Multiply && (binary.Right is IrConst || binary.Left is IrConst))
        return this.Product(binary);

      var left = this.Value(binary.Left);
      var right = this.Value(binary.Right);
      // Tree IR masks shifts to the operand width. Both values have unsigned proof types;
      // AShr itself carries signed shift semantics independently of that spelling.
      var contract = binary.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical ? "[mask]" : "";
      var name = "%v" + this._lines.Count.ToString(CultureInfo.InvariantCulture);
      this._lines.Add($"{name} = {op}.{Type(binary)}{contract} {left}, {right}");
      return name;
    }

    /// <summary>
    /// A multiplication by a constant as the sum of the other operand shifted to each set bit of the constant,
    /// which is what it is modulo the width. A bit-blasted multiplier of 64 bits costs the solver seconds; the
    /// sum of shifts it is compared with costs milliseconds. The expansion is taken from the constant alone.
    /// </summary>
    private string Product(IrBinary binary) {
      var (constant, operand) = binary.Right is IrConst right ? (right, binary.Left) : ((IrConst)binary.Left, binary.Right);
      var value = this.Value(operand);
      var mask = Mask(binary.Type.Bits);
      var bits = (ulong)constant.Value & mask;
      // c · x is also -((-c) · x); a negative constant has far fewer set bits negated.
      var negated = (0 - bits) & mask;
      var negative = System.Numerics.BitOperations.PopCount(negated) + 1 < System.Numerics.BitOperations.PopCount(bits);
      var expanded = negative ? negated : bits;
      var result = "0";
      for (var bit = 0; bit < binary.Type.Bits; ++bit)
        if ((expanded >> bit & 1) != 0)
          result = this.Emit("Add", binary, result, bit == 0 ? value : this.Emit("Shl", binary, value, bit.ToString(CultureInfo.InvariantCulture)));

      return negative ? this.Emit("Neg", binary, result) : result;
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
