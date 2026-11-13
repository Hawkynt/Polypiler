using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// Emits one C# file per IR module: a static class with one static method per function and one
/// constant or read-only table per constant of the module. The code computes what the IR computes
/// whatever the consuming project sets: wrap-around arithmetic stands in an <c>unchecked</c> block,
/// trapping arithmetic in a <c>checked(...)</c> expression, a conversion C# would not apply by itself
/// is written as a cast, every binary32 result is cast to <c>float</c> so that it is never kept at a
/// higher precision, and the traps are the exceptions the C# operators and the base library throw
/// themselves — <see cref="DivideByZeroException"/>, <see cref="OverflowException"/>,
/// <see cref="IndexOutOfRangeException"/> (from <c>Array.Copy</c> an <see cref="ArgumentException"/>)
/// and <see cref="ArithmeticException"/>.
/// </summary>
public static class CSharpEmitter {

  /// <summary>The code for an IR construct this backend cannot write.</summary>
  public const string Unsupported = "PLY1052";

  /// <summary>The base library by its full name, which no name of the module can hide.</summary>
  private const string Base = "global::System.";

  /// <summary>The name of the helper that converts a floating-point number to an integer narrower than <c>int</c>.</summary>
  private const string Saturated = "Saturated";

  /// <summary>
  /// The private methods a class gets when it uses them, in the order they are written. A cast to
  /// <c>byte</c> or <c>short</c> saturates to <c>int</c> and then keeps the low bits (ADR 0002), which
  /// is not the nearest value of the narrow type the IR asks for; the helper compares first, and its
  /// one cast only ever sees a number within the range.
  /// </summary>
  private static readonly (string Name, string[] Lines)[] _helpers = [
    (Saturated, [
      "private static int Saturated(double value, int low, int high) {",
      "  return value >= high ? high : value > low ? (int)value : value <= low ? low : 0;",
      "}"
    ])
  ];

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
    [IrTypeKind.I8] = "sbyte",
    [IrTypeKind.U8] = "byte",
    [IrTypeKind.I16] = "short",
    [IrTypeKind.U16] = "ushort",
    [IrTypeKind.I32] = "int",
    [IrTypeKind.U32] = "uint",
    [IrTypeKind.I64] = "long",
    [IrTypeKind.U64] = "ulong",
    [IrTypeKind.F32] = "float",
    [IrTypeKind.F64] = "double"
  };

  private static readonly Dictionary<IrUnaryOp, string> _unary = new() {
    [IrUnaryOp.Negate] = "-",
    [IrUnaryOp.NegateChecked] = "-",
    [IrUnaryOp.BitNot] = "~",
    [IrUnaryOp.LogicalNot] = "!"
  };

  /// <summary>
  /// The C# operator for each IR operator. On two operands of one type of 32 or 64 bits C# computes in
  /// that type, takes a shift count modulo the width and shifts a signed number arithmetically and an
  /// unsigned one logically (ECMA-334, "Shift operators") — which is what the IR means by each of them.
  /// </summary>
  private static readonly Dictionary<IrBinaryOp, string> _binary = new() {
    [IrBinaryOp.Add] = "+",
    [IrBinaryOp.Subtract] = "-",
    [IrBinaryOp.Multiply] = "*",
    [IrBinaryOp.AddChecked] = "+",
    [IrBinaryOp.SubtractChecked] = "-",
    [IrBinaryOp.MultiplyChecked] = "*",
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

  /// <summary>
  /// The method that is an intrinsic operation. Overload resolution picks the one for the type of
  /// the arguments, and each returns the type the IR gives the operation; where the method for a
  /// 32-bit operand has a name of its own, that is the second entry. A new operation is a new line.
  /// </summary>
  private static readonly Dictionary<IrIntrinsicOp, Call> _intrinsics = new() {
    [IrIntrinsicOp.ArrayCopy] = new(Base + "Array.Copy"),
    [IrIntrinsicOp.ArrayFill] = new(Base + "Array.Fill"),
    [IrIntrinsicOp.ArrayClear] = new(Base + "Array.Clear"),
    [IrIntrinsicOp.Abs] = new(Base + "Math.Abs"),
    [IrIntrinsicOp.Min] = new(Base + "Math.Min"),
    [IrIntrinsicOp.Max] = new(Base + "Math.Max"),
    [IrIntrinsicOp.Sign] = new(Base + "Math.Sign"),
    [IrIntrinsicOp.Sqrt] = new(Base + "Math.Sqrt", Base + "MathF.Sqrt"),
    [IrIntrinsicOp.Floor] = new(Base + "Math.Floor", Base + "MathF.Floor"),
    [IrIntrinsicOp.Ceiling] = new(Base + "Math.Ceiling", Base + "MathF.Ceiling"),
    [IrIntrinsicOp.Truncate] = new(Base + "Math.Truncate", Base + "MathF.Truncate"),
    [IrIntrinsicOp.RoundEven] = new(Base + "Math.Round", Base + "MathF.Round"),
    [IrIntrinsicOp.RoundAway] = new(Base + "Math.Round", Base + "MathF.Round", Base + "MidpointRounding.AwayFromZero"),
    [IrIntrinsicOp.RotateLeft] = new(Base + "Numerics.BitOperations.RotateLeft"),
    [IrIntrinsicOp.RotateRight] = new(Base + "Numerics.BitOperations.RotateRight"),
    [IrIntrinsicOp.PopCount] = new(Base + "Numerics.BitOperations.PopCount"),
    [IrIntrinsicOp.LeadingZeroCount] = new(Base + "Numerics.BitOperations.LeadingZeroCount"),
    [IrIntrinsicOp.TrailingZeroCount] = new(Base + "Numerics.BitOperations.TrailingZeroCount"),
    [IrIntrinsicOp.Log2] = new(Base + "Numerics.BitOperations.Log2"),
    [IrIntrinsicOp.BigMul] = new(Base + "Math.BigMul"),
    [IrIntrinsicOp.FloatBits] = new(Base + "BitConverter.DoubleToInt64Bits", Base + "BitConverter.SingleToInt32Bits"),
    [IrIntrinsicOp.BitsFloat] = new(Base + "BitConverter.Int64BitsToDouble", Base + "BitConverter.Int32BitsToSingle")
  };

  /// <summary>The method an intrinsic operation is written as.</summary>
  /// <param name="Method">The method as the generated code names it.</param>
  /// <param name="Single">The method for a first argument of 32 bits, where that is another one.</param>
  /// <param name="Further">An argument that follows those of the IR, or <see langword="null"/>.</param>
  private sealed record Call(string Method, string? Single = null, string? Further = null);

  /// <summary>Says why a module cannot be emitted.</summary>
  /// <param name="module">The module to check.</param>
  /// <returns>
  /// One <see cref="Unsupported"/> diagnostic per construct this backend does not know; when it knows
  /// them all, the diagnostics of <see cref="IrVerifier"/>; empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) {
    var unknown = new List<string>();
    foreach (var constant in module.Constants)
      Known(constant.Type, unknown);

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
    else if (expression is IrIntrinsic { Op: not IrIntrinsicOp.ArrayOf } intrinsic && !_intrinsics.ContainsKey(intrinsic.Op))
      unknown.Add(intrinsic.Op.ToString());

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
    IrDoWhile loop => ([loop.Condition], [loop.Body]),
    IrFor loop => ([loop.Condition], [loop.Body, loop.Step]),
    IrSwitch choice => ([choice.Value], choice.Sections.Select(section => section.Body).ToList()),
    IrBreak or IrContinue => ([], []),
    IrReturn result => (result.Value is null ? [] : [result.Value], []),
    _ => null
  };

  /// <summary>The operands of an expression, or <see langword="null"/> for a kind this backend does not know.</summary>
  private static IReadOnlyList<IrExpr>? Operands(IrExpr expression) => expression switch {
    IrConst or IrLocalRef or IrConstRef => [],
    IrUnary unary => [unary.Operand],
    IrBinary binary => [binary.Left, binary.Right],
    IrConvert conversion => [conversion.Operand],
    IrConditional choice => [choice.Condition, choice.WhenTrue, choice.WhenFalse],
    IrArrayLoad load => [load.Array, load.Index],
    IrArrayLength length => [length.Array],
    IrNewArray creation => [creation.Length],
    IrCall call => call.Arguments,
    IrStep step => [step.Target],
    IrIntrinsic intrinsic => intrinsic.Arguments,
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
  /// Whether C# checks the expression for overflow in a checked context, where the IR wraps around or
  /// saturates: the integer arithmetic that can leave its type, a step, and every cast that can lose
  /// a bit or the sign or meet a number outside its range.
  /// </summary>
  private static bool Wraps(IrExpr expression) => expression switch {
    IrUnary unary => unary.Op == IrUnaryOp.Negate && unary.Type.IsInteger,
    IrBinary binary => (binary.Op is IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply && binary.Type.IsInteger)
      || (binary.Op == IrBinaryOp.ShiftRightLogical && binary.Type.IsSigned),
    IrConvert conversion => conversion.Contract != IrConversion.Trap && !Keeps(conversion) && !Clamps(conversion),
    IrStep step => !step.Trap && step.Type.IsInteger,
    _ => false
  };

  /// <summary>Whether the IR traps where the expression overflows, which C# does in a checked context.</summary>
  private static bool Traps(IrExpr expression) => expression switch {
    IrUnary unary => unary.Op == IrUnaryOp.NegateChecked,
    IrBinary binary => IrOperators.Traps(binary.Op),
    IrConvert conversion => conversion.Contract == IrConversion.Trap && !Keeps(conversion),
    IrStep step => step.Trap,
    _ => false
  };

  /// <summary>
  /// Whether a conversion keeps every value, so that its contract does not matter: one to a
  /// floating-point type, or to an integer type that holds every value of the source.
  /// </summary>
  private static bool Keeps(IrConvert conversion)
    => conversion.Type.IsFloat || (conversion.Operand.Type.IsInteger && IrOperators.Contains(conversion.Type, conversion.Operand.Type));

  /// <summary>Whether a conversion saturates to a type narrower than <c>int</c>, which no cast does.</summary>
  private static bool Clamps(IrConvert conversion) => conversion.Contract == IrConversion.Saturate && conversion.Type.Bits < 32;

  /// <summary>The helper an expression calls, or <see langword="null"/> when it calls none.</summary>
  private static string? Helper(IrExpr expression) => expression is IrConvert conversion && Clamps(conversion) ? Saturated : null;

  /// <summary>
  /// The operand without the conversion to <c>int</c> that C# applies by itself to a narrower integer
  /// wherever it computes with one or stores one in an <c>int</c>.
  /// </summary>
  private static IrExpr Promoted(IrExpr expression)
    => expression is IrConvert { Operand.Type: { IsInteger: true, Bits: < 32 } } conversion && conversion.Type == IrType.I32 ? conversion.Operand : expression;

  /// <summary>
  /// Whether the C# compiler would compute the expression itself. It then also judges it: a constant
  /// zero divisor, a constant negative array length, a constant that overflows in a checked context
  /// and a constant quotient that overflows are errors, and a constant number outside the range of
  /// the integer it is cast to becomes whatever the compiler makes of it.
  /// </summary>
  private static bool IsConstant(IrExpr expression)
    => expression is IrConst or IrConstRef
      || (expression is IrUnary or IrBinary or IrConvert or IrConditional && Operands(expression)!.All(IsConstant));

  /// <summary>Whether a condition is the constant <c>true</c>, which C# knows never to be false.</summary>
  private static bool Always(IrExpr condition) => condition is IrConst { Value: not 0 };

  /// <summary>Whether a statement holds a <c>break</c> that leaves the loop or switch it is the body of.</summary>
  private static bool Breaks(IrStmt statement) => statement switch {
    IrBreak => true,
    IrBlock block => block.Statements.Any(Breaks),
    IrIf conditional => Breaks(conditional.Then) || (conditional.Else is not null && Breaks(conditional.Else)),
    _ => false
  };

  /// <summary>
  /// Whether execution can go on behind a statement, as C# decides it (ECMA-334, "End points and
  /// reachability"). C# refuses a switch section that can, and warns about a statement behind one
  /// that cannot, so the emitter has to know before it writes a <c>break</c> or the step of a loop.
  /// </summary>
  private static bool Completes(IrStmt statement) => statement switch {
    IrBreak or IrContinue or IrReturn => false,
    IrBlock block => block.Statements.All(Completes),
    IrIf conditional => Completes(conditional.Then) || conditional.Else is null || Completes(conditional.Else),
    IrWhile loop => !Always(loop.Condition) || Breaks(loop.Body),
    IrDoWhile loop => !Always(loop.Condition) || Breaks(loop.Body),
    IrFor loop => !Always(loop.Condition) || Breaks(loop.Body),
    IrSwitch choice => !choice.Sections.Any(section => section.IsDefault)
      || choice.Sections.Any(section => Completes(section.Body) || Breaks(section.Body)),
    _ => true
  };

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

    /// <summary>How tightly an expression binds: a primary expression, a cast, another unary one, or anything looser.</summary>
    private const int Primary = 0, Cast = 1, Unary = 2, Loose = 3;

    /// <summary>How many elements of a table stand on one line.</summary>
    private const int Row = 8;

    private readonly IrModule _module;
    private readonly StringBuilder _code = new();
    private readonly string _class;
    private readonly HashSet<string> _used;
    private readonly HashSet<string> _taken;
    private readonly Dictionary<string, string> _members;
    private Dictionary<string, string> _locals = [];

    /// <summary>
    /// Per loop around the statement being written, the step a <c>continue</c> has to run first, or
    /// <see langword="null"/> where the loop runs it by itself.
    /// </summary>
    private readonly Stack<IrBlock?> _steps = new();

    /// <summary>Whether the expression being written stands in a <c>checked(...)</c>.</summary>
    private bool _checked;

    public Writer(IrModule module) {
      this._module = module;
      this._class = Names([module.Name], [])[module.Name];
      // Without the '@' the compiler warns that a type name of lower-case letters only may become a keyword (CS8981).
      if (this._class.All(char.IsAsciiLetterLower))
        this._class = "@" + this._class;

      this._used = [.. module.Functions.SelectMany(function => Expressions(function.Body)).Select(Helper).OfType<string>()];
      var members = module.Constants.Select(constant => constant.Name).Concat(module.Functions.Select(function => function.Name));
      this._members = Names(members, this._used.Prepend(this.ClassName));
      this._taken = [.. this._members.Values.Select(member => member.TrimStart('@')), .. this._used];
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
      foreach (var constant in this._module.Constants)
        this.Constant(constant);

      foreach (var function in this._module.Functions)
        this.Function(function);

      foreach (var (_, lines) in _helpers.Where(helper => this._used.Contains(helper.Name)))
        this._code.Append("\n  ").AppendJoin("\n  ", lines).Append('\n');

      return this._code.Append("\n}\n").ToString();
    }

    /// <summary>
    /// A number of the module as a <c>const</c>, a table as a field that is assigned once: nothing
    /// the IR can say stores into a table, so the generated class never does either.
    /// </summary>
    private void Constant(IrModuleConst constant) {
      this._code.Append("\n  private ");
      var name = this._members[constant.Name];
      if (constant.Type.Element is not { } element) {
        this._code.Append("const ").Append(TypeName(constant.Type)).Append(' ').Append(name).Append(" = ").Append(Bare(constant.Type, constant.Values[0])).Append(";\n");
        return;
      }

      var elements = constant.Values.Select(value => Bare(element, value)).ToList();
      this._code.Append("static readonly ").Append(TypeName(constant.Type)).Append(' ').Append(name).Append(" = ");
      if (elements.Count <= Row)
        this._code.Append(Braces(elements));
      else
        this._code.Append("{\n    ").AppendJoin(",\n    ", elements.Chunk(Row).Select(row => string.Join(", ", row))).Append("\n  }");

      this._code.Append(";\n");
    }

    private void Function(IrFunction function) {
      // A variable spelled like a member or a helper would hide it, so their names are taken.
      this._locals = Names(function.Parameters.Concat(function.Locals).Select(variable => variable.Name), this._taken);
      var parameters = string.Join(", ", function.Parameters.Select(parameter => $"{TypeName(parameter.Type)} {this._locals[parameter.Name]}"));
      this._code.Append("\n  ").Append(function.IsPublic ? "public" : "private").Append(" static ")
        .Append(TypeName(function.ReturnType)).Append(' ').Append(this._members[function.Name])
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
        case IrAssign or IrStore or IrExprStmt:
          this.Line(depth).Append(this.Effect(statement)).Append(";\n");
          break;
        case IrIf conditional:
          this.Line(depth);
          this.If(conditional, depth);
          break;
        case IrWhile loop:
          this.Line(depth).Append("while (").Append(this.Text(loop.Condition)).Append(") {\n");
          this.Body(loop.Body, null, depth);
          this.Line(depth).Append("}\n");
          break;
        case IrDoWhile loop:
          this.Line(depth).Append("do {\n");
          this.Body(loop.Body, null, depth);
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
          if (this._steps.Peek() is { } step)
            this.Step(step, depth);

          this.Line(depth).Append("continue;\n");
          break;
        default:
          var result = (IrReturn)statement;
          this.Line(depth).Append(result.Value is null ? "return" : "return " + this.Stored(result.Value)).Append(";\n");
          break;
      }
    }

    /// <summary>
    /// A statement that is one expression to C#, without its semicolon: an assignment, a call or a
    /// step as it stands, any other expression assigned to the discard so that it is evaluated.
    /// </summary>
    private string Effect(IrStmt statement) {
      switch (statement) {
        case IrAssign assign:
          return $"{this._locals[assign.Local.Name]} = {this.Stored(assign.Value)}";
        case IrStore store:
          return $"{this.Element(store.Array, store.Index)} = {this.Stored(store.Value)}";
        default:
          var expression = ((IrExprStmt)statement).Expression;
          var text = this.Text(expression);
          return expression.Type == IrType.Void || expression is IrCall or IrStep { Trap: false } ? text : "_ = " + text;
      }
    }

    /// <summary>The body of a loop, whose <c>continue</c> statements run the given step first.</summary>
    private void Body(IrBlock body, IrBlock? step, int depth) {
      this._steps.Push(step);
      this.Statements(body, depth + 1);
      this._steps.Pop();
    }

    /// <summary>
    /// A loop with a step. One statement that C# takes as an expression is the third part of a
    /// <c>for</c>. Any other step is written out wherever a pass ends: at the end of the body when
    /// execution gets there, and in front of every <c>continue</c> of the loop.
    /// </summary>
    private void For(IrFor loop, int depth) {
      if (loop.Step.Statements is [IrAssign or IrStore or IrExprStmt]) {
        this.Line(depth).Append("for (; ").Append(this.Text(loop.Condition)).Append("; ").Append(this.Effect(loop.Step.Statements[0])).Append(") {\n");
        this.Body(loop.Body, null, depth);
      } else {
        this.Line(depth).Append("while (").Append(this.Text(loop.Condition)).Append(") {\n");
        this.Body(loop.Body, loop.Step, depth);
        if (Completes(loop.Body))
          this.Step(loop.Step, depth + 1);
      }

      this.Line(depth).Append("}\n");
    }

    /// <summary>The statements of a step, in a block of their own when they declare something, because a step can stand more than once in a block.</summary>
    private void Step(IrBlock step, int depth) {
      if (step.Statements.Any(statement => statement is IrDeclare))
        this.Statement(step, depth);
      else
        this.Statements(step, depth);
    }

    /// <summary>
    /// A switch whose sections are blocks, as the sections of the IR are scopes, each ending in the
    /// <c>break</c> C# demands where execution would otherwise leave the section at its end.
    /// </summary>
    private void Switch(IrSwitch choice, int depth) {
      // C# warns about a switch without sections; its value is evaluated all the same.
      if (choice.Sections.Count == 0) {
        this.Line(depth).Append("_ = ").Append(this.Text(choice.Value)).Append(";\n");
        return;
      }

      var type = choice.Value.Type;
      this.Line(depth).Append("switch (").Append(this.Text(choice.Value)).Append(") {\n");
      foreach (var section in choice.Sections) {
        var labels = section.Labels.Select(label => $"case {Bare(type, label)}:").ToList();
        if (section.IsDefault)
          labels.Add("default:");

        for (var index = 0; index < labels.Count; ++index)
          this.Line(depth + 1).Append(labels[index]).Append(index + 1 < labels.Count ? "\n" : " {\n");

        this.Statements(section.Body, depth + 2);
        if (Completes(section.Body))
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

    /// <summary>The expression as it stands on its own: a condition, an argument of the base library or the operand of nothing.</summary>
    private string Text(IrExpr expression) => this.Expression(expression).Text;

    /// <summary>
    /// The expression where its type is already given — an initializer, a right-hand side, an
    /// argument, a returned value. A constant needs neither cast nor suffix there, because C#
    /// converts a literal to the integer type that holds its value, and a narrow integer becomes an
    /// <c>int</c> unasked.
    /// </summary>
    private string Stored(IrExpr expression)
      => expression is IrConst constant ? Bare(constant.Type, constant.Value) : this.Text(Promoted(expression));

    /// <summary>The expression as an operand: in parentheses when it binds more loosely than <paramref name="level"/>.</summary>
    private string Operand(IrExpr expression, int level) {
      var (text, binding) = this.Expression(expression);
      return binding > level ? $"({text})" : text;
    }

    /// <summary>
    /// An operand the C# compiler must leave to run time when it could compute it and would then
    /// refuse or fold what the IR defines otherwise: the divisor 0, the divisor -1, a negative index
    /// or length, an operand of checked arithmetic, a number cast to an integer type that does not
    /// hold it. <c>Math.Max</c> of the value and itself is the value, and no constant to C#.
    /// </summary>
    private string AtRunTime(IrExpr expression, bool harmless, int level) {
      if (expression is IrConst ? harmless : !IsConstant(expression))
        return this.Operand(expression, level);

      var text = this.Text(expression);
      return $"{Base}Math.Max({text}, {text})";
    }

    private string Element(IrExpr array, IrExpr index)
      => $"{this.Operand(array, Primary)}[{this.AtRunTime(Promoted(index), index is IrConst { Value: >= 0 }, Loose)}]";

    /// <summary>The digits of an integer; those of a <c>u64</c> are the ones of its 64 bits read as unsigned.</summary>
    private static string Number(IrType type, long value) => IrOperators.Number(type, value).ToString(CultureInfo.InvariantCulture);

    /// <summary>A constant where C# knows its type: an integer as its digits, anything else as its literal.</summary>
    private static string Bare(IrType type, long value) => type.IsInteger ? Number(type, value) : Literal(type, value).Text;

    private static string Braces(IReadOnlyList<string> elements) => elements.Count == 0 ? "{ }" : $"{{ {string.Join(", ", elements)} }}";

    /// <summary>
    /// A constant as an expression of exactly its type: a suffix where C# has one, a cast for the
    /// integers narrower than <c>int</c>, and the shortest digits that read back as the same
    /// floating-point number.
    /// </summary>
    private static (string Text, int Binding) Literal(IrType type, long value) {
      if (type == IrType.Bool)
        return (value != 0 ? "true" : "false", Primary);

      var text = type.IsFloat ? Real(type, IrOperators.Float(type, value)) : Number(type, value);
      var binding = text[0] == '-' ? Unary : Primary;
      return type.Kind switch {
        IrTypeKind.I32 or IrTypeKind.F32 or IrTypeKind.F64 => (text, binding),
        IrTypeKind.U32 => (text + "u", Primary),
        IrTypeKind.I64 => (text + "L", binding),
        IrTypeKind.U64 => (text + "UL", Primary),
        _ => ($"({_types[type.Kind]}){(binding == Unary ? $"({text})" : text)}", Cast)
      };
    }

    private static string Real(IrType type, double value) {
      var name = _types[type.Kind];
      if (double.IsNaN(value))
        return name + ".NaN";

      if (double.IsInfinity(value))
        return name + (value < 0 ? ".NegativeInfinity" : ".PositiveInfinity");

      if (type == IrType.F32)
        return ((float)value).ToString("R", CultureInfo.InvariantCulture) + "f";

      var digits = value.ToString("R", CultureInfo.InvariantCulture);
      return digits.Contains('.') || digits.Contains('E') ? digits : digits + ".0";
    }

    /// <summary>
    /// The expression, in a <c>checked(...)</c> where the IR traps on overflow and in an
    /// <c>unchecked(...)</c> where it wraps around inside one of those. Outside of both, the body of
    /// the method is unchecked.
    /// </summary>
    private (string Text, int Binding) Expression(IrExpr expression) {
      var traps = Traps(expression);
      if (traps == this._checked || !(traps || Wraps(expression)))
        return this.Plain(expression);

      this._checked = traps;
      var text = this.Plain(expression).Text;
      this._checked = !traps;
      return ($"{(traps ? "checked" : "unchecked")}({text})", Primary);
    }

    private (string Text, int Binding) Plain(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          return Literal(constant.Type, constant.Value);
        case IrConstRef reference:
          return (this._members[reference.Constant.Name], Primary);
        case IrLocalRef reference:
          return (this._locals[reference.Local.Name], Primary);
        case IrUnary unary:
          var operand = Promoted(unary.Operand);
          return (_unary[unary.Op] + (unary.Op == IrUnaryOp.NegateChecked ? this.AtRunTime(operand, false, Cast) : this.Operand(operand, Cast)), Unary);
        case IrBinary binary:
          return this.Binary(binary);
        case IrConvert conversion:
          return this.Convert(conversion);
        case IrConditional choice:
          return ($"{this.Operand(choice.Condition, Unary)} ? {this.Operand(choice.WhenTrue, Unary)} : {this.Operand(choice.WhenFalse, Unary)}", Loose);
        case IrArrayLoad load:
          return (this.Element(load.Array, load.Index), Primary);
        case IrArrayLength length:
          return ($"{this.Operand(length.Array, Primary)}.Length", Primary);
        case IrNewArray creation:
          var count = this.AtRunTime(Promoted(creation.Length), creation.Length is IrConst { Value: >= 0 }, Loose);
          return ($"new {TypeName(creation.Type.Element!)}[{count}]", Unary);
        case IrStep step:
          var target = step.Target is IrArrayLoad element ? this.Element(element.Array, element.Index) : this._locals[((IrLocalRef)step.Target).Local.Name];
          var sign = step.Increment ? "++" : "--";
          return step.Postfix ? (target + sign, Primary) : (sign + target, Unary);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        default:
          var call = (IrCall)expression;
          return ($"{this._members[call.Function]}({string.Join(", ", call.Arguments.Select(this.Stored))})", Primary);
      }
    }

    private (string Text, int Binding) Binary(IrBinary binary) {
      var type = binary.Left.Type;
      var symbol = _binary[binary.Op];
      var first = Promoted(binary.Left);
      var second = Promoted(binary.Right);
      var left = this.Operand(first, Unary);
      string right;
      switch (binary.Op) {
        case IrBinaryOp.Divide or IrBinaryOp.Remainder when type.IsInteger:
          right = this.AtRunTime(second, second is IrConst { Value: not 0 } divisor && !(type.IsSigned && divisor.Value == -1), Unary);
          break;
        case IrBinaryOp.ShiftRightLogical when type.IsSigned:
          // Written with casts rather than '>>>' so that the file also compiles as C# older than 11.
          return ($"({TypeName(type)})(({(type.IsLong ? "ulong" : "uint")}){this.Operand(first, Cast)} >> {this.Operand(second, Unary)})", Cast);
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          left = this.AtRunTime(first, !IsConstant(second), Unary);
          right = this.Operand(second, Unary);
          break;
        default:
          right = this.Operand(second, Unary);
          break;
      }

      // C# may compute a float expression more precisely than float; the cast makes it binary32 (ECMA-334, "Floating-point types").
      return binary.Type == IrType.F32 ? ($"(float)({left} {symbol} {right})", Cast) : ($"{left} {symbol} {right}", Loose);
    }

    /// <summary>
    /// A conversion as a cast. The context it stands in gives the cast its contract: checked it
    /// throws, unchecked it keeps the low bits of an integer and takes the nearest value for a
    /// floating-point number, a NaN becoming 0 (ADR 0002, .NET 9 and later).
    /// </summary>
    private (string Text, int Binding) Convert(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      if (from == to)
        return this.Expression(conversion.Operand);

      if (Clamps(conversion)) {
        var low = Number(to, (long)IrOperators.Minimum(to));
        var high = Number(to, (long)IrOperators.Maximum(to));
        return ($"({TypeName(to)}){Saturated}({this.Text(conversion.Operand)}, {low}, {high})", Cast);
      }

      var foldable = !Keeps(conversion) && (from.IsFloat || conversion.Contract == IrConversion.Trap);
      var operand = foldable ? this.AtRunTime(conversion.Operand, false, Cast) : this.Operand(conversion.Operand, Cast);
      return ($"({TypeName(to)}){operand}", Cast);
    }

    private (string Text, int Binding) Intrinsic(IrIntrinsic intrinsic) {
      if (intrinsic.Op == IrIntrinsicOp.ArrayOf)
        return ($"new {TypeName(intrinsic.Type)} {Braces(intrinsic.Arguments.Select(this.Stored).ToList())}", Unary);

      var call = _intrinsics[intrinsic.Op];
      var method = intrinsic.Arguments[0].Type.Kind is IrTypeKind.F32 or IrTypeKind.I32 ? call.Single ?? call.Method : call.Method;
      var arguments = intrinsic.Arguments.Select(this.Text);
      return ($"{method}({string.Join(", ", call.Further is null ? arguments : arguments.Append(call.Further))})", Primary);
    }

  }

}
