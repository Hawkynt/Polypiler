using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic.Net;

/// <summary>
/// Builds the syntax tree of the Visual Basic .NET file of one module; what the profile cannot carry is
/// collected in <see cref="Problems"/>. The file is compiled with <c>RemoveIntegerChecks</c>, so the
/// IR's wrapping arithmetic and conversions are Visual Basic's own operators and conversion functions
/// and its checked ones go through helpers that throw an <c>OverflowException</c>.
/// </summary>
internal sealed partial class VbNetWriter(VisualBasicProfile profile, VbNetCapabilities capabilities, FloatMode mode) {

  private readonly List<Diagnostic> _problems = [];
  private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
  private readonly HashSet<string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, string> _functions = new(StringComparer.Ordinal);
  private readonly Dictionary<IrTypeDeclaration, string> _typeNames = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrField, string> _fieldNames = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);
  private string _function = "";

  /// <summary>What the profile cannot express of the module written; empty when it can express all of it.</summary>
  public IReadOnlyList<Diagnostic> Problems => this._problems;

  /// <summary>The helpers the module carries, their texts still empty.</summary>
  public IReadOnlyList<HelperText> Helpers { get; private set; } = [];

  /// <summary>Whether the profile has a construct of the table.</summary>
  private bool Has(string capability) => capabilities.Has(capability, profile);

  private void Refuse(DiagnosticDescriptor descriptor, string message) {
    var located = this._function.Length == 0 ? message : $"{message} (function '{this._function}')";
    if (this._refused.Add(descriptor.Code + located))
      this._problems.Add(descriptor.At(located));
  }

  /// <summary>Reports an operation the profile cannot express and stands in a placeholder for it.</summary>
  private VnExpression Inexpressible(string what) {
    this.Refuse(VisualBasicEmitter.InexpressibleDescriptor, $"'{what}' cannot be expressed in Visual Basic profile '{profile.Name}'");
    return new VnLiteral("0");
  }

  private void ControlRefused(string what) => this.Refuse(VisualBasicEmitter.ControlUnsupportedDescriptor, $"'{what}' has no form in Visual Basic profile '{profile.Name}'");

  /// <summary>A name at the level of the module that no other name there has, compared without case.</summary>
  private string Take(string wanted) {
    var name = VbNetNaming.Free(wanted, this._moduleNames.Contains);
    this._moduleNames.Add(name);
    return name;
  }

  public VnModule Module(IrModule module) {
    this._moduleNames.UnionWith(VbNetNaming.Reserved);
    var moduleName = VbNetNaming.Valid(module.Name, "Module");
    this._moduleNames.Add(moduleName);
    foreach (var foreign in module.ForeignFunctions)
      this.Refuse(VisualBasicEmitter.InexpressibleDescriptor, $"the foreign function '{foreign.Name}' cannot be called from Visual Basic profile '{profile.Name}'");

    var structures = this.Types(module);
    this.NameFunctions(module);
    foreach (var constant in module.Constants)
      this._constants[constant.Name] = this.Take(VbNetNaming.Valid(constant.Name, "Table"));
    foreach (var global in module.Globals)
      this._globals[global.Name] = this.Take(VbNetNaming.Valid(global.Name, "Global"));

    var members = new List<VnDeclaration>(structures);
    foreach (var constant in module.Constants)
      members.Add(this.Constant(constant));
    this.BeginFunction("the module", []);
    foreach (var global in module.Globals)
      members.Add(new VnField("Private", this._globals[global.Name], this.TypeName(global.Type), global.Initializer is null ? this.Default(global.Type) : this.Expression(global.Initializer)));
    if (module.Initializer is { } initializer)
      members.Add(new VnMethod("", "New", [], null, [new VnCallStatement(new VnInvoke(new VnName(this._functions[initializer]), []))]));

    var copied = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
    var helpers = new List<HelperText>();
    foreach (var function in module.Functions) {
      members.Add(this.Function(function));
      if (copied.Contains(function.Name))
        helpers.Add(ImplementationSources.Helper(this._functions[function.Name], module.Implementations.First(pair => pair.Function == function.Name).Implementation, ""));
    }

    this._function = "";
    foreach (var helper in this.HelperMethods()) {
      members.Add(helper);
      if (!helpers.Any(known => known.Name == helper.Name))
        helpers.Add(new(helper.Name, helper.Name, EmittedHelper.OwnLicence, EmittedHelper.OriginOf(VisualBasicHelpers.Folder), ""));
    }

    this.Helpers = helpers;
    var header = new List<string> {
      $"{module.Name}: generated by polypiler. Change the source, not this file.",
      $"Visual Basic profile: {profile.Name} ({profile.Title}), compiled with RemoveIntegerChecks."
    };
    if (EmittedHelper.HeaderLine(helpers.Select(helper => helper.Licence)) is { } licences)
      header.Add(licences);

    header.AddRange(FloatManifest.Lines(module, mode, VisualBasicEmitter.NetRuntime));
    header.AddRange(ImplementationSources.Notices(module.Implementations.Select(pair => pair.Implementation).Concat(module.Inlined)));
    return new VnModule(moduleName, header, members);
  }

  private void NameFunctions(IrModule module) {
    foreach (var group in module.Functions.Where(function => function.IsPublic).GroupBy(function => VbNetNaming.Valid(function.Name, "Function"), StringComparer.OrdinalIgnoreCase)) {
      var first = group.First();
      if (group.Skip(1).FirstOrDefault() is { } second)
        this.Refuse(VisualBasicEmitter.NameCollisionDescriptor, $"exports '{first.Name}' and '{second.Name}' both map to the Visual Basic name '{group.Key}'");

      var name = this.Take(group.Key);
      foreach (var function in group)
        this._functions[function.Name] = name;
    }

    foreach (var function in module.Functions.Where(function => !function.IsPublic))
      this._functions[function.Name] = this.Take(VbNetNaming.Valid(function.Name, "Function"));
  }

  /// <summary>The structures of the module's structs, each with a constructor of every field; an enum is its underlying integer.</summary>
  private List<VnDeclaration> Types(IrModule module) {
    foreach (var declaration in module.Types) {
      if (declaration is IrStructType)
        this._typeNames[declaration] = this.Take(VbNetNaming.Valid(declaration.Name, "Record"));
      else if (declaration is not IrEnumType)
        this.Refuse(VisualBasicEmitter.TypeUnsupportedDescriptor, $"the type '{declaration.Name}' ({declaration.GetType().Name[2..]}) has no Visual Basic representation in profile '{profile.Name}'");
    }

    var structures = new List<VnDeclaration>();
    foreach (var type in module.Types.OfType<IrStructType>()) {
      var name = this._typeNames[type];
      var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
      var members = new List<VnDeclaration>();
      foreach (var field in type.Fields) {
        var fieldName = VbNetNaming.Free(VbNetNaming.Valid(field.Name, "Field"), taken.Contains);
        taken.Add(fieldName);
        this._fieldNames[field] = fieldName;
        members.Add(new VnField(field.IsReadOnly ? "Public ReadOnly" : "Public", fieldName, this.TypeName(field.Type), null));
      }

      if (type.Fields.Count == 0)
        this.Refuse(VisualBasicEmitter.TypeUnsupportedDescriptor, $"the struct '{type.Name}' has no fields, which a Visual Basic structure must have");
      else
        members.Add(new VnMethod("Public", "New", [.. type.Fields.Select(field => new VnParameter("v" + VbNetNaming.Bare(this._fieldNames[field]), this.TypeName(field.Type)))], null,
          [.. type.Fields.Select(field => new VnAssign(new VnMember(new VnName("Me"), this._fieldNames[field]), "=", new VnName("v" + VbNetNaming.Bare(this._fieldNames[field]))))]));
      structures.Add(new VnStructure(name, members));
    }

    return structures;
  }

  /// <summary>The Visual Basic type of an IR type; <c>Integer</c> after reporting a type Visual Basic cannot carry.</summary>
  private string TypeName(IrType type) {
    switch (type) {
      case { IsStruct: true, Declaration: { } declaration } when this._typeNames.TryGetValue(declaration, out var name):
        return name;
      case { IsArray: true, Element: { } element }:
        return this.TypeName(element) + "()";
      case { IsString: true }:
        return "String";
      case { IsObject: true, Declaration: IrClassType { IsLibrary: true } library } when ExceptionName(library) is { } exception:
        return exception;
      case { Kind: IrTypeKind.Bool }:
        return "Boolean";
      default:
        if (Scalar(type) is { } scalar)
          return scalar;

        this.Refuse(VisualBasicEmitter.TypeUnsupportedDescriptor, $"type '{type}' has no Visual Basic representation in profile '{profile.Name}'");
        return "Integer";
    }
  }

  /// <summary>The Visual Basic name of a numeric type of the IR, an enum by its underlying integer; <see langword="null"/> for any other.</summary>
  internal static string? Scalar(IrType type) => type.Kind switch {
    IrTypeKind.I8 => "SByte",
    IrTypeKind.U8 => "Byte",
    IrTypeKind.I16 => "Short",
    IrTypeKind.U16 => "UShort",
    IrTypeKind.I32 => "Integer",
    IrTypeKind.U32 => "UInteger",
    IrTypeKind.I64 => "Long",
    IrTypeKind.U64 => "ULong",
    IrTypeKind.F32 => "Single",
    IrTypeKind.F64 => "Double",
    _ => null
  };

  /// <summary>The .NET name of a library exception class, or <see langword="null"/> for one .NET does not have as such.</summary>
  private static string? ExceptionName(IrClassType type)
    => type == IrExceptionClass.KeyNotFoundException ? "System.Collections.Generic.KeyNotFoundException"
      : type == IrExceptionClass.AssertionException ? null
      : "System." + type.Name;

  /// <summary>The value a variable of a type starts with.</summary>
  private VnExpression Default(IrType type) => type switch {
    { Kind: IrTypeKind.Bool } => new VnLiteral("False"),
    { IsStruct: true } => new VnNew(this.TypeName(type), []),
    _ when Scalar(type) is not null => Literal(type, 0),
    _ => new VnLiteral("Nothing")
  };

  /// <summary>A module constant: a scalar or a string as <c>Const</c> where its literal is constant, a table as a read-only array.</summary>
  private VnDeclaration Constant(IrModuleConst constant) {
    var name = this._constants[constant.Name];
    if (constant.Type.Element is not { } element) {
      var value = constant.Type.IsString ? this.Text(constant.Strings.Count > 0 ? constant.Strings[0] : "") : Literal(constant.Type, constant.Values.Count > 0 ? constant.Values[0] : 0);
      return new VnField(value is VnLiteral { IsConstant: false } or VnBinary ? "Private ReadOnly" : "Private Const", name, this.TypeName(constant.Type), value);
    }

    var elements = element.IsString ? constant.Strings.Select(this.Text).ToList() : constant.Values.Select(value => Literal(element, value)).ToList();
    return new VnField("Private ReadOnly", name, this.TypeName(constant.Type), new VnNewArray(this.TypeName(element), null, elements));
  }

  private VnMethod Function(IrFunction function) {
    this.BeginFunction(function.Name, [this._functions[function.Name]]);
    var parameters = function.Parameters.Select(parameter => new VnParameter(this.LocalName(parameter), this.TypeName(parameter.Type))).ToList();
    var body = this.Statements(function.Body);
    var isFunction = function.ReturnType != IrType.Void;
    if (isFunction && body is not [.., VnReturn or VnThrow])
      body.Add(new VnReturn(this.Default(function.ReturnType)));

    return new VnMethod(function.IsPublic ? "Public" : "Private", this._functions[function.Name], parameters, isFunction ? this.TypeName(function.ReturnType) : null, body);
  }

}
