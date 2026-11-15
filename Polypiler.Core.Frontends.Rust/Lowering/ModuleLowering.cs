using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>What lowering a crate produced: the functions, the types, the constant tables and the tests.</summary>
internal sealed record LoweredCrate(IReadOnlyList<IrFunction> Functions, IReadOnlyList<IrTypeDeclaration> Types, IReadOnlyList<IrModuleConst> Constants, IReadOnlyList<string> Tests, IReadOnlyList<string> ModuleOrder);

/// <summary>
/// Checks and lowers a crate: its module tree, the declarations of its types, the constants, every
/// function, then the helpers the functions asked for (the integer powers, which are written in
/// Rust and lowered by this same frontend). The functions are listed module by module in the order
/// of the module graph: a module after every module whose items it uses and after its children,
/// ties by crate path. A crate whose functions do not all check imports nothing.
/// </summary>
internal sealed class ModuleLowering {

  private readonly OverflowChecks _mode;
  private readonly Typed _typed = new();
  private readonly SortedSet<(string Kind, string Type)> _helpers = [];
  private readonly SortedSet<string> _textHelpers = new(StringComparer.Ordinal);
  private string? _textPrefix;
  private readonly HashSet<string> _names = new(StringComparer.Ordinal);
  private readonly Action<string, string, Span> _report;
  private readonly Dictionary<StructInfo, IrStructType> _structs = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<EnumInfo, IrEnumType> _enums = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<string, IrStructType> _shapes = new(StringComparer.Ordinal);
  private readonly List<IrTypeDeclaration> _types = [];
  private readonly List<IrModuleConst> _tables = [];
  private readonly Dictionary<Binding, IrModuleConst> _tablesByConstant = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<string> _typeNames = new(StringComparer.Ordinal);

  private ModuleLowering(OverflowChecks mode, Action<string, string, Span> report) {
    this._mode = mode;
    this._report = report;
  }

  /// <summary>The crate lowered, or <see langword="null"/> when something was reported.</summary>
  public static LoweredCrate? Run(SourceFile file, string path, RustImportOptions options, Action<string, string, Span> report) {
    var failed = false;
    void Report(string code, string message, Span at) {
      failed = true;
      report(code, message, at);
    }

    var lowering = new ModuleLowering(options.OverflowChecks, Report);
    var crate = CrateScope.Build(file, path, Report, options.ReadFile);
    var checker = new TypeChecker(lowering._typed, crate, Report) { DebugAssertions = options.DebugAssertions, Internal = options.Internal };
    lowering._typed.DebugAssertions = options.DebugAssertions == true;
    lowering._typed.Modules.AddRange(crate.Modules);
    if (!checker.Declare() || failed)
      return null;

    checker.FinishDeclarations();
    var signatures = lowering._typed.Functions.ToList();
    foreach (var signature in signatures)
      checker.Function(signature);

    if (failed)
      return null;

    lowering.Name(crate);
    lowering.Declare();
    var all = lowering._typed.Functions.ToList();
    var order = Order(crate, checker.ModuleUses);
    var functions = new List<IrFunction>();
    foreach (var module in order)
      foreach (var signature in all.Where(signature => ReferenceEquals(signature.Module, module)))
        functions.Add(new FunctionLowering(lowering, lowering._typed, lowering._mode, Report).Lower(signature));

    if (failed)
      return null;

    var helpers = lowering.Helpers(Report);
    if (failed || helpers is null)
      return null;

    var tests = all.Where(signature => signature.Item.IsTest).Select(signature => signature.IrName).ToList();
    return new LoweredCrate([.. functions, .. helpers], lowering._types, lowering._tables, tests, [.. order.Select(module => module.CratePath)]);
  }

  /// <summary>Gives every function its IR name - its crate path, unique in the module - and marks the exported ones.</summary>
  private void Name(CrateScope crate) {
    var exported = Exports(crate);
    foreach (var signature in this._typed.Functions) {
      var wanted = exported.TryGetValue(signature.Item, out var export) ? export : signature.IrName;
      signature.IsExported = exported.ContainsKey(signature.Item);
      var name = wanted;
      for (var suffix = 2; !this._names.Add(name); ++suffix)
        name = wanted + suffix;

      signature.IrName = name;
    }
  }

  /// <summary>
  /// The functions the crate exports - every function reachable as <c>pub</c> from the root, through
  /// <c>pub</c> modules and <c>pub use</c> - each by the shortest path that reaches it, ties by path.
  /// </summary>
  private static Dictionary<FunctionItem, string> Exports(CrateScope crate) {
    var exports = new Dictionary<FunctionItem, string>(ReferenceEqualityComparer.Instance);
    var queue = new Queue<(ModuleScope Module, string Prefix)>([(crate.Root, "")]);
    var seen = new HashSet<ModuleScope>(ReferenceEqualityComparer.Instance) { crate.Root };
    while (queue.Count > 0) {
      var (module, prefix) = queue.Dequeue();
      foreach (var (name, symbol) in module.Items.Concat(module.Imported).OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
        if (!symbol.IsPublic)
          continue;

        if (symbol.Target is FunctionItem function) {
          var path = prefix + name;
          if (!exports.TryGetValue(function, out var known) || path.Count(character => character == '_') < known.Count(character => character == '_'))
            exports[function] = path;
        } else if (symbol.Target is ModuleScope child && !child.TestOnly && seen.Add(child)) {
          queue.Enqueue((child, prefix + name + "_"));
        }
      }
    }

    return exports;
  }

  /// <summary>
  /// The modules in the order of the module graph: every module after the modules whose items it
  /// uses and after its children, ties by crate path in ordinal order; a cycle is broken at the
  /// module of the smallest path. The modules compiled for tests only come after the library's.
  /// </summary>
  private static List<ModuleScope> Order(CrateScope crate, IEnumerable<(ModuleScope User, ModuleScope Used)> uses) {
    var dependencies = new Dictionary<ModuleScope, HashSet<ModuleScope>>(ReferenceEqualityComparer.Instance);
    foreach (var module in crate.Modules)
      dependencies[module] = new HashSet<ModuleScope>(ReferenceEqualityComparer.Instance);

    foreach (var module in crate.Modules)
      if (module.Parent is { } parent && !(module.TestOnly && !parent.TestOnly))
        dependencies[parent].Add(module);

    foreach (var (user, used) in uses)
      // A module that uses an item of one of its ancestors - a test module's `use super::*` - would close a circle with it.
      if (!user.IsWithin(used))
        dependencies[user].Add(used);

    var order = new List<ModuleScope>();
    var remaining = crate.Modules.ToList();
    while (remaining.Count > 0) {
      var ready = remaining.Where(module => dependencies[module].All(order.Contains)).ToList();
      var next = (ready.Count > 0 ? ready : remaining).OrderBy(module => module.TestOnly).ThenBy(module => module.CratePath, StringComparer.Ordinal).First();
      order.Add(next);
      remaining.Remove(next);
    }

    return order;
  }

  /// <summary>The names of the functions of the module, which no local may take.</summary>
  public IReadOnlySet<string> Names => this._names;

  // ---------------------------------------------------------------- types

  /// <summary>Declares the IR types of the structs and enums of the crate, in declaration order.</summary>
  private void Declare() {
    foreach (var info in this._typed.Enums) {
      var members = info.Variants.Select(variant => new IrEnumMember(variant.Name, (long)variant.Value)).ToList();
      var declaration = new IrEnumType(this.TypeName(info.Path), info.Representation.Ir, members) { IsPublic = info.Item.IsPublic };
      this._enums[info] = declaration;
      this._types.Add(declaration);
    }

    foreach (var info in this._typed.Structs) {
      var declaration = new IrStructType(this.TypeName(info.Path));
      this._structs[info] = declaration;
      this._types.Add(declaration);
    }

    foreach (var info in this._typed.Structs)
      this._structs[info].Define([.. info.Fields.Select(field => new IrField(FieldName(field.Name), this.Ir(field.Type), false))]);
  }

  /// <summary>The IR name of a field: a tuple struct's fields are numbered, which no identifier may be.</summary>
  public static string FieldName(string name) => char.IsAsciiDigit(name[0]) ? "_" + name : name;

  private string TypeName(string wanted) {
    var name = wanted;
    for (var suffix = 2; !this._typeNames.Add(name); ++suffix)
      name = wanted + suffix;

    return name;
  }

  /// <summary>The IR struct of a struct of the crate.</summary>
  public IrStructType Struct(StructInfo info) => this._structs[info];

  /// <summary>The IR enum of an enum of the crate.</summary>
  public IrEnumType Enum(EnumInfo info) => this._enums[info];

  /// <summary>
  /// The type of the IR a type of the subset is represented by. A reference is what it points to - a
  /// shared one the value, a <c>&amp;mut</c> a place the lowering tracks - except a reference to a
  /// slice, which is a view, writable for <c>&amp;mut</c>; an array is an array, a <c>Vec</c> a list,
  /// a tuple and a <c>Result</c> a struct of their parts, an <c>Option</c> an optional value.
  /// </summary>
  public IrType Ir(RType type) {
    var resolved = type.Resolved;
    switch (resolved.Kind) {
      case RKind.Integer or RKind.Float or RKind.Bool or RKind.Char or RKind.Str or RKind.String:
        return resolved.Ir;
      case RKind.Reference: {
        var target = resolved.Element!.Resolved;
        if (target.Kind == RKind.Slice) {
          var element = this.Ir(target.Element!);
          return resolved.Mutable ? IrType.Span(element) : IrType.ReadOnlySpan(element);
        }

        return this.Ir(target);
      }
      case RKind.Array:
        return IrType.Array(this.Ir(resolved.Element!));
      case RKind.Slice:
        return IrType.Span(this.Ir(resolved.Element!));
      case RKind.Vec:
        return IrType.List(this.Ir(resolved.Element!));
      case RKind.Option:
        return IrType.Optional(this.Ir(resolved.Element!));
      case RKind.Struct:
        return IrType.Struct(this._structs[resolved.Struct!]);
      case RKind.Enum:
        return this._enums[resolved.Enum!].Type;
      case RKind.Tuple:
        return IrType.Struct(this.Shape("Tuple", resolved.Elements.Select((element, index) => ("item" + index, element)).ToList()));
      case RKind.Result: {
        var fields = new List<(string, RType)> { ("is_ok", RType.Bool) };
        if (resolved.Element!.Resolved.Kind != RKind.Unit)
          fields.Add(("ok", resolved.Element));

        if (resolved.Error.Resolved.Kind != RKind.Unit)
          fields.Add(("err", resolved.Error));

        return IrType.Struct(this.Shape("Result", fields, [resolved.Element, resolved.Error]));
      }
      case RKind.Function:
        return IrType.Function(new IrFunctionType([.. resolved.Elements.Select(this.Ir)], this.Ir(resolved.Element!)));
      default:
        return IrType.Void;
    }
  }

  /// <summary>The struct of a tuple or a result of the given parts, one per distinct shape.</summary>
  private IrStructType Shape(string kind, List<(string Name, RType Type)> fields, IReadOnlyList<RType>? parts = null) {
    var key = $"{kind}<{string.Join(", ", (parts ?? fields.Select(field => field.Type).ToList()).Select(part => part.ToString()))}>";
    if (this._shapes.TryGetValue(key, out var known))
      return known;

    var name = kind + "_" + string.Join("_", (parts ?? fields.Select(field => field.Type).ToList()).Select(part => Word(this.Ir(part))));
    var declaration = new IrStructType(this.TypeName(name), [.. fields.Select(field => new IrField(field.Name, this.Ir(field.Type), false))]);
    this._shapes[key] = declaration;
    this._types.Add(declaration);
    return declaration;
  }

  /// <summary>A type as a word of a generated name.</summary>
  private static string Word(IrType type) {
    if (type == IrType.Void)
      return "unit";

    var text = type.ToString();
    return new string([.. text.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')]).Trim('_');
  }

  private readonly Dictionary<FunctionSignature, IrStructType> _outputs = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// The struct a function with <c>&amp;mut</c> parameters to values returns: its result, if it has
  /// one, then the final value of each such parameter, the receiver first.
  /// </summary>
  public IrStructType OutputOf(FunctionSignature signature) {
    if (this._outputs.TryGetValue(signature, out var known))
      return known;

    var fields = new List<IrField>();
    if (signature.Result.Resolved.Kind is not (RKind.Unit or RKind.Never))
      fields.Add(new IrField("result", this.Ir(signature.Result), false));

    foreach (var index in FunctionLowering.Outputs(signature)) {
      var type = index < 0 ? signature.Receiver! : signature.Parameters[index];
      var name = index < 0 ? "receiver" : signature.Item.Parameters[index].Name ?? "arg" + index;
      fields.Add(new IrField(fields.Any(field => field.Name == name) ? name + index : name, this.Ir(type), false));
    }

    var declaration = new IrStructType(this.TypeName(signature.IrName + "_out"), fields);
    this._outputs[signature] = declaration;
    this._types.Add(declaration);
    return declaration;
  }

  /// <summary>The table of the module a constant or static array is, made once.</summary>
  public IrModuleConst? Table(Binding constant, Func<IrModuleConst?> make) {
    if (this._tablesByConstant.TryGetValue(constant, out var known))
      return known;

    var table = make();
    if (table is null)
      return null;

    var name = constant.Name;
    for (var suffix = 2; !this._names.Add(name); ++suffix)
      name = constant.Name + suffix;

    table = table with { Name = name };
    this._tablesByConstant[constant] = table;
    this._tables.Add(table);
    return table;
  }

  // ---------------------------------------------------------------- helpers

  /// <summary>
  /// The name of a helper of the module: <c>wrapping</c>, the power modulo 2^width, or
  /// <c>overflows</c>, whether the exact power is no value of the type; the helper is lowered with the module.
  /// </summary>
  public string Power(string kind, RType type) {
    var rust = type.Ir.ToString();
    this._helpers.Add((kind, rust));
    return this.HelperName(kind, rust);
  }

  private string HelperName(string kind, string type) {
    var name = $"rust_pow_{kind}_{type}";
    while (this._typed.Functions.Any(function => function.IrName == name || function.Item.Name == name))
      name = "_" + name;

    return name;
  }

  private List<IrFunction>? Helpers(Action<string, string, Span> report) {
    if (this._helpers.Count == 0 && this._textHelpers.Count == 0)
      return [];

    var source = string.Concat(this._helpers.Select(helper => PowerSource(this.HelperName(helper.Kind, helper.Type), helper.Kind, helper.Type)));
    if (this._textHelpers.Count > 0)
      source += TextHelpers.Source(this._textHelpers, this.TextPrefix);

    var file = Parser.Parse(source);
    var lowered = Run(file, "helpers.rs", new RustImportOptions(OverflowChecks.Wrap) { Internal = true }, report);
    return lowered?.Functions.ToList();
  }

  /// <summary>The name of a text helper of the module (<see cref="TextHelpers"/>); the helper is lowered with the module.</summary>
  public string Text(string helper) {
    this._textHelpers.Add(helper);
    return this.TextPrefix + helper;
  }

  /// <summary><c>rust_</c>, or with as many underscores in front as keep it the prefix of no function of the crate.</summary>
  private string TextPrefix {
    get {
      if (this._textPrefix is null) {
        var prefix = "rust_";
        while (this._typed.Functions.Any(function => function.IrName.StartsWith(prefix, StringComparison.Ordinal) || function.Item.Name.StartsWith(prefix, StringComparison.Ordinal)))
          prefix = "_" + prefix;

        this._textPrefix = prefix;
      }

      return this._textPrefix;
    }
  }

  /// <summary>
  /// The Rust source of a power helper: exponentiation by squaring as <c>core</c>'s <c>wrapping_pow</c>
  /// and <c>overflowing_pow</c> do it, so that the flag is set exactly when one of the products
  /// overflows, which is exactly when the power is no value of the type.
  /// </summary>
  private static string PowerSource(string name, string kind, string type) => kind == "wrapping"
    ? $$"""
      fn {{name}}(base: {{type}}, exp: u32) -> {{type}} {
        let mut base = base;
        let mut exp = exp;
        let mut acc: {{type}} = 1;
        while exp > 1 {
          if exp & 1 == 1 {
            acc = acc.wrapping_mul(base);
          }
          exp /= 2;
          base = base.wrapping_mul(base);
        }
        if exp == 1 {
          acc = acc.wrapping_mul(base);
        }
        acc
      }

      """
    : $$"""
      fn {{name}}(base: {{type}}, exp: u32) -> bool {
        let mut base = base;
        let mut exp = exp;
        let mut acc: {{type}} = 1;
        let mut overflown = false;
        while exp > 1 {
          if exp & 1 == 1 {
            overflown = overflown | acc.overflowing_mul(base).1;
            acc = acc.wrapping_mul(base);
          }
          exp /= 2;
          overflown = overflown | base.overflowing_mul(base).1;
          base = base.wrapping_mul(base);
        }
        if exp == 1 {
          overflown = overflown | acc.overflowing_mul(base).1;
        }
        overflown
      }

      """;

}
