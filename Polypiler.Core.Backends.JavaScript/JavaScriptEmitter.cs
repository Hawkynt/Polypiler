using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

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
  /// same bits on every target.
  /// </summary>
  /// <param name="module">A module, legalized for the profile and the mode, that <see cref="Check"/> accepts.</param>
  /// <param name="profile">The JavaScript to write.</param>
  /// <param name="mode">The floating-point mode.</param>
  /// <param name="capabilities">The table the constructs are chosen from; the standard one when <see langword="null"/>.</param>
  /// <returns>The generated file.</returns>
  /// <exception cref="ArgumentException">The module is ill-typed or cannot be expressed in the profile.</exception>
  public static IReadOnlyList<GeneratedFile> Emit(IrModule module, JavaScriptProfile profile, FloatMode mode, JavaScriptCapabilities? capabilities = null) {
    var writer = new Writer(profile, capabilities ?? JavaScriptCapabilities.Standard, mode);
    var problems = IrVerifier.Verify(module);
    var text = problems.Count > 0 ? "" : writer.Module(module);
    if (problems.Concat(writer.Problems).FirstOrDefault() is { } problem)
      throw new ArgumentException($"The module '{module.Name}' cannot be emitted: {problem.Message}.", nameof(module));

    return [new GeneratedFile(module.Name + profile.Extension, text)];
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

  /// <summary>A 64-bit integer as the object of its two unsigned halves.</summary>
  private static string Pair(ulong bits) => $"{{ hi: {bits >> 32}, lo: {bits & 0xFFFFFFFF} }}";

  /// <summary>
  /// A floating-point number as a literal that reads back as exactly that number: the shortest digits
  /// that identify the double. A binary32 number is widened first, which changes nothing about it.
  /// </summary>
  private static string Literal(double value) {
    if (double.IsNaN(value))
      return "NaN";

    return double.IsInfinity(value) ? (value < 0 ? "-Infinity" : "Infinity") : value.ToString("R", CultureInfo.InvariantCulture);
  }

  private sealed partial class Writer {

    /// <summary>The variable a postfix step of a local keeps the old value in; a function that needs it declares it.</summary>
    private const string Previous = "$p";

    private const string Zero64 = "{ hi: 0, lo: 0 }";
    private const string One64 = "{ hi: 0, lo: 1 }";

    private readonly JavaScriptProfile _profile;
    private readonly JavaScriptCapabilities _capabilities;
    private readonly FloatMode _mode;
    private readonly HashSet<string> _reserved;
    private readonly StringBuilder _code = new();
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

    public string Module(IrModule module) {
      foreach (var function in module.Functions)
        this.Function(function);

      this._construct = "the module";
      var structs = this.StructHelpers();
      var body = new StringBuilder();
      foreach (var (_, lines) in JavaScriptHelpers.For(this._used, this.Native))
        body.Append('\n').AppendJoin('\n', lines).Append('\n');

      body.Append(structs);

      if (module.Constants.Count > 0)
        body.Append('\n');

      foreach (var constant in module.Constants)
        body.Append(this.Native("const") ? "const " : "var ").Append(this.Name(constant.Name)).Append(" = ").Append(this.Value(constant)).Append(";\n");

      body.Append(this._code);
      var text = new StringBuilder();
      text.Append("// ").Append(module.Name).Append(": generated by polypiler. Change the source, not this file.\n");
      text.Append("// JavaScript profile: ").Append(this._profile).Append(".\n");
      foreach (var line in FloatManifest.Lines(module, this._mode, NativeAccuracy.Node))
        text.Append("// ").Append(line).Append('\n');

      if (this._halves)
        text.Append("// A 64-bit integer is an object { hi, lo }: its upper and its lower 32 bits, each as an unsigned number.\n");

      if (this._structs)
        text.Append("// A struct is an object of its fields, copied wherever the place it is read from can still be used.\n");

      switch (this._profile.Module) {
        case JavaScriptModuleFormat.Esm:
          this.Native("export");
          text.Append(body);
          break;
        case JavaScriptModuleFormat.Cjs:
          text.Append("\"use strict\";\n").Append(body);
          if (this._exported.Count > 0)
            text.Append('\n');

          foreach (var name in this._exported)
            text.Append("exports.").Append(name).Append(" = ").Append(name).Append(";\n");

          break;
        default:
          text.Append("var ").Append(this._profile.Global).Append(" = (function () {\n  \"use strict\";\n");
          foreach (var line in body.ToString().TrimEnd('\n').Split('\n'))
            text.Append(line.Length > 0 ? "  " : "").Append(line).Append('\n');

          text.Append("\n  return {");
          text.AppendJoin(",", this._exported.Select(name => $"\n    {name}: {name}"));
          text.Append(this._exported.Count > 0 ? "\n  };\n" : "};\n").Append("})();\n");
          break;
      }

      return text.ToString();
    }

    /// <summary>
    /// The value of a constant of the module. A table is an array built once from its elements,
    /// eight to a line when it has more than that.
    /// </summary>
    private string Value(IrModuleConst constant) {
      if (constant.Type.Element is not { } element)
        return this.Constant(constant.Type, constant.Values[0]).Text;

      var elements = constant.Values.Select(value => this.Constant(element, value).Text).ToList();
      var list = elements.Count <= 8
        ? string.Join(", ", elements)
        : "\n  " + string.Join(",\n  ", elements.Chunk(8).Select(row => string.Join(", ", row))) + "\n";
      return this.ArrayOf(element, list);
    }

    /// <summary>An array of the given elements: the typed array of the element type, or a plain array where the profile has none.</summary>
    private string ArrayOf(IrType element, string list) => this.Native(Storage(element)) ? $"new {Storage(element)}([{list}])" : $"[{list}]";

    /// <summary>The zero of an element type, which a new or a cleared array holds.</summary>
    private string Zero(IrType element) => !element.IsLong ? "0" : this.Halves ? Zero64 : "0n";

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
    private string Literal(IrType type, Int128 value) {
      if (!type.IsLong)
        return value.ToString(CultureInfo.InvariantCulture);

      return this.Halves ? Pair(unchecked((ulong)value)) : value.ToString(CultureInfo.InvariantCulture) + "n";
    }

    /// <summary>A constant as a literal of its type; it is an operand as it stands unless it is negative.</summary>
    private (string Text, bool Atomic) Constant(IrType type, long value) {
      if (type == IrType.Bool)
        return (value != 0 ? "true" : "false", true);

      if (type.IsFloat) {
        var literal = JavaScriptEmitter.Literal(IrOperators.Float(type, value));
        return (literal, literal[0] != '-');
      }

      var number = IrOperators.Number(type, value);
      var text = this.Literal(type, number);
      return (text, number >= 0 || text[0] == '{');
    }

    /// <summary>
    /// A function under a comment that states what crosses its boundary: integers of up to 32 bits
    /// are numbers, those of 64 bits are <c>BigInt</c>s or pairs of halves, arrays are typed or plain.
    /// </summary>
    private void Function(IrFunction function) {
      this._construct = $"the function '{function.Name}'";
      this._taken.Clear();
      this._renamed.Clear();
      foreach (var parameter in function.Parameters)
        this._taken[this.Name(parameter.Name)] = parameter;

      var name = this.Name(function.Name);
      var parameters = string.Join(", ", function.Parameters.Select(parameter => this.Name(parameter.Name)));
      var signature = string.Join(", ", function.Parameters.Select(parameter => $"{this.Name(parameter.Name)}: {this.Describe(parameter.Type)}"));
      this._code.Append("\n// ").Append(name).Append('(').Append(signature).Append("): ")
        .Append(this.Describe(function.ReturnType)).Append('\n');
      var exported = function.IsPublic && this._profile.Module == JavaScriptModuleFormat.Esm;
      if (function.IsPublic && !exported)
        this._exported.Add(name);

      this._code.Append(exported ? "export function " : "function ").Append(name).Append('(').Append(parameters).Append(") {\n");
      this._liveness = Liveness.Of(function);
      foreach (var parameter in this._liveness.CopiedOnEntry)
        this._code.Append("  ").Append(this.Local(parameter)).Append(" = ").Append(this.Copy(new IrLocalRef(parameter))).Append(";\n");

      var start = this._code.Length;
      this._previous = false;
      this.Statements(function.Body, 1);
      if (this._previous)
        this._code.Insert(start, $"  {this.Let} {Previous};\n");

      this._code.Append("}\n");
    }

    /// <summary>
    /// The statements of a block. Where a variable is a <c>var</c>, which belongs to the whole
    /// function, the names the block declared are free again when it ends.
    /// </summary>
    private void Statements(IrBlock block, int depth) {
      var outer = this._taken.Keys.ToHashSet();
      foreach (var statement in block.Statements)
        this.Statement(statement, depth);

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

    private StringBuilder Line(int depth) => this._code.Append(' ', depth * 2);

    private void Statement(IrStmt statement, int depth) {
      this._construct = statement;
      switch (statement) {
        case IrBlock block:
          this.Line(depth).Append("{\n");
          this.Statements(block, depth + 1);
          this.Line(depth).Append("}\n");
          break;
        case IrDeclare declare:
          var initializer = declare.Initializer is null ? "" : " = " + this.Value(declare.Initializer);
          this.Line(depth).Append(this.Let).Append(' ').Append(this.Declare(declare.Local)).Append(initializer).Append(";\n");
          break;
        case IrAssign assign:
          this.Line(depth).Append(this.Local(assign.Local)).Append(" = ").Append(this.Value(assign.Value)).Append(";\n");
          break;
        case IrStore store:
          this.Line(depth).Append(this.Stored(store)).Append(";\n");
          break;
        case IrFieldStore store:
          this.Line(depth).Append(this.FieldStore(store)).Append(";\n");
          break;
        case IrExprStmt expression:
          // An object literal at the start of a statement would be read as a block.
          var text = this.Text(expression.Expression);
          this.Line(depth).Append(text[0] == '{' ? $"({text})" : text).Append(";\n");
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
          this.Line(depth).Append(result.Value is null ? "return" : "return " + this.Value(result.Value)).Append(";\n");
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
      var outer = this._taken.Keys.ToHashSet();
      if (scoped)
        this.Line(depth++).Append("{\n");

      var parts = new List<string>();
      foreach (var step in loop.Step.Statements)
        switch (step) {
          case IrDeclare declare:
            var name = this.Declare(declare.Local);
            this.Line(depth).Append(this.Let).Append(' ').Append(name).Append(";\n");
            if (declare.Initializer is not null)
              parts.Add($"{name} = {this.Value(declare.Initializer)}");

            break;
          case IrAssign assign:
            parts.Add($"{this.Local(assign.Local)} = {this.Value(assign.Value)}");
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

      this.Line(depth).Append("for (; ").Append(this.Text(loop.Condition)).Append("; ").AppendJoin(", ", parts).Append(") {\n");
      this.Statements(loop.Body, depth + 1);
      this.Line(depth).Append("}\n");
      if (scoped)
        this.Line(depth - 1).Append("}\n");

      foreach (var name in this._taken.Keys.Where(name => !outer.Contains(name)).ToList())
        this._taken.Remove(name);
    }

    /// <summary>
    /// A switch. Every section is a block of its own and ends with a <c>break</c> unless its last
    /// statement already leaves it, because the IR has no fall-through and JavaScript has. A pair of
    /// halves is an object, which a <c>case</c> cannot compare, so a switch over one compares the
    /// text of the two halves.
    /// </summary>
    private void Switch(IrSwitch choice, int depth) {
      var type = choice.Value.Type;
      var halves = type.IsLong && this.Halves;
      this.Line(depth).Append("switch (").Append(halves ? this.Helper("$key64", choice.Value) : this.Text(choice.Value)).Append(") {\n");
      foreach (var section in choice.Sections) {
        var labels = section.Labels
          .Select(label => halves ? $"case \"{(ulong)label >> 32}:{(ulong)label & 0xFFFFFFFF}\":" : $"case {this.Literal(type, IrOperators.Number(type, label))}:")
          .ToList();
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

    /// <summary>A store into an array element, through the helper that checks the index; a struct is stored as a value.</summary>
    private string Stored(IrStore store) => this.Helper("$set", [this.Text(store.Array), this.Text(store.Index), this.Value(store.Value)]);

    private string Helper(string name, IEnumerable<string> arguments) {
      this._used.Add(name);
      return $"{name}({string.Join(", ", arguments)})";
    }

    /// <summary>A product of two 32-bit integers cut to 32 bits: <c>Math.imul</c> or its replacement.</summary>
    private string Imul(string arguments) => this.Native("Math.imul") ? $"Math.imul({arguments})" : this.Helper("$imul", [arguments]);

    /// <summary>The number of leading zero bits of a 32-bit integer: <c>Math.clz32</c> or its replacement.</summary>
    private string Clz32(string argument) => this.Native("Math.clz32") ? $"Math.clz32({argument})" : this.Helper("$clz32", [argument]);

    /// <summary>A number rounded to binary32: <c>Math.fround</c> or its replacement.</summary>
    private string Fround(string argument) => this.Native("Math.fround") ? $"Math.fround({argument})" : this.Helper("$fround", [argument]);

    /// <summary>
    /// An identifier JavaScript reserves, or one the emitted code relies on itself, gets a <c>$</c>
    /// appended, which no C# identifier can contain.
    /// </summary>
    private string Name(string identifier) => this._reserved.Contains(identifier) ? identifier + "$" : identifier;

  }

}
