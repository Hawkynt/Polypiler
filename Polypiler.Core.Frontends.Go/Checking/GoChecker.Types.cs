using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Checking;

internal sealed partial class GoChecker {

  private readonly Dictionary<GoNode, GoObject> _methods = new(ReferenceEqualityComparer.Instance);

  /// <summary>The type a type expression denotes, recorded on the node with the mode <c>type</c>.</summary>
  private GoType TypeOf(GoNode node) {
    var type = this.TypeExpression(node);
    node.TypeOfNode = type;
    node.Mode = "type";
    return type;
  }

  private GoType TypeExpression(GoNode node) {
    switch (node.Kind) {
      case "Ident": {
        var name = node.Text("Name")!;
        if (name == "_")
          throw Error(node, "cannot use _ as value or type");

        var @object = this.Lookup(node) ?? throw Error(node, $"undefined: {name}");
        node.Target = @object;
        if (@object.Kind != GoObjectKind.TypeName)
          throw Error(node, $"{name} is not a type");

        if (@object.Type is null && @object.Resolution == GoResolution.Resolved)
          throw Unsupported(node, "the type constraint comparable");

        this.Resolve(@object);
        return @object.Type!;
      }
      case "SelectorExpr":
        // math and math/bits declare no types: a qualified name is never one.
        this.QualifiedMember(node);
        throw Error(node, $"{GoNodeText.Of(node)} is not a type");
      case "ParenExpr":
        return this.TypeOf(node.Node("X")!);
      case "StarExpr":
        return new GoPointer(this.TypeOf(node.Node("X")!));
      case "ArrayType": {
        var element = node.Node("Elt")!;
        switch (node.Node("Len")) {
          case null:
            return new GoSlice(this.TypeOf(element));
          case { Kind: "Ellipsis" }:
            throw Error(node, "invalid use of [...] array (outside a composite literal)");
          case var length:
            var count = this.ArrayLength(length);
            return new GoArray(count, this.TypeOf(element));
        }
      }
      case "StructType":
        return this.StructType(node);
      case "FuncType":
        return this.Signature(node, null);
      case "InterfaceType":
        return this.InterfaceType(node);
      case "MapType": {
        var key = this.TypeOf(node.Node("Key")!);
        var value = this.TypeOf(node.Node("Value")!);
        var keyNode = node.Node("Key")!;
        void CheckKey() {
          if (!GoTypes.Comparable(key))
            throw Error(keyNode, $"invalid map key type {key}");
        }

        if (key is GoNamed { IsResolved: false })
          this._later.Add(CheckKey);
        else
          CheckKey();

        return new GoMap(key, value);
      }
      case "ChanType":
        throw Unsupported(node, "channel type");
      case "IndexExpr" or "IndexListExpr":
        throw Unsupported(node, "generic type instantiation");
      default:
        throw Error(node, $"{GoNodeText.Of(node)} is not a type");
    }
  }

  /// <summary>The member of an imported package a selector names, the package marked used; <see langword="null"/> where the selector names no package.</summary>
  private GoObject? QualifiedMember(GoNode selector) {
    if (selector.Node("X") is not { Kind: "Ident" } qualifier || this.Lookup(qualifier) is not { Kind: GoObjectKind.PackageName } package)
      return null;

    qualifier.Target = package;
    package.Used = true;
    var name = selector.Node("Sel")!.Text("Name")!;
    if (!char.IsUpper(name[0]))
      throw Error(selector.Node("Sel"), $"name {name} not exported by package {LastElement(package.Package!)}");

    var member = package.Members!.Local(name) ?? throw Error(selector.Node("Sel"), $"undefined: {GoNodeText.Of(selector)}");
    selector.Node("Sel")!.Target = member;
    selector.Node("Sel")!.TypeOfNode = member.Type;
    return member;
  }

  /// <summary>The object an identifier names in the current scope, or <see langword="null"/>.</summary>
  private GoObject? Lookup(GoNode identifier) => this._scope.Lookup(identifier.Text("Name")!)?.Object;

  private long ArrayLength(GoNode length) {
    if (length.Is("Ident")) {
      var name = length.Text("Name")!;
      var @object = this.Lookup(length) ?? throw Error(length, $"undefined array length {name} or missing type constraint");
      if (@object.Kind != GoObjectKind.Const)
        throw Error(length, $"invalid array length {name}");
    }

    var x = this.Expression(length);
    if (x.Mode != GoMode.Constant)
      throw Error(length, $"array length {x} must be constant");

    if ((GoTypes.IsUntyped(x.Type) || GoTypes.IsInteger(x.Type)) && x.Value!.ToInt() is { } count && Representable(count, GoTypes.Int) is not null && count.Sign >= 0)
      return (long)count.Numerator;

    throw Error(length, GoTypes.IsInteger(x.Type) ? $"invalid array length {x}" : $"array length {x} must be integer");
  }

  private GoStruct StructType(GoNode node) {
    var fields = new List<GoField>();
    var names = new HashSet<string>(System.StringComparer.Ordinal);
    foreach (var field in node.Node("Fields")!.Nodes("List")) {
      var type = this.TypeOf(field.Node("Type")!);
      var tag = field.Node("Tag") is { } literal ? System.Text.Encoding.UTF8.GetString(GoConstant.Unquote(literal.Text("Value")!)) : null;
      var fieldNames = field.Nodes("Names");
      if (fieldNames.Count == 0)
        throw Unsupported(field, "embedded field");

      foreach (var name in fieldNames) {
        var text = name.Text("Name")!;
        if (text != "_" && !names.Add(text))
          throw Error(name, $"{text} redeclared");

        name.Target = new GoObject(GoObjectKind.Var, text, type, name.Line, name.Column) { IsField = true };
        name.TypeOfNode = type;
        fields.Add(new GoField(text, type, false, tag));
      }
    }

    return new GoStruct(fields);
  }

  private GoInterface InterfaceType(GoNode node) {
    var methods = new List<GoMethod>();
    foreach (var element in node.Node("Methods")!.Nodes("List")) {
      var names = element.Nodes("Names");
      if (names.Count == 0)
        throw Unsupported(element, element.Node("Type")!.Kind is "BinaryExpr" or "UnaryExpr" ? "type constraint" : "embedded interface");

      var name = names[0].Text("Name")!;
      if (name == "_")
        throw Error(names[0], "methods must have a unique non-blank name");

      if (methods.Any(method => method.Name == name))
        throw Error(names[0], $"duplicate method {name}");

      var signature = this.Signature(element.Node("Type")!, null);
      names[0].TypeOfNode = signature;
      methods.Add(new GoMethod(name, signature));
    }

    return new GoInterface(methods);
  }

  /// <summary>The type of a function type; with a scope, its parameters and results are declared there.</summary>
  private GoSignature Signature(GoNode node, GoScope? scope) {
    var variadic = false;
    var names = new HashSet<string>(System.StringComparer.Ordinal);
    var parameters = this.Parameters(node.Node("Params"), scope, names, true, ref variadic);
    var none = false;
    var results = this.Parameters(node.Node("Results"), scope, names, false, ref none);
    return new GoSignature(parameters, results, variadic);
  }

  private List<GoParameter> Parameters(GoNode? list, GoScope? scope, HashSet<string> names, bool allowVariadic, ref bool variadic) {
    var parameters = new List<GoParameter>();
    if (list is null)
      return parameters;

    foreach (var field in list.Nodes("List")) {
      var typeNode = field.Node("Type")!;
      GoType type;
      if (typeNode.Is("Ellipsis") && allowVariadic) {
        type = new GoSlice(this.TypeOf(typeNode.Node("Elt")!));
        typeNode.TypeOfNode = type;
        variadic = true;
      } else {
        type = this.TypeOf(typeNode);
      }

      var fieldNames = field.Nodes("Names");
      if (fieldNames.Count == 0) {
        parameters.Add(new GoParameter("", type));
        continue;
      }

      foreach (var name in fieldNames) {
        var text = name.Text("Name")!;
        if (text != "_" && !names.Add(text))
          throw Error(name, $"{text} redeclared in this block");

        parameters.Add(new GoParameter(text, type));
        if (scope is not null)
          this.Declare(scope, name, GoObjectKind.Var, type, @object => @object.Used = true);
        else
          name.TypeOfNode = type;
      }
    }

    return parameters;
  }

  private void TypeDeclaration(GoObject @object, GoNode spec) {
    var rhs = spec.Node("Type")!;
    if (spec.Flag("alias")) {
      @object.Type = this.TypeOf(rhs);
      spec.Node("Name")!.TypeOfNode = @object.Type;
      return;
    }

    var named = (GoNamed)@object.Type!;
    var type = this.TypeOf(rhs);
    if (type is GoNamed { IsResolved: false })
      throw Error(spec.Node("Name"), $"invalid recursive type {named.Name}");

    named.Resolve(type);

    if (this.Cycles(named.Underlying, named, []))
      throw Error(spec.Node("Name"), $"invalid recursive type {named.Name}");
  }

  /// <summary>Whether a type contains a value of the defined type itself, through fields and array elements (<c>validType</c>).</summary>
  private bool Cycles(GoType type, GoNamed start, HashSet<GoNamed> path) {
    switch (type) {
      case GoNamed named:
        if (named == start)
          return true;

        if (!named.IsResolved || !path.Add(named))
          return false;

        var cycles = this.Cycles(named.Underlying, start, path);
        path.Remove(named);
        return cycles;
      case GoArray array:
        return this.Cycles(array.Element, start, path);
      case GoStruct structure:
        return structure.Fields.Any(field => this.Cycles(field.Type, start, path));
      default:
        return false;
    }
  }

  private void CollectMethod(GoNode declaration) {
    var receivers = declaration.Node("Recv")!.Nodes("List");
    var name = declaration.Node("Name")!;
    if (receivers.Count == 0)
      throw Error(name, "method has no receiver");

    if (receivers.Count > 1 || receivers[0].Nodes("Names").Count > 1)
      throw Error(name, "method has multiple receivers");

    var receiverNode = receivers[0].Node("Type")!;
    var pointer = false;
    var baseNode = Unparen(receiverNode);
    if (baseNode.Is("StarExpr")) {
      pointer = true;
      baseNode = Unparen(baseNode.Node("X")!);
    }

    var receiver = this.TypeOf(receiverNode);
    var @base = pointer ? ((GoPointer)receiver).Element : receiver;
    if (@base is not GoNamed named || named.Package is not null || named.Name == "error" && this._scope.Lookup("error")?.Scope == this._universe)
      throw Error(baseNode, @base is GoBasic or GoNamed ? $"cannot define new methods on non-local type {@base}" : $"invalid receiver type {receiver}");

    if (named.Underlying is GoPointer or GoInterface)
      throw Error(baseNode, $"invalid receiver type {receiver} (pointer or interface type)");

    var signature = this.Signature(declaration.Node("Type")!, null);
    var methodName = name.Text("Name")!;
    var method = new GoObject(GoObjectKind.Func, methodName, signature, name.Line, name.Column) { Declaration = name, PointerReceiver = pointer };
    name.Target = method;
    name.TypeOfNode = signature;
    this._methods[declaration] = method;
    if (methodName == "_")
      return;

    if (named.Methods.Any(existing => existing.Name == methodName))
      throw Error(name, $"method {named.Name}.{methodName} already declared");

    if (named.Underlying is GoStruct structure && structure.Field(methodName) is not null)
      throw Error(name, $"field and method with the same name {methodName}");

    named.Methods.Add(method);
  }

  private void ConstantDeclaration(GoObject @object, GoNode? typeNode, GoNode valueNode) {
    GoType? type = null;
    if (typeNode is not null) {
      type = this.TypeOf(typeNode);
      if (!GoTypes.IsConstType(type))
        throw Error(typeNode, $"invalid constant type {type}");
    }

    var x = this.Expression(valueNode);
    if (x.Mode != GoMode.Constant)
      throw Error(valueNode, $"{x} is not constant");

    @object.Type = type ?? x.Type;
    this.Assignment(x, @object.Type, "constant declaration");
    @object.Value = x.Value;
    @object.Declaration!.TypeOfNode = @object.Type;
  }

}
