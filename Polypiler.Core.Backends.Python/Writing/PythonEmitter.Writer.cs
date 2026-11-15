using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Passes;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  /// <summary>Builds the syntax tree of one module; what Python cannot carry is collected in <see cref="Problems"/>.</summary>
  private sealed partial class Writer(PythonProfile profile, PythonCapabilities capabilities, FloatMode mode) {

    private readonly List<Diagnostic> _problems = [];
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly HashSet<string> _helpers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);

    /// <summary>Every name at the level of the module: functions, constants, globals, helpers and imports, which no local takes.</summary>
    private readonly HashSet<string> _moduleNames = new(StringComparer.Ordinal);

    private string _function = "";

    /// <summary>What the profile cannot express of the module written; empty when it can express all of it.</summary>
    public IReadOnlyList<Diagnostic> Problems => this._problems;

    /// <summary>The helpers and library functions the module carries, their texts still empty; none before <see cref="Module"/> ran.</summary>
    public IReadOnlyList<HelperText> Helpers { get; private set; } = [];

    /// <summary>Whether the profile has a construct.</summary>
    private bool Has(string capability) => capabilities.Has(capability, profile);

    /// <summary>Reports a construct the profile cannot express, once per construct and function.</summary>
    private void Refuse(DiagnosticDescriptor descriptor, string message) {
      var located = this._function.Length == 0 ? message : $"{message} (function '{this._function}')";
      if (this._refused.Add(descriptor.Code + located))
        this._problems.Add(descriptor.At(located));
    }

    /// <summary>Reports an operation the profile cannot express.</summary>
    private PyExpression Inexpressible(string what) {
      this.Refuse(InexpressibleDescriptor, $"'{what}' cannot be expressed in Python profile '{profile.Name}'");
      return new PyNone();
    }

    /// <summary>The name of an IR node's kind, without its <c>Ir</c> prefix.</summary>
    private static string KindOf(object node) => node.GetType().Name[2..];

    public PyModule Module(IrModule module) {
      this.Reserve();
      this.NameFunctions(module);
      foreach (var constant in module.Constants)
        this._constants[constant.Name] = this.Take(PythonNaming.Constant(constant.Name));

      foreach (var global in module.Globals)
        this._globals[global.Name] = this.Take(PythonNaming.Global(global.Name));

      var copied = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
      var functions = new List<PyStatement>();
      foreach (var function in module.Functions) {
        var written = this.Function(function);
        functions.Add(copied.Contains(function.Name) ? written with { IsHelper = true } : written);
      }

      this._function = "";
      var constants = module.Constants.Select(this.Constant).ToList();
      var globals = module.Globals.Select(this.Global).ToList();

      var body = new List<PyStatement>();
      var exported = module.Functions.Where(function => function.IsPublic).Select(function => this._functions[function.Name]).Order(StringComparer.Ordinal);
      body.Add(new PyAssign(new PyName("__all__"), new PyList([.. exported.Select(name => new PyString(name))])) { Leading = PyTrivia.Blank });
      var helpers = new List<HelperText>();
      foreach (var name in PythonHelpers.Order.Where(this._helpers.Contains)) {
        body.Add(PythonHelpers.Declaration(name) with { Leading = PyTrivia.TwoBlank });
        var (licence, origin) = HelperOrigins[name];
        helpers.Add(new(name, name, licence, origin, ""));
      }

      helpers.AddRange(module.Implementations.Select(pair => ImplementationSources.Helper(this._functions[pair.Function], pair.Implementation, "")));
      this.Helpers = helpers;
      body.AddRange(constants.Select((constant, index) => constant with { Leading = index == 0 ? PyTrivia.TwoBlank : PyTrivia.None }));
      body.AddRange(functions.Select(function => function with { Leading = PyTrivia.TwoBlank }));
      body.AddRange(globals.Select((global, index) => global with { Leading = index == 0 ? PyTrivia.TwoBlank : PyTrivia.None }));

      // The imports are those of the standard library the module names, in sorted order (PEP 8).
      var names = body.SelectMany(PythonTargetPasses.Names).ToHashSet(StringComparer.Ordinal);
      var imports = new List<PyStatement>();
      foreach (var library in new[] { "math", "struct" }.Where(names.Contains))
        imports.Add(new PyImport(library) { Leading = imports.Count == 0 ? PyTrivia.Blank : PyTrivia.None });

      if (names.Contains("List"))
        imports.Add(new PyImportFrom("typing", ["List"]) { Leading = imports.Count == 0 ? PyTrivia.Blank : PyTrivia.None });

      var header = new List<string> {
        $"{module.Name}: generated by polypiler. Change the source, not this file.",
        $"Python profile: {profile.Name}."
      };
      if (EmittedHelper.HeaderLine(helpers.Select(helper => helper.Licence)) is { } licences)
        header.Add(licences);

      header.AddRange(FloatManifest.Lines(module, mode, Runtime));
      header.AddRange(ImplementationSources.Notices(module.Implementations.Select(pair => pair.Implementation).Concat(module.Inlined)));
      return new PyModule(header, [.. imports, .. body]);
    }

    /// <summary>The Python name of every function, by its name in the IR.</summary>
    public IReadOnlyDictionary<string, string> Names(IrModule module) {
      this.Reserve();
      this.NameFunctions(module);
      return this._functions;
    }

    /// <summary>Takes the names of the helpers and of what the module imports, which nothing of the module may have.</summary>
    private void Reserve() {
      this._moduleNames.UnionWith(PythonHelpers.Order);
      this._moduleNames.UnionWith(["math", "struct", "List", "__all__"]);
    }

    /// <summary>A name at the level of the module that no other name there has.</summary>
    private string Take(string wanted) {
      var name = PythonNaming.Free(wanted, this._moduleNames.Contains);
      this._moduleNames.Add(name);
      return name;
    }

    /// <summary>
    /// The Python name of every function. Public functions keep their snake_case name, which two of
    /// them may not share (<see cref="NameCollision"/>); a private one takes the next free name.
    /// </summary>
    private void NameFunctions(IrModule module) {
      foreach (var group in module.Functions.Where(function => function.IsPublic).GroupBy(function => PythonNaming.Function(function.Name, true), StringComparer.Ordinal)) {
        var first = group.First();
        if (group.Key.Length == 0)
          this.Refuse(NameCollisionDescriptor, $"export '{first.Name}' has no valid Python name");
        else if (group.Skip(1).FirstOrDefault() is { } second)
          this.Refuse(NameCollisionDescriptor, $"exports '{first.Name}' and '{second.Name}' both map to the Python name '{group.Key}'");

        // A refused name still names the function, so that the rest of the module can be checked.
        var name = group.Key.Length == 0 ? this.Take("_function") : group.Key;
        foreach (var function in group)
          this._functions[function.Name] = name;

        this._moduleNames.Add(name);
      }

      foreach (var function in module.Functions.Where(function => !function.IsPublic)) {
        var wanted = PythonNaming.Function(function.Name, false);
        this._functions[function.Name] = this.Take(wanted.Length == 0 ? "_function" : wanted);
      }
    }

    /// <summary>The annotation of a type, or <see langword="null"/> after reporting a type Python cannot carry.</summary>
    private PyExpression Annotation(IrType type) {
      switch (type) {
        case { Kind: IrTypeKind.Void }:
          return new PyNone();
        case { Kind: IrTypeKind.Bool }:
          return new PyName("bool");
        case { IsInteger: true, IsEnum: false }:
          return new PyName("int");
        case { IsFloat: true }:
          return new PyName("float");
        case { IsArray: true, Element: { IsNumeric: true, IsEnum: false } element }:
          var inner = this.Annotation(element);
          return this.Has(PythonCapabilities.BuiltinGenerics) ? new PySubscript(new PyName("list"), inner) : new PySubscript(new PyName("List"), inner);
        default:
          this.Refuse(TypeUnsupportedDescriptor, $"type '{type}' has no Python representation in profile '{profile.Name}'");
          return new PyName("object");
      }
    }

    /// <summary>A constant of the module: a value, or a table as a tuple, eight elements to a line.</summary>
    private PyStatement Constant(IrModuleConst constant) {
      var name = this._constants[constant.Name];
      if (constant.Type.Element is not { } element)
        return new PyAssign(new PyName(name), this.Literal(constant.Type, constant.Values.Count > 0 ? constant.Values[0] : 0)) { Annotation = this.Annotation(constant.Type) };

      this.Annotation(constant.Type);
      return new PyAssign(new PyName(name), new PyTuple([.. constant.Values.Select(value => this.Literal(element, value))]) { Tabulated = true });
    }

    /// <summary>
    /// A global of the module, assigned after every function: its initializer may call a function of
    /// the module, which reads no global (<see cref="IrGlobal"/>), so the order cannot be observed.
    /// </summary>
    private PyStatement Global(IrGlobal global) {
      this.BeginFunction("the module");
      var value = global.Initializer is { } initializer ? this.Expression(initializer) : this.Zero(global.Type);
      return new PyAssign(new PyName(this._globals[global.Name]), value) { Annotation = this.Annotation(global.Type) };
    }

    /// <summary>The zero of a number type or <c>False</c>.</summary>
    private PyExpression Zero(IrType type) => type.IsFloat ? new PyFloat(0.0) { Type = type } : type == IrType.Bool ? new PyBool(false) { Type = type } : new PyInt(0) { Type = type };

    /// <summary>A function with its parameters and result typed; a trailing <c>return</c> of a function without result is left out.</summary>
    private PyFunctionDef Function(IrFunction function) {
      this.BeginFunction(function.Name);
      var parameters = new List<PyParameter>();
      foreach (var parameter in function.Parameters) {
        var name = this.LocalName(parameter);
        this._annotated.Add(name);
        parameters.Add(new PyParameter(name, this.Annotation(parameter.Type)));
      }

      foreach (var local in function.Locals)
        this.LocalName(local);

      var returns = this.Annotation(function.ReturnType);
      var body = this.Statements(function.Body.Statements);
      if (function.ReturnType == IrType.Void && body.Count > 0 && body[^1] is PyReturn { Value: null })
        body.RemoveAt(body.Count - 1);

      if (this._assignedGlobals.Count > 0)
        body.Insert(0, new PyGlobal([.. this._assignedGlobals.Order(StringComparer.Ordinal)]));

      return new PyFunctionDef(this._functions[function.Name], parameters, returns, body);
    }

  }

}
