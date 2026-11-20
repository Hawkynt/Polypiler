using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Core.Verification.Proofs.Obligations;

/// <summary>The operations an obligation may use: the integer and truth-value operations of ADR 0002.</summary>
internal enum ObligationOp {
  Add, Sub, Mul, Neg, And, Or, Xor, Not,
  Shl, LShr, AShr, RotateLeft, RotateRight,
  UDiv, URem, SDiv, SRem,
  Eq, Ne, ULt, ULe, UGt, UGe, SLt, SLe, SGt, SGe,
  Select, ZeroExtend, SignExtend, Truncate
}

/// <summary>What a shift does with an amount of at least its width (ADR 0002, "Shifts").</summary>
internal enum ShiftContract {

  /// <summary>The amount must be below the width; at or above it the fragment is invalid.</summary>
  Strict,

  /// <summary>The amount is taken modulo the width.</summary>
  Mask,

  /// <summary>An amount of at least the width shifts every bit out.</summary>
  Unbounded

}

/// <summary>An operand: a value defined before, or a constant of the type the operation expects.</summary>
internal abstract record ObligationOperand(ObligationType Type);

/// <summary>A value named by an input or by an earlier instruction.</summary>
/// <param name="Name">The name, without the <c>%</c>.</param>
/// <param name="Type">The type of the value.</param>
internal sealed record ObligationReference(string Name, ObligationType Type) : ObligationOperand(Type);

/// <summary>A constant, as its bit pattern in its type.</summary>
/// <param name="Bits">The bit pattern.</param>
/// <param name="Type">The type.</param>
internal sealed record ObligationConstant(ulong Bits, ObligationType Type) : ObligationOperand(Type);

/// <summary>One instruction: <c>%target = Op.type[contracts] operand, operand</c>.</summary>
/// <param name="Target">The name the instruction defines.</param>
/// <param name="Op">The operation.</param>
/// <param name="Type">The type written after the operation: of the operands for a comparison, of the result otherwise.</param>
/// <param name="Result">The type of the value defined.</param>
/// <param name="Operands">The operands, in order.</param>
/// <param name="Text">The operation as written, such as <c>Shl.u8[strict]</c>.</param>
internal sealed record ObligationInstruction(string Target, ObligationOp Op, ObligationType Type, ObligationType Result, IReadOnlyList<ObligationOperand> Operands, string Text) {

  /// <summary>The amount contract of a shift.</summary>
  public ShiftContract Shift { get; init; }

  /// <summary>Whether a signed division traps on the smallest value divided by -1 instead of wrapping.</summary>
  public bool OverflowTraps { get; init; }

}

/// <summary>A fragment: instructions that define, in the end, the value named by <see cref="Result"/>.</summary>
/// <param name="Instructions">The instructions, in order.</param>
/// <param name="Result">The name of the value the fragment defines.</param>
/// <param name="Type">The type of that value.</param>
internal sealed record ObligationFragment(IReadOnlyList<ObligationInstruction> Instructions, string Result, ObligationType Type);

/// <summary>An input of an obligation with its type at the width the obligation is read at.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
internal sealed record ObligationVariable(string Name, ObligationType Type);

/// <summary>
/// An operation the obligation's text spells, or a form of text, that no prover models: the result of
/// a proof is <see cref="ProofResult.Unsupported"/>.
/// </summary>
/// <param name="operation">The operation as written.</param>
/// <param name="detail">What is wrong with it, for an operation the parser does not understand; <see langword="null"/> for one it does but no prover models.</param>
internal sealed class ObligationUnsupportedException(string operation, string? detail = null) : Exception(detail ?? $"operation '{operation}' is not modelled") {

  /// <summary>The operation as written.</summary>
  public string Operation { get; } = operation;

  /// <summary>What the parser said, or <see langword="null"/>.</summary>
  public string? Detail { get; } = detail;

}

/// <summary>
/// An obligation read at one width: its inputs, preconditions and both sides as instructions. The IR
/// notation is that of <c>backlog/README.md</c> and ADR 0002 - one instruction per line or per
/// <c>;</c>, <c>%result = Op.type[contracts] operand, operand</c> - with <c>&lt;w&gt;</c> standing
/// for the width, in a type (<c>u&lt;w&gt;</c>) and as a constant.
/// </summary>
internal sealed partial class ObligationProgram {

  private static readonly HashSet<string> _memory = new(StringComparer.Ordinal) { "Load", "Store", "Call", "Alloca", "GetElementPtr", "AtomicLoad", "AtomicStore", "Fence" };

  private ObligationProgram(IReadOnlyList<ObligationVariable> inputs, IReadOnlyList<ObligationFragment> preconditions, ObligationFragment before, ObligationFragment after, int width) {
    this.Inputs = inputs;
    this.Preconditions = preconditions;
    this.Before = before;
    this.After = after;
    this.Width = width;
  }

  /// <summary>The inputs, in order.</summary>
  public IReadOnlyList<ObligationVariable> Inputs { get; }

  /// <summary>The preconditions, each defining a truth value.</summary>
  public IReadOnlyList<ObligationFragment> Preconditions { get; }

  /// <summary>The side before the rewrite.</summary>
  public ObligationFragment Before { get; }

  /// <summary>The side after the rewrite.</summary>
  public ObligationFragment After { get; }

  /// <summary>The width <c>&lt;w&gt;</c> was read as.</summary>
  public int Width { get; }

  /// <summary>Whether any text of an obligation uses the width parameter <c>&lt;w&gt;</c>.</summary>
  /// <param name="obligation">The obligation.</param>
  /// <returns><see langword="true"/> for a width-generic obligation.</returns>
  public static bool IsWidthGeneric(ProofObligation obligation)
    => new[] { obligation.Before, obligation.After }.Concat(obligation.Preconditions).Concat(obligation.Inputs.Select(input => input.Type)).Any(text => text.Contains(WidthParameter, StringComparison.Ordinal));

  /// <summary>The width parameter as an obligation writes it.</summary>
  public const string WidthParameter = "<w>";

  /// <summary>Reads an obligation at a width.</summary>
  /// <param name="obligation">The obligation.</param>
  /// <param name="width">What <c>&lt;w&gt;</c> stands for.</param>
  /// <returns>The program.</returns>
  /// <exception cref="ObligationUnsupportedException">An operation, a type or a constant is outside the model, or the text cannot be read.</exception>
  /// <exception cref="ArgumentException">The sides name values that are neither inputs nor defined before, or their results differ in type.</exception>
  public static ObligationProgram Parse(ProofObligation obligation, int width)
    => Parse(obligation.Before, obligation.After, obligation.Inputs, obligation.Preconditions, obligation.Result, width);

  /// <summary>Reads the parts of an obligation at a width.</summary>
  /// <param name="beforeText">The before side.</param>
  /// <param name="afterText">The after side.</param>
  /// <param name="variables">The inputs.</param>
  /// <param name="conditions">The preconditions.</param>
  /// <param name="result">The name of the value both sides define.</param>
  /// <param name="width">What <c>&lt;w&gt;</c> stands for.</param>
  /// <returns>The program.</returns>
  /// <exception cref="ObligationUnsupportedException">An operation, a type or a constant is outside the model, or the text cannot be read.</exception>
  /// <exception cref="ArgumentException">The sides name values that are neither inputs nor defined before, or their results differ in type.</exception>
  public static ObligationProgram Parse(string beforeText, string afterText, IReadOnlyList<ProofInput> variables, IReadOnlyList<string> conditions, string result, int width) {
    string At(string text) => text.Replace(WidthParameter, width.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    var inputs = new List<ObligationVariable>();
    foreach (var input in variables) {
      var text = At(input.Type);
      if (!ObligationType.TryParse(text, out var type))
        throw new ObligationUnsupportedException(text, $"'{text}' is not a type");
      inputs.Add(new(input.Name, type));
    }

    var preconditions = conditions.Select((text, index) => Fragment(At(text), null, inputs, $"precondition {index + 1}")).ToList();
    foreach (var (precondition, index) in preconditions.Select((precondition, index) => (precondition, index)))
      if (precondition.Type != ObligationType.Bool)
        throw new ArgumentException($"Precondition {index + 1} defines a {precondition.Type}, not a bool.", nameof(conditions));

    var before = Fragment(At(beforeText), result, inputs, "before");
    var after = Fragment(At(afterText), result, inputs, "after");
    if (before.Type != after.Type)
      throw new ArgumentException($"The before side defines '%{result}' as {before.Type}, the after side as {after.Type}.", nameof(afterText));

    // An input of floating point that no instruction reads is still outside the model.
    if (inputs.FirstOrDefault(input => input.Type.Kind == ObligationTypeKind.Float) is { } number)
      throw new ObligationUnsupportedException(number.Type.ToString());

    return new(inputs, preconditions, before, after, width);
  }

  /// <summary>Reads the instructions of one fragment.</summary>
  private static ObligationFragment Fragment(string text, string? result, IReadOnlyList<ObligationVariable> inputs, string side) {
    var values = inputs.ToDictionary(input => input.Name, input => input.Type, StringComparer.Ordinal);
    var instructions = new List<ObligationInstruction>();
    foreach (var line in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var instruction = Instruction(line, values, side);
      if (!values.TryAdd(instruction.Target, instruction.Result))
        throw new ArgumentException($"The {side} side defines '%{instruction.Target}' twice.");

      instructions.Add(instruction);
    }

    if (instructions.Count == 0)
      throw new ArgumentException($"The {side} side has no instruction.");

    var name = result ?? instructions[^1].Target;
    var defined = instructions.FirstOrDefault(instruction => instruction.Target == name)
      ?? throw new ArgumentException($"The {side} side does not define '%{name}'.");
    return new(instructions, name, defined.Result);
  }

  /// <summary>Reads one instruction against the values defined before it.</summary>
  private static ObligationInstruction Instruction(string line, Dictionary<string, ObligationType> values, string side) {
    var match = InstructionPattern().Match(line);
    if (!match.Success)
      throw new ObligationUnsupportedException(line, $"'{line}' is not an instruction '%name = Op.type operand, ...'");

    var target = match.Groups["target"].Value;
    var name = match.Groups["op"].Value;
    var typeText = match.Groups["type"].Value;
    var contracts = match.Groups["contracts"].Success
      ? match.Groups["contracts"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      : [];
    var text = $"{name}.{typeText}" + (contracts.Length > 0 ? $"[{string.Join(", ", contracts)}]" : "");
    if (_memory.Contains(name))
      throw new ObligationUnsupportedException(name);
    if (!Enum.TryParse<ObligationOp>(name, false, out var op))
      throw new ObligationUnsupportedException(text, $"unknown operation '{name}'");
    if (!ObligationType.TryParse(typeText, out var type))
      throw new ObligationUnsupportedException(text, $"'{typeText}' is not a type");
    if (type.Kind == ObligationTypeKind.Float)
      throw new ObligationUnsupportedException(text);

    var arguments = match.Groups["operands"].Value.Split(',', StringSplitOptions.TrimEntries);
    if (arguments.Length == 1 && arguments[0].Length == 0)
      arguments = [];

    var shape = Shape(op, type) ?? throw new ObligationUnsupportedException(text, $"'{name}' is not defined on {type}");
    if (arguments.Length != shape.Operands.Length)
      throw new ObligationUnsupportedException(text, $"'{name}' takes {shape.Operands.Length} operands, not {arguments.Length}");

    var operands = new ObligationOperand[arguments.Length];
    for (var index = 0; index < arguments.Length; ++index)
      operands[index] = Operand(arguments[index], shape.Operands[index], op is ObligationOp.ZeroExtend or ObligationOp.SignExtend or ObligationOp.Truncate ? null : type, values, side, text);

    // A conversion's operand is an integer whose width lies on the right side of the result's.
    if (op is ObligationOp.ZeroExtend or ObligationOp.SignExtend or ObligationOp.Truncate) {
      var source = operands[0].Type;
      if (!source.IsInteger || (op == ObligationOp.Truncate ? source.Width < type.Width : source.Width > type.Width))
        throw new ObligationUnsupportedException(text, $"'{name}' cannot turn a {source} into a {type}");
    }

    return new(target, op, type, shape.Result, operands, text) {
      Shift = Shift(op, contracts, text),
      OverflowTraps = Overflow(op, contracts, text)
    };
  }

  /// <summary>The types an operation's operands must have, and the type of its result; <see langword="null"/> when it is not defined on the type.</summary>
  private static (ObligationType?[] Operands, ObligationType Result)? Shape(ObligationOp op, ObligationType type) {
    var integer = type.IsInteger;
    var truth = type == ObligationType.Bool;
    ObligationType? any = null;
    return op switch {
      ObligationOp.Add or ObligationOp.Sub or ObligationOp.Mul
        or ObligationOp.UDiv or ObligationOp.URem or ObligationOp.SDiv or ObligationOp.SRem when integer => ([type, type], type),
      ObligationOp.And or ObligationOp.Or or ObligationOp.Xor when integer || truth => ([type, type], type),
      ObligationOp.Neg when integer => ([type], type),
      ObligationOp.Not when integer || truth => ([type], type),
      ObligationOp.Shl or ObligationOp.LShr or ObligationOp.AShr
        or ObligationOp.RotateLeft or ObligationOp.RotateRight when integer => ([type, any], type),
      ObligationOp.Eq or ObligationOp.Ne when integer || truth => ([type, type], ObligationType.Bool),
      ObligationOp.ULt or ObligationOp.ULe or ObligationOp.UGt or ObligationOp.UGe
        or ObligationOp.SLt or ObligationOp.SLe or ObligationOp.SGt or ObligationOp.SGe when integer => ([type, type], ObligationType.Bool),
      ObligationOp.Select when integer || truth => ([ObligationType.Bool, type, type], type),
      ObligationOp.ZeroExtend or ObligationOp.SignExtend or ObligationOp.Truncate when integer => ([any], type),
      _ => null
    };
  }

  /// <summary>
  /// Reads an operand. A name must be defined before and have the expected type; a constant takes the
  /// expected type, or that of the first operand where any integer would do, and must fit it.
  /// </summary>
  private static ObligationOperand Operand(string text, ObligationType? expected, ObligationType? constant, Dictionary<string, ObligationType> values, string side, string operation) {
    if (text.StartsWith('%')) {
      var name = text[1..];
      if (!values.TryGetValue(name, out var type))
        throw new ArgumentException($"The {side} side reads '%{name}', which is neither an input nor defined before.");
      if (expected is { } wanted ? type != wanted : !type.IsInteger)
        throw new ObligationUnsupportedException(operation, $"'%{name}' is a {type}, not a {(expected is { } shown ? shown.ToString() : "integer")}");

      return new ObligationReference(name, type);
    }

    var constantType = expected ?? constant ?? throw new ObligationUnsupportedException(operation, $"the operand '{text}' of '{operation}' must be a value, not a constant");
    return new ObligationConstant(Constant(text, constantType, operation), constantType);
  }

  /// <summary>The bit pattern of a constant in a type.</summary>
  private static ulong Constant(string text, ObligationType type, string operation) {
    if (type == ObligationType.Bool)
      return text switch {
        "true" => 1,
        "false" => 0,
        _ => throw new ObligationUnsupportedException(operation, $"'{text}' is not a bool")
      };

    var negative = text.StartsWith('-');
    var digits = negative ? text[1..] : text;
    var parsed = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? ulong.TryParse(digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : (ulong?)null
      : ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    if (parsed is not { } magnitude)
      throw new ObligationUnsupportedException(operation, $"'{text}' is not a constant");

    // An unsigned constant fits when it is below 2^width; a negative one when it is at least -2^(width-1).
    var fits = negative ? magnitude <= (1UL << (type.Width - 1)) : magnitude <= type.Mask;
    if (!fits)
      throw new ObligationUnsupportedException(operation, $"the constant {text} does not fit {type}");

    return (negative ? 0 - magnitude : magnitude) & type.Mask;
  }

  /// <summary>The amount contract of a shift: written, or <see cref="ShiftContract.Strict"/> when none is.</summary>
  private static ShiftContract Shift(ObligationOp op, string[] contracts, string text) {
    if (op is not (ObligationOp.Shl or ObligationOp.LShr or ObligationOp.AShr)) {
      if (op is ObligationOp.Add or ObligationOp.Sub or ObligationOp.Mul or ObligationOp.Neg or ObligationOp.Truncate) {
        if (contracts.Any(contract => contract is not ("wrap" or "modular")))
          throw new ObligationUnsupportedException(text);
      } else if (op is not (ObligationOp.UDiv or ObligationOp.URem or ObligationOp.SDiv or ObligationOp.SRem) && contracts.Length > 0)
        throw new ObligationUnsupportedException(text, $"'{text}' takes no contract");

      return ShiftContract.Strict;
    }

    return contracts switch {
      [] or ["strict"] => ShiftContract.Strict,
      ["mask"] => ShiftContract.Mask,
      ["unbounded"] => ShiftContract.Unbounded,
      _ => throw new ObligationUnsupportedException(text)
    };
  }

  /// <summary>
  /// Whether a division traps on overflow. A division traps on a zero divisor, which is the only
  /// zero-divisor contract modelled; a signed one wraps on the smallest value divided by -1 unless it
  /// says <c>ovf=trap</c> or <c>checked</c>.
  /// </summary>
  private static bool Overflow(ObligationOp op, string[] contracts, string text) {
    if (op is not (ObligationOp.UDiv or ObligationOp.URem or ObligationOp.SDiv or ObligationOp.SRem))
      return false;

    var traps = false;
    foreach (var contract in contracts)
      if (contract is "checked" || contract.StartsWith("ovf=trap", StringComparison.Ordinal))
        traps = true;
      else if (contract is not ("wrap" or "ovf=wrap" or "trap" or "trap DivideByZero"))
        throw new ObligationUnsupportedException(text);

    return traps && op is ObligationOp.SDiv or ObligationOp.SRem;
  }

  [GeneratedRegex(@"^%(?<target>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<op>[A-Za-z][A-Za-z0-9]*)\.(?<type>[a-z][a-z0-9]*)(?:\[(?<contracts>[^\]]*)\])?(?:\s+(?<operands>.*))?$")]
  private static partial Regex InstructionPattern();

}
