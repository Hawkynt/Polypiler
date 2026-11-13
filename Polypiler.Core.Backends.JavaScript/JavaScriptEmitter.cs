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
/// <c>BigInt.asUintN</c>. A <c>double</c> is a number as it is; every <c>float</c> operation is
/// rounded to binary32 with <c>Math.fround</c>, which is exact because a double holds the sum,
/// difference, product and quotient of two binary32 numbers precisely enough to round once. Arrays
/// are typed arrays, and the constants of the module are <c>const</c> declarations in front of its
/// functions. The operations that can trap or need more than an expression go through small helper
/// functions, of which a module carries exactly the ones it uses.
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
    ("$f2i", [
      "function $f2i(value, low, high) {",
      "  if (value !== value) return 0;",
      "  return value <= low ? low : value >= high ? high : Math.trunc(value) + 0;",
      "}"
    ]),
    ("$f2l", [
      "function $f2l(value, low, limit) {",
      "  if (value !== value) return 0n;",
      "  if (value >= limit) return BigInt(limit) - 1n;",
      "  return value <= low ? BigInt(low) : BigInt(Math.trunc(value));",
      "}"
    ]),
    ("$f2ic", [
      "function $f2ic(value, low, high) {",
      "  const whole = Math.trunc(value);",
      "  if (!(whole >= low && whole <= high)) " + Overflow,
      "  return whole + 0;",
      "}"
    ]),
    ("$f2lc", [
      "function $f2lc(value, low, limit) {",
      "  const whole = Math.trunc(value);",
      "  if (!(whole >= low && whole < limit)) " + Overflow,
      "  return BigInt(whole);",
      "}"
    ]),
    ("$f32", [
      "function $f32(value) {",
      "  const negative = value < 0n;",
      "  let magnitude = negative ? -value : value;",
      "  let scale = 1;",
      "  if (magnitude >= 9007199254740992n) {",
      "    magnitude = (magnitude >> 11n) | (magnitude & 2047n ? 1n : 0n);",
      "    scale = 2048;",
      "  }",
      "  const result = Math.fround(Number(magnitude)) * scale;",
      "  return negative ? -result : result;",
      "}"
    ]),
    ("$get", ["function $get(array, index) {", OutOfRange, "  return array[index];", "}"]),
    ("$set", ["function $set(array, index, value) {", OutOfRange, "  array[index] = value;", "}"]),
    ("$abs", ["function $abs(value, low) {", "  if (value === low) " + Overflow, "  return value < 0 ? -value : value;", "}"]),
    ("$min64", ["function $min64(a, b) {", "  return a < b ? a : b;", "}"]),
    ("$max64", ["function $max64(a, b) {", "  return a > b ? a : b;", "}"]),
    ("$sign64", ["function $sign64(value) {", "  return value < 0n ? -1 : value > 0n ? 1 : 0;", "}"]),
    ("$fsign", [
      "function $fsign(value) {",
      "  if (value !== value) throw new RangeError(\"invalid operation\");",
      "  return value < 0 ? -1 : value > 0 ? 1 : 0;",
      "}"
    ]),
    ("$round", [
      "function $round(value) {",
      "  const rounded = Math.round(value);",
      "  return rounded - value === 0.5 && rounded % 2 !== 0 ? rounded - 1 : rounded;",
      "}"
    ]),
    ("$rounda", ["function $rounda(value) {", "  return value < 0 ? -Math.round(-value) : Math.round(value);", "}"]),
    ("$rotl", ["function $rotl(value, count) {", "  return ((value << count) | (value >>> (-count & 31))) >>> 0;", "}"]),
    ("$rotr", ["function $rotr(value, count) {", "  return ((value >>> count) | (value << (-count & 31))) >>> 0;", "}"]),
    ("$rotl64", [
      "function $rotl64(value, count) {",
      "  const by = BigInt(count & 63);",
      "  return BigInt.asUintN(64, (value << by) | (value >> (64n - by)));",
      "}"
    ]),
    ("$rotr64", [
      "function $rotr64(value, count) {",
      "  const by = BigInt(count & 63);",
      "  return BigInt.asUintN(64, (value >> by) | (value << (64n - by)));",
      "}"
    ]),
    ("$pop", [
      "function $pop(value) {",
      "  value -= (value >>> 1) & 0x55555555;",
      "  value = (value & 0x33333333) + ((value >>> 2) & 0x33333333);",
      "  return Math.imul((value + (value >>> 4)) & 0x0F0F0F0F, 0x01010101) >>> 24;",
      "}"
    ]),
    ("$pop64", ["function $pop64(value) {", "  return $pop(Number(value >> 32n)) + $pop(Number(value & 0xFFFFFFFFn));", "}"]),
    ("$clz64", [
      "function $clz64(value) {",
      "  const high = Number(value >> 32n);",
      "  return high !== 0 ? Math.clz32(high) : 32 + Math.clz32(Number(value & 0xFFFFFFFFn));",
      "}"
    ]),
    ("$ctz", ["function $ctz(value) {", "  return value === 0 ? 32 : 31 - Math.clz32(value & -value);", "}"]),
    ("$ctz64", [
      "function $ctz64(value) {",
      "  const low = Number(value & 0xFFFFFFFFn);",
      "  return low !== 0 ? $ctz(low) : 32 + $ctz(Number(value >> 32n));",
      "}"
    ]),
    ("$view", ["const $view = new DataView(new ArrayBuffer(8));"]),
    ("$bits64", ["function $bits64(value) {", "  $view.setFloat64(0, value);", "  return $view.getBigInt64(0);", "}"]),
    ("$bits32", ["function $bits32(value) {", "  $view.setFloat32(0, value);", "  return $view.getInt32(0);", "}"]),
    ("$float64", ["function $float64(value) {", "  $view.setBigInt64(0, value);", "  return $view.getFloat64(0);", "}"]),
    ("$float32", ["function $float32(value) {", "  $view.setInt32(0, value);", "  return $view.getFloat32(0);", "}"]),
    ("$copy", [
      "function $copy(source, from, target, to, length) {",
      "  if (from < 0 || to < 0 || length < 0 || from + length > source.length || to + length > target.length) throw new RangeError(\"index out of range\");",
      "  target.set(source.subarray(from, from + length), to);",
      "}"
    ]),
    ("$step", [
      "function $step(array, index, delta, post) {",
      OutOfRange,
      "  const before = array[index];",
      "  array[index] = before + delta;",
      "  return post ? before : array[index];",
      "}"
    ]),
    ("$stepck", [
      "function $stepck(array, index, delta, post, low, high) {",
      OutOfRange,
      "  const before = array[index];",
      "  const after = before + delta;",
      "  if (after < low || after > high) " + Overflow,
      "  array[index] = after;",
      "  return post ? before : after;",
      "}"
    ]),
    ("$len", ["function $len(length) {", "  if (length < 0) " + Overflow, "  return length;", "}"])
  ];

  /// <summary>The helper a helper calls, which a module then carries as well.</summary>
  private static readonly Dictionary<string, string> _needs = new() {
    ["$pop64"] = "$pop", ["$ctz64"] = "$ctz", ["$bits64"] = "$view", ["$bits32"] = "$view", ["$float64"] = "$view", ["$float32"] = "$view"
  };

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
    "Int32Array", "Uint32Array", "BigInt64Array", "BigUint64Array", "Float32Array", "Float64Array", "NaN", "Infinity",
    "DataView", "ArrayBuffer"
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
    IrTypeKind.U64 => "BigUint64Array",
    IrTypeKind.F32 => "Float32Array",
    _ => "Float64Array"
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

  /// <summary>
  /// A floating-point number as a literal that reads back as exactly that number: the shortest digits
  /// that identify the double. A binary32 number is widened first, which changes nothing about it.
  /// </summary>
  private static string Literal(double value) {
    if (double.IsNaN(value))
      return "NaN";

    return double.IsInfinity(value) ? (value < 0 ? "-Infinity" : "Infinity") : value.ToString("R", CultureInfo.InvariantCulture);
  }

  private sealed class Writer {

    /// <summary>The variable a postfix step of a local keeps the old value in; a function that needs it declares it.</summary>
    private const string Previous = "$p";

    private readonly StringBuilder _code = new();
    private readonly HashSet<string> _used = [];
    private bool _previous;

    public string Module(IrModule module) {
      foreach (var function in module.Functions)
        this.Function(function);

      var text = new StringBuilder();
      text.Append("// ").Append(module.Name).Append(": generated by polypiler. Change the source, not this file.\n");
      foreach (var (name, lines) in _helpers)
        if (this._used.Contains(name))
          text.Append('\n').AppendJoin('\n', lines).Append('\n');

      if (module.Constants.Count > 0)
        text.Append('\n');

      foreach (var constant in module.Constants)
        text.Append("const ").Append(Name(constant.Name)).Append(" = ").Append(Value(constant)).Append(";\n");

      return text.Append(this._code).ToString();
    }

    /// <summary>
    /// The value of a constant of the module. A table is a typed array built once from its elements,
    /// eight to a line when it has more than that.
    /// </summary>
    private static string Value(IrModuleConst constant) {
      if (constant.Type.Element is not { } element)
        return Constant(constant.Type, constant.Values[0]).Text;

      var elements = constant.Values.Select(value => Constant(element, value).Text).ToList();
      var list = elements.Count <= 8
        ? string.Join(", ", elements)
        : "\n  " + string.Join(",\n  ", elements.Chunk(8).Select(row => string.Join(", ", row))) + "\n";
      return $"new {Storage(element)}([{list}])";
    }

    /// <summary>A constant as a literal of its type; it is an operand as it stands unless it is negative.</summary>
    private static (string Text, bool Atomic) Constant(IrType type, long value) {
      if (type == IrType.Bool)
        return (value != 0 ? "true" : "false", true);

      if (type.IsFloat) {
        var literal = Literal(IrOperators.Float(type, value));
        return (literal, literal[0] != '-');
      }

      var number = IrOperators.Number(type, value);
      return (Literal(type, number), number >= 0);
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
      var start = this._code.Length;
      this._previous = false;
      this.Statements(function.Body, 1);
      if (this._previous)
        this._code.Insert(start, $"  let {Previous};\n");

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
        case IrDoWhile loop:
          this.Line(depth).Append("do {\n");
          this.Statements(loop.Body, depth + 1);
          this.Line(depth).Append("} while (").Append(this.Text(loop.Condition)).Append(");\n");
          break;
        case IrFor loop:
          this.For(loop, depth);
          break;
        case IrSwitch choice:
          this.Switch(choice, depth);
          break;
        case IrBreak:
          this.Line(depth).Append("break;\n");
          break;
        case IrContinue:
          this.Line(depth).Append("continue;\n");
          break;
        default:
          var result = (IrReturn)statement;
          this.Line(depth).Append(result.Value is null ? "return" : "return " + this.Text(result.Value)).Append(";\n");
          break;
      }
    }

    /// <summary>
    /// A loop with a step is a JavaScript <c>for</c>, whose third part a <c>continue</c> does not skip
    /// either. That part is an expression, so the step is written as a list of expressions and the
    /// temporaries it declares are declared in front of the loop, in a block of their own with it.
    /// </summary>
    private void For(IrFor loop, int depth) {
      var scoped = loop.Step.Statements.Any(step => step is IrDeclare);
      if (scoped)
        this.Line(depth++).Append("{\n");

      var parts = new List<string>();
      foreach (var step in loop.Step.Statements)
        switch (step) {
          case IrDeclare declare:
            this.Line(depth).Append("let ").Append(Name(declare.Local.Name)).Append(";\n");
            if (declare.Initializer is not null)
              parts.Add($"{Name(declare.Local.Name)} = {this.Text(declare.Initializer)}");

            break;
          case IrAssign assign:
            parts.Add($"{Name(assign.Local.Name)} = {this.Text(assign.Value)}");
            break;
          case IrStore store:
            parts.Add(this.Helper("$set", store.Array, store.Index, store.Value));
            break;
          default:
            parts.Add(this.Text(((IrExprStmt)step).Expression));
            break;
        }

      this.Line(depth).Append("for (; ").Append(this.Text(loop.Condition)).Append("; ").AppendJoin(", ", parts).Append(") {\n");
      this.Statements(loop.Body, depth + 1);
      this.Line(depth).Append("}\n");
      if (scoped)
        this.Line(depth - 1).Append("}\n");
    }

    /// <summary>
    /// A switch. Every section is a block of its own and ends with a <c>break</c> unless its last
    /// statement already leaves it, because the IR has no fall-through and JavaScript has.
    /// </summary>
    private void Switch(IrSwitch choice, int depth) {
      var type = choice.Value.Type;
      this.Line(depth).Append("switch (").Append(this.Text(choice.Value)).Append(") {\n");
      foreach (var section in choice.Sections) {
        var labels = section.Labels.Select(label => $"case {Literal(type, IrOperators.Number(type, label))}:").ToList();
        if (section.IsDefault)
          labels.Add("default:");

        for (var index = 0; index < labels.Count; ++index)
          this.Line(depth + 1).Append(labels[index]).Append(index + 1 < labels.Count ? "\n" : " {\n");

        this.Statements(section.Body, depth + 2);
        if (section.Body.Statements is not [.., IrBreak or IrContinue or IrReturn])
          this.Line(depth + 2).Append("break;\n");

        this.Line(depth + 1).Append("}\n");
      }

      this.Line(depth).Append("}\n");
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
      if (_needs.TryGetValue(name, out var needed))
        this._used.Add(needed);

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
          return Constant(constant.Type, constant.Value);
        case IrConstRef reference:
          return (Name(reference.Constant.Name), true);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        case IrLocalRef reference:
          return (Name(reference.Local.Name), true);
        case IrUnary unary:
          return this.Unary(unary);
        case IrBinary binary:
          var operands = binary.Left.Type;
          return operands.IsFloat ? this.BinaryFloat(binary) : operands.IsLong ? this.Binary64(binary) : this.Binary(binary);
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
        case IrStep step:
          return this.Step(step);
        default:
          var call = (IrCall)expression;
          return ($"{Name(call.Function)}({string.Join(", ", call.Arguments.Select(this.Text))})", true);
      }
    }

    private (string Text, bool Atomic) Intrinsic(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayOf:
          return ($"new {Storage(intrinsic.Type.Element!)}([{string.Join(", ", arguments.Select(this.Text))}])", true);
        case IrIntrinsicOp.ArrayCopy:
          return (this.Helper("$copy", arguments.ToArray()), true);
        case IrIntrinsicOp.ArrayFill:
          return ($"{this.Operand(arguments[0])}.fill({this.Text(arguments[1])})", true);
        case IrIntrinsicOp.ArrayClear:
          return ($"{this.Operand(arguments[0])}.fill({(arguments[0].Type.Element!.IsLong ? "0n" : "0")})", true);
      }

      var type = arguments[0].Type;
      var wide = type.IsLong;
      var all = string.Join(", ", arguments.Select(this.Text));
      switch (intrinsic.Op) {
        case IrIntrinsicOp.Abs:
          return type.IsFloat
            ? ($"Math.abs({all})", true)
            : (this.Helper("$abs", [all, Literal(type, IrOperators.Minimum(type))]), true);
        case IrIntrinsicOp.Min:
          return (wide ? this.Helper("$min64", arguments.ToArray()) : $"Math.min({all})", true);
        case IrIntrinsicOp.Max:
          return (wide ? this.Helper("$max64", arguments.ToArray()) : $"Math.max({all})", true);
        case IrIntrinsicOp.Sign:
          return (type.IsFloat ? this.Helper("$fsign", arguments.ToArray()) : wide ? this.Helper("$sign64", arguments.ToArray()) : $"Math.sign({all})", true);
        case IrIntrinsicOp.Sqrt:
          return (type == IrType.F32 ? $"Math.fround(Math.sqrt({all}))" : $"Math.sqrt({all})", true);
        case IrIntrinsicOp.Floor:
          return ($"Math.floor({all})", true);
        case IrIntrinsicOp.Ceiling:
          return ($"Math.ceil({all})", true);
        case IrIntrinsicOp.Truncate:
          return ($"Math.trunc({all})", true);
        case IrIntrinsicOp.RoundEven:
          return (this.Helper("$round", arguments.ToArray()), true);
        case IrIntrinsicOp.RoundAway:
          return (this.Helper("$rounda", arguments.ToArray()), true);
        case IrIntrinsicOp.RotateLeft:
          return (this.Helper(wide ? "$rotl64" : "$rotl", arguments.ToArray()), true);
        case IrIntrinsicOp.RotateRight:
          return (this.Helper(wide ? "$rotr64" : "$rotr", arguments.ToArray()), true);
        case IrIntrinsicOp.PopCount:
          return (this.Helper(wide ? "$pop64" : "$pop", arguments.ToArray()), true);
        case IrIntrinsicOp.LeadingZeroCount:
          return (wide ? this.Helper("$clz64", arguments.ToArray()) : $"Math.clz32({all})", true);
        case IrIntrinsicOp.TrailingZeroCount:
          return (this.Helper(wide ? "$ctz64" : "$ctz", arguments.ToArray()), true);
        case IrIntrinsicOp.Log2:
          // The lowest bit set by hand makes 0 behave like 1, whose logarithm is the 0 that is wanted.
          return wide
            ? ($"63 - {this.Helper("$clz64", [this.Operand(arguments[0]) + " | 1n"])}", false)
            : ($"31 - Math.clz32({this.Operand(arguments[0])} | 1)", false);
        case IrIntrinsicOp.BigMul:
          return ($"BigInt({this.Text(arguments[0])}) * BigInt({this.Text(arguments[1])})", false);
        case IrIntrinsicOp.FloatBits:
          return (this.Helper(type == IrType.F64 ? "$bits64" : "$bits32", arguments.ToArray()), true);
        case IrIntrinsicOp.RoundHalfUp:
          // The result is integral, and an integral number that a binary32 operand rounds to is a binary32 number.
          return ($"Math.round({all})", true);
        case IrIntrinsicOp.ToInt32:
          return ($"{this.Operand(arguments[0])} | 0", false);
        case IrIntrinsicOp.ToUint32:
          return ($"{this.Operand(arguments[0])} >>> 0", false);
        default:
          return (this.Helper(wide ? "$float64" : "$float32", arguments.ToArray()), true);
      }
    }

    /// <summary>
    /// A step by one as an expression. An array element goes through a helper: storing into a typed
    /// array wraps an integer around and rounds to binary32 by itself. A variable is assigned inside
    /// the expression; the postfix form keeps the old value in a variable of the function.
    /// </summary>
    private (string Text, bool Atomic) Step(IrStep step) {
      var type = step.Type;
      var delta = (step.Increment ? "1" : "-1") + (type.IsLong ? "n" : "");
      var post = step.Postfix ? "true" : "false";
      if (step.Target is IrArrayLoad element) {
        var arguments = new List<string> { this.Text(element.Array), this.Text(element.Index), delta, post };
        if (!step.Trap)
          return (this.Helper("$step", arguments), true);

        arguments.Add(Literal(type, IrOperators.Minimum(type)));
        arguments.Add(Literal(type, IrOperators.Maximum(type)));
        return (this.Helper("$stepck", arguments), true);
      }

      var name = Name(((IrLocalRef)step.Target).Local.Name);
      var current = step.Postfix ? Previous : name;
      var moved = $"{current} {(step.Increment ? "+" : "-")} 1{(type.IsLong ? "n" : "")}";
      var stepped = step.Trap ? this.Check(type, moved).Text
        : type == IrType.F64 ? moved
        : type == IrType.F32 ? $"Math.fround({moved})"
        : type.IsWideInteger ? Wrap(type, moved).Text
        : Narrow(type, $"({moved})");
      if (!step.Postfix)
        return ($"{name} = {stepped}", false);

      this._previous = true;
      return ($"({Previous} = {name}, {name} = {stepped}, {Previous})", true);
    }

    /// <summary>A number cut to an integer type of up to 32 bits; the operand is written as an operand already.</summary>
    private static string Narrow(IrType type, string operand) => type.Kind switch {
      IrTypeKind.I8 => $"{operand} << 24 >> 24",
      IrTypeKind.U8 => $"{operand} & 0xFF",
      IrTypeKind.I16 => $"{operand} << 16 >> 16",
      IrTypeKind.U16 => $"{operand} & 0xFFFF",
      IrTypeKind.I32 => $"{operand} | 0",
      _ => $"{operand} >>> 0"
    };

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
        case IrUnaryOp.Negate when type.IsFloat:
          return ("-" + operand, false);
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

    /// <summary>
    /// An operator on floating-point numbers. JavaScript computes in binary64, which is the operation
    /// itself for <c>f64</c>; for <c>f32</c> the result is rounded to binary32.
    /// </summary>
    private (string Text, bool Atomic) BinaryFloat(IrBinary binary) {
      var left = this.Operand(binary.Left);
      var right = this.Operand(binary.Right);
      var sign = binary.Op switch {
        IrBinaryOp.Add => "+",
        IrBinaryOp.Subtract => "-",
        IrBinaryOp.Multiply => "*",
        IrBinaryOp.Divide => "/",
        IrBinaryOp.Remainder => "%",
        _ => Sign(binary.Op)
      };
      return binary.Type == IrType.F32 ? ($"Math.fround({left} {sign} {right})", true) : ($"{left} {sign} {right}", false);
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
      if (to.IsFloat)
        return this.ToFloat(conversion);

      if (from.IsFloat)
        return (this.FromFloat(conversion), true);

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

      return (Narrow(to, this.Operand(conversion.Operand)), false);
    }

    /// <summary>
    /// A number converted to floating point. An integer of up to 32 bits is already a double with its
    /// value; a BigInt becomes the nearest double, and for <c>f32</c> goes through a helper that rounds
    /// it once, because rounding to binary64 and then to binary32 can differ from that.
    /// </summary>
    private (string Text, bool Atomic) ToFloat(IrConvert conversion) {
      var from = conversion.Operand.Type;
      if (from.IsLong)
        return (conversion.Type == IrType.F32 ? this.Helper("$f32", conversion.Operand) : $"Number({this.Text(conversion.Operand)})", true);

      return conversion.Type == IrType.F64 ? this.Expression(conversion.Operand) : ($"Math.fround({this.Text(conversion.Operand)})", true);
    }

    /// <summary>
    /// A floating-point number converted to an integer by a helper that saturates or traps. The bounds
    /// of a 64-bit target are numbers: its smallest value and the power of two just above its largest.
    /// </summary>
    private string FromFloat(IrConvert conversion) {
      var to = conversion.Type;
      var trap = conversion.Contract == IrConversion.Trap;
      var low = IrOperators.Minimum(to).ToString(CultureInfo.InvariantCulture);
      return to.IsLong
        ? this.Helper(trap ? "$f2lc" : "$f2l", [this.Text(conversion.Operand), low, (IrOperators.Maximum(to) + 1).ToString(CultureInfo.InvariantCulture)])
        : this.Helper(trap ? "$f2ic" : "$f2i", [this.Text(conversion.Operand), low, IrOperators.Maximum(to).ToString(CultureInfo.InvariantCulture)]);
    }

    private static string Name(string identifier) => _reserved.Contains(identifier) ? identifier + "$" : identifier;

  }

}
