using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;

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
/// <para>
/// What is written depends on the <see cref="CSharpProfile"/>: the target framework and the version
/// of the language. Every such decision is a row of <see cref="CSharpCapabilities"/> — an API the
/// framework has is called, one it lacks is replaced by a call of the older base library or by a
/// private helper the class gets only when it uses it, and a feature of the language is written in
/// the form the language version has.
/// </para>
/// </summary>
public static class CSharpEmitter {

  /// <summary>The code for an IR construct this backend cannot write.</summary>
  public const string Unsupported = "PLY1052";

  /// <summary>The base library by its full name, which no name of the module can hide.</summary>
  private const string Base = "global::System.";

  /// <summary>The code for an IR construct that has no form in the chosen profile.</summary>
  public const string Inexpressible = "PLY1053";

  /// <summary>A place of a <see cref="CSharpForm.Template"/>: an argument by its position, or the helper.</summary>
  private static readonly System.Text.RegularExpressions.Regex _placeholder = new(@"\{[0-9h]\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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

  /// <summary>Says why a module cannot be emitted for the default profile.</summary>
  /// <param name="module">The module to check.</param>
  /// <returns>What <see cref="Check(IrModule, CSharpProfile, CSharpCapabilities?)"/> returns for <see cref="CSharpProfile.Default"/>.</returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) => Check(module, CSharpProfile.Default);

  /// <summary>Says why a module cannot be emitted for a profile.</summary>
  /// <param name="module">The module to check.</param>
  /// <param name="profile">The profile the module is to be written for.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <returns>
  /// One <see cref="Unsupported"/> diagnostic per construct this backend does not know; when it knows
  /// them all, the diagnostics of <see cref="IrVerifier"/>; for well-typed IR, per operation the
  /// table has no row for on its type of operand an <see cref="Unsupported"/> diagnostic, and per
  /// operation the profile neither has nor has a replacement for an <see cref="Inexpressible"/> one;
  /// empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, CSharpProfile profile, CSharpCapabilities? capabilities = null) {
    var table = capabilities ?? CSharpCapabilities.Default;
    var unknown = new List<string>();
    foreach (var constant in module.Constants)
      Known(constant.Type, unknown);

    foreach (var function in module.Functions) {
      Known(function.ReturnType, unknown);
      foreach (var variable in function.Parameters.Concat(function.Locals))
        Known(variable.Type, unknown);

      Known(function.Body, table, unknown);
    }

    if (unknown.Count > 0)
      return unknown.Distinct().Select(Unknown).ToList();

    var illTyped = IrVerifier.Verify(module);
    if (illTyped.Count > 0)
      return illTyped;

    // What the table decides is asked of well-typed IR only: an operation then has its operands.
    var problems = new List<Diagnostic>();
    void Ask(IrExpr expression) {
      var row = Row(expression, table);
      if (row is null)
        problems.Add(Unknown(expression is IrIntrinsic intrinsic ? $"{intrinsic.Op} of {intrinsic.Arguments[0].Type}" : $"{expression.Type} with saturation"));
      else if (row.IsRewrittenIn(profile))
        // Written as other operations, the profile has to have what those are written with.
        foreach (var part in Expressions(row.Rewrite!((IrIntrinsic)expression)).Where(IsTabled))
          Ask(part);
      else if (!row.IsIn(profile) && row.Replacement is null)
        problems.Add(new Diagnostic(Inexpressible, DiagnosticSeverity.Error, $"'{row.Name}' cannot be expressed in C# profile '{profile.Name}'"));
    }

    foreach (var expression in module.Functions.SelectMany(function => Expressions(function.Body)).Where(IsTabled))
      Ask(expression);

    return problems.Distinct().ToList();
  }

  private static Diagnostic Unknown(string what) => new(Unsupported, DiagnosticSeverity.Error, $"'{what}' is not supported by the C# backend yet");

  /// <summary>Whether a text is a namespace name: identifiers separated by dots.</summary>
  /// <param name="name">The text.</param>
  /// <returns><see langword="true"/> when the text can follow the word <c>namespace</c>.</returns>
  public static bool IsNamespace(string name) => name.Split('.').All(IsIdentifier);

  /// <summary>
  /// Emits the module as <c>&lt;name&gt;.cs</c>: a static class whose public methods are the public
  /// functions. File and class carry the name of the module as C# spells it.
  /// </summary>
  /// <param name="module">A module that <see cref="Check(IrModule)"/> accepts.</param>
  /// <param name="namespace">The namespace of the class, or <see langword="null"/> for none.</param>
  /// <returns>The generated file, written for <see cref="CSharpProfile.Default"/>.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted, or the namespace is not a name.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, string? @namespace = null) => Emit(module, CSharpProfile.Default, @namespace);

  /// <summary>
  /// Emits the module as <c>&lt;name&gt;.cs</c> for a profile: a static class whose public methods are
  /// the public functions, written with what the profile's framework and language version have. The
  /// header of the file names the profile.
  /// </summary>
  /// <param name="module">A module that <see cref="Check(IrModule, CSharpProfile, CSharpCapabilities?)"/> accepts for the profile.</param>
  /// <param name="profile">The target framework and language version.</param>
  /// <param name="namespace">The namespace of the class, or <see langword="null"/> for none.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted, or the namespace is not a name.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, CSharpProfile profile, string? @namespace = null, CSharpCapabilities? capabilities = null)
    => Emit(module, profile, FloatMode.Accurate, @namespace, capabilities);

  /// <summary>
  /// Whether the class writes a floating-point operation itself, in a profile and a mode: an exact one
  /// where the profile's framework has the call, one with an <c>ulp(n)</c> contract only in
  /// <see cref="FloatMode.Accurate"/> and where <see cref="NativeAccuracy"/> admits .NET's function.
  /// Every other operation of the table is its portable implementation, which
  /// <see cref="Legalize"/> puts in; an operation outside the table is the emitter's own business.
  /// </summary>
  /// <param name="intrinsic">The operation.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <returns><see langword="true"/> when the operation is written as a call of the base library or a helper of the emitter.</returns>
  public static bool Writes(IrIntrinsic intrinsic, CSharpProfile profile, FloatMode mode, CSharpCapabilities? capabilities = null) {
    if (!FloatLegalization.IsReplaceable(intrinsic))
      return true;

    var format = intrinsic.Arguments[0].Type;
    if ((capabilities ?? CSharpCapabilities.Default).Of(intrinsic.Op, format.Kind) is not { } row || !row.IsIn(profile))
      return false;

    return FloatOperationTable.Of(intrinsic.Op)!.Contract.Kind == FloatContractKind.Exact
      || (mode == FloatMode.Accurate && NativeAccuracy.Slack(NativeAccuracy.DotNet, intrinsic.Op, format) is not null);
  }

  /// <summary>
  /// The module with every floating-point operation the class does not write itself (<see cref="Writes"/>)
  /// replaced by its portable implementation, copied into the class.
  /// </summary>
  /// <param name="module">The module.</param>
  /// <param name="implementations">Gives the portable implementations, as the C# frontend imports <see cref="FloatOperationSources"/>; asked only when needed.</param>
  /// <param name="profile">The profile.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <returns>The module to emit.</returns>
  public static IrModule Legalize(IrModule module, Func<IrModule> implementations, CSharpProfile profile, FloatMode mode, CSharpCapabilities? capabilities = null)
    => FloatLegalization.Lower(module, implementations, intrinsic => Writes(intrinsic, profile, mode, capabilities));

  /// <summary>
  /// Emits the module for a profile and a floating-point mode. The header names the profile and, where
  /// the class calls a native function with an <c>ulp(n)</c> contract, each such function with the
  /// comparison bound of ADR 0005; in <see cref="FloatMode.Strict"/> it says that the results are the
  /// same bits on every target.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check(IrModule, CSharpProfile, CSharpCapabilities?)"/> accepts.</param>
  /// <param name="profile">The target framework and language version.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="namespace">The namespace of the class, or <see langword="null"/> for none.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted, or the namespace is not a name.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace = null, CSharpCapabilities? capabilities = null) {
    if (@namespace is not null && !IsNamespace(@namespace))
      throw new ArgumentException($"'{@namespace}' is not a namespace name.", nameof(@namespace));

    var table = capabilities ?? CSharpCapabilities.Default;
    var problems = Check(module, profile, table);
    if (problems.Count > 0)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problems[0].Message}.", nameof(module));

    // The first pass finds out which helpers the class calls; their names are settled before the second one writes it.
    var draft = new Writer(module, profile, table, [], mode);
    draft.Module(@namespace);
    var writer = new Writer(module, profile, table, draft.Helpers, mode);
    return [new GeneratedFile(writer.ClassName + ".cs", writer.Module(@namespace))];
  }

  private static void Known(IrType type, List<string> unknown) {
    var scalar = type;
    while (scalar.Element is not null)
      scalar = scalar.Element;

    if (!_types.ContainsKey(scalar.Kind))
      unknown.Add(type.ToString());
  }

  private static void Known(IrStmt statement, CSharpCapabilities table, List<string> unknown) {
    var parts = Parts(statement);
    if (parts is null) {
      unknown.Add(statement.GetType().Name);
      return;
    }

    foreach (var expression in parts.Value.Expressions)
      Known(expression, table, unknown);

    foreach (var inner in parts.Value.Statements)
      Known(inner, table, unknown);
  }

  private static void Known(IrExpr expression, CSharpCapabilities table, List<string> unknown) {
    Known(expression.Type, unknown);
    var operands = Operands(expression);
    if (operands is null)
      unknown.Add(expression.GetType().Name);
    else if (expression is IrUnary unary && !_unary.ContainsKey(unary.Op))
      unknown.Add(unary.Op.ToString());
    else if (expression is IrBinary binary && !_binary.ContainsKey(binary.Op))
      unknown.Add(binary.Op.ToString());
    else if (expression is IrIntrinsic { Op: not IrIntrinsicOp.ArrayOf } intrinsic && !table.Rows.Any(row => row.Operation == intrinsic.Op))
      unknown.Add(intrinsic.Op.ToString());

    foreach (var operand in operands ?? [])
      Known(operand, table, unknown);
  }

  /// <summary>
  /// Whether the capability table says how an expression is written: an intrinsic operation that is
  /// a call, and the conversion of a floating-point number to an integer that saturates.
  /// </summary>
  private static bool IsTabled(IrExpr expression)
    => expression is IrIntrinsic { Op: not IrIntrinsicOp.ArrayOf } || (expression is IrConvert conversion && Saturates(conversion));

  /// <summary>The row of the capability table an expression that <see cref="IsTabled"/> is written by, or <see langword="null"/> when the table has none.</summary>
  private static CSharpCapability? Row(IrExpr expression, CSharpCapabilities table)
    => expression is IrIntrinsic intrinsic ? table.Of(intrinsic.Op, CSharpCapabilities.OperandOf(intrinsic)) : table.OfSaturation(expression.Type.Kind);

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

  /// <summary>Every local a statement or one in it sets to a value, with the value.</summary>
  private static IEnumerable<(IrLocal Local, IrExpr Value)> Definitions(IrStmt statement) {
    IEnumerable<(IrLocal, IrExpr)> own = statement switch {
      IrDeclare { Initializer: { } value } declare => [(declare.Local, value)],
      IrAssign assign => [(assign.Local, assign.Value)],
      _ => []
    };
    return own.Concat(Parts(statement)!.Value.Statements.SelectMany(Definitions));
  }

  /// <summary>
  /// The locals a function sets to the complement of a shift count for a width, which the JIT may
  /// substitute into a shift-xor (<see cref="ShiftXorGuard"/>), each with that width.
  /// </summary>
  private static HashSet<(IrLocal Local, int Width)> Complements(IrFunction function)
    => [.. Definitions(function.Body).SelectMany(definition => new[] { 32, 64 }.Where(width => ShiftXorGuard.IsComplement(definition.Value, width)).Select(width => (definition.Local, width)))];


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

  /// <summary>Whether a conversion takes a floating-point number to the nearest value of an integer type.</summary>
  private static bool Saturates(IrConvert conversion) => conversion.Contract == IrConversion.Saturate && conversion.Type.IsInteger;

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
    private readonly CSharpProfile _profile;
    private readonly CSharpCapabilities _table;
    private readonly FloatMode _mode;
    private readonly StringBuilder _code = new();
    private readonly string _class;
    private readonly HashSet<CSharpHelper> _used = [];
    private readonly Dictionary<string, string> _helpers;
    private readonly HashSet<string> _taken;
    private readonly Dictionary<string, string> _members;
    private Dictionary<string, string> _locals = [];

    /// <summary>The locals the function being written sets to the complement of a shift count, with its width.</summary>
    private HashSet<(IrLocal Local, int Width)> _complements = [];

    /// <summary>
    /// Per loop around the statement being written, the step a <c>continue</c> has to run first, or
    /// <see langword="null"/> where the loop runs it by itself.
    /// </summary>
    private readonly Stack<IrBlock?> _steps = new();

    /// <summary>Whether the expression being written stands in a <c>checked(...)</c>.</summary>
    private bool _checked;

    /// <param name="module">The module to write.</param>
    /// <param name="profile">The profile to write it for.</param>
    /// <param name="table">The capability table.</param>
    /// <param name="helpers">The helpers the class is known to call, whose names no variable may take.</param>
    /// <param name="mode">The floating-point mode, which the header states.</param>
    public Writer(IrModule module, CSharpProfile profile, CSharpCapabilities table, IReadOnlyCollection<CSharpHelper> helpers, FloatMode mode) {
      this._module = module;
      this._mode = mode;
      this._profile = profile;
      this._table = table;
      this._class = Names([module.Name], [])[module.Name];
      // Without the '@' the compiler warns that a type name of lower-case letters only may become a keyword (CS8981).
      if (this._class.All(char.IsAsciiLetterLower))
        this._class = "@" + this._class;

      // A member keeps its name in every profile; a helper of that name gives way, as does a variable to both.
      var members = module.Constants.Select(constant => constant.Name).Concat(module.Functions.Select(function => function.Name));
      this._members = Names(members, [this.ClassName]);
      var taken = this._members.Values.Select(member => member.TrimStart('@')).Append(this.ClassName).ToList();
      this._helpers = Names(helpers.Select(helper => helper.Name), taken);
      this._taken = [.. taken, .. this._helpers.Values];
    }

    /// <summary>The helpers the class has called so far.</summary>
    public IReadOnlyCollection<CSharpHelper> Helpers => this._used;

    private bool Has(CSharpFeature feature) => this._table.Has(feature, this._profile);

    /// <summary>The name of a helper in this class; calling it is what makes the class declare it.</summary>
    private string Helper(CSharpHelper helper) {
      this._used.Add(helper);
      return this._helpers.GetValueOrDefault(helper.Name, helper.Name);
    }

    /// <summary>
    /// Whether C# checks the expression for overflow in a checked context, where the IR wraps around or
    /// saturates: the integer arithmetic that can leave its type, a step, and every cast that can lose
    /// a bit or the sign or meet a number outside its range. A helper is no cast, and neither is the
    /// logical shift where the language has an operator for it.
    /// </summary>
    private bool Wraps(IrExpr expression) => expression switch {
      IrUnary unary => unary.Op == IrUnaryOp.Negate && unary.Type.IsInteger,
      IrBinary binary => (binary.Op is IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply && binary.Type.IsInteger)
        || (binary.Op == IrBinaryOp.ShiftRightLogical && binary.Type.IsSigned && !this.Has(CSharpFeature.UnsignedRightShift)),
      IrConvert conversion => conversion.Contract != IrConversion.Trap && !Keeps(conversion) && this.Casts(conversion),
      IrStep step => !step.Trap && step.Type.IsInteger,
      IrIntrinsic { Op: not IrIntrinsicOp.ArrayOf } intrinsic => this.Rewritten(intrinsic) is { } rewritten ? Expressions(rewritten).Any(this.Wraps) : this.Form(intrinsic).Template is not null,
      _ => false
    };

    /// <summary>
    /// The form an intrinsic is written in for the profile, which the check before writing made sure
    /// there is. A form with a template casts between the widths of the integers it works on, which
    /// has to wrap around: it stands where overflow is not checked.
    /// </summary>
    private CSharpForm Form(IrIntrinsic intrinsic) => this.Form(Row(intrinsic, this._table)!, intrinsic.Arguments);

    /// <summary>An intrinsic as the other operations its row writes it as in the profile, or <see langword="null"/> when its row writes it itself.</summary>
    private IrExpr? Rewritten(IrIntrinsic intrinsic) {
      var row = Row(intrinsic, this._table)!;
      return row.IsRewrittenIn(this._profile) ? row.Rewrite!(intrinsic) : null;
    }

    /// <summary>The form of a row for arguments: the one for arguments that cannot trap where they cannot, else the profile's.</summary>
    private CSharpForm Form(CSharpCapability row, IReadOnlyList<IrExpr> arguments)
      => row.Total is { } total && row.IsTotal!(arguments) ? total : row.IsIn(this._profile) ? row.Native! : row.Replacement!;

    /// <summary>Whether a conversion is written as a cast: any but a saturating one the profile has no cast for.</summary>
    private bool Casts(IrConvert conversion) => !Saturates(conversion) || Row(conversion, this._table)!.IsIn(this._profile);

    /// <summary>The name of the class as the compiler reads it, without the <c>@</c> a keyword needs.</summary>
    public string ClassName => this._class.TrimStart('@');

    /// <summary>
    /// The file: a header that names the profile, the namespace in the form the language version has
    /// — one that holds the rest of the file, or a block around the class — and the class.
    /// </summary>
    public string Module(string? @namespace) {
      this._code.Append("public static class ").Append(this._class).Append(" {\n");
      foreach (var constant in this._module.Constants)
        this.Constant(constant);

      foreach (var function in this._module.Functions)
        this.Function(function);

      // The table's order is the order of the helpers, whatever order the class calls them in.
      foreach (var helper in this._table.Rows.Select(row => row.Helper).OfType<CSharpHelper>().Distinct().Where(this._used.Contains))
        this._code.Append("\n  ").AppendJoin("\n  ", helper.Lines(this._helpers.GetValueOrDefault(helper.Name, helper.Name))).Append('\n');

      var type = this._code.Append("\n}\n").ToString();
      var header = $"// {this.ClassName}: generated by polypiler. Change the source, not this file.\n// Profile: {this._profile}.\n"
        + string.Concat(FloatManifest.Lines(this._module, this._mode, NativeAccuracy.DotNet).Select(line => $"// {line}\n")) + "\n";
      if (@namespace is null)
        return header + type;

      var name = string.Join('.', @namespace.Split('.').Select(part => Names([part], [])[part]));
      if (this.Has(CSharpFeature.FileScopedNamespace))
        return $"{header}namespace {name};\n\n{type}";

      var indented = string.Join('\n', type.TrimEnd('\n').Split('\n').Select(line => line.Length == 0 ? line : "  " + line));
      return $"{header}namespace {name} {{\n\n{indented}\n\n}}\n";
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
      this._complements = Complements(function);
      var parameters = string.Join(", ", function.Parameters.Select(parameter => $"{TypeName(parameter.Type)} {this._locals[parameter.Name]}"));
      this._code.Append("\n  ").Append(function.IsPublic ? "public" : "private").Append(" static ")
        .Append(TypeName(function.ReturnType)).Append(' ').Append(this._members[function.Name])
        .Append('(').Append(parameters).Append(')');
      var wraps = Expressions(function.Body).Any(this.Wraps);
      if (function.Body.Statements is [IrReturn { Value: { } result }] && this.Has(CSharpFeature.ExpressionBody)) {
        // A result that is itself a checked expression sets the context of everything in it.
        var value = this.Stored(result);
        this._code.Append(" => ").Append(wraps && !Traps(Promoted(result)) ? $"unchecked({value})" : value).Append(";\n");
        return;
      }

      this._code.Append(" {\n");
      if (wraps) {
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
          this.Line(depth).Append(this.Infers(declare) ? "var" : TypeName(declare.Local.Type)).Append(' ').Append(this._locals[declare.Local.Name]);
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
    /// Whether a declaration leaves its type to the compiler: where the language has <c>var</c> and
    /// the initializer is written as an expression of exactly the variable's type — which a constant
    /// is not, because it stands without its suffix, nor a narrow integer that C# makes an
    /// <c>int</c> unasked. Inside a class named <c>var</c> the word is that class.
    /// </summary>
    private bool Infers(IrDeclare declare)
      => declare.Initializer is { } initializer and not IrConst && Promoted(initializer) == initializer
        && this.ClassName != "var" && this.Has(CSharpFeature.ImplicitlyTypedLocal);

    /// <summary>
    /// An expression evaluated for its effects only: assigned to the discard where the language has
    /// one, handed to a helper that does nothing with it where it has not.
    /// </summary>
    private string Discarded(IrExpr expression) {
      var text = this.Text(expression);
      return this.Has(CSharpFeature.Discard) ? "_ = " + text : $"{this.Helper(this._table.Of(CSharpFeature.Discard)!.Helper!)}({text})";
    }

    /// <summary>
    /// A statement that is one expression to C#, without its semicolon: an assignment, a call or a
    /// step as it stands, any other expression discarded so that it is evaluated.
    /// </summary>
    private string Effect(IrStmt statement) {
      switch (statement) {
        case IrAssign assign:
          return $"{this._locals[assign.Local.Name]} = {this.Stored(assign.Value)}";
        case IrStore store:
          return $"{this.Element(store.Array, store.Index)} = {this.Stored(store.Value)}";
        default:
          var expression = ((IrExprStmt)statement).Expression;
          return expression.Type == IrType.Void || expression is IrCall or IrStep { Trap: false } ? this.Text(expression) : this.Discarded(expression);
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
        this.Line(depth).Append(this.Discarded(choice.Value)).Append(";\n");
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
      if (traps == this._checked || !(traps || this.Wraps(expression)))
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
      // RyuJIT takes some exclusive ors of two shifts for a rotation also where they are none (CSharpRuntimeDefect.ShiftXorRotation).
      if (binary.Op == IrBinaryOp.Xor && this._table.Suffers(CSharpRuntimeDefect.ShiftXorRotation, this._profile))
        binary = ShiftXorGuard.Guarded(binary, this._complements);

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
        case IrBinaryOp.ShiftRightLogical when type.IsSigned && this.Has(CSharpFeature.UnsignedRightShift):
          return ($"{left} >>> {this.Operand(second, Unary)}", Loose);
        case IrBinaryOp.ShiftRightLogical when type.IsSigned:
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
    /// floating-point number, a NaN becoming 0 (ADR 0002, .NET 9 and later). Where the profile has
    /// no cast that saturates — an older framework, or a target narrower than <c>int</c>, whose cast
    /// saturates to <c>int</c> and then keeps the low bits — the row's helper compares first.
    /// </summary>
    private (string Text, int Binding) Convert(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      if (from == to)
        return this.Expression(conversion.Operand);

      if (!this.Casts(conversion))
        return this.Call(Row(conversion, this._table)!, [conversion.Operand]);

      var foldable = !Keeps(conversion) && (from.IsFloat || conversion.Contract == IrConversion.Trap);
      var operand = foldable ? this.AtRunTime(conversion.Operand, false, Cast) : this.Operand(conversion.Operand, Cast);
      return ($"({TypeName(to)}){operand}", Cast);
    }

    private (string Text, int Binding) Intrinsic(IrIntrinsic intrinsic) {
      if (intrinsic.Op == IrIntrinsicOp.ArrayOf)
        return ($"new {TypeName(intrinsic.Type)} {Braces(intrinsic.Arguments.Select(this.Stored).ToList())}", Unary);

      return this.Rewritten(intrinsic) is { } rewritten ? this.Expression(rewritten) : this.Call(Row(intrinsic, this._table)!, intrinsic.Arguments);
    }

    /// <summary>
    /// A row of the capability table as a call: the framework's own method where the profile has it,
    /// otherwise the replacement — a method of the older base library or the helper of the row.
    /// </summary>
    private (string Text, int Binding) Call(CSharpCapability row, IReadOnlyList<IrExpr> arguments) {
      var form = this.Form(row, arguments);
      if (form.Template is { } template) {
        // The arguments are written in their order, which is the order of the IR; {h} is the helper of the row.
        var written = arguments.Select(argument => this.Operand(argument, Primary)).ToList();
        // A template may be a cast, so it binds like one.
        return (_placeholder.Replace(template, match => match.Value == "{h}" ? this.Helper(row.Helper!) : written[match.Value[1] - '0']), Cast);
      }

      var all = arguments.Select(this.Text);
      var call = $"{form.Method ?? this.Helper(row.Helper!)}({string.Join(", ", form.Further is null ? all : all.Append(form.Further))})";
      return form.Cast is null ? (call, Primary) : ($"({form.Cast}){call}", Cast);
    }

  }

}
