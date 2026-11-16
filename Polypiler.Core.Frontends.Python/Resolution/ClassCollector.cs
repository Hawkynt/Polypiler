using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>
/// Reads a <c>class</c> statement into a <see cref="ClassModel"/>: a class without bases, a
/// <c>@dataclass</c>, a <c>NamedTuple</c>, an <c>Enum</c> or <c>IntEnum</c> of integer members, or an
/// exception class derived from a built-in exception or another of the module. <c>dataclass</c>,
/// <c>Enum</c> and <c>NamedTuple</c> are recognised by the import that binds them, never by their bare
/// names. Everything that makes a class more than its declared attributes is refused.
/// </summary>
internal sealed class ClassCollector(ModuleModel module, List<Diagnostic> diagnostics) {

  /// <summary>The special methods a class may define, beside <c>__init__</c>.</summary>
  public static readonly IReadOnlySet<string> SpecialMethods = new HashSet<string> { "__init__", "__eq__", "__len__", "__getitem__", "__setitem__" };

  private void Refuse(PyNode node, string feature, string issue)
    => diagnostics.Add(module.File.At(node, PythonFrontend.UnsupportedClassDescriptor,
      $"'{feature}' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by {issue}"));

  /// <summary>Reads a <c>class</c> statement; a class outside the subset adds a diagnostic and no class.</summary>
  public void Collect(PyNode statement) {
    var name = statement.Text("name")!;
    if (module.Classes.ContainsKey(name) || module.Functions.ContainsKey(name)) {
      this.Refuse(statement, $"redefinition of {name}", "#481");
      return;
    }

    if (statement.Nodes("type_params").Count > 0) {
      this.Generic(statement, $"class {name}[…]");
      return;
    }

    if (statement.Nodes("keywords") is [var keyword, ..]) {
      this.Refuse(keyword, $"{keyword.Text("arg")}= in the bases of {name}", keyword.Text("arg") == "metaclass" ? "#751" : "#481");
      return;
    }

    var bases = statement.Nodes("bases");
    var (dataclass, frozen, eq) = (false, false, true);
    foreach (var decorator in statement.Nodes("decorator_list")) {
      if (this.Dataclass(decorator) is not { } options) {
        this.Refuse(decorator, $"the decorator {module.File.Quote(decorator)} of {name}", "#481");
        return;
      }

      if (options.Problem is { } problem) {
        this.Refuse(decorator, problem, "#481");
        return;
      }

      (dataclass, frozen, eq) = (true, options.Frozen, options.Eq);
    }

    ClassModel type;
    if (bases.Count == 0) {
      type = new ClassModel(name, dataclass ? ClassKind.Dataclass : ClassKind.Plain, statement) { IsFrozen = frozen, ComparesFields = dataclass && eq };
    } else if (bases.Count > 1) {
      this.Refuse(bases[1], $"multiple inheritance of {name}", "#762");
      return;
    } else if (bases[0].Is("Subscript") && this.Typing(bases[0].Node("value")!) is "Generic" or "Protocol") {
      this.Generic(bases[0], module.File.Quote(bases[0]));
      return;
    } else if (dataclass) {
      this.Refuse(bases[0], $"the base class {module.File.Quote(bases[0])} of the dataclass {name}", "#762");
      return;
    } else {
      var kind = this.Base(bases[0], out var parent);
      if (kind is null) {
        this.Refuse(bases[0], $"the base class {module.File.Quote(bases[0])} of {name}", this.Typing(bases[0]) == "Protocol" ? "#473" : "#762");
        return;
      }

      type = new ClassModel(name, kind.Value, statement) { IsFrozen = kind == ClassKind.NamedTuple, ComparesFields = kind == ClassKind.NamedTuple, Base = parent };
    }

    if (this.Body(type, statement))
      module.Classes[name] = type;
  }

  private void Generic(PyNode node, string what)
    => diagnostics.Add(module.File.At(node, PythonFrontend.UnsupportedGenericDescriptor, $"generic declaration '{what}' is not supported; generic classes and functions are imported by #472"));

  /// <summary>The name of <c>typing</c> a node binds, or <see langword="null"/>.</summary>
  private string? Typing(PyNode node) => this.Library(node, "typing");

  /// <summary>The name of a module of the standard library a node binds by an import - <c>X</c> of <c>from m import X</c> or <c>m.X</c> - or <see langword="null"/>.</summary>
  public string? Library(PyNode node, string library) {
    if (node.Is("Name") && module.Imports.TryGetValue(node.Text("id")!, out var binding) && binding.User is null && binding.Module == library)
      return binding.Name;

    if (node.Is("Attribute") && node.Node("value") is { Kind: "Name" } owner && module.Imports.TryGetValue(owner.Text("id")!, out var imported)
        && imported.User is null && imported.Name is null && imported.Module == library)
      return node.Text("attr");

    return null;
  }

  /// <summary>The options of a <c>@dataclass</c> decorator, or <see langword="null"/> for another decorator.</summary>
  private (bool Frozen, bool Eq, string? Problem)? Dataclass(PyNode decorator) {
    var callee = decorator.Is("Call") ? decorator.Node("func")! : decorator;
    if (this.Library(callee, "dataclasses") != "dataclass")
      return null;

    var (frozen, eq) = (false, true);
    if (decorator.Is("Call")) {
      if (decorator.Nodes("args").Count > 0)
        return (false, true, "positional arguments of @dataclass");

      foreach (var keyword in decorator.Nodes("keywords")) {
        var value = keyword.Node("value")!;
        if (!(value.Is("Constant") && value.Text("kind") == "bool"))
          return (false, true, $"@dataclass({keyword.Text("arg")}=…) of a value other than True or False");

        var on = value.Text("value") == "True";
        switch (keyword.Text("arg")) {
          case "frozen":
            frozen = on;
            break;
          case "eq":
            eq = on;
            break;
          case "slots" or "init" or "repr" when keyword.Text("arg") != "init" || on:
            break;
          default:
            return (false, true, $"@dataclass({keyword.Text("arg")}={value.Text("value")})");
        }
      }
    }

    return (frozen, eq, null);
  }

  /// <summary>The kind of a class with one base, and the class of exceptions it derives from; <see langword="null"/> for a base outside the subset.</summary>
  private ClassKind? Base(PyNode @base, out ClassModel? parent) {
    parent = null;
    if (this.Typing(@base) == "NamedTuple")
      return ClassKind.NamedTuple;

    switch (this.Library(@base, "enum")) {
      case "Enum":
        return ClassKind.Enum;
      case "IntEnum":
        return ClassKind.IntEnum;
    }

    if (@base.Is("Name")) {
      var name = @base.Text("id")!;
      if (module.Classes.TryGetValue(name, out var declared) && declared.IsException) {
        parent = declared;
        return ClassKind.Exception;
      }

      if (!module.Classes.ContainsKey(name) && !module.Imports.ContainsKey(name) && Classes.IsBuiltinException(name)) {
        parent = Classes.Builtin(name);
        return ClassKind.Exception;
      }
    }

    return null;
  }

  /// <summary>Reads the body of a class into its fields, members and methods; <see langword="false"/> where it was refused.</summary>
  private bool Body(ClassModel type, PyNode statement) {
    var before = diagnostics.Count;
    foreach (var member in statement.Nodes("body")) {
      if (member.Is("Expr") && member.Node("value") is { Kind: "Constant" } docstring && docstring.Text("kind") == "str")
        continue;

      if (member.Is("Pass"))
        continue;

      switch (type.Kind) {
        case ClassKind.Exception:
          diagnostics.Add(module.File.At(member, PythonFrontend.UnsupportedExceptionDescriptor,
            $"exception construct 'an exception class with members: {type.Name}' is not supported by the Python frontend"));
          break;
        case ClassKind.Enum or ClassKind.IntEnum:
          this.Member(type, member);
          break;
        case ClassKind.Dataclass or ClassKind.NamedTuple when member.Is("AnnAssign"):
          this.Field(type, member);
          break;
        default:
          if (member.Is("FunctionDef"))
            this.Method(type, member);
          else
            this.Refuse(member, $"the class attribute {module.File.Quote(member)} of {type.Name}", "#481");

          break;
      }
    }

    if (type.Kind == ClassKind.Plain && type.Method("__init__") is { } init)
      this.Attributes(type, init);

    return diagnostics.Count == before;
  }

  private void Member(ClassModel type, PyNode member) {
    if (member.Is("Assign") && member.Nodes("targets") is [{ Kind: "Name" } target] && Constants.Integer(member.Node("value")!) is { } value
        && value >= long.MinValue && value <= long.MaxValue) {
      if (type.Members.Any(known => known.Name == target.Text("id")))
        this.Refuse(member, $"the member {target.Text("id")} of {type.Name} given twice", "#481");
      else if (type.Members.Any(known => known.Value == (long)value))
        this.Refuse(member, $"the alias {target.Text("id")} of another member of {type.Name}", "#481");
      else
        type.Members.Add((target.Text("id")!, (long)value, member));

      return;
    }

    this.Refuse(member, member.Is("FunctionDef") ? $"the method {member.Text("name")} of the enum {type.Name}" : $"the member {module.File.Quote(member)} of {type.Name}, whose value is no integer literal", "#481");
  }

  private void Field(ClassModel type, PyNode member) {
    var target = member.Node("target")!;
    if (!target.Is("Name")) {
      this.Refuse(member, $"the field {module.File.Quote(target)} of {type.Name}", "#481");
      return;
    }

    if (member.Node("value") is { Kind: "Call" } call && this.Library(call.Node("func")!, "dataclasses") == "field") {
      this.Refuse(call, $"field(…) in {type.Name}, a default made by a factory", "#481");
      return;
    }

    if (this.Typing(member.Node("annotation")!) == "ClassVar" || member.Node("annotation") is { Kind: "Subscript" } annotation && this.Typing(annotation.Node("value")!) == "ClassVar") {
      this.Refuse(member, $"the class attribute {target.Text("id")} of {type.Name}", "#481");
      return;
    }

    if (type.Fields.Any(field => field.Name == target.Text("id"))) {
      this.Refuse(member, $"the field {target.Text("id")} of {type.Name} declared twice", "#481");
      return;
    }

    if (member.Node("value") is null && type.Fields.Any(field => field.Default is not null)) {
      this.Refuse(member, $"the field {target.Text("id")} without a default after one with a default in {type.Name}, which raises TypeError", "#481");
      return;
    }

    type.Fields.Add(new FieldModel(target.Text("id")!, member) { Default = member.Node("value") });
  }

  private void Method(ClassModel type, PyNode member) {
    var name = member.Text("name")!;
    var (isStatic, isProperty) = (false, false);
    foreach (var decorator in member.Nodes("decorator_list")) {
      var decoratorName = decorator.Is("Name") ? decorator.Text("id") : null;
      if (decoratorName == "staticmethod" && !module.Functions.ContainsKey("staticmethod")) {
        isStatic = true;
      } else if (decoratorName == "property" && !module.Functions.ContainsKey("property")) {
        isProperty = true;
      } else {
        this.Refuse(decorator, $"the decorator {module.File.Quote(decorator)} of {type.Name}.{name}", decoratorName == "classmethod" ? "#481" : "#481");
        return;
      }
    }

    var arguments = member.Node("args")!;
    if (arguments.Node("vararg") is not null || arguments.Node("kwarg") is not null || member.Nodes("type_params").Count > 0) {
      diagnostics.Add(module.File.At(member, PythonFrontend.UnsupportedFunctionDescriptor, $"'{Collector.Feature(member) ?? "a feature of " + name}' is not supported for functions by the Python frontend"));
      return;
    }

    if (name.StartsWith("__", System.StringComparison.Ordinal) && name.EndsWith("__", System.StringComparison.Ordinal) && !SpecialMethods.Contains(name)) {
      this.Refuse(member, $"the operator method {name} of {type.Name}", name is "__getattr__" or "__setattr__" or "__getattribute__" or "__call__" ? "#751" : "#474");
      return;
    }

    if (type.Kind == ClassKind.Dataclass && name == "__init__") {
      this.Refuse(member, $"a dataclass {type.Name} with its own __init__", "#481");
      return;
    }

    var positional = arguments.Nodes("posonlyargs").Count + arguments.Nodes("args").Count;
    if ((!isStatic && positional == 0) || (isProperty && (positional != 1 || isStatic))) {
      this.Refuse(member, $"the method {name} of {type.Name} without its receiver", "#481");
      return;
    }

    if (type.Methods.ContainsKey(name)) {
      this.Refuse(member, $"the method {name} of {type.Name} defined twice", "#481");
      return;
    }

    type.Methods[name] = new FunctionModel(member, type) { IsStatic = isStatic, IsProperty = isProperty };
  }

  /// <summary>The attributes of a plain class: the names <c>__init__</c> assigns to its receiver, in the order of the first assignment.</summary>
  private void Attributes(ClassModel type, FunctionModel init) {
    var self = init.Parameters[0].Name;
    void Visit(PyNode statement) {
      var targets = statement.Kind switch {
        "Assign" => statement.Nodes("targets"),
        "AnnAssign" or "AugAssign" => [statement.Node("target")!],
        _ => []
      };
      foreach (var target in targets)
        if (target.Is("Attribute") && target.Node("value") is { Kind: "Name" } receiver && receiver.Text("id") == self && type.Field(target.Text("attr")!) is null)
          type.Fields.Add(new FieldModel(target.Text("attr")!, target));

      foreach (var field in new[] { "body", "orelse", "finalbody" })
        foreach (var inner in statement.Nodes(field))
          Visit(inner);

      foreach (var handler in statement.Nodes("handlers"))
        foreach (var inner in handler.Nodes("body"))
          Visit(inner);
    }

    foreach (var statement in init.Body)
      Visit(statement);

    if (type.Methods.ContainsKey(type.Fields.FirstOrDefault(field => type.Methods.ContainsKey(field.Name))?.Name ?? ""))
      this.Refuse(init.Node, $"an attribute of {type.Name} named as one of its methods", "#481");
  }

}
