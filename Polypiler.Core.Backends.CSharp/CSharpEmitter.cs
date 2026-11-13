using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// Emits one C# file per IR module: a static class with one static method per function. The code
/// computes what the IR computes whatever the consuming project sets: wrap-around arithmetic stands
/// in an <c>unchecked</c> block, a conversion C# would not apply by itself is written as a cast, and
/// the three traps are the exceptions the C# operators throw themselves —
/// <see cref="DivideByZeroException"/>, <see cref="OverflowException"/> and
/// <see cref="IndexOutOfRangeException"/>.
/// </summary>
public static class CSharpEmitter {

  /// <summary>The code for an IR construct this backend cannot write.</summary>
  public const string Unsupported = "PLY1052";

  /// <summary>The reserved words of C# (ECMA-334, "Keywords"); as an identifier each needs an <c>@</c>.</summary>
  private static readonly HashSet<string> _keywords = [
    "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
    "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
    "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
    "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
    "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
    "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
    "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
  ];

  private static readonly Dictionary<IrTypeKind, string> _types = new() {
    [IrTypeKind.Void] = "void",
    [IrTypeKind.Bool] = "bool",
    [IrTypeKind.U8] = "byte",
    [IrTypeKind.I32] = "int",
    [IrTypeKind.U32] = "uint"
  };

  private static readonly Dictionary<IrUnaryOp, string> _unary = new() {
    [IrUnaryOp.Negate] = "-",
    [IrUnaryOp.BitNot] = "~",
    [IrUnaryOp.LogicalNot] = "!"
  };

  /// <summary>
  /// The C# operator for each IR operator. On two operands of one 32-bit type C# computes in that
  /// type, takes a shift count modulo 32 and shifts an <c>int</c> arithmetically and a <c>uint</c>
  /// logically (ECMA-334, "Shift operators") — which is what the IR means by each of them.
  /// </summary>
  private static readonly Dictionary<IrBinaryOp, string> _binary = new() {
    [IrBinaryOp.Add] = "+",
    [IrBinaryOp.Subtract] = "-",
    [IrBinaryOp.Multiply] = "*",
    [IrBinaryOp.Divide] = "/",
    [IrBinaryOp.Remainder] = "%",
    [IrBinaryOp.And] = "&",
    [IrBinaryOp.Or] = "|",
    [IrBinaryOp.Xor] = "^",
    [IrBinaryOp.ShiftLeft] = "<<",
    [IrBinaryOp.ShiftRight] = ">>",
    [IrBinaryOp.ShiftRightLogical] = ">>",
    [IrBinaryOp.Equal] = "==",
    [IrBinaryOp.NotEqual] = "!=",
    [IrBinaryOp.Less] = "<",
    [IrBinaryOp.LessOrEqual] = "<=",
    [IrBinaryOp.Greater] = ">",
    [IrBinaryOp.GreaterOrEqual] = ">=",
    [IrBinaryOp.LogicalAnd] = "&&",
    [IrBinaryOp.LogicalOr] = "||"
  };

  /// <summary>Says why a module cannot be emitted.</summary>
  /// <param name="module">The module to check.</param>
  /// <returns>
  /// One <see cref="Unsupported"/> diagnostic per construct this backend does not know; when it knows
  /// them all, the diagnostics of <see cref="IrVerifier"/>; empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) {
    var unknown = new List<string>();
    if (module.Constants.Count > 0)
      unknown.Add(nameof(IrModuleConst));

    foreach (var function in module.Functions) {
      Known(function.ReturnType, unknown);
      foreach (var variable in function.Parameters.Concat(function.Locals))
        Known(variable.Type, unknown);

      Known(function.Body, unknown);
    }

    if (unknown.Count == 0)
      return IrVerifier.Verify(module);

    return unknown.Distinct()
      .Select(what => new Diagnostic(Unsupported, DiagnosticSeverity.Error, $"'{what}' is not supported by the C# backend yet"))
      .ToList();
  }

  /// <summary>Whether a text is a namespace name: identifiers separated by dots.</summary>
  /// <param name="name">The text.</param>
  /// <returns><see langword="true"/> when the text can follow the word <c>namespace</c>.</returns>
  public static bool IsNamespace(string name) => name.Split('.').All(IsIdentifier);

  /// <summary>
  /// Emits the module as <c>&lt;name&gt;.cs</c>: a static class whose public methods are the public
  /// functions. File and class carry the name of the module as C# spells it.
  /// </summary>
  /// <param name="module">A module that <see cref="Check"/> accepts.</param>
  /// <param name="namespace">The file-scoped namespace of the class, or <see langword="null"/> for none.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted, or the namespace is not a name.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, string? @namespace = null) {
    if (@namespace is not null && !IsNamespace(@namespace))
      throw new ArgumentException($"'{@namespace}' is not a namespace name.", nameof(@namespace));

    var problems = Check(module);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    var writer = new Writer(module);
    return [new GeneratedFile(writer.ClassName + ".cs", writer.Module(@namespace))];
  }

  private static void Known(IrType type, List<string> unknown) {
    var scalar = type;
    while (scalar.Element is not null)
      scalar = scalar.Element;

    if (!_types.ContainsKey(scalar.Kind))
      unknown.Add(type.ToString());
  }

  private static void Known(IrStmt statement, List<string> unknown) {
    var parts = Parts(statement);
    if (parts is null) {
      unknown.Add(statement.GetType().Name);
      return;
    }

    foreach (var expression in parts.Value.Expressions)
      Known(expression, unknown);

    foreach (var inner in parts.Value.Statements)
      Known(inner, unknown);
  }

  private static void Known(IrExpr expression, List<string> unknown) {
    Known(expression.Type, unknown);
    var operands = Operands(expression);
    if (operands is null)
      unknown.Add(expression.GetType().Name);
    else if (expression is IrUnary unary && !_unary.ContainsKey(unary.Op))
      unknown.Add(unary.Op.ToString());
    else if (expression is IrBinary binary && !_binary.ContainsKey(binary.Op))
      unknown.Add(binary.Op.ToString());
    else if (expression is IrConvert { Contract: not IrConversion.Wrap } conversion)
      unknown.Add($"{nameof(IrConvert)} with the contract {conversion.Contract}");

    foreach (var operand in operands ?? [])
      Known(operand, unknown);
  }

  /// <summary>The expressions and statements a statement consists of, or <see langword="null"/> for a kind this backend does not know.</summary>
  private static (IReadOnlyList<IrExpr> Expressions, IReadOnlyList<IrStmt> Statements)? Parts(IrStmt statement) => statement switch {
    IrBlock block => ([], block.Statements),
    IrDeclare declare => (declare.Initializer is null ? [] : [declare.Initializer], []),
    IrAssign assign => ([assign.Value], []),
    IrStore store => ([store.Array, store.Index, store.Value], []),
    IrExprStmt expression => ([expression.Expression], []),
    IrIf conditional => ([conditional.Condition], conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else]),
    IrWhile loop => ([loop.Condition], [loop.Body]),
    IrReturn result => (result.Value is null ? [] : [result.Value], []),
    _ => null
  };

  /// <summary>The operands of an expression, or <see langword="null"/> for a kind this backend does not know.</summary>
  private static IReadOnlyList<IrExpr>? Operands(IrExpr expression) => expression switch {
    IrConst or IrLocalRef => [],
    IrUnary unary => [unary.Operand],
    IrBinary binary => [binary.Left, binary.Right],
    IrConvert conversion => [conversion.Operand],
    IrConditional choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
    IrArrayLoad load => [load.Array, load.Index],
    IrArrayLength length => [length.Array],
    IrNewArray creation => [creation.Length],
    IrCall call => call.Arguments,
    _ => null
  };

  /// <summary>Every expression below a statement, operands included.</summary>
  private static IEnumerable<IrExpr> Expressions(IrStmt statement) {
    var (expressions, statements) = Parts(statement)!.Value;
    return expressions.SelectMany(Expressions).Concat(statements.SelectMany(Expressions));
  }

  private static IEnumerable<IrExpr> Expressions(IrExpr expression)
    => Operands(expression)!.SelectMany(Expressions).Prepend(expression);

  /// <summary>
  /// Whether C# checks the expression for overflow in a checked context, where the IR wraps around:
  /// the arithmetic that can leave 32 bits, and every cast that can lose a bit or the sign.
  /// </summary>
  private static bool Wraps(IrExpr expression) => expression switch {
    IrUnary unary => unary.Op == IrUnaryOp.Negate,
    IrBinary binary => binary.Op is IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply
      || (binary.Op == IrBinaryOp.ShiftRightLogical && binary.Left.Type == IrType.I32),
    IrConvert conversion => !Widens(conversion),
    _ => false
  };

  /// <summary>Whether a conversion keeps every value: one to the same type, or from a byte.</summary>
  private static bool Widens(IrConvert conversion)
    => conversion.Type == conversion.Operand.Type || conversion.Operand.Type == IrType.U8;

  /// <summary>
  /// Whether the C# compiler would compute the expression itself. It then also judges it: a constant
  /// zero divisor, a constant negative array length and a constant quotient that overflows are errors.
  /// </summary>
  private static bool IsConstant(IrExpr expression)
    => expression is IrConst or IrUnary or IrBinary or IrConvert or IrConditional && Operands(expression)!.All(IsConstant);

  private static bool IsIdentifier(string name)
    => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(character => char.IsLetterOrDigit(character) || character == '_');

  /// <summary>
  /// The C# name of every given name. A name C# can write keeps its spelling, with an <c>@</c> when it
  /// is a keyword. Any other — one with a <c>$</c>, or the discard <c>_</c> — has each character C#
  /// rejects replaced by an underscore and is then lengthened until no other name is spelled like it.
  /// </summary>
  private static Dictionary<string, string> Names(IEnumerable<string> names, IEnumerable<string> taken) {
    var distinct = names.Distinct().ToList();
    var used = new HashSet<string>(taken);
    var result = new Dictionary<string, string>();
    foreach (var name in distinct)
      if (IsIdentifier(name) && name != "_" && used.Add(name))
        result[name] = name;

    foreach (var name in distinct.Where(name => !result.ContainsKey(name))) {
      var spelling = new string(name.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
      while (!IsIdentifier(spelling) || spelling == "_")
        spelling = "_" + spelling;

      while (!used.Add(spelling))
        spelling += "_";

      result[name] = spelling;
    }

    foreach (var name in distinct.Where(name => _keywords.Contains(result[name])))
      result[name] = "@" + result[name];

    return result;
  }

  private sealed class Writer {

    /// <summary>How tightly an expression binds: a primary expression, a unary one, or anything looser.</summary>
    private const int Primary = 0, Unary = 1, Loose = 2;

    private readonly IrModule _module;
    private readonly StringBuilder _code = new();
    private readonly string _class;
    private readonly HashSet<string> _methods;
    private readonly Dictionary<string, string> _functions;
    private Dictionary<string, string> _locals = [];

    public Writer(IrModule module) {
      this._module = module;
      this._class = Names([module.Name], [])[module.Name];
      // Without the '@' the compiler warns that a type name of lower-case letters only may become a keyword (CS8981).
      if (this._class.All(char.IsAsciiLetterLower))
        this._class = "@" + this._class;

      this._functions = Names(module.Functions.Select(function => function.Name), [this.ClassName]);
      this._methods = [.. this._functions.Values.Select(method => method.TrimStart('@'))];
    }

    /// <summary>The name of the class as the compiler reads it, without the <c>@</c> a keyword needs.</summary>
    public string ClassName => this._class.TrimStart('@');

    public string Module(string? @namespace) {
      this._code.Append("// ").Append(this.ClassName).Append(": generated by polypiler. Change the source, not this file.\n");
      if (@namespace is not null) {
        var parts = @namespace.Split('.');
        this._code.Append("\nnamespace ").AppendJoin('.', parts.Select(part => Names([part], [])[part])).Append(";\n");
      }

      this._code.Append("\npublic static class ").Append(this._class).Append(" {\n");
      foreach (var function in this._module.Functions)
        this.Function(function);

      return this._code.Append("\n}\n").ToString();
    }

    private void Function(IrFunction function) {
      // A variable spelled like a method would hide it from the calls, so the methods' names are taken.
      this._locals = Names(function.Parameters.Concat(function.Locals).Select(variable => variable.Name), this._methods);
      var parameters = string.Join(", ", function.Parameters.Select(parameter => $"{TypeName(parameter.Type)} {this._locals[parameter.Name]}"));
      this._code.Append("\n  ").Append(function.IsPublic ? "public" : "private").Append(" static ")
        .Append(TypeName(function.ReturnType)).Append(' ').Append(this._functions[function.Name])
        .Append('(').Append(parameters).Append(") {\n");
      if (Expressions(function.Body).Any(Wraps)) {
        this.Line(2).Append("unchecked {\n");
        this.Statements(function.Body, 3);
        this.Line(2).Append("}\n");
      } else {
        this.Statements(function.Body, 2);
      }

      this._code.Append("  }\n");
    }

    private static string TypeName(IrType type) => type.Element is null ? _types[type.Kind] : TypeName(type.Element) + "[]";

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
          this.Line(depth).Append(TypeName(declare.Local.Type)).Append(' ').Append(this._locals[declare.Local.Name]);
          if (declare.Initializer is not null)
            this._code.Append(" = ").Append(this.Stored(declare.Initializer));

          this._code.Append(";\n");
          break;
        case IrAssign assign:
          this.Line(depth).Append(this._locals[assign.Local.Name]).Append(" = ").Append(this.Stored(assign.Value)).Append(";\n");
          break;
        case IrStore store:
          this.Line(depth).Append(this.Element(store.Array, store.Index)).Append(" = ").Append(this.Stored(store.Value)).Append(";\n");
          break;
        case IrExprStmt expression:
          // C# takes a call as a statement; any other expression is evaluated by assigning it to the discard.
          this.Line(depth).Append(expression.Expression is IrCall ? "" : "_ = ").Append(this.Text(expression.Expression)).Append(";\n");
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
          this.Line(depth).Append(result.Value is null ? "return" : "return " + this.Stored(result.Value)).Append(";\n");
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

    /// <summary>The expression as it stands on its own: a condition, an index or the operand of nothing.</summary>
    private string Text(IrExpr expression) => this.Expression(expression).Text;

    /// <summary>
    /// The expression where its type is already given — an initializer, a right-hand side, an
    /// argument, a returned value. A constant needs neither cast nor suffix there: C# converts a
    /// literal to the <c>byte</c> or <c>uint</c> that holds its value.
    /// </summary>
    private string Stored(IrExpr expression)
      => expression is IrConst constant && constant.Type.IsInteger ? Number(constant.Value) : this.Text(expression);

    /// <summary>The expression as an operand: in parentheses when it binds more loosely than <paramref name="level"/>.</summary>
    private string Operand(IrExpr expression, int level) {
      var (text, binding) = this.Expression(expression);
      return binding > level ? $"({text})" : text;
    }

    /// <summary>
    /// An operand the C# compiler must leave to run time when it could compute it and would then
    /// refuse or fold what the IR defines as a trap: the divisor 0, the divisor -1, a negative index
    /// or length. <c>Math.Max</c> of the value and itself is the value, and no constant to C#.
    /// </summary>
    private string AtRunTime(IrExpr expression, bool harmless, int level) {
      if (expression is IrConst ? harmless : !IsConstant(expression))
        return this.Operand(expression, level);

      var text = this.Text(expression);
      return $"global::System.Math.Max({text}, {text})";
    }

    private string Element(IrExpr array, IrExpr index)
      => $"{this.Operand(array, Primary)}[{this.AtRunTime(index, index is IrConst { Value: >= 0 }, Loose)}]";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private (string Text, int Binding) Expression(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          return constant.Type.Kind switch {
            IrTypeKind.Bool => (constant.Value != 0 ? "true" : "false", Primary),
            IrTypeKind.U8 => ($"(byte){Number(constant.Value)}", Unary),
            IrTypeKind.U32 => (Number(constant.Value) + "u", Primary),
            _ => (Number(constant.Value), constant.Value < 0 ? Unary : Primary)
          };
        case IrLocalRef reference:
          return (this._locals[reference.Local.Name], Primary);
        case IrUnary unary:
          return (_unary[unary.Op] + this.Operand(unary.Operand, Primary), Unary);
        case IrBinary binary:
          return this.Binary(binary);
        case IrConvert conversion:
          // A byte becomes an int wherever C# computes with it; only the other conversions need a cast.
          if (Widens(conversion) && conversion.Type != IrType.U32)
            return this.Expression(conversion.Operand);

          return ($"({TypeName(conversion.Type)}){this.Operand(conversion.Operand, Primary)}", Unary);
        case IrConditional choice:
          return ($"{this.Operand(choice.Condition, Unary)} ? {this.Operand(choice.WhenTrue, Unary)} : {this.Operand(choice.WhenFalse, Unary)}", Loose);
        case IrArrayLoad load:
          return (this.Element(load.Array, load.Index), Primary);
        case IrArrayLength length:
          return ($"{this.Operand(length.Array, Primary)}.Length", Primary);
        case IrNewArray creation:
          var count = this.AtRunTime(creation.Length, creation.Length is IrConst { Value: >= 0 }, Loose);
          return ($"new {TypeName(creation.Type.Element!)}[{count}]", Unary);
        default:
          var call = (IrCall)expression;
          return ($"{this._functions[call.Function]}({string.Join(", ", call.Arguments.Select(this.Stored))})", Primary);
      }
    }

    private (string Text, int Binding) Binary(IrBinary binary) {
      var left = this.Operand(binary.Left, Unary);
      var symbol = _binary[binary.Op];
      switch (binary.Op) {
        case IrBinaryOp.Divide or IrBinaryOp.Remainder:
          var divisor = this.AtRunTime(binary.Right, binary.Right is IrConst { Value: not (0 or -1) }, Unary);
          return ($"{left} {symbol} {divisor}", Loose);
        case IrBinaryOp.ShiftRightLogical when binary.Left.Type == IrType.I32:
          // Written with casts rather than '>>>' so that the file also compiles as C# older than 11.
          return ($"(int)((uint){this.Operand(binary.Left, Primary)} >> {this.Operand(binary.Right, Unary)})", Unary);
        default:
          return ($"{left} {symbol} {this.Operand(binary.Right, Unary)}", Loose);
      }
    }

  }

}
