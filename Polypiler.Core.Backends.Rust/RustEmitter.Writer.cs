using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Helpers;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>Builds the crate of one module; collects every construct it cannot write as a diagnostic.</summary>
  private sealed partial class Writer(RustProfile profile, RustCapabilities capabilities, RustEmitOptions options) {

    private readonly RustProfile _profile = profile;
    private readonly RustCapabilities _capabilities = capabilities;
    private readonly RustEmitOptions _options = options;
    private readonly List<Diagnostic> _problems = [];
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, RsFunction> _spellings = new(StringComparer.Ordinal);
    private IrModule _module = new("module", []);
    private string _construct = "the module";

    /// <summary>Every construct the module has that the profile cannot write.</summary>
    public IReadOnlyList<Diagnostic> Problems => this._problems;

    /// <summary>The helpers and spelling functions the crate carries, their texts still empty.</summary>
    public IReadOnlyList<HelperText> Helpers { get; private set; } = [];

    private int Edition => this._profile.Edition;

    private bool Has(string capability) => this._capabilities.Has(capability, this._profile);

    /// <summary>Records that a construct cannot be written, once per construct, and returns a stand-in.</summary>
    private RsExpression Refuse(string what) {
      if (this._refused.Add(what))
        this._problems.Add(InexpressibleDescriptor.At($"'{what}' cannot be expressed in Rust profile '{this._profile.Name}' (in {this._construct})"));

      return new RsPath("_refused");
    }

    /// <summary>The crate: manifest, root and module.</summary>
    public RustCrate Crate(IrModule module) {
      module = LowerUsing(module);
      module = this.GeneralizedClasses(this.Generalized(module));
      module = Polypiler.Core.Backends.Rust.Emit.AliasSpecialization.Run(Polypiler.Core.Backends.Rust.Emit.ObjectLowering.Run(Polypiler.Core.Backends.Rust.Emit.ListLiterals.Run(Polypiler.Core.Backends.Rust.Emit.BindHoisting.Run(module))));
      this._module = module;
      var moduleName = ModuleName(module.Name, this._profile);
      var package = this._options.PackageName ?? moduleName;
      // The verifier admits structs, enums, classes and interfaces only: each has its form below.
      this.NameItems(module);
      this.NameInstances();
      this.NameTypes(module);
      this.NameForeign(module);
      this.NameIterators(module);
      this.NameGlobals(module);
      this.NameReaders(module);
      this.SharedCollections(module);
      this.Loading(module);
      this.Classes(module);
      this.Signatures(module);
      this.Exceptions(module);
      this.CollectionParameters(module);
      this._arrays = Polypiler.Core.Backends.Rust.Emit.ArrayOwnership.Of(module);
      this._arrayNulls = Polypiler.Core.Backends.Rust.Emit.ArrayNullness.Of(module);
      this._stringNulls = Polypiler.Core.Backends.Rust.Emit.StringNullness.Of(module);
      this.Ownership(module);
      this.ConfirmGenericClasses();
      this.ClosedEnums(module);
      var copied = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
      var items = new List<RsItem>();
      foreach (var constant in module.Constants)
        items.Add(this.Constant(constant));
      items.AddRange(this.Globals(module));
      items.AddRange(this.ModuleStateItems(module));
      items.AddRange(this.ErrorItems());
      if (this.Results && this._types.Values.Contains(TrapErrorType))
        this.LimitResult($"a type of the module named '{TrapErrorType}'");
      var written = module.Functions.Where(function => !this._generalized.ContainsKey(function.Name) && !this._unwritten.Contains(function.Name)).Select(function => (Source: function, Written: this.Function(function) with { IsHelper = copied.Contains(function.Name) })).ToList();
      var members = written.Where(pair => this._members.ContainsKey(pair.Source.Name))
        .ToDictionary(pair => pair.Source.Name, pair => this.AsMember(pair.Written, pair.Source, this._members[pair.Source.Name]), StringComparer.Ordinal);
      var functions = written.Where(pair => !this._members.ContainsKey(pair.Source.Name) && !this._bodiless.Contains(pair.Source.Name) && !this._options.TestFunctions.Contains(pair.Source.Name)).Select(pair => pair.Written).ToList();
      // A test function of the source is a #[test] of the tests module, which only a test build compiles.
      var tests = written.Where(pair => this._options.TestFunctions.Contains(pair.Source.Name)).Select(pair => (RsItem)(pair.Written with { Visibility = "", Attributes = ["test"] })).ToList();
      this._written = members;
      this._definition = null;
      this._standIns = [];
      foreach (var type in module.Types)
        items.AddRange(type switch {
          IrStructType structType => this.Struct(structType),
          IrEnumType enumType when this._closedEnums.Contains(enumType) => this.ClosedEnumItems(enumType),
          IrEnumType enumType => this.EnumItems(enumType),
          IrClassType classType when !classType.IsLibrary && !this._hierarchy.Contains(classType) && !IrExceptionClass.IsException(classType) && !this._unwrittenClasses.Contains(classType) => this.Class(classType, members),
          _ => []
        });

      items.AddRange(this.HierarchyItems());
      items.AddRange(this.ForeignItems(module));

      var unit = this._options.Tests.Count > 0 || tests.Count > 0 ? this.Tests(module) : null;
      this._construct = "the module";
      foreach (var spelling in this._spellings.Values)
        this.PanicFree(spelling);
      var errors = this.ErrorFile();
      if (errors is not null)
        this._uses.Add("crate::" + TrapErrorType);
      if (this.Results && this.PlyErrors)
        items.AddRange(this.ErrorDisplay());
      items.InsertRange(0, this._uses.Select(path => new RsUse(path)));
      items.AddRange(this._enumTableItems);
      items.AddRange(this._whitespace.Values);
      items.AddRange(this._regexTables);
      items.AddRange(this.BigItems());
      items.AddRange(this._spellings.Values);
      items.AddRange(this._iteratorItems);
      items.AddRange(functions);
      if (unit is not null)
        items.Add(unit with { Items = [.. unit.Items, .. tests] });

      var helpers = module.Implementations.Select(pair => ImplementationSources.Helper(this._functions[pair.Function], pair.Implementation, "")).ToList();
      helpers.AddRange(this._spellings.Keys.Select(name => new HelperText(name, name, EmittedHelper.OwnLicence, EmittedHelper.OriginOf(BigInts.Names.Contains(name) ? BigInts.Origin : Folder), "")));
      this.Helpers = helpers;

      var header = new List<string> {
        $"{package}: generated by polypiler. Change the source, not this file.",
        $"Rust profile: {this._profile}."
      };
      if (EmittedHelper.HeaderLine(helpers.Select(helper => helper.Licence)) is { } licences)
        header.Add(licences);
      if (this._bigInts)
        header.Add(BigInts.Notice);

      header.AddRange(FloatManifest.Lines(module, this._options.Mode, "rust"));
      header.AddRange(module.Caveats.Select(caveat => $"Caveat: {caveat}."));
      header.AddRange(ImplementationSources.Notices(module.Implementations.Select(pair => pair.Implementation).Concat(module.Inlined)));
      // The unsafe ledger: each use of unsafe code, the function that needed it and why; the crate then denies
      // unsafe code instead of forbidding it, so that the one spelling function may allow it.
      header.AddRange(this._unsafe.Select(use => $"Unsafe code: {use.Helper}, for the {use.Construct} in the function '{use.Function}'; the crate passes Miri."));
      RsItem[] declarations = errors is null ? [new RsModuleDeclaration(moduleName, "pub")]
        // The modules in the order of their names, as rustfmt's reorder_modules sorts them.
        : [.. new[] { new RsModuleDeclaration("error", ""), new RsModuleDeclaration(moduleName, "pub") }.OrderBy(declaration => declaration.Name, StringComparer.Ordinal), new RsUse("error::" + TrapErrorType) { Visibility = "pub" }];
      var root = new RsFile(header, [this._unsafe.Count > 0 ? "deny(unsafe_code)" : "forbid(unsafe_code)"], declarations);
      var file = new RsFile([], [], items);
      var crate = new RustCrate(package, Manifest(package, this._options.PackageVersion, this._profile, this._capabilities), root, moduleName, file) { Errors = errors };
      this.Place(crate);
      return crate;
    }

    /// <summary>The module of each function placed in one (<see cref="RustEmitOptions.Modules"/>), by the function's Rust name.</summary>
    public IReadOnlyDictionary<string, string> Placement { get; private set; } = new Dictionary<string, string>();

    /// <summary>Whether what stays of the IR module is written in the crate root (<see cref="RustEmitOptions.CrateRoot"/>).</summary>
    public bool InRoot { get; private set; }

    /// <summary>
    /// Finds the module of every placed function - a test stays with the tests - and refuses a module
    /// below the root named <c>lib</c> or <c>main</c>, whose file is the crate's root or a program's.
    /// </summary>
    private void Place(RustCrate crate) {
      this._construct = "the modules";
      this.InRoot = this._options.CrateRoot || crate.ModuleName is "lib" or "main";
      var placement = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (function, module) in this._options.Modules.Where(pair => this._functions.ContainsKey(pair.Key) && !this._options.TestFunctions.Contains(pair.Key))) {
        var path = PlacedPath(module, this._profile);
        if (path.Split("::")[0] is "lib" or "main")
          this.Refuse($"the module name '{path.Split("::")[0]}'");
        else
          placement[this._functions[function]] = path;
      }

      this.Placement = placement;
    }

    /// <summary>
    /// Names the functions and constants of the module. Exported functions keep the name the
    /// conversion gives them: two that collide are refused. Private ones and the constants take a
    /// suffix where they would collide.
    /// </summary>
    private void NameItems(IrModule module) {
      var taken = new HashSet<string>(StringComparer.Ordinal);
      var exported = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var function in module.Functions.Where(function => function.IsPublic && !IrGenerics.IsInstance(function.Name))) {
        var name = RustNames.Snake(function.Name, this.Edition);
        if (exported.TryGetValue(name, out var other)) {
          this._problems.Add(ExportCollisionDescriptor.At($"exported names '{other}' and '{function.Name}' both map to '{name}'"));
          continue;
        }

        exported[name] = function.Name;
        taken.Add(name);
        this._functions[function.Name] = name;
      }

      // A colliding export, refused above, is named like a private function so that the rest can still be checked.
      var others = module.Functions.Where(function => !this._functions.ContainsKey(function.Name)).ToList();
      foreach (var (function, rust) in others.Zip(RustNames.Unique([.. others.Select(function => function.Name)], name => this.FunctionName(others.First(other => other.Name == name)), taken)))
        this._functions[function.Name] = rust;

      // A function in a module of its own is called by its name in it, util_bits_mix in util::bits by mix, where no other function of the crate has that name.
      var names = this._functions.Values.ToHashSet(StringComparer.Ordinal);
      foreach (var function in module.Functions)
        if (this._options.Modules.TryGetValue(function.Name, out var path) && Unprefixed(this._functions[function.Name], PlacedPath(path, this._profile)) is { } own && names.Add(own)) {
          names.Remove(this._functions[function.Name]);
          this._functions[function.Name] = own;
        }

      var constants = module.Constants.Select(constant => constant.Name).ToList();
      foreach (var (name, rust) in constants.Zip(RustNames.Unique(constants, name => RustNames.Screaming(name, this.Edition), new HashSet<string>(StringComparer.Ordinal))))
        this._constants[name] = rust;
    }

    /// <summary>
    /// The name of a function that is no export: its own in snake case; an instantiation of a generic
    /// method, <c>Max&lt;f64&gt;</c>, the method's with its type arguments after it, <c>max_f64</c>.
    /// </summary>
    private string FunctionName(IrFunction function) {
      if (function.Origin is not { } origin || !IrGenerics.IsInstance(function.Name))
        return RustNames.Snake(function.Name, this.Edition);

      var arguments = origin.TypeArguments.Select(type => TypeName(type) ?? RustNames.Snake(type.Declaration?.Name[(type.Declaration.Name.LastIndexOf('.') + 1)..] ?? type.ToString(), this.Edition).Replace("r#", "", StringComparison.Ordinal));
      return RustNames.Snake(IrGenerics.SourceName(function.Name), this.Edition) + "_" + string.Join("_", arguments);
    }

    /// <summary>A constant of the module: a scalar, or a table as an array constant.</summary>
    private RsConst Constant(IrModuleConst constant) {
      this._construct = $"the constant '{constant.Name}'";
      var name = this._constants[constant.Name];
      if (constant.Type.IsArray && constant.Type.Element is { } element && TypeName(element) is { } elementName && constant.Strings.Count == 0)
        return new RsConst(name, "", $"[{elementName}; {constant.Values.Count}]", new RsArray([.. constant.Values.Select(value => Literal(new IrConst(element, value)))])) { IsStatic = true };

      if (TypeName(constant.Type) is { } typeName && constant.Strings.Count == 0 && constant.Values.Count == 1)
        return new RsConst(name, "", typeName, Literal(new IrConst(constant.Type, constant.Values[0])));

      // Text the crate keeps as Rust's own: its code units are taken where it is read.
      if (constant.Type.IsString && constant.Strings.Count == 1 && !HasLoneSurrogate(constant.Strings[0]))
        return new RsConst(name, "", "&str", new RsString(constant.Strings[0])) { IsStatic = true };
      if (constant.Type is { IsArray: true, Element.IsString: true } && !constant.Strings.Any(HasLoneSurrogate))
        return new RsConst(name, "", $"[&str; {constant.Strings.Count}]", new RsArray([.. constant.Strings.Select(text => (RsExpression)new RsString(text))])) { IsStatic = true };

      this.Refuse($"constant of type {constant.Type}");
      return new RsConst(name, "", "()", new RsPath("()"));
    }

    /// <summary>The Rust type of an IR type a parameter, a local or a result has: a scalar or a struct; refused for any other.</summary>
    private string Type(IrType type)
      => this._standIns.GetValueOrDefault(type) ?? TypeName(type) ?? (type.IsString ? OwnedString
        : type.Declaration is IrStructType or IrEnumType && this._types.TryGetValue(type.Declaration, out var name) ? name
        : IsError(type) ? ErrorType
        : type.IsObject ? this.ObjectType(type, false)
        : type.IsFunction ? this.FunctionType(type)
        : IsOptionalValue(type) ? $"Option<{this.Type(type.Underlying!)}>"
        : type.IsBigInt ? this.BigType()
        : ((RsPath)this.Refuse($"the type {type}")).Name);

    /// <summary>A spelling function the crate carries once: an operation of the IR Rust has no method for.</summary>
    private void Spell(string name, Func<RsFunction> build) {
      if (!this._spellings.ContainsKey(name))
        this._spellings[name] = build() with { IsHelper = true };
    }

  }

}
