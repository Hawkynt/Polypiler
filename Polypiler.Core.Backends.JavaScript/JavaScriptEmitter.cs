using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Passes;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// Emits one file per IR module, for the JavaScript a <see cref="JavaScriptProfile"/> names. Every
/// operation is written so that JavaScript reproduces it exactly, and every construct is chosen from
/// the <see cref="JavaScriptCapabilities"/>: what the level has is written as it is, what it lacks
/// is written as its replacement.
/// <para>
/// Integers of up to 32 bits are numbers: <c>| 0</c> and <c>Math.imul</c> for <c>i32</c>,
/// <c>&gt;&gt;&gt; 0</c> for <c>u32</c>, masks and sign-extending shifts for the 8- and 16-bit types.
/// Integers of 64 bits are <c>BigInt</c>s cut to size with <c>BigInt.asIntN</c> and
/// <c>BigInt.asUintN</c>; below ECMAScript 2020 they are objects <c>{ hi, lo }</c> of two unsigned
/// 32-bit halves, computed by helper functions that never leave the integers a number holds
/// exactly. A <c>double</c> is a number as it is; every <c>float</c> operation is rounded to binary32
/// with <c>Math.fround</c> or its replacement, which is exact because a double holds the sum,
/// difference, product and quotient of two binary32 numbers precisely enough to round once. Arrays
/// are typed arrays where the profile has them and plain arrays where it has not, and the constants
/// of the module are declared in front of its functions. The operations that can trap or need more
/// than an expression go through small helper functions, of which a module carries exactly the ones
/// it uses.
/// </para>
/// </summary>
public static partial class JavaScriptEmitter {

  /// <summary>The code for an IR construct that has neither a native form nor a replacement in the chosen profile.</summary>
  public const string Inexpressible = "PLY1152";

  /// <summary>The registered diagnostic of <see cref="Inexpressible"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor InexpressibleDescriptor = new(Inexpressible, DiagnosticSeverity.Error, ExitCode.UnsupportedConstruct, "{0}", "javascript.backend.profile");

  /// <summary>Emits the module as <c>&lt;name&gt;.mjs</c>, an ES module of ECMAScript 2022 exporting every public function.</summary>
  /// <param name="module">A module that <see cref="IrVerifier"/> accepts.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module) => Emit(module, JavaScriptProfile.Default);

  /// <summary>
  /// Emits the module for a profile: as <c>&lt;name&gt;.mjs</c>, <c>&lt;name&gt;.cjs</c> or
  /// <c>&lt;name&gt;.js</c> by its module format, holding every public function.
  /// </summary>
  /// <param name="module">A module that <see cref="Check"/> accepts for the profile.</param>
  /// <param name="profile">The JavaScript to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be expressed in the profile.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, JavaScriptProfile profile, JavaScriptCapabilities? capabilities = null)
    => Emit(module, profile, FloatMode.Accurate, capabilities);

  /// <summary>
  /// Emits the module for a profile and a floating-point mode. The header names the profile and, where
  /// the module calls a native function with an <c>ulp(n)</c> contract, each such function with the
  /// comparison bound of ADR 0005; in <see cref="FloatMode.Strict"/> it says that the results are the
  /// same bits on every target. The tree is printed as the emitter built it, without the target
  /// passes; the overload that takes <see cref="TargetPassOptions"/> runs them, as <c>translate</c> does.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The JavaScript to write.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be expressed in the profile.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities = null)
    => Emit(module, profile, mode, capabilities, TargetPassOptions.None).Files;

  /// <summary>
  /// Emits the module for a profile and a floating-point mode: builds its syntax tree, runs the target
  /// passes the options let run (<see cref="JavaScriptTargetPasses.Pipeline"/>) and prints the tree.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The JavaScript to write.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The generated file and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be expressed in the profile.</exception>
  public static TargetEmission Emit(IrModule module, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities, TargetPassOptions passes) {
    var (program, applications) = Build(module, profile, mode, capabilities, passes);
    return new([new GeneratedFile(module.Name + profile.Extension, JavaScriptPrinter.Print(program, profile))], applications);
  }

  /// <summary>
  /// The syntax tree of the module for a profile and a floating-point mode, after the target passes
  /// the options let run: what <see cref="Emit(IrModule, JavaScriptProfile, FloatMode, JavaScriptCapabilities?, TargetPassOptions)"/> prints.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The JavaScript to write.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run.</param>
  /// <returns>The tree and what the passes changed.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be expressed in the profile.</exception>
  public static TargetPipelineResult<JsProgram> Build(IrModule module, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities, TargetPassOptions passes) {
    ArgumentNullException.ThrowIfNull(passes);
    var writer = new Writer(profile, capabilities ?? JavaScriptCapabilities.Standard, mode);
    var problems = IrVerifier.Verify(module);
    var program = problems.Count > 0 ? null : writer.Module(module);
    if (problems.Concat(writer.Problems).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    return JavaScriptTargetPasses.Pipeline.Run(program!, profile, passes);
  }

  /// <summary>Says why a module cannot be emitted for a profile.</summary>
  /// <param name="module">The module.</param>
  /// <param name="profile">The JavaScript to write.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>
  /// The findings of <see cref="IrVerifier"/> for an ill-typed module; otherwise one
  /// <see cref="Inexpressible"/> per construct the profile neither has nor has a replacement for.
  /// Empty when the module can be emitted.
  /// </returns>
  public static IReadOnlyList<Diagnostic> Check(IrModule module, JavaScriptProfile profile, JavaScriptCapabilities? capabilities = null) {
    var problems = IrVerifier.Verify(module);
    if (problems.Count > 0)
      return problems;

    var writer = new Writer(profile, capabilities ?? JavaScriptCapabilities.Standard);
    writer.Module(module);
    return writer.Problems;
  }

  /// <summary>
  /// The source of every helper as a profile has it, in the order a module lists them: what a module
  /// that calls the helper declares, read into the syntax tree and printed back.
  /// </summary>
  /// <param name="profile">The profile.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>Each helper's name and source.</returns>
  public static IReadOnlyList<(string Name, string Source)> HelperSources(JavaScriptProfile profile, JavaScriptCapabilities? capabilities = null) {
    var table = capabilities ?? JavaScriptCapabilities.Standard;
    return [.. JavaScriptHelpers.Sources(name => table.Has(name, profile))];
  }

  /// <summary>The typed array that stores the elements of an array type, which is also the name of its capability.</summary>
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

  /// <summary>A declaration of a helper, marked as one so that the target passes may inline or remove it.</summary>
  private static JsStatement AsHelper(JsStatement statement) => statement switch {
    JsFunctionDeclaration function => function with { IsHelper = true },
    JsVariableDeclaration variable => variable with { IsHelper = true },
    _ => statement
  };

  /// <summary>A 64-bit integer as the object of its two unsigned halves.</summary>
  private static JsObject Pair(ulong bits) => Halves(Number((bits >> 32).ToString(CultureInfo.InvariantCulture)), Number((bits & 0xFFFFFFFF).ToString(CultureInfo.InvariantCulture)));

  /// <summary>The object of two halves.</summary>
  private static JsObject Halves(JsExpression high, JsExpression low) => new([new JsProperty("hi", high), new JsProperty("lo", low)]);

  /// <summary>A number as the emitter spells it, which the printer keeps.</summary>
  private static JsNumber Number(string spelled) => new(JavaScriptLiterals.ParseNumber(spelled)!.Value) { Raw = spelled };

  /// <summary>An integer as its digits.</summary>
  private static JsNumber Number(Int128 value) => Number(value.ToString(CultureInfo.InvariantCulture));

  private static JsIdentifier Id(string name) => new(name);

  /// <summary>A name with the members read from it, such as <c>Math.imul</c> or <c>BigInt.asIntN</c>.</summary>
  private static JsExpression Dotted(string path) {
    var parts = path.Split('.');
    JsExpression result = Id(parts[0]);
    foreach (var part in parts.Skip(1))
      result = new JsMember(result, part);

    return result;
  }

  /// <summary>A call of a function of the library or of a name, such as <c>Math.fround(x)</c>.</summary>
  private static JsCall Invoke(string callee, params IEnumerable<JsExpression> arguments) => new(Dotted(callee), [.. arguments]);

  private static JsBinary Infix(JsExpression left, string op, JsExpression right) => new(left, op, right);

  private static JsParenthesized Parenthesized(JsExpression inner) => new(inner);

  /// <summary>An expression as an operand: in parentheses unless it is one already.</summary>
  private static JsExpression Operand((JsExpression Node, bool Atomic) written) => written.Atomic ? written.Node : Parenthesized(written.Node);

  /// <summary>
  /// A floating-point number as a literal that reads back as exactly that number: the shortest digits
  /// that identify the double. A binary32 number is widened first, which changes nothing about it.
  /// </summary>
  private static JsNumber Literal(double value) => new(value);

  private sealed partial class Writer {

    /// <summary>The variable a postfix step of a local keeps the old value in; a function that needs it declares it.</summary>
    private const string Previous = "$p";

    private static JsObject Zero64 => Halves(Number("0"), Number("0"));

    private static JsObject One64 => Halves(Number("0"), Number("1"));

    private readonly JavaScriptProfile _profile;
    private readonly JavaScriptCapabilities _capabilities;
    private readonly FloatMode _mode;
    private readonly HashSet<string> _reserved;
    private readonly HashSet<string> _used = [];
    private readonly List<string> _exported = [];
    private readonly HashSet<string> _refused = [];
    private readonly List<Diagnostic> _problems = [];
    private readonly Dictionary<string, IrLocal> _taken = [];
    private readonly Dictionary<IrLocal, string> _renamed = [];
    private object _construct = "the module";
    private bool _previous;
    private bool _halves;

    public Writer(JavaScriptProfile profile, JavaScriptCapabilities capabilities, FloatMode mode = FloatMode.Accurate) {
      this._profile = profile;
      this._mode = mode;
      this._capabilities = capabilities;
      this._reserved = [.. JavaScriptWords.Reserved, .. capabilities.Globals()];
      if (profile.Module == JavaScriptModuleFormat.Cjs)
        this._reserved.Add("exports");
    }

    /// <summary>What the profile cannot express of the module written; empty when it can express all of it.</summary>
    public IReadOnlyList<Diagnostic> Problems => this._problems;

    /// <summary>Whether a 64-bit integer is a pair of halves, because the profile has no <c>BigInt</c>.</summary>
    private bool Halves => !this.Native("BigInt");

    /// <summary>The keyword that declares a variable.</summary>
    private string Let => this.Native("let") ? "let" : "var";

    /// <summary>
    /// Whether the profile has a capability. One it lacks and has no replacement for is reported
    /// against the construct being written.
    /// </summary>
    private bool Native(string name) {
      if (this._capabilities.Has(name, this._profile))
        return true;

      if (this._capabilities[name].Replacement is not null)
        return false;

      var what = this._construct switch {
        IrBinary binary => binary.Op.ToString(),
        IrUnary unary => unary.Op.ToString(),
        IrIntrinsic intrinsic => intrinsic.Op.ToString(),
        string text => text,
        _ => this._construct.GetType().Name[2..]
      };
      if (this._refused.Add(what))
        this._problems.Add(InexpressibleDescriptor.At($"'{what}' cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));

      return false;
    }

    /// <summary>
    /// The file: the header, then the helpers the module uses, the helpers of its structs, its
    /// constants and its functions, packaged as the module format says - an ES module that exports
    /// its public functions, a CommonJS module that assigns them to <c>exports</c>, or a script whose
    /// function expression returns them to a global.
    /// </summary>
    public JsProgram Module(IrModule module) {
      this.NameGlobals(module);
      var functions = new List<JsStatement>();
      foreach (var function in module.Functions)
        functions.Add(this.Function(function));

      // The initializers of the globals call helpers as well, which have to be known before they are declared.
      var globals = this.Globals(module);
      this._construct = "the module";
      var structs = this.StructHelpers();
      var body = new List<JsStatement>();
      foreach (var (_, statements) in JavaScriptHelpers.For(this._used, this.Native))
        body.AddRange(statements.Select((statement, index) => AsHelper(index == 0 ? statement with { Leading = JsTrivia.Blank } : statement)));

      body.AddRange(structs);
      foreach (var constant in module.Constants) {
        var keyword = this.Native("const") ? "const" : "var";
        body.Add(new JsVariableDeclaration(keyword, [new JsDeclarator(this.Name(constant.Name), this.Value(constant))]) {
          Leading = constant == module.Constants[0] ? JsTrivia.Blank : JsTrivia.None
        });
      }

      body.AddRange(globals);
      body.AddRange(functions);
      var header = new List<string> {
        $"{module.Name}: generated by polypiler. Change the source, not this file.",
        $"JavaScript profile: {this._profile}."
      };
      header.AddRange(FloatManifest.Lines(module, this._mode, NativeAccuracy.Node));
      if (this._halves)
        header.Add("A 64-bit integer is an object { hi, lo }: its upper and its lower 32 bits, each as an unsigned number.");

      if (this._structs)
        header.Add("A struct is an object of its fields, copied wherever the place it is read from can still be used.");

      var strict = new JsExpressionStatement(new JsString("use strict"));
      switch (this._profile.Module) {
        case JavaScriptModuleFormat.Esm:
          this.Native("export");
          return new JsProgram(header, body, true);
        case JavaScriptModuleFormat.Cjs:
          List<JsStatement> exports = [.. this._exported.Select((name, index) => new JsExpressionStatement(new JsAssignment(new JsMember(Id("exports"), name), "=", Id(name))) {
            Leading = index == 0 ? JsTrivia.Blank : JsTrivia.None
          })];
          return new JsProgram(header, [strict, .. body, .. exports], false);
        default:
          // An empty body still leaves its line, which the blank line before the result follows.
          var result = new JsReturn(new JsObject([.. this._exported.Select(name => new JsProperty(name, Id(name)))]) { Multiline = true }) {
            Leading = new JsTrivia(body.Count == 0 ? 2 : 1, [])
          };
          var function = new JsFunctionExpression(null, [], new JsBlock([strict, .. body, result]));
          return new JsProgram(header, [new JsVariableDeclaration("var", [new JsDeclarator(this._profile.Global!, new JsCall(Parenthesized(function), []))])], false);
      }
    }

    /// <summary>
    /// The value of a constant of the module. A table is an array built once from its elements,
    /// eight to a line when it has more than that.
    /// </summary>
    private JsExpression Value(IrModuleConst constant) {
      if (constant.Type.Element is not { } element)
        return this.Constant(constant.Type, constant.Values[0]).Node;

      var elements = constant.Values.Select(value => this.Constant(element, value).Node).ToList();
      return this.ArrayOf(element, elements, true);
    }

    /// <summary>An array of the given elements: the typed array of the element type, or a plain array where the profile has none.</summary>
    private JsExpression ArrayOf(IrType element, IReadOnlyList<JsExpression> elements, bool tabulated = false) {
      var array = new JsArray(elements) { Tabulated = tabulated };
      return this.Native(Storage(element)) ? new JsNew(Id(Storage(element)), [array]) : array;
    }

    /// <summary>The zero of an element type, which a new or a cleared array holds.</summary>
    private JsExpression Zero(IrType element) => !element.IsLong ? Number("0") : this.Halves ? Zero64 : new JsBigInt(0);

    /// <summary>What a value of an IR type is in JavaScript, as the header of a function states it.</summary>
    private string Describe(IrType type) {
      switch (type.Kind) {
        case IrTypeKind.Void:
          return "void";
        case IrTypeKind.Bool:
          return "boolean";
        case IrTypeKind.Array:
          return !type.Element!.IsStruct && this.Native(Storage(type.Element!)) ? Storage(type.Element!) : $"Array<{this.Describe(type.Element!)}>";
        case IrTypeKind.Struct:
          return type.Declaration!.Name;
        case IrTypeKind.I64 or IrTypeKind.U64:
          if (!this.Halves)
            return "bigint";

          this._halves = true;
          return "{ hi, lo }";
        default:
          return "number";
      }
    }

    /// <summary>An integer as a literal of its type: a number, or for 64 bits a <c>BigInt</c> or the pair of its halves.</summary>
    private JsExpression Literal(IrType type, Int128 value) {
      if (!type.IsLong)
        return Number(value);

      return this.Halves ? Pair(unchecked((ulong)value)) : new JsBigInt((System.Numerics.BigInteger)value);
    }

    /// <summary>A constant as a literal of its type; it is an operand as it stands unless it is negative.</summary>
    private (JsExpression Node, bool Atomic) Constant(IrType type, long value) {
      if (type == IrType.Bool)
        return (new JsBoolean(value != 0), true);

      if (type.IsFloat) {
        var literal = JavaScriptEmitter.Literal(IrOperators.Float(type, value));
        return (literal, !JavaScriptPrinter.IsNegative(literal.Value));
      }

      var number = IrOperators.Number(type, value);
      var node = this.Literal(type, number);
      return (node, number >= 0 || node is JsObject);
    }

    /// <summary>
    /// A function under a comment that states what crosses its boundary: integers of up to 32 bits
    /// are numbers, those of 64 bits are <c>BigInt</c>s or pairs of halves, arrays are typed or plain.
    /// </summary>
    private JsStatement Function(IrFunction function) {
      this._construct = $"the function '{function.Name}'";
      this._taken.Clear();
      this._renamed.Clear();
      foreach (var parameter in function.Parameters)
        this._taken[this.Name(parameter.Name)] = parameter;

      var name = this.Name(function.Name);
      var parameters = function.Parameters.Select(parameter => this.Name(parameter.Name)).ToList();
      var signature = string.Join(", ", function.Parameters.Select(parameter => $"{this.Name(parameter.Name)}: {this.Describe(parameter.Type)}"));
      var comment = $"{name}({signature}): {this.Describe(function.ReturnType)}";
      var exported = function.IsPublic && this._profile.Module == JavaScriptModuleFormat.Esm;
      if (function.IsPublic && !exported)
        this._exported.Add(name);

      var statements = new List<JsStatement>();
      this._liveness = Liveness.Of(function);
      foreach (var parameter in this._liveness.CopiedOnEntry)
        statements.Add(new JsExpressionStatement(new JsAssignment(Id(this.Local(parameter)), "=", this.Copy(new IrLocalRef(parameter)))));

      var start = statements.Count;
      this._previous = false;
      this.Statements(function.Body, statements);
      if (this._previous)
        statements.Insert(start, new JsVariableDeclaration(this.Let, [new JsDeclarator(Previous, null)]));

      JsStatement declaration = new JsFunctionDeclaration(name, parameters, new JsBlock(statements));
      return (exported ? new JsExport(declaration) : declaration) with { Leading = new JsTrivia(1, [comment]) };
    }

    /// <summary>The statements of a block, as a list.</summary>
    private List<JsStatement> Statements(IrBlock block) {
      var statements = new List<JsStatement>();
      this.Statements(block, statements);
      return statements;
    }

    /// <summary>
    /// The statements of a block. Where a variable is a <c>var</c>, which belongs to the whole
    /// function, the names the block declared are free again when it ends.
    /// </summary>
    private void Statements(IrBlock block, List<JsStatement> statements) {
      var outer = this._taken.Keys.ToHashSet();
      foreach (var statement in block.Statements)
        this.Statement(statement, statements);

      foreach (var name in this._taken.Keys.Where(name => !outer.Contains(name)).ToList())
        this._taken.Remove(name);
    }

    /// <summary>
    /// The name a declared local is written with. A <c>let</c> belongs to its block and keeps its
    /// name. A <c>var</c> belongs to its function, so a local that is declared while another one of
    /// its name is visible gets a name of its own: the two would otherwise be one variable.
    /// </summary>
    private string Declare(IrLocal local) {
      var name = this.Name(local.Name);
      if (this.Native("let"))
        return name;

      var chosen = name;
      for (var number = 1; this._taken.ContainsKey(chosen); ++number)
        chosen = $"{name}${number}";

      this._taken[chosen] = local;
      this._renamed[local] = chosen;
      return chosen;
    }

    /// <summary>The name a parameter or local is read and assigned with.</summary>
    private string Local(IrLocal local) => this._renamed.TryGetValue(local, out var name) ? name : this.Name(local.Name);

    /// <summary>A parameter or local as an expression of its type.</summary>
    private JsIdentifier Variable(IrLocal local) => new(this.Local(local)) { Type = local.Type };

    private void Statement(IrStmt statement, List<JsStatement> statements) {
      this._construct = statement;
      switch (statement) {
        case IrBlock block:
          statements.Add(new JsBlock(this.Statements(block)));
          break;
        case IrDeclare declare:
          var initializer = declare.Initializer is null ? null : this.Value(declare.Initializer);
          var keyword = this.Let;
          statements.Add(new JsVariableDeclaration(keyword, [new JsDeclarator(this.Declare(declare.Local), initializer)]));
          break;
        case IrAssign assign:
          var variable = this.Variable(assign.Local);
          statements.Add(new JsExpressionStatement(new JsAssignment(variable, "=", this.Value(assign.Value))));
          break;
        case IrGlobalAssign assign:
          statements.Add(new JsExpressionStatement(this.GlobalAssignment(assign)));
          break;
        case IrStore store:
          statements.Add(new JsExpressionStatement(this.Stored(store)));
          break;
        case IrFieldStore store:
          statements.Add(new JsExpressionStatement(this.FieldStore(store)));
          break;
        case IrExprStmt expression:
          // An object literal at the start of a statement would be read as a block; the printer puts it in parentheses.
          statements.Add(new JsExpressionStatement(this.Text(expression.Expression)));
          break;
        case IrIf conditional:
          statements.Add(this.If(conditional));
          break;
        case IrWhile loop:
          var test = this.Text(loop.Condition);
          statements.Add(new JsWhile(test, new JsBlock(this.Statements(loop.Body))));
          break;
        case IrDoWhile loop:
          var body = this.Statements(loop.Body);
          statements.Add(new JsDoWhile(new JsBlock(body), this.Text(loop.Condition)));
          break;
        case IrFor loop:
          this.For(loop, statements);
          break;
        case IrSwitch choice:
          statements.Add(this.Switch(choice));
          break;
        case IrBreak:
          statements.Add(new JsBreak());
          break;
        case IrContinue:
          statements.Add(new JsContinue());
          break;
        default:
          var result = (IrReturn)statement;
          statements.Add(new JsReturn(result.Value is null ? null : this.Value(result.Value)));
          break;
      }
    }

    /// <summary>
    /// A loop with a step is a JavaScript <c>for</c>, whose third part a <c>continue</c> does not skip
    /// either. That part is an expression, so the step is written as a list of expressions and the
    /// temporaries it declares are declared in front of the loop, in a block of their own with it.
    /// </summary>
    private void For(IrFor loop, List<JsStatement> statements) {
      var scoped = loop.Step.Statements.Any(step => step is IrDeclare);
      var outer = this._taken.Keys.ToHashSet();
      var target = scoped ? new List<JsStatement>() : statements;
      var parts = new List<JsExpression>();
      foreach (var step in loop.Step.Statements)
        switch (step) {
          case IrDeclare declare:
            var name = this.Declare(declare.Local);
            target.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(name, null)]));
            if (declare.Initializer is not null)
              parts.Add(new JsAssignment(Id(name), "=", this.Value(declare.Initializer)));

            break;
          case IrAssign assign:
            var variable = this.Variable(assign.Local);
            parts.Add(new JsAssignment(variable, "=", this.Value(assign.Value)));
            break;
          case IrGlobalAssign assign:
            parts.Add(this.GlobalAssignment(assign));
            break;
          case IrStore store:
            parts.Add(this.Stored(store));
            break;
          case IrFieldStore store:
            parts.Add(this.FieldStore(store));
            break;
          default:
            parts.Add(this.Text(((IrExprStmt)step).Expression));
            break;
        }

      var test = this.Text(loop.Condition);
      var update = parts.Count switch {
        0 => null,
        1 => parts[0],
        _ => new JsSequence(parts)
      };
      target.Add(new JsFor(null, test, update, new JsBlock(this.Statements(loop.Body))));
      if (scoped)
        statements.Add(new JsBlock(target));

      foreach (var name in this._taken.Keys.Where(name => !outer.Contains(name)).ToList())
        this._taken.Remove(name);
    }

    /// <summary>
    /// A switch. Every section is a block of its own and ends with a <c>break</c> unless its last
    /// statement already leaves it, because the IR has no fall-through and JavaScript has. A pair of
    /// halves is an object, which a <c>case</c> cannot compare, so a switch over one compares the
    /// text of the two halves.
    /// </summary>
    private JsSwitch Switch(IrSwitch choice) {
      var type = choice.Value.Type;
      var halves = type.IsLong && this.Halves;
      var discriminant = halves ? this.Helper("$key64", choice.Value) : this.Text(choice.Value);
      var cases = new List<JsSwitchCase>();
      foreach (var section in choice.Sections) {
        var labels = section.Labels
          .Select(label => halves ? (JsExpression?)new JsString($"{(ulong)label >> 32}:{(ulong)label & 0xFFFFFFFF}") : this.Literal(type, IrOperators.Number(type, label)))
          .ToList();
        if (section.IsDefault)
          labels.Add(null);

        var body = this.Statements(section.Body);
        if (section.Body.Statements is not [.., IrBreak or IrContinue or IrReturn])
          body.Add(new JsBreak());

        for (var index = 0; index < labels.Count; ++index)
          cases.Add(new JsSwitchCase(labels[index], index + 1 < labels.Count ? [] : [new JsBlock(body)]));
      }

      return new JsSwitch(discriminant, cases);
    }

    private JsIf If(IrIf conditional) {
      var test = this.Text(conditional.Condition);
      var then = new JsBlock(this.Statements(conditional.Then));
      if (conditional.Else is null)
        return new JsIf(test, then, null);

      return new JsIf(test, then, conditional.Else.Statements is [IrIf chained] ? this.If(chained) : new JsBlock(this.Statements(conditional.Else)));
    }

    private JsCall Helper(string name, params IrExpr[] arguments) => this.Helper(name, arguments.Select(this.Text));

    /// <summary>A store into an array element, through the helper that checks the index; a struct is stored as a value.</summary>
    private JsCall Stored(IrStore store) => this.Helper("$set", [this.Text(store.Array), this.Text(store.Index), this.Value(store.Value)]);

    private JsCall Helper(string name, IEnumerable<JsExpression> arguments) {
      this._used.Add(name);
      return Invoke(name, arguments);
    }

    /// <summary>A product of two 32-bit integers cut to 32 bits: <c>Math.imul</c> or its replacement.</summary>
    private JsCall Imul(params JsExpression[] arguments) => this.Native("Math.imul") ? Invoke("Math.imul", arguments) : this.Helper("$imul", arguments);

    /// <summary>The number of leading zero bits of a 32-bit integer: <c>Math.clz32</c> or its replacement.</summary>
    private JsCall Clz32(JsExpression argument) => this.Native("Math.clz32") ? Invoke("Math.clz32", argument) : this.Helper("$clz32", [argument]);

    /// <summary>A number rounded to binary32: <c>Math.fround</c> or its replacement.</summary>
    private JsCall Fround(JsExpression argument) => this.Native("Math.fround") ? Invoke("Math.fround", argument) : this.Helper("$fround", [argument]);

    /// <summary>
    /// An identifier JavaScript reserves, or one the emitted code relies on itself, gets a <c>$</c>
    /// appended, which no C# identifier can contain.
    /// </summary>
    private string Name(string identifier) => this._reserved.Contains(identifier) ? identifier + "$" : identifier;

  }

}
