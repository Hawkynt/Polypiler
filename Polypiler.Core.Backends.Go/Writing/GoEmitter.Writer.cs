using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Go;

public static partial class GoEmitter {

  /// <summary>Builds the syntax trees of one module; what Go cannot carry is collected in <see cref="Problems"/>.</summary>
  private sealed partial class Writer(GoProfile profile, GoCapabilities capabilities, FloatMode mode, string package) {

    private readonly List<Diagnostic> _problems = [];
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly HashSet<string> _helpers = new(StringComparer.Ordinal);
    private readonly List<GoDecl> _generated = [];
    private readonly Dictionary<string, string> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrFunction> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<IrGlobal, string> _globals = [];
    private readonly Dictionary<IrTypeDeclaration, string> _types = [];
    private readonly Dictionary<IrField, string> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrTypeDeclaration, string> _equalities = [];

    /// <summary>Every name at the level of the package: functions, constants, globals, types and helpers, which no local takes.</summary>
    private readonly HashSet<string> _packageNames = new(StringComparer.Ordinal);

    private IrModule _module = new("", []);
    private string _function = "";

    /// <summary>What the profile cannot express of the module written; empty when it can express all of it.</summary>
    public IReadOnlyList<Diagnostic> Problems => this._problems;

    /// <summary>The helpers and library functions the package carries, their texts still empty; none before <see cref="Module"/> ran.</summary>
    public IReadOnlyList<HelperText> HelperTexts { get; private set; } = [];

    /// <summary>Whether the profile has a construct.</summary>
    private bool Has(string capability) => capabilities.Has(capability, profile);

    /// <summary>Reports a construct the profile cannot express, once per construct and function.</summary>
    private void Refuse(DiagnosticDescriptor descriptor, string message) {
      var located = this._function.Length == 0 ? message : $"{message} (function '{this._function}')";
      if (this._refused.Add(descriptor.Code + located))
        this._problems.Add(descriptor.At(located));
    }

    /// <summary>Reports an operation the profile cannot express and stands in for it.</summary>
    private GoExpr Inexpressible(string what) {
      this.Refuse(InexpressibleDescriptor, $"operation '{what}' cannot be emitted as Go in profile '{profile.Name}'");
      return new GoIdent("nil");
    }

    /// <summary>Requests a helper of the backend; the package declares every one requested.</summary>
    private GoIdent Helper(string name) {
      this._helpers.Add(name);
      return new GoIdent(name);
    }

    /// <summary>The call of a helper of the backend.</summary>
    private GoCall CallHelper(string name, IrType result, params GoExpr[] arguments) => new(this.Helper(name), arguments) { Type = result };

    /// <summary>The name of an IR node's kind, without its <c>Ir</c> prefix.</summary>
    private static string KindOf(object node) => node.GetType().Name[2..];

    /// <summary>The package of a module as one file: its declarations in source order, the helpers after them, which the emitter puts into a file of their own.</summary>
    public GoFile Module(IrModule module) {
      this._module = module;
      this.Reserve();
      foreach (var function in module.Functions)
        this._byName[function.Name] = function;

      this.NameTypes(module);
      this.NameFunctions(module);
      foreach (var constant in module.Constants)
        this._constants[constant.Name] = this.Take(GoNaming.Mixed(constant.Name, false));

      foreach (var global in module.Globals)
        this._globals[global] = this.Take(GoNaming.Mixed(global.Name, false));

      this.Unsupported(module);
      var declarations = new List<GoDecl>();
      declarations.AddRange(this.TypeDeclarations(module));
      declarations.AddRange(module.Constants.Select(this.Constant));
      this._function = "the package";
      declarations.AddRange(module.Globals.Select(this.Global));
      var copied = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
      var helpers = new List<GoDecl>();
      foreach (var function in module.Functions) {
        var written = this.Function(function);
        if (copied.Contains(function.Name))
          helpers.Add(written with { IsHelper = true });
        else
          declarations.Add(written);
      }

      this._function = "";
      var texts = new List<HelperText>();
      foreach (var name in this._helpers.Order(StringComparer.Ordinal)) {
        helpers.Add(GoHelpers.Declaration(name));
        texts.Add(new(name, name, EmittedHelper.OwnLicence, EmittedHelper.OriginOf(GoHelpers.Folder), ""));
      }

      helpers.AddRange(this._generated);
      texts.AddRange(module.Implementations.Select(pair => ImplementationSources.Helper(this._functions[pair.Function], pair.Implementation, "")));
      this.HelperTexts = texts;

      var header = new List<string> {
        "Code generated by polypiler. DO NOT EDIT.",
        "",
        $"{module.Name}: change the source, not this file.",
        $"Go profile: {profile.Name}."
      };
      if (EmittedHelper.HeaderLine(texts.Select(helper => helper.Licence)) is { } licences)
        header.Add(licences);

      header.AddRange(FloatManifest.Lines(module, mode, Runtime));
      header.AddRange(ImplementationSources.Notices(module.Implementations.Select(pair => pair.Implementation).Concat(module.Inlined)));
      return new GoFile(header, package, [], [.. declarations, .. helpers]);
    }

    /// <summary>The packages of the standard library a file names, through the selectors it uses.</summary>
    public static IReadOnlyList<string> Imports(GoFile file) {
      var used = GoSyntaxRewriter.DescendantsAndSelf(file).OfType<GoSelector>().Select(selector => selector.Target).OfType<GoIdent>().Select(name => name.Name).ToHashSet(StringComparer.Ordinal);
      return [.. _packages.Where(pair => used.Contains(pair.Name)).Select(pair => pair.Path).Order(StringComparer.Ordinal)];
    }

    private static readonly (string Name, string Path)[] _packages = [("bits", "math/bits"), ("fmt", "fmt"), ("math", "math"), ("slices", "slices"), ("strconv", "strconv")];

    /// <summary>The Go name of every function, by its name in the IR.</summary>
    public IReadOnlyDictionary<string, string> Names(IrModule module) {
      this.Reserve();
      this.NameFunctions(module);
      return this._functions;
    }

    /// <summary>Takes the names of the helpers and of what the package imports, which nothing of the package may have.</summary>
    private void Reserve() {
      this._packageNames.UnionWith(GoNaming.Keywords);
      this._packageNames.UnionWith(GoNaming.Reserved);
      this._packageNames.Add(package);
    }

    /// <summary>A name at the level of the package that no other name there has.</summary>
    private string Take(string wanted) {
      var name = GoNaming.Free(Unhelper(wanted), this._packageNames.Contains);
      this._packageNames.Add(name);
      return name;
    }

    /// <summary>A name of the package's own for a helper the writer generates, which starts with <c>ply</c>.</summary>
    private string TakeHelper(string wanted) {
      var name = GoNaming.Free(wanted, this._packageNames.Contains);
      this._packageNames.Add(name);
      return name;
    }

    /// <summary>A name of the source that starts as the helpers do, <c>ply</c>, behind an <c>x</c>, so that no helper can take it.</summary>
    private static string Unhelper(string name) => name.StartsWith("ply", StringComparison.Ordinal) ? "x" + char.ToUpperInvariant(name[0]) + name[1..] : name;

    /// <summary>
    /// The Go name of every function. Exported functions keep their MixedCaps name, which two of them
    /// may not share (<see cref="NameCollision"/>): Go has no overloading; an unexported one takes the
    /// next free name.
    /// </summary>
    private void NameFunctions(IrModule module) {
      foreach (var group in module.Functions.Where(function => function.IsPublic).GroupBy(function => GoNaming.Function(function.Name, true), StringComparer.Ordinal)) {
        var first = group.First();
        if (group.Skip(1).FirstOrDefault() is { } second)
          this.Refuse(NameCollisionDescriptor, $"functions '{first.Name}' and '{second.Name}' collide as '{group.Key}'");

        var name = this._packageNames.Contains(group.Key) ? this.Take(group.Key) : Unhelper(group.Key);
        foreach (var function in group)
          this._functions[function.Name] = name;

        this._packageNames.Add(name);
      }

      foreach (var function in module.Functions.Where(function => !function.IsPublic))
        this._functions[function.Name] = this.Take(GoNaming.Function(function.Name, false));
    }

    /// <summary>The names of the struct types and their fields: exported, as a caller builds and reads the values.</summary>
    private void NameTypes(IrModule module) {
      foreach (var declaration in module.Types) {
        this._types[declaration] = this.Take(GoNaming.Mixed(declaration.Name, true));
        if (declaration is not IrStructType structure || !structure.IsDefined)
          continue;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in structure.Fields) {
          var name = GoNaming.Free(GoNaming.Mixed(field.Name, true), taken.Contains);
          taken.Add(name);
          this._fields[field] = name;
        }
      }
    }

    /// <summary>Refuses what the package has outside the subset: foreign functions, an initializer, classes, interfaces, generic and enum declarations of their own.</summary>
    private void Unsupported(IrModule module) {
      if (module.ForeignFunctions.Count > 0)
        this.Refuse(FunctionUnsupportedDescriptor, $"function feature 'foreign function {module.ForeignFunctions[0].Name}' cannot be emitted as Go");

      if (module.Initializer is { } initializer)
        this.Refuse(FunctionUnsupportedDescriptor, $"function feature 'module initializer {initializer}' cannot be emitted as Go");

      foreach (var declaration in module.Types.Where(declaration => declaration is not IrStructType and not IrEnumType))
        this.Refuse(TypeUnsupportedDescriptor, $"type '{declaration.Name}' ({KindOf(declaration)}) has no Go representation in profile '{profile.Name}'");
    }

    /// <summary>The type declarations: a struct for every struct type, nothing for an enum, whose values are those of its underlying type.</summary>
    private IEnumerable<GoDecl> TypeDeclarations(IrModule module) {
      foreach (var structure in module.Types.OfType<IrStructType>()) {
        if (!structure.IsDefined)
          continue;

        this._function = structure.Name;
        var fields = structure.Fields.Select(field => new GoField([this._fields[field]], this.TypeOf(field.Type))).ToList();
        yield return new GoTypeDecl(this._types[structure], new GoStructType(fields));
      }

      this._function = "";
    }

    /// <summary>The Go type of an IR type; <c>any</c> after reporting a type Go cannot carry.</summary>
    private GoExpr TypeOf(IrType type) {
      switch (type) {
        case { Kind: IrTypeKind.Bool }:
          return new GoIdent("bool");
        case { IsInteger: true } or { IsFloat: true }:
          return new GoIdent(GoHelpers.GoName(type));
        case { IsString: true }:
          return new GoIdent("string");
        case { IsArray: true, Element: { } element }:
          return new GoArrayType(null, this.TypeOf(element));
        case { IsFunction: true, Signature: { } signature }:
          return new GoFuncType([.. signature.Parameters.Select(parameter => new GoField([], this.TypeOf(parameter)))],
            signature.Result == IrType.Void ? [] : [new GoField([], this.TypeOf(signature.Result))]);
        case { IsStruct: true, Declaration: { } declaration } when this._types.TryGetValue(declaration, out var name):
          return new GoIdent(name);
        default:
          this.Refuse(TypeUnsupportedDescriptor, $"type '{type}' has no Go representation in profile '{profile.Name}'");
          return new GoIdent("any");
      }
    }

    /// <summary>A constant of the package: <c>const</c> for a value, an unexported <c>var</c> slice for a table, eight elements to a line.</summary>
    private GoDecl Constant(IrModuleConst constant) {
      this._function = constant.Name;
      var name = this._constants[constant.Name];
      GoDecl declaration;
      if (constant.Type.IsString) {
        declaration = new GoConstDecl(new GoValueSpec([name], null, [this.Text(constant.Strings.Count > 0 ? constant.Strings[0] : "")]));
      } else if (constant.Type.Element is { IsString: true }) {
        declaration = new GoVarDecl(new GoValueSpec([name], null, [new GoComposite(this.TypeOf(constant.Type), [.. constant.Strings.Select(this.Text)]) { PerLine = 4 }]));
      } else if (constant.Type.Element is not { } element) {
        declaration = new GoConstDecl(new GoValueSpec([name], this.TypeOf(constant.Type), [this.Literal(constant.Type, constant.Values.Count > 0 ? constant.Values[0] : 0)]));
      } else {
        declaration = new GoVarDecl(new GoValueSpec([name], null, [new GoComposite(this.TypeOf(constant.Type), [.. constant.Values.Select(value => this.Literal(element, value))]) { PerLine = 8 }]));
      }

      this._function = "";
      return declaration;
    }

    /// <summary>
    /// A global of the package: a <c>var</c> with its initializer, which Go runs before any function
    /// of the package; the initializer is static (<see cref="IrGlobal"/>), so the order Go chooses cannot be observed.
    /// </summary>
    private GoDecl Global(IrGlobal global) {
      this.BeginFunction(null);
      var name = this._globals[global];
      if (global.Initializer is not { } initializer)
        return new GoVarDecl(new GoValueSpec([name], this.TypeOf(global.Type), []));

      var value = this.Expression(initializer);
      if (this._prelude.Count > 0)
        this.Refuse(InexpressibleDescriptor, $"the initializer of the global '{global.Name}' needs statements, which a Go variable declaration cannot hold");

      return new GoVarDecl(new GoValueSpec([name], this.TypeOf(global.Type), [value]));
    }

  }

}
