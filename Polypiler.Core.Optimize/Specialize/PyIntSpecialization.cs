using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Core.Optimize.Specialize;

/// <summary>
/// O0062: the arbitrary-precision integer specialised to fixed-width integers where that is exact,
/// and kept exact everywhere else. A frontend whose integers are unbounded - Python's <c>int</c> -
/// imports every one as <see cref="IrType.BigInt"/>; this pass gives back the native arithmetic the
/// program's own masks and ranges prove:
/// <list type="bullet">
/// <item>Range facts: a variable whose every value lies in a proven range - a constant, a mask
/// <c>e &amp; m</c> of a constant <c>m</c> that is not negative, a counter that a loop's test bounds,
/// arithmetic of such values - is held in the narrowest of <c>i32</c>, <c>u32</c>, <c>i64</c> and
/// <c>u64</c> that holds them; so is a result that every return of its function bounds.</item>
/// <item>The exact-range form: an expression whose result and operands lie in one fixed-width type
/// is computed in it; no operation can wrap there.</item>
/// <item>The mask form: <c>e &amp; (2^w - 1)</c> for w of 8, 16, 32 or 64 computes <c>e</c> modulo
/// 2^32 or 2^64 when <c>e</c> is built from <c>+ - *</c>, the bitwise operators, the negation and the
/// complement, and shifts to the left by a constant: the low bits of their results depend only on the
/// low bits of their operands (the lemma of demanded-bits narrowing). A right shift, a division, a
/// remainder and a call inside the mask are not low-bit closed: what they compute stays exact, or is
/// computed in a fixed width only where its own range proves that exact.</item>
/// </list>
/// Everything else stays arbitrary precision, so nothing is ever truncated (PRD §6.2). The pass is
/// required: it runs at every optimization level unless <c>--disable-opt O0062</c> switches it off.
/// </summary>
public static class PyIntSpecialization {

  /// <summary>The optimization number.</summary>
  public const string Id = "O0062";

  /// <summary>What the pass does, as <c>--explain-opt</c> names it.</summary>
  public const string Name = "Python int specialization";

  /// <summary>Runs the pass over every function of a module.</summary>
  /// <param name="module">The module.</param>
  /// <param name="report">Receives every decision: the function, the statement it was made at, and what was or was not applied.</param>
  /// <returns>The module, the same object when it has no arbitrary-precision integer.</returns>
  public static IrModule Run(IrModule module, Action<string, string, string> report) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(report);
    if (!module.Functions.Any(Uses))
      return module;

    // A result that every return of its function proves bounded is returned in a fixed width, and
    // every call widens it back where it is used: the value is the same, only its type is narrower.
    var results = new Dictionary<string, IrType>(StringComparer.Ordinal);
    foreach (var function in module.Functions.Where(function => function.ReturnType.IsBigInt))
      if (TypeFor(new Specializer(function, null, (_, _, _) => { }).Results()) is { } type)
        results[function.Name] = type;

    return module with { Functions = [.. module.Functions.Select(function => Called(function, results)).Select(function => Uses(function) ? new Specializer(function, results.GetValueOrDefault(function.Name), report).Run() : function)] };
  }

  /// <summary>A function whose calls of a function with a narrowed result widen that result back.</summary>
  private static IrFunction Called(IrFunction function, Dictionary<string, IrType> results) {
    if (results.Count == 0)
      return function;

    IrExpr Widen(IrExpr expression) {
      var operands = IrTree.Operands(expression);
      var rebuilt = operands.Count == 0 ? expression : IrTree.With(expression, [.. operands.Select(Widen)]);
      return rebuilt is IrCall call && results.TryGetValue(call.Function, out var type) && call.Type.IsBigInt
        ? new IrConvert(IrType.BigInt, call with { Type = type })
        : rebuilt;
    }

    IrBlock Block(IrBlock block) => new([.. block.Statements.Select(statement => IrPassSupport.Map(statement, Widen, Block))]);
    return function with { Body = Block(function.Body) };
  }

  /// <summary>Runs the pass and lists its decisions as rule applications, as the pipeline reports them.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The module and the decisions.</returns>
  public static OptimizationResult Run(IrModule module) {
    var applications = new List<RuleApplication>();
    var result = Run(module, (function, position, what) => applications.Add(new(Id, what, function, position)));
    return new(result, applications);
  }

  private static bool Uses(IrFunction function)
    => function.Parameters.Concat(function.Locals).Any(local => local.Type.IsBigInt) || IrPassSupport.Nodes(function.Body).Any(node => node.Type.IsBigInt);

  /// <summary>A closed range of integers; an end that is <see langword="null"/> is unbounded.</summary>
  private readonly record struct Range(BigInteger? Low, BigInteger? High) {

    public static Range Unbounded => new(null, null);

    public static Range Of(BigInteger value) => new(value, value);

    public static Range Of(IrType type) => new(IrOperators.Minimum(type) is var low ? (BigInteger)low : 0, (BigInteger)IrOperators.Maximum(type));

    public bool Bounded => this.Low is not null && this.High is not null;

    public bool Within(Range other) => this.Bounded && other.Bounded && this.Low >= other.Low && this.High <= other.High;

    public Range Join(Range other) => new(
      this.Low is { } a && other.Low is { } b ? BigInteger.Min(a, b) : null,
      this.High is { } c && other.High is { } d ? BigInteger.Max(c, d) : null);

    public override string ToString() => this.Bounded ? $"[{this.Low}, {this.High}]" : "unbounded";

  }

  /// <summary>
  /// The narrowest fixed-width type of 32 or 64 bits an exact computation of values in a range takes:
  /// <c>i32</c>, <c>u32</c>, <c>i64</c>, <c>u64</c>, a signed one first; none beyond 64 bits.
  /// </summary>
  private static IrType? TypeFor(Range range)
    => new[] { IrType.I32, IrType.U32, IrType.I64, IrType.U64 }.FirstOrDefault(type => range.Within(Range.Of(type)));

  /// <summary>
  /// A constant that keeps only bits below 2^64 when it masks: positive and of at most 64 bits, such
  /// as <c>2^w - 1</c> for w of 8, 16, 32 or 64; <see langword="null"/> for anything else.
  /// </summary>
  private static BigInteger? Mask(IrExpr expression)
    => expression is IrBigConst { Value: var value } && value.Sign > 0 && value.GetBitLength() <= 64 ? value : null;

  private sealed class Specializer(IrFunction function, IrType? result, Action<string, string, string> report) {

    private readonly Dictionary<IrLocal, Range> _ranges = [];
    private readonly Dictionary<IrLocal, IrLocal> _narrowed = [];
    private Dictionary<IrLocal, Range> _refined = [];
    private string _position = "";

    private void Report(string what) => report(function.Name, this._position, what);

    public IrFunction Run() {
      this.Ranges();
      foreach (var local in function.Locals.Where(local => local.Type.IsBigInt)) {
        var range = this._ranges.GetValueOrDefault(local, Range.Unbounded);
        if (TypeFor(range) is { } type) {
          this._narrowed[local] = new IrLocal(local.Name, type);
          this.Report($"applied: '{local.Name}' held in {type}, its values proven in {range} [proof: range-facts]");
        } else {
          this.Report($"applied: '{local.Name}' kept as arbitrary-precision integer, its range {range}");
        }
      }

      foreach (var parameter in function.Parameters.Where(parameter => parameter.Type.IsBigInt))
        this.Report($"applied: '{parameter.Name}' kept as arbitrary-precision integer, a parameter whose callers no contract bounds");

      if (result is not null)
        this.Report($"applied: the result held in {result}, every value returned proven in {this.Results()} [proof: range-facts]");

      var body = this.Block(function.Body, []);
      return function with {
        ReturnType = result ?? function.ReturnType,
        Locals = [.. function.Locals.Select(local => this._narrowed.GetValueOrDefault(local, local))],
        Body = body
      };
    }

    /// <summary>The range of every value the function returns; unbounded for a function that returns none.</summary>
    public Range Results() {
      if (this._ranges.Count == 0)
        this.Ranges();

      var values = IrPassSupport.Statements(function.Body).OfType<IrReturn>().Select(returned => returned.Value).OfType<IrExpr>().ToList();
      return values.Count == 0 ? Range.Unbounded : values.Select(this.RangeOf).Aggregate((first, second) => first.Join(second));
    }

    // ---- range facts ----

    /// <summary>
    /// The range of every variable of the arbitrary-precision type: the join of every value assigned to
    /// it, iterated until nothing grows; a variable that keeps growing is unbounded. A parameter is
    /// unbounded: its callers are bound by no contract.
    /// </summary>
    private void Ranges() {
      var growth = new Dictionary<IrLocal, int>();
      for (var round = 0; round < 256; ++round) {
        var changed = false;
        foreach (var (local, value, refined) in this.Definitions(function.Body, [])) {
          this._refined = refined;
          var range = this.RangeOf(value);
          var joined = this._ranges.TryGetValue(local, out var before) ? before.Join(range) : range;
          if (this._ranges.ContainsKey(local) && joined == before)
            continue;

          growth[local] = growth.GetValueOrDefault(local) + 1;
          this._ranges[local] = growth[local] > 8 && this._ranges.ContainsKey(local) ? Widened(before, joined) : joined;
          changed = true;
        }

        if (!changed)
          break;
      }

      this._refined = [];
    }

    /// <summary>
    /// A range that keeps growing, widened on each side that grew to the next limit of a fixed-width type
    /// - so that a mask or a test caps it there - and beyond the last to unbounded.
    /// </summary>
    private static Range Widened(Range before, Range now) {
      BigInteger[] highs = [int.MaxValue, uint.MaxValue, long.MaxValue, ulong.MaxValue];
      BigInteger[] lows = [0, int.MinValue, long.MinValue];
      return new(
        now.Low == before.Low || now.Low is not { } low ? now.Low : lows.Where(limit => limit <= low).Select(limit => (BigInteger?)limit).FirstOrDefault(),
        now.High == before.High || now.High is not { } high ? now.High : highs.Where(limit => limit >= high).Select(limit => (BigInteger?)limit).FirstOrDefault());
    }

    /// <summary>Every assignment of a value to a local of the arbitrary-precision type, with what a loop's test proves about the counter where it is stepped.</summary>
    private IEnumerable<(IrLocal Local, IrExpr Value, Dictionary<IrLocal, Range> Refined)> Definitions(IrBlock block, Dictionary<IrLocal, Range> refined) {
      foreach (var statement in block.Statements) {
        switch (statement) {
          case IrDeclare { Local.Type.IsBigInt: true, Initializer: { } value } declare when !function.Parameters.Contains(declare.Local):
            yield return (declare.Local, value, refined);
            break;
          case IrAssign { Local.Type.IsBigInt: true } assign when !function.Parameters.Contains(assign.Local):
            yield return (assign.Local, assign.Value, refined);
            break;
          case IrFor loop:
            foreach (var definition in this.Definitions(loop.Body, refined))
              yield return definition;

            foreach (var definition in this.Definitions(loop.Step, this.Bound(loop.Condition, loop.Body, refined)))
              yield return definition;

            break;
          default:
            foreach (var inner in IrTree.Blocks(statement))
              foreach (var definition in this.Definitions(inner, refined))
                yield return definition;

            break;
        }
      }
    }

    /// <summary>
    /// What the test <c>c &lt; e</c> or <c>c &gt; e</c> of a loop proves about the counter <c>c</c> in the
    /// step: it held when the pass began, and the body does not assign the counter.
    /// </summary>
    private Dictionary<IrLocal, Range> Bound(IrExpr condition, IrBlock body, Dictionary<IrLocal, Range> refined) {
      if (condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.Greater, Left: IrLocalRef { Local: var counter, Type.IsBigInt: true } } test
        || IrPassSupport.Writes(body).ContainsKey(counter))
        return refined;

      var bound = this.RangeOf(test.Right);
      var own = this._ranges.GetValueOrDefault(counter, Range.Unbounded);
      // The test bounds the counter whatever it held before: the fixpoint reaches the bound at once.
      var limited = test.Op == IrBinaryOp.Less
        ? bound.High is { } high ? new Range(own.Low, high - 1) : own
        : bound.Low is { } low ? new Range(low + 1, own.High) : own;
      return new Dictionary<IrLocal, Range>(refined) { [counter] = limited };
    }

    /// <summary>The range of the values an expression of the arbitrary-precision type can have.</summary>
    private Range RangeOf(IrExpr expression) {
      switch (expression) {
        case IrBigConst constant:
          return Range.Of(constant.Value);
        case IrLocalRef reference when this._refined.TryGetValue(reference.Local, out var refined):
          return refined;
        case IrLocalRef reference when !function.Parameters.Contains(reference.Local):
          // A variable not yet seen in the fixpoint holds nothing yet; its first value widens it.
          return this._ranges.TryGetValue(reference.Local, out var known) ? known : new Range(0, 0);
        case IrConvert { Type.IsBigInt: true, Operand.Type: { IsInteger: true } from }:
          return Range.Of(from);
        case IrConditional choice:
          return this.RangeOf(choice.WhenTrue).Join(this.RangeOf(choice.WhenFalse));
        case IrUnary { Op: IrUnaryOp.Negate } negation:
          var operand = this.RangeOf(negation.Operand);
          return new(-operand.High, -operand.Low);
        case IrUnary { Op: IrUnaryOp.BitNot } complement:
          var inverted = this.RangeOf(complement.Operand);
          return new(-inverted.High - 1, -inverted.Low - 1);
        case IrBinary binary:
          return this.RangeOf(binary);
        default:
          return Range.Unbounded;
      }
    }

    private Range RangeOf(IrBinary binary) {
      var left = this.RangeOf(binary.Left);
      if (binary.Op is IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight)
        return binary.Right is IrConst { Value: >= 0 and <= 4096 } count
          ? binary.Op == IrBinaryOp.ShiftLeft
            ? new(left.Low << (int)count.Value, left.High << (int)count.Value)
            : new(left.Low >> (int)count.Value, left.High >> (int)count.Value)
          : Range.Unbounded;

      var right = this.RangeOf(binary.Right);
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return new(left.Low + right.Low, left.High + right.High);
        case IrBinaryOp.Subtract:
          return new(left.Low - right.High, left.High - right.Low);
        case IrBinaryOp.Multiply when left.Bounded && right.Bounded:
          BigInteger[] products = [left.Low!.Value * right.Low!.Value, left.Low.Value * right.High!.Value, left.High!.Value * right.Low.Value, left.High.Value * right.High.Value];
          return new(products.Min(), products.Max());
        case IrBinaryOp.And when left.Low >= 0 || right.Low >= 0:
          // The two's complement of a value that is not negative has no bit above its own: the result is no greater.
          var high = left.Low >= 0 && right.Low >= 0 ? BigInteger.Min(left.High ?? right.High!.Value, right.High ?? left.High!.Value)
            : left.Low >= 0 ? left.High : right.High;
          return new(0, high);
        case IrBinaryOp.Or or IrBinaryOp.Xor when left.Low >= 0 && right.Low >= 0 && left.Bounded && right.Bounded:
          return new(0, (BigInteger.One << (int)BigInteger.Max(left.High!.Value, right.High!.Value).GetBitLength()) - 1);
        case IrBinaryOp.Divide when right.Low > 0 && left.Bounded:
          return new(BigInteger.Min(left.Low!.Value / right.Low!.Value, left.Low.Value / right.High!.Value), BigInteger.Max(left.High!.Value / right.Low.Value, left.High.Value / right.High.Value));
        case IrBinaryOp.Remainder when right.Low > 0 && right.High is { } divisor:
          return left.Low >= 0 ? new(0, divisor - 1) : new(1 - divisor, divisor - 1);
        default:
          return Range.Unbounded;
      }
    }

    // ---- rewriting ----

    private IrBlock Block(IrBlock block, IReadOnlyList<int> path) {
      var statements = new List<IrStmt>();
      for (var index = 0; index < block.Statements.Count; ++index) {
        var position = (IReadOnlyList<int>)[.. path, index + 1];
        this._position = string.Join('.', position);
        statements.Add(this.Statement(block.Statements[index], position));
      }

      return new IrBlock(statements);
    }

    private IrStmt Statement(IrStmt statement, IReadOnlyList<int> path) {
      switch (statement) {
        case IrDeclare declare when this._narrowed.TryGetValue(declare.Local, out var narrowed):
          return new IrDeclare(narrowed, declare.Initializer is null ? null : this.Lower(declare.Initializer, narrowed.Type));
        case IrAssign assign when this._narrowed.TryGetValue(assign.Local, out var narrowed):
          return new IrAssign(narrowed, this.Lower(assign.Value, narrowed.Type));
        case IrReturn { Value: { } value } when result is not null:
          return new IrReturn(this.Lower(value, result));
      }

      var blocks = 0;
      return IrPassSupport.Map(statement, this.Rewrite, inner => this.Block(inner, [.. path, ++blocks]));
    }

    /// <summary>An expression with every arbitrary-precision computation in it that a range bounds done in a fixed width.</summary>
    private IrExpr Rewrite(IrExpr expression) {
      switch (expression) {
        case IrLocalRef reference when this._narrowed.TryGetValue(reference.Local, out var narrowed):
          return new IrConvert(IrType.BigInt, new IrLocalRef(narrowed));
        case IrBigConst:
          return expression;
        case { Type.IsBigInt: true } when expression is IrBinary or IrUnary or IrConditional && TypeFor(this.RangeOf(expression)) is { } type:
          return new IrConvert(IrType.BigInt, this.Lower(expression, type));
        case IrBinary { Left.Type.IsBigInt: true, Op: >= IrBinaryOp.Equal } comparison
          when TypeFor(this.RangeOf(comparison.Left).Join(this.RangeOf(comparison.Right))) is { } common:
          return new IrBinary(comparison.Op, this.Lower(comparison.Left, common), this.Lower(comparison.Right, common));
        case IrConvert { Operand.Type.IsBigInt: true, Type.IsInteger: true } conversion
          when this.RangeOf(conversion.Operand) is var range && TypeFor(range) is { } from
            && (conversion.Contract == IrConversion.Wrap || range.Within(Range.Of(conversion.Type))):
          return Converted(conversion.Type, this.Lower(conversion.Operand, from));
        case IrConvert { Operand.Type.IsBigInt: true, Type.IsFloat: true } conversion when TypeFor(this.RangeOf(conversion.Operand)) is { } from:
          return new IrConvert(conversion.Type, this.Lower(conversion.Operand, from));
        default:
          return this.Operands(expression);
      }
    }

    /// <summary>
    /// The value of an arbitrary-precision expression as a value of a fixed-width type that holds every
    /// value the expression can have: computed in a fixed width where its operands are bounded too, by
    /// the mask form where it is a mask, and as the conversion of the exact value otherwise.
    /// </summary>
    private IrExpr Lower(IrExpr expression, IrType type) {
      switch (expression) {
        case IrBigConst constant:
          return new IrConst(type, IrBigInts.Wrap(type, constant.Value));
        case IrLocalRef reference when this._narrowed.TryGetValue(reference.Local, out var narrowed):
          return Converted(type, new IrLocalRef(narrowed));
        case IrConvert { Operand.Type.IsInteger: true } conversion when conversion.Type.IsBigInt:
          return Converted(type, conversion.Operand);
        case IrBinary { Op: IrBinaryOp.And } mask when Mask(mask.Right) is { } bits:
          return Converted(type, this.Masked(mask.Left, bits));
        case IrBinary { Op: IrBinaryOp.And } mask when Mask(mask.Left) is { } bits:
          return Converted(type, this.Masked(mask.Right, bits));
        case IrBinary or IrUnary or IrConditional when this.Computes(expression) is { } computed:
          return Converted(type, computed);
        default:
          return Converted(type, this.Operands(expression));
      }
    }

    /// <summary>An expression kept as it is, its operands rewritten.</summary>
    private IrExpr Operands(IrExpr expression) {
      var operands = IrTree.Operands(expression);
      return operands.Count == 0 ? expression : IrTree.With(expression, [.. operands.Select(this.Rewrite)]);
    }

    /// <summary>
    /// An operation of the arbitrary-precision type computed in the one fixed width that holds its
    /// result and the values of its operands, where there is one: no operation can wrap there.
    /// </summary>
    private IrExpr? Computes(IrExpr expression) {
      var range = this.RangeOf(expression);
      foreach (var operand in IrTree.Operands(expression).Where(operand => operand.Type.IsBigInt))
        range = range.Join(this.RangeOf(operand));

      if (TypeFor(range) is not { } type)
        return null;

      return expression switch {
        IrUnary { Op: IrUnaryOp.Negate } negation => new IrBinary(IrBinaryOp.Subtract, new IrConst(type, 0), this.Lower(negation.Operand, type)),
        IrUnary complement => new IrUnary(IrUnaryOp.BitNot, this.Lower(complement.Operand, type)),
        IrConditional choice => new IrConditional(this.Rewrite(choice.Condition), this.Lower(choice.WhenTrue, type), this.Lower(choice.WhenFalse, type)),
        IrBinary { Op: IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight, Right: IrConst { Value: >= 0 and < 64 } } shift
          => new IrBinary(shift.Op, this.Lower(shift.Left, type), shift.Right),
        IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor } binary
          => new IrBinary(binary.Op, this.Lower(binary.Left, type), this.Lower(binary.Right, type)),
        // A truncating division of values the type holds is the type's own, which traps only on a zero divisor here.
        IrBinary { Op: IrBinaryOp.Divide or IrBinaryOp.Remainder } binary when this.RangeOf(binary.Right).Low > 0
          => new IrBinary(binary.Op, this.Lower(binary.Left, type), this.Lower(binary.Right, type)),
        _ => null
      };
    }

    /// <summary>
    /// <c>e &amp; m</c> for a mask below 2^64: the low bits of <c>e</c>, computed modulo 2^32 (for a mask
    /// of up to 32 bits) or 2^64, then masked, as a value of the unsigned type of that size.
    /// </summary>
    private IrExpr Masked(IrExpr expression, BigInteger mask) {
      var type = mask.GetBitLength() <= 32 ? IrType.U32 : IrType.U64;
      var low = this.Low(expression, type);
      this.Report($"applied: & 0x{mask.ToString("X", CultureInfo.InvariantCulture).TrimStart('0')} -> {type} [wrap] (mask) [proof: range-facts]");
      return mask == (BigInteger.One << type.Bits) - 1 ? low : new IrBinary(IrBinaryOp.And, low, new IrConst(type, IrBigInts.Wrap(type, mask)));
    }

    /// <summary>
    /// An expression modulo 2^width of an unsigned type: the operations whose low bits depend only on
    /// the low bits of their operands are done in the type; anything else is computed as it is and
    /// its low bits taken, so the result is exact whatever the expression's range.
    /// </summary>
    private IrExpr Low(IrExpr expression, IrType type) {
      switch (expression) {
        case IrBigConst constant:
          return new IrConst(type, IrBigInts.Wrap(type, constant.Value));
        case IrLocalRef reference when this._narrowed.TryGetValue(reference.Local, out var narrowed):
          return Converted(type, new IrLocalRef(narrowed));
        case IrConvert { Operand.Type.IsInteger: true } conversion when conversion.Type.IsBigInt:
          return Converted(type, conversion.Operand);
        case IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor } binary:
          return new IrBinary(binary.Op, this.Low(binary.Left, type), this.Low(binary.Right, type));
        case IrUnary { Op: IrUnaryOp.Negate } negation:
          return new IrBinary(IrBinaryOp.Subtract, new IrConst(type, 0), this.Low(negation.Operand, type));
        case IrUnary complement:
          return new IrUnary(IrUnaryOp.BitNot, this.Low(complement.Operand, type));
        case IrBinary { Op: IrBinaryOp.ShiftLeft, Right: IrConst count } shift when count.Value >= 0 && count.Value < type.Bits:
          return new IrBinary(IrBinaryOp.ShiftLeft, this.Low(shift.Left, type), shift.Right);
        case IrBinary { Op: IrBinaryOp.ShiftLeft, Right: IrConst { Value: >= 0 } } shift when IrPassSupport.IsPure(shift.Left):
          // Shifted as far as the width or beyond, no low bit is left.
          return new IrConst(type, 0);
        case IrBinary { Op: IrBinaryOp.ShiftLeft }:
          this.Report("not applied: shift count may reach width");
          break;
        case IrBinary binary:
          this.Report($"not applied: masked expression contains {Symbol(binary.Op)}");
          break;
        case IrCall call:
          this.Report($"not applied: masked expression contains a call of '{call.Function}'");
          break;
      }

      // Not low-bit closed: exact, in a fixed width where the range proves that, and then cut.
      if (expression is IrBinary or IrUnary or IrConditional && this.Computes(expression) is { } computed)
        return Converted(type, computed);

      return TypeFor(this.RangeOf(expression)) is { } exact ? Converted(type, this.Lower(expression, exact)) : Converted(type, this.Rewrite(expression));
    }

    /// <summary>The operator of the arbitrary-precision type that is not low-bit closed: a right shift, a division or a remainder.</summary>
    private static string Symbol(IrBinaryOp op) => op switch {
      IrBinaryOp.ShiftRight => ">>",
      IrBinaryOp.Divide => "a truncating division",
      _ => "a truncating remainder"
    };

    /// <summary>A value converted to a fixed-width type, wrapping; nothing where it has the type already.</summary>
    private static IrExpr Converted(IrType type, IrExpr value) => value.Type == type ? value : new IrConvert(type, value);

  }

}
