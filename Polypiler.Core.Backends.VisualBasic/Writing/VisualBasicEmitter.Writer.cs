using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic;

public static partial class VisualBasicEmitter {

  /// <summary>Builds the syntax tree of one module; what Visual Basic cannot carry is collected in <see cref="Problems"/>.</summary>
  private sealed partial class Writer(VisualBasicProfile profile, VisualBasicCapabilities capabilities, FloatMode mode) {

    private readonly List<Diagnostic> _problems = [];
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

    /// <summary>Every name at the level of the module, compared without case as Visual Basic compares names.</summary>
    private readonly HashSet<string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrFunction> _functionsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<IrTypeDeclaration, string> _typeNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrField, string> _fieldNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _globals = new(StringComparer.Ordinal);

    /// <summary>The fixed helpers the module names.</summary>
    private readonly HashSet<string> _helpers = new(StringComparer.Ordinal);

    /// <summary>The array helpers the module names: per element type (its Visual Basic name and the suffix of the helpers), the prefixes used.</summary>
    private readonly SortedDictionary<string, (string Type, HashSet<string> Prefixes)> _arrayHelpers = new(StringComparer.Ordinal);

    /// <summary>The structs whose constructor, default or equality helper the module names.</summary>
    private readonly SortedDictionary<string, (IrStructType Type, HashSet<string> Kinds)> _structHelpers = new(StringComparer.Ordinal);

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

    /// <summary>Reports an operation the profile cannot express and stands in a placeholder for it.</summary>
    private VbExpression Inexpressible(string what) {
      this.Refuse(InexpressibleDescriptor, $"'{what}' cannot be expressed in Visual Basic profile '{profile.Name}'");
      return new VbInteger(0);
    }

    /// <summary>Reports a control construct Visual Basic has no form of.</summary>
    private void ControlRefused(string what) => this.Refuse(ControlUnsupportedDescriptor, $"'{what}' has no form in Visual Basic profile '{profile.Name}'");

    /// <summary>A name at the level of the module that no other name there has.</summary>
    private string Take(string wanted) {
      var name = VisualBasicNaming.Free(wanted, this._moduleNames.Contains);
      this._moduleNames.Add(name);
      return name;
    }

    /// <summary>Takes the helpers' names and the built-in names, which nothing of the module may have.</summary>
    private void Reserve() {
      this._moduleNames.UnionWith(VisualBasicNaming.Reserved);
      this._moduleNames.UnionWith(VisualBasicHelpers.Order);
      this._moduleNames.Add(InitName);
      this._moduleNames.Add(ReadyName);
    }

    private const string InitName = "PlyInit";
    private const string ReadyName = "PlyReady";

    public VbModule Module(IrModule module) {
      this.Reserve();
      var moduleName = VisualBasicNaming.Module(module.Name);
      this._moduleNames.Add(moduleName);
      foreach (var foreign in module.ForeignFunctions)
        this.Refuse(InexpressibleDescriptor, $"the foreign function '{foreign.Name}' cannot be called from Visual Basic profile '{profile.Name}'");

      var structs = this.Types(module);
      this.NameFunctions(module);
      foreach (var constant in module.Constants)
        this._constants[constant.Name] = this.Take(VisualBasicNaming.Valid(constant.Name, "Table"));
      foreach (var global in module.Globals)
        this._globals[global.Name] = this.Take(VisualBasicNaming.Valid(global.Name, "Global"));

      var copied = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
      var procedures = new List<VbProcedure>();
      var needsInit = module.Globals.Count > 0 || module.Constants.Any(constant => constant.Type.IsArray);
      foreach (var function in module.Functions) {
        var written = this.Function(function, needsInit && function.IsPublic);
        procedures.Add(copied.Contains(function.Name) ? written with { IsHelper = true } : written);
      }

      this._function = "";
      var declarations = new List<VbDeclaration> { new VbOption("Explicit") { Leading = VbTrivia.Blank } };
      declarations.AddRange(structs.Select((declaration, index) => declaration with { Leading = index == 0 ? VbTrivia.Blank : VbTrivia.None }));
      var moduleLevel = new List<VbDeclaration>();
      var init = new List<VbStatement>();
      foreach (var constant in module.Constants)
        moduleLevel.Add(this.Constant(constant, init));
      foreach (var global in module.Globals)
        moduleLevel.Add(this.Global(global, init));
      if (needsInit)
        moduleLevel.Add(new VbModuleVariable(false, new(ReadyName, new("Boolean"))));
      declarations.AddRange(moduleLevel.Select((declaration, index) => declaration with { Leading = index == 0 ? VbTrivia.Blank : VbTrivia.None }));
      declarations.AddRange(procedures.Select(procedure => procedure with { Leading = VbTrivia.Blank }));
      if (needsInit) {
        init.Add(new VbAssign(new VbName(ReadyName), new VbBoolean(true)));
        declarations.Add(new VbProcedure(false, InitName, [], null, [.. this._initLocals.Select(local => new VbDim([local])), .. init]) { Leading = VbTrivia.Blank });
      }

      // A helper declares what it calls: other helpers.
      var pending = new Stack<string>(this._helpers);
      while (pending.TryPop(out var helper))
        foreach (var needed in VisualBasicHelpers.Needs(helper))
          if (this._helpers.Add(needed))
            pending.Push(needed);

      var helpers = new List<HelperText>();
      foreach (var name in VisualBasicHelpers.Order.Where(this._helpers.Contains)) {
        declarations.Add(VisualBasicHelpers.Declaration(name) with { Leading = VbTrivia.Blank });
        var (licence, origin) = HelperOrigins[name];
        helpers.Add(new(name, name, licence, origin, ""));
      }

      foreach (var (suffix, (type, prefixes)) in this._arrayHelpers) {
        foreach (var prefix in prefixes.ToList())
          foreach (var needed in ArrayHelpers.Needs(prefix))
            prefixes.Add(needed);

        var set = ArrayHelpers.For(type, suffix);
        foreach (var prefix in new[] { ArrayHelpers.Length, ArrayHelpers.New, ArrayHelpers.Copy, ArrayHelpers.Fill }.Where(prefixes.Contains)) {
          declarations.Add(set[prefix + suffix] with { Leading = VbTrivia.Blank });
          helpers.Add(new(prefix + suffix, prefix + suffix, EmittedHelper.OwnLicence, EmittedHelper.OriginOf(VisualBasicHelpers.Folder), ""));
        }
      }

      // The equality of a struct compares the structs it holds with their own equality.
      for (var changed = true; changed;) {
        changed = false;
        foreach (var (type, kinds) in this._structHelpers.Values.ToList())
          if (kinds.Contains(EqualsPrefix))
            foreach (var field in type.Fields)
              if (field.Type is { IsStruct: true, Declaration: IrStructType inner } && !(this._structHelpers.TryGetValue(this._typeNames[inner], out var held) && held.Kinds.Contains(EqualsPrefix))) {
                this.StructHelper(EqualsPrefix, inner);
                changed = true;
              }
      }

      foreach (var (_, (type, kinds)) in this._structHelpers.ToList())
        foreach (var procedure in this.StructHelpers(type, kinds)) {
          declarations.Add(procedure with { Leading = VbTrivia.Blank });
          helpers.Add(new(procedure.Name, procedure.Name, EmittedHelper.OwnLicence, EmittedHelper.OriginOf(VisualBasicHelpers.Folder), ""));
        }

      helpers.AddRange(module.Implementations.Select(pair => ImplementationSources.Helper(this._functions[pair.Function], pair.Implementation, "")));
      this.Helpers = helpers;
      var header = new List<string> {
        $"{module.Name}: generated by polypiler. Change the source, not this file.",
        $"Visual Basic profile: {profile.Name} ({profile.Title})."
      };
      if (EmittedHelper.HeaderLine(helpers.Select(helper => helper.Licence)) is { } licences)
        header.Add(licences);

      header.AddRange(FloatManifest.Lines(module, mode, Runtime));
      header.AddRange(ImplementationSources.Notices(module.Implementations.Select(pair => pair.Implementation).Concat(module.Inlined)));
      return new VbModule(moduleName, header, declarations);
    }

    /// <summary>The Visual Basic name of every function, by its name in the IR.</summary>
    public IReadOnlyDictionary<string, string> Names(IrModule module) {
      this.Reserve();
      this.NameFunctions(module);
      return this._functions;
    }

    /// <summary>
    /// The name of every function: a public one keeps its valid name, which two public ones may not
    /// share without regard to case (<see cref="NameCollision"/>); a private one takes the next free name.
    /// </summary>
    private void NameFunctions(IrModule module) {
      foreach (var function in module.Functions)
        this._functionsByName[function.Name] = function;

      foreach (var group in module.Functions.Where(function => function.IsPublic).GroupBy(function => VisualBasicNaming.Valid(function.Name, "Function"), StringComparer.OrdinalIgnoreCase)) {
        var first = group.First();
        if (group.Skip(1).FirstOrDefault() is { } second)
          this.Refuse(NameCollisionDescriptor, $"exports '{first.Name}' and '{second.Name}' both map to the Visual Basic name '{group.Key}'");

        var name = this._moduleNames.Contains(group.Key) ? this.Take(group.Key) : group.Key;
        this._moduleNames.Add(name);
        foreach (var function in group)
          this._functions[function.Name] = name;
      }

      foreach (var function in module.Functions.Where(function => !function.IsPublic))
        this._functions[function.Name] = this.Take(VisualBasicNaming.Valid(function.Name, "Function"));
    }

    /// <summary>The user-defined types of the module's structs, a struct after the structs its fields hold; an enum is its underlying integer.</summary>
    private List<VbDeclaration> Types(IrModule module) {
      var declarations = new List<VbDeclaration>();
      var written = new HashSet<IrTypeDeclaration>(ReferenceEqualityComparer.Instance);
      foreach (var declaration in module.Types) {
        if (declaration is IrStructType or IrEnumType)
          this._typeNames[declaration] = declaration is IrStructType ? this.Take(VisualBasicNaming.Valid(declaration.Name, "Record")) : declaration.Name;
        else
          this.Refuse(TypeUnsupportedDescriptor, $"the type '{declaration.Name}' ({declaration.GetType().Name[2..]}) has no Visual Basic representation in profile '{profile.Name}'");
      }

      void Write(IrStructType type) {
        if (!written.Add(type))
          return;

        foreach (var field in type.Fields)
          if (field.Type is { IsStruct: true, Declaration: IrStructType inner } && this._typeNames.ContainsKey(inner))
            Write(inner);

        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<VbVariable>();
        foreach (var field in type.Fields) {
          var name = VisualBasicNaming.Free(VisualBasicNaming.Valid(field.Name, "Field"), fieldNames.Contains);
          fieldNames.Add(name);
          this._fieldNames[field] = name;
          if (field.Type.IsArray)
            this.Refuse(TypeUnsupportedDescriptor, $"the array field '{field.Name}' of '{type.Name}' would be copied with its record in Visual Basic, where the IR shares it");
          fields.Add(this.Variable(name, field.Type));
        }

        declarations.Add(new VbTypeDeclaration(true, this._typeNames[type], fields));
      }

      foreach (var type in module.Types.OfType<IrStructType>())
        Write(type);

      return declarations;
    }

    /// <summary>The Visual Basic type of an IR type; <c>Long</c> after reporting a type Visual Basic cannot carry.</summary>
    private VbTypeName TypeName(IrType type) {
      switch (type) {
        case { Kind: IrTypeKind.Bool }:
          return new("Boolean");
        case { Kind: IrTypeKind.U8 }:
          return new("Byte");
        case { Kind: IrTypeKind.I8 or IrTypeKind.I16 }:
          return new("Integer");
        case { Kind: IrTypeKind.U16 or IrTypeKind.I32 or IrTypeKind.U32 }:
          return new("Long");
        case { Kind: IrTypeKind.I64 or IrTypeKind.U64 }:
          if (!this.Has(VisualBasicCapabilities.LongLong))
            this.Refuse(TypeUnsupportedDescriptor, $"type '{type}' needs LongLong, which only profile 'visualbasic/vba7-64' has; profile '{profile.Name}' has no 64-bit integer");
          return new("LongLong");
        case { Kind: IrTypeKind.F32 }:
          return new("Single");
        case { Kind: IrTypeKind.F64 }:
          return new("Double");
        case { IsString: true }:
          return new("String");
        case { IsVariant: true }:
          return new("Variant");
        case { IsStruct: true, Declaration: { } declaration } when this._typeNames.TryGetValue(declaration, out var name):
          return new(name);
        case { IsArray: true, Element: { } element }:
          if (element.IsArray)
            this.Refuse(TypeUnsupportedDescriptor, $"type '{type}' is an array of arrays, which Visual Basic has no typed form of");
          return this.TypeName(element);
        default:
          this.Refuse(TypeUnsupportedDescriptor, $"type '{type}' has no Visual Basic representation in profile '{profile.Name}'");
          return new("Long");
      }
    }

    /// <summary>A variable of a type: a dynamic array for an array type.</summary>
    private VbVariable Variable(string name, IrType type) => new(name, this.TypeName(type)) { IsArray = type.IsArray };

    /// <summary>The element type's name and the suffix of its array helpers, which names the type.</summary>
    private (string Type, string Suffix) ElementOf(IrType array) {
      var element = this.TypeName(array.Element!).Name;
      return (element, element);
    }

    /// <summary>Names an array helper of an element type, which the module then declares.</summary>
    private string ArrayHelper(string prefix, IrType array) {
      var (type, suffix) = this.ElementOf(array);
      if (!this._arrayHelpers.TryGetValue(suffix, out var entry)) {
        entry = (type, new HashSet<string>(StringComparer.Ordinal));
        this._arrayHelpers[suffix] = entry;
      }

      entry.Prefixes.Add(prefix);
      return prefix + suffix;
    }

    /// <summary>Names a fixed helper, which the module then declares.</summary>
    private VbName Helper(string name) {
      this._helpers.Add(name);
      return new VbName(name);
    }

    private VbCall CallHelper(string name, params VbExpression[] arguments) => new(this.Helper(name), arguments);

    private readonly List<VbVariable> _initLocals = [];

    /// <summary>A module constant: a scalar as <c>Private Const</c>, a table as an array the initializer fills.</summary>
    private VbDeclaration Constant(IrModuleConst constant, List<VbStatement> init) {
      var name = this._constants[constant.Name];
      if (constant.Type.IsString)
        return new VbConst(false, name, new("String"), this.Text(constant.Strings.Count > 0 ? constant.Strings[0] : "", true));

      if (constant.Type.Element is not { } element) {
        var value = this.Literal(constant.Type, constant.Values.Count > 0 ? constant.Values[0] : 0);
        return new VbConst(false, name, this.TypeName(constant.Type), value);
      }

      var count = element.IsString ? constant.Strings.Count : constant.Values.Count;
      if (count > 0)
        init.Add(new VbReDim(false, new VbName(name), new(new VbInteger(0), new VbInteger(count - 1))));
      for (var index = 0; index < count; ++index) {
        var value = element.IsString ? this.Text(constant.Strings[index], false) : this.Literal(element, constant.Values[index]);
        init.Add(new VbAssign(new VbCall(new VbName(name), [new VbInteger(index)]), value));
      }

      return new VbModuleVariable(false, this.Variable(name, constant.Type));
    }

    /// <summary>A global: a module variable the initializer sets.</summary>
    private VbDeclaration Global(IrGlobal global, List<VbStatement> init) {
      var name = this._globals[global.Name];
      if (global.Initializer is { } initializer) {
        this.BeginFunction("the module", this._initLocals.Select(local => local.Name));
        var statements = new List<VbStatement>();
        this.Into(statements, () => statements.Add(new VbAssign(new VbName(name), this.Expression(initializer))));
        this._initLocals.AddRange(this._temps);
        init.AddRange(statements);
      }

      return new VbModuleVariable(false, this.Variable(name, global.Type));
    }

    /// <summary>The constructors, defaults and equality tests of a struct the module names.</summary>
    private IEnumerable<VbProcedure> StructHelpers(IrStructType type, HashSet<string> kinds) {
      var name = this._typeNames[type];
      var typeName = new VbTypeName(name);
      if (kinds.Contains(MakePrefix)) {
        var parameters = type.Fields.Select(field => new VbParameter(!field.Type.IsStruct, "v" + this._fieldNames[field], this.TypeName(field.Type))).ToList();
        var body = new List<VbStatement> { new VbDim([new("r", typeName)]) };
        body.AddRange(type.Fields.Select(field => new VbAssign(new VbMember(new VbName("r"), this._fieldNames[field]), new VbName("v" + this._fieldNames[field]))));
        body.Add(new VbAssign(new VbName(MakePrefix + name), new VbName("r")));
        yield return new VbProcedure(false, MakePrefix + name, parameters, typeName, body) { IsHelper = true };
      }

      if (kinds.Contains(ZeroPrefix)) {
        var body = new List<VbStatement> { new VbDim([new("r", typeName)]), new VbAssign(new VbName(ZeroPrefix + name), new VbName("r")) };
        yield return new VbProcedure(false, ZeroPrefix + name, [], typeName, body) { IsHelper = true };
      }

      if (kinds.Contains(EqualsPrefix)) {
        VbExpression test = new VbBoolean(true);
        foreach (var field in type.Fields) {
          var left = new VbMember(new VbName("a"), this._fieldNames[field]);
          var right = new VbMember(new VbName("b"), this._fieldNames[field]);
          VbExpression same = field.Type is { IsStruct: true, Declaration: IrStructType inner }
            ? new VbCall(new VbName(this.StructHelper(EqualsPrefix, inner)), [left, right])
            : new VbBinary(left, "=", right);
          test = test is VbBoolean ? same : new VbBinary(test, "And", same);
        }

        yield return new VbProcedure(false, EqualsPrefix + name, [new(false, "a", typeName), new(false, "b", typeName)], new("Boolean"), [new VbAssign(new VbName(EqualsPrefix + name), test)]) { IsHelper = true };
      }
    }

    private const string MakePrefix = "PlyMake_";
    private const string ZeroPrefix = "PlyZero_";
    private const string EqualsPrefix = "PlyEq_";

    /// <summary>Names a helper of a struct, which the module then declares.</summary>
    private string StructHelper(string prefix, IrStructType type) {
      var name = this._typeNames[type];
      if (!this._structHelpers.TryGetValue(name, out var entry)) {
        entry = (type, new HashSet<string>(StringComparer.Ordinal));
        this._structHelpers[name] = entry;
      }

      entry.Kinds.Add(prefix);
      return prefix + name;
    }

  }

}
