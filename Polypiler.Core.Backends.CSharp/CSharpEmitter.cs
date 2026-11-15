using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Passes;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Legalize;
using Polypiler.Core.Optimize.Target;

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
public static partial class CSharpEmitter {

  /// <summary>The code for an IR construct this backend cannot write.</summary>
  public const string Unsupported = "PLY1052";

  /// <summary>The registered diagnostic of <see cref="Unsupported"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor UnsupportedDescriptor = new(Unsupported, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "csharp.backend.ir");

  /// <summary>
  /// Every helper a class can carry with the default capability table, by its symbol
  /// (<see cref="CSharpHelper.Symbol"/>), with its licence and the source it is copied from. A
  /// portable floating-point implementation a class carries is looked up in <see cref="FloatOperationSources.Origin"/>.
  /// </summary>
  public static IReadOnlyDictionary<string, (string Licence, string Origin)> HelperOrigins { get; }
    = CSharpCapabilities.Default.Rows.Select(row => row.Helper).OfType<CSharpHelper>().Distinct()
      .ToDictionary(helper => helper.Symbol(helper.Name), helper => (helper.Licence, helper.Origin), StringComparer.Ordinal);

  /// <summary>The base library by its full name, which no name of the module can hide.</summary>
  private const string Base = "global::System.";

  /// <summary>The code for an IR construct that has no form in the chosen profile.</summary>
  public const string Inexpressible = "PLY1053";

  /// <summary>The registered diagnostic of <see cref="Inexpressible"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InexpressibleDescriptor = new(Inexpressible, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "csharp.backend.profile");

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
    [IrTypeKind.F64] = "double",
    [IrTypeKind.String] = "string"
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

  /// <summary>The keyword of a scalar IR type in C#, such as <c>uint</c> for <c>u32</c>.</summary>
  internal static string Keyword(IrType type) => _types[type.Kind];

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

    foreach (var global in module.Globals) {
      Known(global.Type, unknown);
      if (global.Initializer is not null)
        Known(global.Initializer, table, unknown);
    }

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
        problems.Add(InexpressibleDescriptor.At($"'{row.Name}' cannot be expressed in C# profile '{profile.Name}'"));
    }

    var initializers = module.Globals.Select(global => global.Initializer).OfType<IrExpr>().SelectMany(Expressions);
    foreach (var expression in module.Functions.SelectMany(function => Expressions(function.Body)).Concat(initializers).Where(IsTabled))
      Ask(expression);

    return problems.Distinct().ToList();
  }

  private static Diagnostic Unknown(string what) => UnsupportedDescriptor.At($"'{what}' is not supported by the C# backend yet");

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
  /// same bits on every target. The tree is printed as the emitter built it, without the target
  /// passes; the overload that takes <see cref="TargetPassOptions"/> runs them, as <c>translate</c> does.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check(IrModule, CSharpProfile, CSharpCapabilities?)"/> accepts.</param>
  /// <param name="profile">The target framework and language version.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="namespace">The namespace of the class, or <see langword="null"/> for none.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted, or the namespace is not a name.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace = null, CSharpCapabilities? capabilities = null)
    => Emit(module, profile, mode, @namespace, capabilities, TargetPassOptions.None).Files;

  /// <summary>
  /// Emits the module for a profile and a floating-point mode: builds its syntax tree, runs the target
  /// passes the options let run (<see cref="CSharpTargetPasses.Pipeline"/>) and prints the tree.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check(IrModule, CSharpProfile, CSharpCapabilities?)"/> accepts.</param>
  /// <param name="profile">The target framework and language version.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="namespace">The namespace of the class, or <see langword="null"/> for none.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The generated file and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted, or the namespace is not a name.</exception>
  public static TargetEmission Emit(IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace, CSharpCapabilities? capabilities, TargetPassOptions passes) {
    var ((unit, applications), helpers) = Built(module, profile, mode, @namespace, capabilities, passes);
    var type = CSharpTargetPasses.ModuleClass(unit)!;
    var name = type.Name.TrimStart('@');
    return new([new GeneratedFile(name + ".cs", CSharpPrinter.Print(unit, profile, capabilities)) { Helpers = Measured(type, helpers, profile) }], applications);
  }

  /// <summary>
  /// The helpers of a class, measured in its members, each printed on its own without the lines in
  /// front of it: the same text whatever namespace indents the class. A helper the target passes
  /// inlined everywhere and removed is still listed, as code of it is in the file, with no text of its own.
  /// </summary>
  private static IReadOnlyList<EmittedHelper> Measured(CsTypeDeclaration type, IReadOnlyList<HelperText> helpers, CSharpProfile profile) {
    var members = (type.Members ?? []).Select(member => (Name: member is CsMethod method ? method.Name.TrimStart('@') : null, Text: CSharpPrinter.Print(member with { Leading = CsTrivia.None }, profile))).ToList();
    var texts = members.Where(member => member.Name is not null).GroupBy(member => member.Name!).ToDictionary(group => group.Key, group => group.Select(member => member.Text).ToList());
    // Overloads share their name: each takes the text of the method its symbol names.
    string Text(HelperText helper) => texts.GetValueOrDefault(helper.Name)?.FirstOrDefault(text => text.Contains(helper.Symbol[helper.Name.Length..], StringComparison.Ordinal) || !helper.Symbol.Contains('(')) ?? "";
    return EmittedHelper.Measure([.. helpers.Select(helper => helper with { Text = Text(helper) })], string.Concat(members.Select(member => member.Text)));
  }

  /// <summary>
  /// The syntax tree of the module for a profile and a floating-point mode, after the target passes
  /// the options let run: what <see cref="Emit(IrModule, CSharpProfile, FloatMode, string?, CSharpCapabilities?, TargetPassOptions)"/> prints.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check(IrModule, CSharpProfile, CSharpCapabilities?)"/> accepts.</param>
  /// <param name="profile">The target framework and language version.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="namespace">The namespace of the class, or <see langword="null"/> for none.</param>
  /// <param name="capabilities">The capability table, or <see langword="null"/> for <see cref="CSharpCapabilities.Default"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The tree and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module cannot be emitted, or the namespace is not a name.</exception>
  public static TargetPipelineResult<CsCompilationUnit> Build(IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace, CSharpCapabilities? capabilities, TargetPassOptions passes)
    => Built(module, profile, mode, @namespace, capabilities, passes).Result;

  /// <summary>What <see cref="Build"/> gives, with the helpers the class carried before the target passes, their texts still empty.</summary>
  private static (TargetPipelineResult<CsCompilationUnit> Result, IReadOnlyList<HelperText> Helpers) Built(IrModule module, CSharpProfile profile, FloatMode mode, string? @namespace, CSharpCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(passes);
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
    var unit = writer.Module(@namespace);
    return (CSharpTargetPasses.Pipeline.Run(unit, profile, passes), writer.Emitted);
  }

  private static void Known(IrType type, List<string> unknown) {
    var scalar = type;
    while (scalar.Element is not null)
      scalar = scalar.Element;

    if (!_types.ContainsKey(scalar.Kind) && !scalar.IsStruct)
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
    => expression is IrIntrinsic intrinsic ? table.Of(intrinsic) : table.OfSaturation(expression.Type.Kind);

  /// <summary>The expressions and statements a statement consists of, or <see langword="null"/> for a kind this backend does not know.</summary>
  private static (IReadOnlyList<IrExpr> Expressions, IReadOnlyList<IrStmt> Statements)? Parts(IrStmt statement) => statement switch {
    IrBlock block => ([], block.Statements),
    IrDeclare declare => (declare.Initializer is null ? [] : [declare.Initializer], []),
    IrAssign assign => ([assign.Value], []),
    IrGlobalAssign assign => ([assign.Value], []),
    IrStore store => ([store.Array, store.Index, store.Value], []),
    IrFieldStore store => ([store.Target, store.Value], []),
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
    IrConst or IrLocalRef or IrConstRef or IrGlobalRef or IrStrConst => [],
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
    IrStructNew creation => creation.Arguments,
    IrStructDefault => [],
    IrFieldLoad load => [load.Target],
    IrStructEquals equality => [equality.Left, equality.Right],
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

  private sealed partial class Writer {

    /// <summary>How tightly an expression binds: a primary expression, a cast, another unary one, or anything looser.</summary>
    private const int Primary = 0, Cast = 1, Unary = 2, Loose = 3;

    private readonly IrModule _module;
    private readonly CSharpProfile _profile;
    private readonly CSharpCapabilities _table;
    private readonly FloatMode _mode;
    private readonly string _class;
    private readonly HashSet<CSharpHelper> _used = [];
    private readonly Dictionary<string, string> _helpers;
    private readonly HashSet<string> _taken;
    private readonly Dictionary<string, string> _members;

    /// <summary>The C# spelling of every name of a parameter or local of the function being written, before any renaming.</summary>
    private Dictionary<string, string> _spellings = [];

    /// <summary>Every name, without its <c>@</c>, that a variable of the function being written may not take when it is renamed.</summary>
    private HashSet<string> _names = [];

    /// <summary>The blocks around the statement being written, the innermost last; the first holds the parameters.</summary>
    private readonly List<Scope> _scopes = [];

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
      var members = module.Constants.Select(constant => constant.Name).Concat(module.Globals.Select(global => global.Name)).Concat(module.Functions.Select(function => function.Name));
      this._members = Names(members, [this.ClassName]);
      // A struct is named beside the class, where a member of the class of its name would hide it.
      this._typeNames = Names(module.Types.Select(type => type.Name), [this.ClassName, .. this._members.Values.Select(member => member.TrimStart('@'))]);
      this._equatable = Equatable(module);
      var taken = this._members.Values.Select(member => member.TrimStart('@')).Append(this.ClassName).ToList();
      this._helpers = Names(helpers.Select(helper => helper.Name), taken);
      this._taken = [.. taken, .. this._helpers.Values];
    }

    /// <summary>The helpers the class has called so far.</summary>
    public IReadOnlyCollection<CSharpHelper> Helpers => this._used;

    /// <summary>The helpers and portable implementations <see cref="Module"/> put into the class, their texts still empty; none before it ran.</summary>
    public IReadOnlyList<HelperText> Emitted { get; private set; } = [];

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
      // A string operation casts between char and ushort, which loses nothing, and computes nothing that overflows.
      IrIntrinsic intrinsic when StringOperations.IsString(intrinsic.Op) => false,
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
    /// — one that holds the rest of the file, or a block around the class — and the class, whose
    /// helpers follow its functions in the order of the table, each as its source has it.
    /// </summary>
    public CsCompilationUnit Module(string? @namespace) {
      var members = new List<CsMember>();
      foreach (var constant in this._module.Constants)
        members.Add(this.Constant(constant));

      foreach (var global in this._module.Globals)
        members.Add(this.Global(global));

      foreach (var function in this._module.Functions)
        members.Add(this.Function(function));

      // A portable implementation the legalization copied in is a helper: code of Polypiler's own the class carries.
      var emitted = this._module.Implementations.Select(pair => FloatOperationSources.Helper(this._members[pair.Function].TrimStart('@'), pair.Implementation, "")).ToList();

      // The table's order is the order of the helpers, whatever order the class calls them in.
      foreach (var helper in this._table.Rows.Select(row => row.Helper).OfType<CSharpHelper>().Distinct().Where(this._used.Contains)) {
        var called = this._helpers.GetValueOrDefault(helper.Name, helper.Name);
        members.Add(HelperDeclaration(helper) with { Name = called, Leading = CsTrivia.Blank, IsHelper = true });
        emitted.Add(new(helper.Symbol(called), called, helper.Licence, helper.Origin, ""));
      }

      this.Emitted = emitted;

      var types = this.Structs();
      types.Add(new CsTypeDeclaration(["public", "static"], "class", this._class, null, [], [.. members.Select(member => member with { Leading = CsTrivia.Blank })]));
      List<string> header = [
        $"{this.ClassName}: generated by polypiler. Change the source, not this file.",
        $"Profile: {this._profile}.",
        .. EmittedHelper.HeaderLine(emitted.Select(helper => helper.Licence)) is { } licences ? [licences] : (string[])[],
        .. FloatManifest.Lines(this._module, this._mode, NativeAccuracy.DotNet)
      ];
      if (@namespace is null)
        return new CsCompilationUnit(header, types);

      var name = string.Join('.', @namespace.Split('.').Select(part => Names([part], [])[part]));
      return new CsCompilationUnit(header, [new CsNamespace(name, this.Has(CSharpFeature.FileScopedNamespace), types)]);
    }

    /// <summary>
    /// A number of the module as a <c>const</c>, a table as a field that is assigned once: nothing
    /// the IR can say stores into a table, so the generated class never does either.
    /// </summary>
    private CsField Constant(IrModuleConst constant) {
      var name = this._members[constant.Name];
      if (constant.Type == IrType.String)
        return new CsField(["private", "const"], new CsType("string"), name, new CsStringLiteral(constant.Strings[0]) { Type = IrType.String });

      if (constant.Type.Element == IrType.String)
        return new CsField(["private", "static", "readonly"], new CsType("string[]"), name,
          new CsArrayInitializer([.. constant.Strings.Select(text => new CsStringLiteral(text) { Type = IrType.String })]) { Tabulated = true });

      if (constant.Type.Element is not { } element)
        return new CsField(["private", "const"], new CsType(this.TypeName(constant.Type)), name, Bare(constant.Type, constant.Values[0]));

      var elements = constant.Values.Select(value => Bare(element, value)).ToList();
      return new CsField(["private", "static", "readonly"], new CsType(this.TypeName(constant.Type)), name, new CsArrayInitializer(elements) { Tabulated = true });
    }

    private CsMethod Function(IrFunction function) {
      // A variable spelled like a member or a helper would hide it, so their names are taken.
      this._spellings = Names(function.Parameters.Concat(function.Locals).Select(variable => variable.Name), this._taken);
      this._names = [.. this._taken.Concat(this._spellings.Values).Select(name => name.TrimStart('@'))];
      this._complements = Complements(function);
      this._scopes.Clear();
      this._scopes.Add(new Scope());
      var parameters = function.Parameters.Select(parameter => new CsParameter(new CsType(this.TypeName(parameter.Type)), this.Declare(parameter))).ToList();
      IReadOnlyList<string> modifiers = [function.IsPublic ? "public" : "private", "static"];
      var returns = new CsType(this.TypeName(function.ReturnType));
      var name = this._members[function.Name];
      var wraps = Expressions(function.Body).Any(this.Wraps);
      if (function.Body.Statements is [IrReturn { Value: { } result }] && this.Has(CSharpFeature.ExpressionBody)) {
        // A result that is itself a checked expression sets the context of everything in it.
        var value = this.Stored(result);
        return new CsMethod(modifiers, returns, name, [], parameters, null, wraps && !Traps(Promoted(result)) ? new CsCheckedExpression(false, value) { Type = value.Type } : value);
      }

      var body = this.Statements(function.Body);
      return new CsMethod(modifiers, returns, name, [], parameters, new CsBlock(wraps ? [new CsCheckedStatement(false, new CsBlock(body))] : body), null);
    }

    private string TypeName(IrType type)
      => type.Declaration is { } declared ? this._typeNames[declared.Name] : type.IsView ? this.ViewTypeName(type)
        : type.Element is null ? _types[type.Kind] : this.TypeName(type.Element) + "[]";

    /// <summary>The statements of a block, which is a block to C# as well: a declaration space of its own.</summary>
    private List<CsStatement> Statements(IrBlock block) {
      var statements = new List<CsStatement>();
      this.Statements(block, statements);
      return statements;
    }

    /// <summary>The statements of a block, added to a list of statements of C#.</summary>
    private void Statements(IrBlock block, List<CsStatement> statements) {
      var scope = new Scope();
      this._scopes.Add(scope);
      foreach (var statement in block.Statements)
        this.Statement(statement, statements);

      this._scopes.RemoveAt(this._scopes.Count - 1);
      this._scopes[^1].Within.UnionWith(scope.Within);
    }

    /// <summary>
    /// Gives a parameter or local declared in the innermost block its C# name. C# forbids a name that
    /// a variable of an enclosing block has, or one of a block inside this one, wherever in the block
    /// either declaration stands (ECMA-334, "Local variable declaration spaces"); the IR allows both.
    /// The later of two such declarations is the one written here, and it becomes
    /// <c>&lt;name&gt;_&lt;k&gt;</c> with the smallest <c>k</c> that no other name of the function or
    /// the class has. Every other variable keeps its spelling, with an <c>@</c> when it is a keyword.
    /// </summary>
    private string Declare(IrLocal local) {
      var scope = this._scopes[^1];
      var name = this._spellings[local.Name].TrimStart('@');
      if (scope.Within.Contains(name) || this._scopes.Any(outer => outer.Own.Contains(name))) {
        var spelling = name;
        var index = 0;
        do
          name = $"{spelling}_{++index}";
        while (!this._names.Add(name));
      }

      scope.Own.Add(name);
      scope.Within.Add(name);
      return scope.Declared[local] = _keywords.Contains(name) ? "@" + name : name;
    }

    /// <summary>The C# name of a variable: that of its declaration in the innermost block around the statement being written that has one.</summary>
    private string Local(IrLocal local) => this._scopes.Last(scope => scope.Declared.ContainsKey(local)).Declared[local];

    /// <summary>A variable as an expression of its type.</summary>
    private CsName Variable(IrLocal local) => new(this.Local(local)) { Type = local.Type };

    private void Statement(IrStmt statement, List<CsStatement> statements) {
      switch (statement) {
        case IrBlock block:
          statements.Add(new CsBlock(this.Statements(block)));
          break;
        case IrDeclare declare:
          // The initializer is read before the variable is declared, as the IR reads it.
          var initializer = declare.Initializer is null ? null : this.Stored(declare.Initializer);
          var type = this.Infers(declare) ? CsType.Var : new CsType(this.TypeName(declare.Local.Type));
          statements.Add(new CsLocalDeclaration(type, this.Declare(declare.Local), initializer));
          break;
        case IrAssign or IrGlobalAssign or IrStore or IrFieldStore or IrExprStmt:
          statements.Add(new CsExpressionStatement(this.Effect(statement)));
          break;
        case IrIf conditional:
          statements.Add(this.If(conditional));
          break;
        case IrWhile loop:
          var condition = this.Text(loop.Condition);
          statements.Add(new CsWhile(condition, new CsBlock(this.Body(loop.Body, null))));
          break;
        case IrDoWhile loop:
          var body = this.Body(loop.Body, null);
          statements.Add(new CsDo(new CsBlock(body), this.Text(loop.Condition)));
          break;
        case IrFor loop:
          statements.Add(this.For(loop));
          break;
        case IrSwitch choice:
          statements.Add(this.Switch(choice));
          break;
        case IrBreak:
          statements.Add(new CsBreak());
          break;
        case IrContinue:
          if (this._steps.Peek() is { } step)
            this.Step(step, statements);

          statements.Add(new CsContinue());
          break;
        default:
          var result = (IrReturn)statement;
          statements.Add(new CsReturn(result.Value is null ? null : this.Stored(result.Value)));
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
      => declare.Initializer is { } initializer and not IrConst and not IrStructDefault && Promoted(initializer) == initializer && !declare.Local.Type.IsView
        && this.ClassName != "var" && this.Has(CSharpFeature.ImplicitlyTypedLocal);

    /// <summary>
    /// An expression evaluated for its effects only: assigned to the discard where the language has
    /// one, handed to a helper that does nothing with it where it has not.
    /// </summary>
    private CsExpression Discarded(IrExpr expression) {
      var value = this.Text(expression);
      return this.Has(CSharpFeature.Discard)
        ? new CsAssignment(new CsName("_"), "=", value)
        : new CsInvocation(new CsName(this.Helper(this._table.Of(CSharpFeature.Discard)!.Helper!)), [value]);
    }

    /// <summary>
    /// A statement that is one expression to C#, without its semicolon: an assignment, a call or a
    /// step as it stands, any other expression discarded so that it is evaluated.
    /// </summary>
    private CsExpression Effect(IrStmt statement) {
      switch (statement) {
        case IrAssign assign:
          var variable = this.Variable(assign.Local);
          return new CsAssignment(variable, "=", this.Stored(assign.Value));
        case IrGlobalAssign assign:
          var global = this.GlobalName(assign.Global);
          return new CsAssignment(global, "=", this.Stored(assign.Value));
        case IrStore store when this.Segmented(store.Array):
          return this.SegmentStore(store);
        case IrStore store:
          var element = this.Element(store.Array, store.Index);
          return new CsAssignment(element, "=", this.Stored(store.Value));
        case IrFieldStore store:
          var field = new CsMemberAccess(this.Operand(store.Target, Primary), this.FieldName(store.Field, store.Target.Type)) { Type = store.Field.Type };
          return new CsAssignment(field, "=", this.Stored(store.Value));
        default:
          var expression = ((IrExprStmt)statement).Expression;
          return expression.Type == IrType.Void || expression is IrCall or IrStep { Trap: false } ? this.Text(expression) : this.Discarded(expression);
      }
    }

    /// <summary>The body of a loop, whose <c>continue</c> statements run the given step first.</summary>
    private List<CsStatement> Body(IrBlock body, IrBlock? step) {
      this._steps.Push(step);
      var statements = this.Statements(body);
      this._steps.Pop();
      return statements;
    }

    /// <summary>
    /// A loop with a step. One statement that C# takes as an expression is the third part of a
    /// <c>for</c>. Any other step is written out wherever a pass ends: at the end of the body when
    /// execution gets there, and in front of every <c>continue</c> of the loop.
    /// </summary>
    private CsStatement For(IrFor loop) {
      var condition = this.Text(loop.Condition);
      if (loop.Step.Statements is [IrAssign or IrGlobalAssign or IrStore or IrFieldStore or IrExprStmt]) {
        var step = this.Effect(loop.Step.Statements[0]);
        return new CsFor(null, condition, [step], new CsBlock(this.Body(loop.Body, null)));
      }

      var body = this.Body(loop.Body, loop.Step);
      if (Completes(loop.Body))
        this.Step(loop.Step, body);

      return new CsWhile(condition, new CsBlock(body));
    }

    /// <summary>The statements of a step, in a block of their own when they declare something, because a step can stand more than once in a block.</summary>
    private void Step(IrBlock step, List<CsStatement> statements) {
      if (step.Statements.Any(statement => statement is IrDeclare))
        this.Statement(step, statements);
      else
        this.Statements(step, statements);
    }

    /// <summary>
    /// A switch whose sections are blocks, as the sections of the IR are scopes, each ending in the
    /// <c>break</c> C# demands where execution would otherwise leave the section at its end.
    /// </summary>
    private CsStatement Switch(IrSwitch choice) {
      // C# warns about a switch without sections; its value is evaluated all the same.
      if (choice.Sections.Count == 0)
        return new CsExpressionStatement(this.Discarded(choice.Value));

      var type = choice.Value.Type;
      var value = this.Text(choice.Value);
      var sections = new List<CsSwitchSection>();
      foreach (var section in choice.Sections) {
        var labels = section.Labels.Select(label => (CsExpression?)Bare(type, label)).ToList();
        if (section.IsDefault)
          labels.Add(null);

        var body = this.Statements(section.Body);
        if (Completes(section.Body))
          body.Add(new CsBreak());

        sections.Add(new CsSwitchSection(labels, [new CsBlock(body)]));
      }

      return new CsSwitch(value, sections);
    }

    private CsIf If(IrIf conditional) {
      var condition = this.Text(conditional.Condition);
      var then = new CsBlock(this.Statements(conditional.Then));
      if (conditional.Else is null)
        return new CsIf(condition, then, null);

      return new CsIf(condition, then, conditional.Else.Statements is [IrIf chained] ? this.If(chained) : new CsBlock(this.Statements(conditional.Else)));
    }

    /// <summary>The expression as it stands on its own: a condition, an argument of the base library or the operand of nothing.</summary>
    private CsExpression Text(IrExpr expression) => this.Expression(expression).Node;

    /// <summary>
    /// The expression where its type is already given — an initializer, a right-hand side, an
    /// argument, a returned value. A constant needs neither cast nor suffix there, because C#
    /// converts a literal to the integer type that holds its value, and a narrow integer becomes an
    /// <c>int</c> unasked.
    /// </summary>
    private CsExpression Stored(IrExpr expression) => expression switch {
      IrConst constant => Bare(constant.Type, constant.Value),
      // The literal default takes the type of the place it goes to.
      IrStructDefault when this.Has(CSharpFeature.DefaultLiteral) => new CsDefaultLiteral { Type = expression.Type },
      _ => this.Text(Promoted(expression))
    };

    /// <summary>The expression as an operand: in parentheses when it binds more loosely than <paramref name="level"/>.</summary>
    private CsExpression Operand(IrExpr expression, int level) {
      var (node, binding) = this.Expression(expression);
      return binding > level ? new CsParenthesized(node) { Type = node.Type } : node;
    }

    /// <summary>
    /// An operand the C# compiler must leave to run time when it could compute it and would then
    /// refuse or fold what the IR defines otherwise: the divisor 0, the divisor -1, a negative index
    /// or length, an operand of checked arithmetic, a number cast to an integer type that does not
    /// hold it. <c>Math.Max</c> of the value and itself is the value, and no constant to C#.
    /// </summary>
    private CsExpression AtRunTime(IrExpr expression, bool harmless, int level) {
      if (expression is IrConst ? harmless : !IsConstant(expression))
        return this.Operand(expression, level);

      var value = this.Text(expression);
      return new CsInvocation(CSharpFragments.Expression(Base + "Math.Max"), [value, value]) { Type = value.Type };
    }

    private CsElementAccess Element(IrExpr array, IrExpr index)
      => new(this.Operand(array, Primary), this.AtRunTime(Promoted(index), index is IrConst { Value: >= 0 }, Loose)) { Type = array.Type.Element };

    /// <summary>The digits of an integer; those of a <c>u64</c> are the ones of its 64 bits read as unsigned.</summary>
    private static System.Numerics.BigInteger Number(IrType type, long value) => (System.Numerics.BigInteger)IrOperators.Number(type, value);

    /// <summary>A constant where C# knows its type: an integer as its digits, anything else as its literal.</summary>
    private static CsExpression Bare(IrType type, long value) => type.IsInteger ? new CsIntegerLiteral(Number(type, value)) { Type = type } : Literal(type, value).Node;

    /// <summary>
    /// A constant as an expression of exactly its type: a suffix where C# has one, a cast for the
    /// integers narrower than <c>int</c>, and the shortest digits that read back as the same
    /// floating-point number.
    /// </summary>
    private static (CsExpression Node, int Binding) Literal(IrType type, long value) {
      if (type == IrType.Bool)
        return (new CsBooleanLiteral(value != 0) { Type = type }, Primary);

      if (type.IsFloat) {
        var real = Real(type, IrOperators.Float(type, value));
        return (real, CSharpPrinter.Precedence(real) < 15 ? Unary : Primary);
      }

      var number = Number(type, value);
      var binding = number.Sign < 0 ? Unary : Primary;
      return type.Kind switch {
        IrTypeKind.I32 => (new CsIntegerLiteral(number) { Type = type }, binding),
        IrTypeKind.U32 => (new CsIntegerLiteral(number, CsIntegerSuffix.U) { Type = type }, Primary),
        IrTypeKind.I64 => (new CsIntegerLiteral(number, CsIntegerSuffix.L) { Type = type }, binding),
        IrTypeKind.U64 => (new CsIntegerLiteral(number, CsIntegerSuffix.UL) { Type = type }, Primary),
        _ => (new CsCast(new CsType(_types[type.Kind]), binding == Unary ? new CsParenthesized(new CsIntegerLiteral(number)) : new CsIntegerLiteral(number)) { Type = type }, Cast)
      };
    }

    private static CsExpression Real(IrType type, double value) {
      var name = _types[type.Kind];
      if (double.IsNaN(value))
        return new CsMemberAccess(new CsName(name), "NaN") { Type = type };

      if (double.IsInfinity(value))
        return new CsMemberAccess(new CsName(name), value < 0 ? "NegativeInfinity" : "PositiveInfinity") { Type = type };

      return new CsRealLiteral(value, type == IrType.F32) { Type = type };
    }

    /// <summary>
    /// The expression, in a <c>checked(...)</c> where the IR traps on overflow and in an
    /// <c>unchecked(...)</c> where it wraps around inside one of those. Outside of both, the body of
    /// the method is unchecked.
    /// </summary>
    private (CsExpression Node, int Binding) Expression(IrExpr expression) {
      var traps = Traps(expression);
      if (traps == this._checked || !(traps || this.Wraps(expression))) {
        var (plain, binding) = this.Plain(expression);
        return (plain.Type is null ? plain with { Type = expression.Type } : plain, binding);
      }

      this._checked = traps;
      var node = this.Plain(expression).Node;
      this._checked = !traps;
      return (new CsCheckedExpression(traps, node) { Type = expression.Type }, Primary);
    }

    private (CsExpression Node, int Binding) Plain(IrExpr expression) {
      switch (expression) {
        case IrConst constant:
          return Literal(constant.Type, constant.Value);
        case IrConstRef reference:
          return (new CsName(this._members[reference.Constant.Name]), Primary);
        case IrStrConst text:
          return (new CsStringLiteral(text.Value), Primary);
        case IrLocalRef reference:
          return (this.Variable(reference.Local), Primary);
        case IrGlobalRef reference:
          return (this.GlobalName(reference.Global), Primary);
        case IrUnary unary:
          var operand = Promoted(unary.Operand);
          return (new CsPrefixUnary(_unary[unary.Op], unary.Op == IrUnaryOp.NegateChecked ? this.AtRunTime(operand, false, Cast) : this.Operand(operand, Cast)), Unary);
        case IrBinary binary:
          return this.Binary(binary);
        case IrConvert conversion:
          return this.Convert(conversion);
        case IrConditional choice:
          var condition = this.Operand(choice.Condition, Unary);
          var whenTrue = this.Operand(choice.WhenTrue, Unary);
          return (new CsConditional(condition, whenTrue, this.Operand(choice.WhenFalse, Unary)), Loose);
        case IrArrayLoad load when this.Segmented(load.Array):
          return (this.SegmentElement(load), Primary);
        case IrArrayLoad load:
          return (this.Element(load.Array, load.Index), Primary);
        case IrArrayLength length:
          return (new CsMemberAccess(this.Operand(length.Array, Primary), "Length"), Primary);
        case IrNewArray creation:
          var count = this.AtRunTime(Promoted(creation.Length), creation.Length is IrConst { Value: >= 0 }, Loose);
          return (new CsArrayCreation(new CsType(this.TypeName(creation.Type.Element!)), count, null), Unary);
        case IrStep step:
          CsExpression target = step.Target switch {
            IrArrayLoad element => this.Segmented(element.Array) ? this.SegmentPlace(element) : this.Element(element.Array, element.Index),
            IrGlobalRef reference => this.GlobalName(reference.Global),
            _ => this.Variable(((IrLocalRef)step.Target).Local)
          };
          var sign = step.Increment ? "++" : "--";
          return step.Postfix ? (new CsPostfixUnary(target, sign), Primary) : (new CsPrefixUnary(sign, target), Unary);
        case IrIntrinsic intrinsic:
          return this.Intrinsic(intrinsic);
        case IrStructNew creation:
          return (new CsObjectCreation(new CsType(this.TypeName(creation.Type)), [.. creation.Arguments.Select(this.Stored)]), Primary);
        case IrStructDefault value:
          return this.Has(CSharpFeature.DefaultLiteral)
            ? (new CsDefaultExpression(new CsType(this.TypeName(value.Type))), Primary)
            : (new CsObjectCreation(new CsType(this.TypeName(value.Type)), []), Primary);
        case IrFieldLoad load:
          return (new CsMemberAccess(this.Operand(load.Target, Primary), this.FieldName(load.Field, load.Target.Type)), Primary);
        case IrStructEquals equality when SameVariable(equality.Left, equality.Right):
          // C# warns about == between a variable and itself (CS1718); Equals is the same comparison.
          var self = this.Operand(equality.Left, Primary);
          return (new CsInvocation(new CsMemberAccess(self, "Equals"), [this.Text(equality.Right)]), Primary);
        case IrStructEquals equality:
          var left = this.Operand(equality.Left, Unary);
          return (new CsBinary(left, "==", this.Operand(equality.Right, Unary)), Loose);
        default:
          var call = (IrCall)expression;
          return (new CsInvocation(new CsName(this._members[call.Function]), [.. call.Arguments.Select(this.Stored)]), Primary);
      }
    }

    private (CsExpression Node, int Binding) Binary(IrBinary binary) {
      // RyuJIT takes some exclusive ors of two shifts for a rotation also where they are none (CSharpRuntimeDefect.ShiftXorRotation).
      if (binary.Op == IrBinaryOp.Xor && this._table.Suffers(CSharpRuntimeDefect.ShiftXorRotation, this._profile))
        binary = ShiftXorGuard.Guarded(binary, this._complements);

      // The sign of an ordinal comparison against 0 is the comparison of the value .NET documents only by its sign.
      if (binary is { Left: IrIntrinsic { Op: IrIntrinsicOp.StrCompareOrdinal } ordinal, Right: IrConst { Value: 0 } }) {
        var compared = new CsInvocation(CSharpFragments.Expression("string.CompareOrdinal"), [this.Text(ordinal.Arguments[0]), this.Text(ordinal.Arguments[1])]) { Type = IrType.I32 };
        return (new CsBinary(compared, _binary[binary.Op], new CsIntegerLiteral(0) { Type = IrType.I32 }), Loose);
      }

      var type = binary.Left.Type;
      var symbol = _binary[binary.Op];
      var first = Promoted(binary.Left);
      var second = Promoted(binary.Right);
      var left = this.Operand(first, Unary);
      CsExpression right;
      switch (binary.Op) {
        case IrBinaryOp.Divide or IrBinaryOp.Remainder when type.IsInteger:
          right = this.AtRunTime(second, second is IrConst { Value: not 0 } divisor && !(type.IsSigned && divisor.Value == -1), Unary);
          break;
        case IrBinaryOp.ShiftRightLogical when type.IsSigned && this.Has(CSharpFeature.UnsignedRightShift):
          return (new CsBinary(left, ">>>", this.Operand(second, Unary)), Loose);
        case IrBinaryOp.ShiftRightLogical when type.IsSigned:
          var shifted = this.Operand(first, Cast);
          var unsigned = new CsCast(new CsType(type.IsLong ? "ulong" : "uint"), shifted);
          return (new CsCast(new CsType(this.TypeName(type)), new CsParenthesized(new CsBinary(unsigned, ">>", this.Operand(second, Unary)))), Cast);
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
          left = this.AtRunTime(first, !IsConstant(second), Unary);
          right = this.Operand(second, Unary);
          break;
        default:
          right = this.Operand(second, Unary);
          break;
      }

      // C# may compute a float expression more precisely than float; the cast makes it binary32 (ECMA-334, "Floating-point types").
      return binary.Type == IrType.F32
        ? (new CsCast(new CsType("float"), new CsParenthesized(new CsBinary(left, symbol, right))), Cast)
        : (new CsBinary(left, symbol, right), Loose);
    }

    /// <summary>
    /// A conversion as a cast. The context it stands in gives the cast its contract: checked it
    /// throws, unchecked it keeps the low bits of an integer and takes the nearest value for a
    /// floating-point number, a NaN becoming 0 (ADR 0002, .NET 9 and later). Where the profile has
    /// no cast that saturates — an older framework, or a target narrower than <c>int</c>, whose cast
    /// saturates to <c>int</c> and then keeps the low bits — the row's helper compares first.
    /// </summary>
    private (CsExpression Node, int Binding) Convert(IrConvert conversion) {
      var from = conversion.Operand.Type;
      var to = conversion.Type;
      if (from == to)
        return this.Expression(conversion.Operand);

      if (!this.Casts(conversion))
        return this.Call(Row(conversion, this._table)!, [conversion.Operand]);

      var foldable = !Keeps(conversion) && (from.IsFloat || conversion.Contract == IrConversion.Trap);
      var operand = foldable ? this.AtRunTime(conversion.Operand, false, Cast) : this.Operand(conversion.Operand, Cast);
      return (new CsCast(new CsType(this.TypeName(to)), operand), Cast);
    }

    private (CsExpression Node, int Binding) Intrinsic(IrIntrinsic intrinsic) {
      if (intrinsic.Op == IrIntrinsicOp.ArrayOf)
        return (new CsArrayCreation(new CsType(this.TypeName(intrinsic.Type.Element!)), null, new CsArrayInitializer([.. intrinsic.Arguments.Select(this.Stored)])), Unary);

      if (this.Rewritten(intrinsic) is { } rewritten)
        return this.Expression(rewritten);

      return this.View(intrinsic) ?? this.Call(Row(intrinsic, this._table)!, intrinsic.Arguments);
    }

    /// <summary>
    /// A row of the capability table as a call: the framework's own method where the profile has it,
    /// otherwise the replacement — a method of the older base library or the helper of the row.
    /// </summary>
    private (CsExpression Node, int Binding) Call(CSharpCapability row, IReadOnlyList<IrExpr> arguments) {
      var form = this.Form(row, arguments);
      if (form.Template is not null) {
        // The arguments are written in their order, which is the order of the IR; {h} is the helper of the row.
        var written = arguments.Select(argument => this.Operand(argument, Primary)).ToList();
        var built = form.Build(written, () => this.Helper(row.Helper!));
        // A template may be a cast, so it binds like one; a string operation's binds as tightly as the expression it is.
        return (built, row.Operation is { } op && StringOperations.IsString(op) && CSharpPrinter.Precedence(built) == 15 ? Primary : Cast);
      }

      var call = form.Build([.. arguments.Select(this.Text)], () => this.Helper(row.Helper!));
      return (call, form.Cast is null ? Primary : Cast);
    }

  }

  /// <summary>
  /// The declaration of a helper as its source has it, read into the syntax tree once and shared by
  /// every class that calls it; the class gives it its name.
  /// </summary>
  private static CsMethod HelperDeclaration(CSharpHelper helper) => (CsMethod)CSharpFragments.Member(string.Join('\n', helper.Lines(helper.Name)));


  /// <summary>A block of the method being written, as a declaration space of C#.</summary>
  private sealed class Scope {

    /// <summary>The C# name of every variable declared in the block itself.</summary>
    public Dictionary<IrLocal, string> Declared { get; } = [];

    /// <summary>The names, without an <c>@</c>, of the variables declared in the block itself.</summary>
    public HashSet<string> Own { get; } = [];

    /// <summary>The names, without an <c>@</c>, of the variables declared in the block so far or in a block inside it.</summary>
    public HashSet<string> Within { get; } = [];

  }

}
