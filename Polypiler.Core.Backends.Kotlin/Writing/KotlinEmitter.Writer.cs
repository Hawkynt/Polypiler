using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Passes;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using static Polypiler.Core.Backends.Kotlin.Syntax.Kt;

namespace Polypiler.Core.Backends.Kotlin;

public static partial class KotlinEmitter {

  /// <summary>Builds the syntax tree of one module; what Kotlin cannot carry is collected in <see cref="Problems"/>.</summary>
  private sealed partial class Writer(KotlinProfile profile, KotlinCapabilities capabilities, FloatMode mode) {

    private readonly List<Diagnostic> _problems = [];
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly HashSet<string> _helpers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrFunction> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);

    /// <summary>Every name at the level of the file: functions, constants, globals, helpers and types, which no local takes.</summary>
    private readonly HashSet<string> _fileNames = new(StringComparer.Ordinal);

    private string _function = "";

    /// <summary>Whether a parameter is rebound as a variable of the same name, which Kotlin warns about unless the file suppresses it.</summary>
    private bool _shadows;

    /// <summary>What the profile cannot express of the module written; empty when it can express all of it.</summary>
    public IReadOnlyList<Diagnostic> Problems => this._problems;

    /// <summary>The helpers and library functions the file carries, their texts still empty; none before <see cref="Module"/> ran.</summary>
    public IReadOnlyList<HelperText> Helpers { get; private set; } = [];

    /// <summary>Whether the profile has a construct.</summary>
    private bool Has(string capability) => capabilities.Has(capability, profile);

    /// <summary>Reports a construct the profile cannot express, once per construct and function.</summary>
    private void Refuse(DiagnosticDescriptor descriptor, string message) {
      var located = this._function.Length == 0 ? message : $"{message} (function '{this._function}')";
      if (this._refused.Add(descriptor.Code + located))
        this._problems.Add(descriptor.At(located));
    }

    /// <summary>Reports an operation the profile cannot express; stands in for it with <c>TODO()</c>, which nothing prints.</summary>
    private KtExpression Inexpressible(string what) {
      this.Refuse(InexpressibleDescriptor, $"'{what}' cannot be expressed in Kotlin profile '{profile}'");
      return Call("TODO");
    }

    /// <summary>The name of an IR node's kind, without its <c>Ir</c> prefix.</summary>
    private static string KindOf(object node) => node.GetType().Name[2..];

    /// <summary>Names a helper of the backend the file needs and gives its call.</summary>
    private KtCall Helper(string name, params IReadOnlyList<KtExpression> arguments) {
      this._helpers.Add(name);
      return Call(name, arguments);
    }

    public KtFile Module(IrModule module) {
      this.Reserve();
      this.NameFunctions(module);
      foreach (var constant in module.Constants)
        this._constants[constant.Name] = this.Take(Fallback(KotlinNaming.Constant(constant.Name), "CONSTANT"));

      foreach (var global in module.Globals)
        this._globals[global.Name] = this.Take(Fallback(KotlinNaming.Camel(global.Name), "global"));

      var types = this.Types(module);
      foreach (var foreign in module.ForeignFunctions)
        this.Refuse(InexpressibleDescriptor, $"the native function '{foreign.Name}' has no Kotlin form: the JVM reaches native code only through JNI, which the backend does not write");

      if (module.Initializer is { } initializer)
        this.Refuse(ControlUnsupportedDescriptor, $"the initializer '{initializer}' of the module has no Kotlin form yet");

      var copied = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
      var functions = new List<KtDeclaration>();
      foreach (var function in module.Functions) {
        var written = this.Function(function);
        functions.Add(copied.Contains(function.Name) ? written with { IsHelper = true } : written);
      }

      this._function = "";
      var constants = module.Constants.Select(this.Constant).ToList();
      var globals = module.Globals.Select(this.Global).ToList();

      var helpers = new List<HelperText>();
      var declarations = new List<KtDeclaration>();
      declarations.AddRange(this.ExceptionClasses());
      declarations.AddRange(types);
      declarations.AddRange(constants);
      declarations.AddRange(globals);
      declarations.AddRange(functions);
      foreach (var name in KotlinHelpers.Names.Where(this._helpers.Contains)) {
        declarations.Add(KotlinHelpers.Declaration(name));
        var (licence, origin) = HelperOrigins[name];
        helpers.Add(new(name, name, licence, origin, ""));
      }

      helpers.AddRange(module.Implementations.Select(pair => ImplementationSources.Helper(this._functions[pair.Function], pair.Implementation, "")));
      this.Helpers = helpers;

      var header = new List<string> {
        $"{module.Name}: generated by polypiler. Change the source, not this file.",
        $"Kotlin profile: {profile.Name}, JVM target {profile.JvmTarget}."
      };
      if (EmittedHelper.HeaderLine(helpers.Select(helper => helper.Licence)) is { } licences)
        header.Add(licences);

      header.AddRange(FloatManifest.Lines(module, mode, Runtime));
      header.AddRange(ImplementationSources.Notices(module.Implementations.Select(pair => pair.Implementation).Concat(module.Inlined)));
      return new KtFile(header, PackageOf(module), [], declarations) { Suppressions = this._shadows ? ["NAME_SHADOWING"] : [] };
    }

    /// <summary>The Kotlin name of every function, by its name in the IR.</summary>
    public IReadOnlyDictionary<string, string> Names(IrModule module) {
      this.Reserve();
      this.NameFunctions(module);
      return this._functions;
    }

    /// <summary>Takes the names of the helpers, which nothing of the module may have.</summary>
    private void Reserve() => this._fileNames.UnionWith(KotlinHelpers.Names);

    private static string Fallback(string name, string otherwise) => name.Length == 0 ? otherwise : name;

    /// <summary>A name at the level of the file that no other name there has.</summary>
    private string Take(string wanted) {
      var name = KotlinNaming.Free(wanted, this._fileNames.Contains);
      this._fileNames.Add(name);
      return name;
    }

    /// <summary>
    /// The Kotlin name of every function. Public functions keep their lowerCamelCase name, which two of
    /// them may not share (<see cref="NameCollision"/>); a private one takes the next free name.
    /// </summary>
    private void NameFunctions(IrModule module) {
      foreach (var function in module.Functions)
        this._byName[function.Name] = function;

      foreach (var group in module.Functions.Where(function => function.IsPublic).GroupBy(function => KotlinNaming.Camel(function.Name), StringComparer.Ordinal)) {
        var first = group.First();
        if (group.Key.Length == 0)
          this.Refuse(NameCollisionDescriptor, $"export '{first.Name}' has no valid Kotlin name");
        else if (group.Skip(1).FirstOrDefault() is { } second)
          this.Refuse(NameCollisionDescriptor, $"exports '{first.Name}' and '{second.Name}' both map to the Kotlin name '{group.Key}'");

        // A refused name still names the function, so that the rest of the module can be checked.
        var name = group.Key.Length == 0 || this._fileNames.Contains(group.Key) ? this.Take(Fallback(group.Key, "function")) : group.Key;
        foreach (var function in group)
          this._functions[function.Name] = name;

        this._fileNames.Add(name);
      }

      foreach (var function in module.Functions.Where(function => !function.IsPublic))
        this._functions[function.Name] = this.Take(Fallback(KotlinNaming.Camel(function.Name), "function"));
    }

    /// <summary>The Kotlin type of an IR type; <c>Any</c> after reporting a type Kotlin cannot carry.</summary>
    private KtType TypeOf(IrType type) {
      if (KotlinTypes.Scalar(type) is { } scalar && !type.IsEnum)
        return Type(scalar);

      switch (type) {
        case { IsString: true }:
          return Type("String");
        case { IsArray: true, Element: { } element } when KotlinTypes.PrimitiveArray(element) is { } array && !element.IsEnum:
          return Type(array);
        case { IsArray: true, Element: { } element }:
          return Generic("Array", this.TypeOf(element));
        case { IsStruct: true, Declaration: { } declaration } when this._typeNames.TryGetValue(declaration, out var name):
          return Type(name);
        case { IsEnum: true, Declaration: { } declaration } when this._typeNames.TryGetValue(declaration, out var name):
          return Type(name);
        default:
          this.Refuse(TypeUnsupportedDescriptor, $"type '{type}' has no Kotlin representation in profile '{profile}'");
          return Type("Any");
      }
    }

    /// <summary>A constant of the module: <c>private const val</c> for a value, a <c>private val</c> primitive array for a table.</summary>
    private KtDeclaration Constant(IrModuleConst constant) {
      var name = this._constants[constant.Name];
      if (constant.Type.IsString)
        return new KtProperty(false, name, Type("String"), new KtString(constant.Strings.Count > 0 ? constant.Strings[0] : "")) { Modifiers = ["private", "const"] };

      if (constant.Type.Element is { IsString: true }) {
        var strings = constant.Strings.Select(value => (KtExpression)new KtString(value)).ToList();
        return new KtProperty(false, name, Generic("Array", Type("String")), Call("arrayOf", strings)) { Modifiers = ["private"] };
      }

      if (constant.Type.Element is not { } element) {
        var value = constant.Values.Count > 0 ? constant.Values[0] : 0;
        return new KtProperty(false, name, this.TypeOf(constant.Type), this.ValueOf(constant.Type, value, typed: true)) { Modifiers = ["private", "const"] };
      }

      var arrayType = this.TypeOf(constant.Type);
      return new KtProperty(false, name, arrayType, this.ArrayOf(element, [.. constant.Values.Select(value => (IrExpr)new IrConst(element, value))])) { Modifiers = ["private"] };
    }

    /// <summary>
    /// A global of the module: a private property of the file, initialised when the file's class is
    /// loaded, in source order, before any function of the file runs.
    /// </summary>
    private KtDeclaration Global(IrGlobal global) {
      this.BeginFunction("the module", null);
      var value = global.Initializer is { } initializer ? this.Expression(initializer) : this.Zero(global.Type);
      return new KtProperty(true, this._globals[global.Name], this.TypeOf(global.Type), value) { Modifiers = ["private"] };
    }

    /// <summary>
    /// A function with its parameters and result typed: an expression body where the body is one
    /// <c>return</c>, otherwise a block; a trailing <c>return</c> of a function without result is left
    /// out. A parameter the body assigns is rebound first, <c>var a = a</c>.
    /// </summary>
    private KtFunction Function(IrFunction function) {
      this.BeginFunction(function.Name, function);
      var parameters = new List<KtParameter>();
      var body = new List<KtStatement>();
      foreach (var parameter in function.Parameters) {
        var parameterName = this.LocalName(parameter);
        this._declared.Add(parameter);
        parameters.Add(new KtParameter(parameterName, this.TypeOf(parameter.Type)));
        if (this._assigned.Contains(parameter)) {
          this._shadows = true;
          body.Add(new KtVariable(true, parameterName, null, Name(parameterName)));
        }
      }

      this.Statements(function.Body, body);
      if (function.ReturnType == IrType.Void && body.Count > 0 && body[^1] is KtReturn { Value: null })
        body.RemoveAt(body.Count - 1);

      var result = function.ReturnType == IrType.Void ? null : this.TypeOf(function.ReturnType);
      var name = this._functions[function.Name];
      string[] modifiers = function.IsPublic ? [] : ["private"];
      if (body is [KtReturn { Value: { } single }])
        return new KtFunction(name, parameters, result, null) { Expression = single, Modifiers = modifiers };

      return new KtFunction(name, parameters, result, new KtBlock(body)) { Modifiers = modifiers };
    }

  }

}
