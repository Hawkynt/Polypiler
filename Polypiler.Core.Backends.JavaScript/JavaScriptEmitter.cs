using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// Emits one ES module per IR module. Every operation is written so that JavaScript reproduces it
/// exactly. Integers of up to 32 bits are numbers: <c>| 0</c> and <c>Math.imul</c> for <c>i32</c>,
/// <c>&gt;&gt;&gt; 0</c> for <c>u32</c>, masks and sign-extending shifts for the 8- and 16-bit types.
/// Integers of 64 bits are <c>BigInt</c>s cut to size with <c>BigInt.asIntN</c> and
/// <c>BigInt.asUintN</c>. Arrays are typed arrays. The operations that can trap go through small
/// helper functions, of which a module carries exactly the ones it uses.
/// </summary>
public static class JavaScriptEmitter {

  private const string Overflow = "throw new RangeError(\"integer overflow\");";
  private const string ZeroDivisor = "  if (b === 0) throw new RangeError(\"divide by zero\");";
  private const string ZeroDivisor64 = "  if (b === 0n) throw new RangeError(\"divide by zero\");";
  private const string Unrepresentable = "  if (a === -2147483648 && b === -1) " + Overflow;
  private const string Unrepresentable64 = "  if (a === -9223372036854775808n && b === -1n) " + Overflow;
  private const string OutOfRange = "  if (index < 0 || index >= array.length) throw new RangeError(\"index out of range\");";

  /// <summary>The helper functions, in the order a module lists the ones it uses.</summary>
  private static readonly (string Name, string[] Lines)[] _helpers = [
    ("$div", ["function $div(a, b) {", ZeroDivisor, Unrepresentable, "  return (a / b) | 0;", "}"]),
    ("$rem", ["function $rem(a, b) {", ZeroDivisor, Unrepresentable, "  return (a % b) | 0;", "}"]),
    ("$udiv", ["function $udiv(a, b) {", ZeroDivisor, "  return (a / b) >>> 0;", "}"]),
    ("$urem", ["function $urem(a, b) {", ZeroDivisor, "  return a % b;", "}"]),
    ("$div64", ["function $div64(a, b) {", ZeroDivisor64, Unrepresentable64, "  return a / b;", "}"]),
    ("$rem64", ["function $rem64(a, b) {", ZeroDivisor64, Unrepresentable64, "  return a % b;", "}"]),
    ("$udiv64", ["function $udiv64(a, b) {", ZeroDivisor64, "  return a / b;", "}"]),
    ("$urem64", ["function $urem64(a, b) {", ZeroDivisor64, "  return a % b;", "}"]),
    ("$ck", ["function $ck(value, low, high) {", "  if (value < low || value > high) " + Overflow, "  return value;", "}"]),
    ("$get", ["function $get(array, index) {", OutOfRange, "  return array[index];", "}"]),
    ("$set", ["function $set(array, index, value) {", OutOfRange, "  array[index] = value;", "}"]),
    ("$len", ["function $len(length) {", "  if (length < 0) " + Overflow, "  return length;", "}"])
  ];

  /// <summary>
  /// Words a JavaScript module cannot use as a name, and the globals the emitted code relies on. An
  /// identifier that is one of them gets a <c>$</c> appended, which no C# identifier can contain.
  /// </summary>
  private static readonly HashSet<string> _reserved = [
    "arguments", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
    "delete", "do", "else", "enum", "eval", "export", "extends", "false", "finally", "for", "function",
    "if", "implements", "import", "in", "instanceof", "interface", "let", "new", "null", "package",
    "private", "protected", "public", "return", "static", "super", "switch", "this", "throw", "true",
    "try", "typeof", "var", "void", "while", "with", "yield",
    "Math", "RangeError", "BigInt", "Number", "Int8Array", "Uint8Array", "Int16Array", "Uint16Array",
    "Int32Array", "Uint32Array", "BigInt64Array", "BigUint64Array"
  ];

  /// <summary>Emits the module as <c>&lt;name&gt;.mjs</c>, exporting every public function.</summary>
  /// <param name="module">A module that <see cref="IrVerifier"/> accepts.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) {
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    return [new GeneratedFile(module.Name + ".mjs", new Writer().Module(module))];
  }

  /// <summary>The typed array that stores the elements of an array type.</summary>
  private static string Storage(IrType element) => element.Kind switch {
    IrTypeKind.I8 => "Int8Array",
    IrTypeKind.U8 => "Uint8Array",
    IrTypeKind.I16 => "Int16Array",
    IrTypeKind.U16 => "Uint16Array",
    IrTypeKind.I32 => "Int32Array",
    IrTypeKind.U32 => "Uint32Array",
    IrTypeKind.I64 => "BigInt64Array",
    _ => "BigUint64Array"
  };

  /// <summary>What a value of an IR type is in JavaScript, as the header of a function states it.</summary>
  private static string Describe(IrType type) => type.Kind switch {
    IrTypeKind.Void => "void",
    IrTypeKind.Bool => "boolean",
    IrTypeKind.Array => Storage(type.Element!),
    IrTypeKind.I64 or IrTypeKind.U64 => "bigint",
    _ => "number"
  };

  /// <summary>An integer as a literal of its type: a number, or a <c>BigInt</c> for 64 bits.</summary>
  private static string Literal(IrType type, Int128 value)
    => value.ToString(CultureInfo.InvariantCulture) + (type.IsLong ? "n" : "");

  private sealed class Writer {

    private readonly StringBuilder _code = new();
    private readonly HashSet<string> _used = [];

    public string Module(IrModule module) {
      foreach (var function in module.Functions)
        this.Function(function);

      var text = new StringBuilder();
      text.Append("// ").Append(module.Name).Append(": generated by polypiler. Change the source, not this file.\n");
      foreach (var (name, lines) in _helpers)
        if (this._used.Contains(name))
          text.Append('\n').AppendJoin('\n', lines).Append('\n');

      return text.Append(this._code).ToString();
    }

    /// <summary>
    /// A function under a comment that states what crosses its boundary: integers of up to 32 bits
    /// are numbers, those of 64 bits are <c>BigInt</c>s, arrays are typed arrays.
    /// </summary>
    private void Function(IrFunction function) {
      var parameters = string.Join(", ", function.Parameters.Select(parameter => Name(parameter.Name)));
      var signature = string.Join(", ", function.Parameters.Select(parameter => $"{Name(parameter.Name)}: {Describe(parameter.Type)}"));
      this._code.Append("\n// ").Append(Name(function.Name)).Append('(').Append(signature).Append("): ")
        .Append(Describe(function.ReturnType)).Append('\n');
      this._code.Append(function.IsPublic ? "export function " : "function ")
        .Append(Name(function.Name)).Append('(').Append(parameters).Append(") {\n");
      this.Statements(function.Body, 1);
      this._code.Append("}\n");
    }

    private void Statements(IrBlock block, int depth) {
      foreach (var statement in block.Statements)
        this.Statement(statement, depth);
    }

    private StringBuilder Line(int depth) => this._code.Append(' ', depth * 2);

    private void Statement(IrStmt statement, int depth) {
      switch (statement) {
        case IrBlock block:
          this.Line(depth).Append("{\n");
          this.Statements(block, depth + 1);
          this.Line(depth).Append("}\n");
          break;
        case IrDeclare declare:
          this.Line(depth).Append("let ").Append(Name(declare.Local.Name));
          if (declare.Initializer is not null)
            this._code.Append(" = ").Append(this.Text(declare.Initializer));

          this._code.Append(";\n");
          break;
        case IrAssign assign:
          this.Line(depth).Append(Name(assign.Local.Name)).Append(" = ").Append(this.Text(assign.Value)).Append(";\n");
          break;
        case IrStore store:
          this.Line(depth).Append(this.Helper("$set", store.Array, store.Index, store.Value)).Append(";\n");
          break;
        case IrExprStmt expression:
          this.Line(depth).Append(this.Text(expression.Expression)).Append(";\n");
          break;
        case IrIf conditional:
          this.Line(depth);
          this.If(conditional, depth);
          break;
        case IrWhile loop:
          this.Line(depth).Append("while (").Append(this.Text(loop.Condition)).Append(") {\n");
          this.Statements(loop.Body, depth + 1);
          this.Line(depth).Append("}\n");
          break;
        default:
          var result = (IrReturn)statement;
          this.Line(depth).Append(result.Value is null ? "return" : "return " + this.Text(result.Value)).Append(";\n");
          break;
      }
    }

    private void If(IrIf conditional, int depth) {
      this._code.Append("if (").Append(this.Text(conditional.Condition)).Append(") {\n");
      this.Statements(conditional.Then, depth + 1);
      this.Line(depth).Append('}');
      if (conditional.Else is null) {
        this._code.Append('\n');
      } else if (conditional.Else.Statements is [IrIf chained]) {
        this._code.Append(" else ");
        this.If(chained, depth);
      } else {
        this._code.Append(" else {\n");
        this.Statements(conditional.Else, depth + 1);
        this.Line(depth).Append("}\n");
      }
    }

    private string Helper(string name, params IrExpr[] arguments) => this.Helper(name, arguments.Select(this.Text));

    private string Helper(string name, IEnumerable<string> arguments) {
      this._used.Add(name);
      return $"{name}({string.Join(", ", arguments)})";
    }

    /// <summary>The expression as it stands on its own: as a statement, an argument or a right-hand side.</summary>
    private string Text(IrExpr expression) => this.Expression(expression).Text;

    /// <summary>The expression as an operand: in parentheses unless it already binds tighter than any operator.</summary>
    private string Operand(IrExpr expression) {
      var (text, atomic) = this.Expression(expression);
      return atomic ? text : $"({text})";
    }

    private (string Text, bool Atomic) Expression(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          if (constant.Type == IrType.Bool)
            return (constant.Value != 0 ? "true" : "false", true);

          var number = IrOperators.Number(constant.Type, constant.Value);
          return (Literal(constant.Type, number), number >= 0);
        case IrLocalRef reference:
          return (Name(reference.Local.Name), true);
        case IrUnary unary:
          return this.Unary(unary);
        case IrBinary binary:
          return binary.Left.Type.IsLong ? this.Binary64(binary) : this.Binary(binary);
        case IrConvert conversion:
          return this.Convert(conversion);
        case IrConditional choice:
          return ($"{this.Operand(choice.Condition)} ? {this.Operand(choice.WhenTrue)} : {this.Operand(choice.WhenFalse)}", false);
        case IrArrayLoad load:
          return (this.Helper("$get", load.Array, load.Index), true);
        case IrArrayLength length:
          return ($"{this.Operand(length.Array)}.length", true);
        case IrNewArray creation:
          return ($"new {Storage(creation.Type.Element!)}({this.Helper("$len", creation.Length)})", true);
        default:
          var call = (IrCall)expression;
          return ($"{Name(call.Function)}({string.Join(", ", call.Arguments.Select(this.Text))})", true);
      }
    }

    /// <summary>A result that may have left its type, cut back to it: modulo 2^width.</summary>
    private static (string Text, bool Atomic) Wrap(IrType type, string text) => type.Kind switch {
      IrTypeKind.I64 => ($"BigInt.asIntN(64, {text})", true),
      IrTypeKind.U64 => ($"BigInt.asUintN(64, {text})", true),
      IrTypeKind.I32 => ($"({text}) | 0", false),
      _ => ($"({text}) >>> 0", false)
    };

    /// <summary>
    /// A result checked against the range of its type; it traps when it is outside. A number that can
    /// come out as the negative zero an integer does not have, is made the zero it has.
    /// </summary>
    private (string Text, bool Atomic) Check(IrType type, string text, bool signedZero = false) {
      var inRange = this.Helper("$ck", [text, Literal(type, IrOperators.Minimum(type)), Literal(type, IrOperators.Maximum(type))]);
      return signedZero && type == IrType.I32 ? (inRange + " | 0", false) : (inRange, true);
    }

    private (string Text, bool Atomic) Unary(IrUnary unary) {
      var operand = this.Operand(unary.Operand);
      var type = unary.Type;
      switch (unary.Op) {
        case IrUnaryOp.Negate:
          return type.IsLong ? Wrap(type, "-" + operand) : ($"-{operand} | 0", false);
        case IrUnaryOp.NegateChecked:
          return this.Check(type, "-" + operand, true);
        case IrUnaryOp.BitNot:
          return type.IsSigned ? ("~" + operand, false) : type.IsLong ? Wrap(type, "~" + operand) : ($"~{operand} >>> 0", false);
        default:
          return ("!" + operand, false);
      }
    }

    /// <summary>An operator on numbers: 32-bit integers and truth values.</summary>
    private (string Text, bool Atomic) Binary(IrBinary binary) {
      var type = binary.Left.Type;
      var signed = type == IrType.I32;
      switch (binary.Op) {
        case IrBinaryOp.Multiply:
          var product = $"Math.imul({this.Text(binary.Left)}, {this.Text(binary.Right)})";
          return signed ? (product, true) : ($"{product} >>> 0", false);
        case IrBinaryOp.Divide:
          return (this.Helper(signed ? "$div" : "$udiv", binary.Left, binary.Right), true);
        case IrBinaryOp.Remainder:
          return (this.Helper(signed ? "$rem" : "$urem", binary.Left, binary.Right), true);
      }

      var left = this.Operand(binary.Left);
      var right = this.Operand(binary.Right);
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return Wrap(type, $"{left} + {right}");
        case IrBinaryOp.Subtract:
          return Wrap(type, $"{left} - {right}");
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          // Sum and difference of two 32-bit integers are exact in a double. A product that is not
          // exact is beyond 2^53 and so outside the range on either side of the rounding.
          return this.Check(type, $"{left} {CheckedSign(binary.Op)} {right}", binary.Op == IrBinaryOp.MultiplyChecked);
      }

      return (binary.Op switch {
        IrBinaryOp.And => signed ? $"{left} & {right}" : $"({left} & {right}) >>> 0",
        IrBinaryOp.Or => signed ? $"{left} | {right}" : $"({left} | {right}) >>> 0",
        IrBinaryOp.Xor => signed ? $"{left} ^ {right}" : $"({left} ^ {right}) >>> 0",
        IrBinaryOp.ShiftLeft => signed ? $"{left} << {right}" : $"({left} << {right}) >>> 0",
        IrBinaryOp.ShiftRight => signed ? $"{left} >> {right}" : $"{left} >>> {right}",
        IrBinaryOp.ShiftRightLogical => signed ? $"({left} >>> {right}) | 0" : $"{left} >>> {right}",
        _ => $"{left} {Sign(binary.Op)} {right}"
      }, false);
    }

    /// <summary>
    /// An operator on 64-bit integers, which are <c>BigInt</c>s. An unsigned value is never negative,
    /// so the bitwise operators and the shift to the right need no cutting back; the count of a shift
    /// is a number and is masked to 6 bits as C# masks it.
    /// </summary>
    private (string Text, bool Atomic) Binary64(IrBinary binary) {
      var type = binary.Left.Type;
      var signed = type == IrType.I64;
      switch (binary.Op) {
        case IrBinaryOp.Divide:
          return (this.Helper(signed ? "$div64" : "$udiv64", binary.Left, binary.Right), true);
        case IrBinaryOp.Remainder:
          return (this.Helper(signed ? "$rem64" : "$urem64", binary.Left, binary.Right), true);
      }

      var left = this.Operand(binary.Left);
      var right = this.Operand(binary.Right);
      var count = binary.Right is IrConst constant ? $"{constant.Value & 63}n" : $"BigInt({right} & 63)";
      switch (binary.Op) {
        case IrBinaryOp.Add:
          return Wrap(type, $"{left} + {right}");
        case IrBinaryOp.Subtract:
          return Wrap(type, $"{left} - {right}");
        case IrBinaryOp.Multiply:
          return Wrap(type, $"{left} * {right}");
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          return this.Check(type, $"{left} {CheckedSign(binary.Op)} {right}");
        case IrBinaryOp.ShiftLeft:
          return Wrap(type, $"{left} << {count}");
        case IrBinaryOp.ShiftRightLogical when signed:
          return Wrap(type, $"BigInt.asUintN(64, {left}) >> {count}");
        case IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical:
          return ($"{left} >> {count}", false);
        default:
          return ($"{left} {Sign(binary.Op)} {right}", false);
      }
    }

    private static string CheckedSign(IrBinaryOp op) => op switch {
      IrBinaryOp.AddChecked => "+",
      IrBinaryOp.SubtractChecked => "-",
      _ => "*"
    };

    /// <summary>The operators JavaScript writes as the IR means them, whatever the type of the operands.</summary>
    private static string Sign(IrBinaryOp op) => op switch {
      IrBinaryOp.And => "&",
      IrBinaryOp.Or => "|",
      IrBinaryOp.Xor => "^",
      IrBinaryOp.Equal => "===",
      IrBinaryOp.NotEqual => "!==",
      IrBinaryOp.Less => "<",
      IrBinaryOp.LessOrEqual => "<=",
      IrBinaryOp.Greater => ">",
      IrBinaryOp.GreaterOrEqual => ">=",
      IrBinaryOp.LogicalAnd => "&&",
      _ => "||"
    };

    private (string Text, bool Atomic) Convert(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      if (conversion.Contract == IrConversion.Trap) {
        // The bounds are those of the target as far as the source reaches, in the notation of the source.
        var low = Int128.Max(IrOperators.Minimum(to), IrOperators.Minimum(from));
        var high = Int128.Min(IrOperators.Maximum(to), IrOperators.Maximum(from));
        var inRange = this.Helper("$ck", [this.Text(conversion.Operand), Literal(from, low), Literal(from, high)]);
        return (from.IsLong == to.IsLong ? inRange : to.IsLong ? $"BigInt({inRange})" : $"Number({inRange})", true);
      }

      var lossless = IrOperators.Contains(to, from);
      if (to.IsLong) {
        var big = from.IsLong ? this.Text(conversion.Operand) : $"BigInt({this.Text(conversion.Operand)})";
        return lossless ? (big, true) : Wrap(to, big);
      }

      if (from.IsLong)
        return ($"Number(BigInt.as{(to.IsSigned ? "Int" : "Uint")}N({to.Bits}, {this.Text(conversion.Operand)}))", true);

      if (lossless)
        return this.Expression(conversion.Operand);

      var operand = this.Operand(conversion.Operand);
      return (to.Kind switch {
        IrTypeKind.I8 => $"{operand} << 24 >> 24",
        IrTypeKind.U8 => $"{operand} & 0xFF",
        IrTypeKind.I16 => $"{operand} << 16 >> 16",
        IrTypeKind.U16 => $"{operand} & 0xFFFF",
        IrTypeKind.I32 => $"{operand} | 0",
        _ => $"{operand} >>> 0"
      }, false);
    }

    private static string Name(string identifier) => _reserved.Contains(identifier) ? identifier + "$" : identifier;

  }

}
