using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The hierarchies of the classes of a file: a base class of the file, interfaces of the file with
/// methods and properties, <c>abstract</c>, <c>virtual</c>, <c>override</c> and <c>sealed
/// override</c> methods, implicit and explicit implementations, <c>base.M(…)</c> and
/// <c>base(…)</c>, and the type tests <c>is</c>, <c>is T t</c>, <c>as</c> and casts. Hiding a member
/// of a base class, default interface methods, static abstract members, generic interfaces, overriding
/// a member of <c>object</c> and a virtual call on the object a constructor is making are refused.
/// </summary>
internal sealed partial class Lowering {

  private readonly Dictionary<INamedTypeSymbol, IrInterfaceType> _interfaces = new(SymbolEqualityComparer.Default);

  /// <summary>The source of every call of a method, by identity, which a diagnostic about the call points at.</summary>
  private readonly Dictionary<IrMethodCall, (string Path, SyntaxNode Node)> _sites = new(ReferenceEqualityComparer.Instance);

  /// <summary>The methods of the interfaces of the file.</summary>
  private readonly HashSet<IrMethod> _signatures = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// Gives every class its base class and its interfaces, refusing a base class or an interface the
  /// file does not declare, and orders the declarations so that an interface comes after those it
  /// extends and a class after its base class: a method can then name the one it overrides or implements.
  /// </summary>
  private List<TypeDeclarationSyntax> Hierarchy(IReadOnlyList<(string Path, BoundModel Model, TypeDeclarationSyntax Type)> declarations) {
    var depths = new Dictionary<TypeDeclarationSyntax, int>();
    foreach (var (file, semantics, type) in declarations) {
      path = file;
      model = semantics;
      var symbol = semantics.GetDeclaredSymbol(type)!;
      if (type is InterfaceDeclarationSyntax) {
        depths[type] = symbol.AllInterfaces.Length;
        continue;
      }

      var depth = 0;
      for (var ancestor = symbol.BaseType; this.ClassOf(ancestor) is not null; ancestor = ancestor!.BaseType)
        ++depth;

      depths[type] = depth + 1000;
      var declared = this._classes[symbol];
      IrClassType? @base = null;
      var interfaces = new List<IrInterfaceType>();
      try {
        foreach (var listed in type is RecordDeclarationSyntax ? [] : type.BaseList?.Types ?? []) {
          var named = semantics.GetTypeInfo(listed.Type).Type as INamedTypeSymbol;
          if (named is { TypeKind: TypeKind.Interface } && this._interfaces.TryGetValue(named, out var contract))
            interfaces.Add(contract);
          else if (named is { TypeKind: TypeKind.Interface })
            throw new UnsupportedException(listed, named.IsGenericType ? "a generic interface" : $"the interface {named.Name}, which the file does not declare");
          else
            @base = this.ClassOf(named) ?? throw new UnsupportedException(listed, $"inheritance from {named?.Name}, which the file does not declare");
        }
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
      }

      declared.Inherit(@base, interfaces);
    }

    return [.. declarations.Select(entry => entry.Type).OrderBy(type => depths[type])];
  }

  /// <summary>An interface: methods and properties without bodies, each the signature of its implementations.</summary>
  private void Interface(TypeDeclarationSyntax type) {
    var symbol = model.GetDeclaredSymbol(type)!;
    var declared = this._interfaces[symbol];
    foreach (var modifier in type.Modifiers)
      if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword))
        throw new UnsupportedException(type, $"a {modifier.Text} interface");

    if (type.TypeParameterList is { } parameters)
      throw new UnsupportedException(parameters, parameters.Parameters.Any(parameter => parameter.VarianceKeyword.RawKind != 0) ? "variance (in or out on a type parameter)" : "a generic interface");

    if (type.AttributeLists.SelectMany(list => list.Attributes).FirstOrDefault() is { } attribute)
      throw new UnsupportedException(attribute, $"the attribute {attribute.Name}");

    var bases = new List<IrInterfaceType>();
    foreach (var listed in type.BaseList?.Types ?? []) {
      var named = (INamedTypeSymbol)model.GetTypeInfo(listed.Type).Type!;
      bases.Add(this._interfaces.TryGetValue(named, out var inherited)
        ? inherited
        : throw new UnsupportedException(listed, named.IsGenericType ? "a generic interface" : $"the interface {named.Name}, which the file does not declare"));
    }

    var methods = new List<IrMethod>();
    var properties = new List<IrProperty>();
    var receiver = IrType.Object(declared);
    var signature = (IMethodSymbol method, SyntaxNode node, string name, string function, IEnumerable<ParameterSyntax> parameters, IrMethodKind kind) => {
      var made = new IrMethod(name, function, false) { Dispatch = IrDispatch.Abstract, Kind = kind };
      methods.Add(made);
      this._signatures.Add(made);
      this._methods[method] = made;
      this.Body(declared, () => this.Function(function, false, method, node, parameters, new IrLocal("this", receiver), null, null, null));
      return made;
    };

    foreach (var member in type.Members) {
      if (member.Modifiers.Any(SyntaxKind.StaticKeyword) || member.Modifiers.Any(SyntaxKind.AbstractKeyword))
        throw new UnsupportedException(member, "a static abstract member");

      if (member.Modifiers.Count > 0)
        throw new UnsupportedException(member, $"a {member.Modifiers[0].Text} interface member");

      switch (member) {
        case MethodDeclarationSyntax method when method.Body is not null || method.ExpressionBody is not null:
          throw new UnsupportedException(method, "a default interface method");
        case MethodDeclarationSyntax { TypeParameterList: { } generic }:
          throw new UnsupportedException(generic, "a generic method");
        case MethodDeclarationSyntax method:
          var methodSymbol = model.GetDeclaredSymbol(method)!;
          signature(methodSymbol, method, methodSymbol.Name, $"{declared.Name}.{methodSymbol.Name}{(methods.Count(made => made.Name == methodSymbol.Name) is var count and > 0 ? count + 1 : "")}",
            method.ParameterList.Parameters, IrMethodKind.Method);
          break;
        case PropertyDeclarationSyntax property when property.AccessorList?.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) != false:
          throw new UnsupportedException(property, "a default interface method");
        case PropertyDeclarationSyntax property:
          var propertySymbol = model.GetDeclaredSymbol(property)!;
          var getter = propertySymbol.GetMethod is { } get ? signature(get, property, propertySymbol.Name, $"{declared.Name}.get_{propertySymbol.Name}", [], IrMethodKind.Getter) : null;
          var setter = propertySymbol.SetMethod is { } set ? signature(set, property, propertySymbol.Name, $"{declared.Name}.set_{propertySymbol.Name}", [], IrMethodKind.Setter) : null;
          var made = new IrProperty(propertySymbol.Name, this.ClassFieldType(propertySymbol.Type, property.Type)) { Getter = getter, Setter = setter };
          this._properties[propertySymbol] = made;
          properties.Add(made);
          break;
        default:
          throw new UnsupportedException(member, Refused(member) ?? "an interface member other than a method or a property");
      }
    }

    declared.DefineMembers(methods, properties, bases);
  }

  /// <summary>The name of a method in the IR: an explicit implementation is named as the method of the interface it implements.</summary>
  private string MethodName(IMethodSymbol symbol) => symbol.ExplicitInterfaceImplementations is [var implemented, ..] ? implemented.Name : symbol.Name;

  /// <summary>
  /// A method with the dispatch its modifiers give it - abstract, virtual, an override of the method
  /// of a base class it replaces, or a sealed override - and the methods of interfaces it implements.
  /// </summary>
  private IrMethod Dispatched(IMethodSymbol symbol, SyntaxNode node, IrMethod method) {
    this.NotHiding(symbol, node);
    if (symbol.IsStatic)
      return method;

    if (symbol.OverriddenMethod is { } overridden) {
      if (!this._methods.TryGetValue(overridden, out var replaced))
        throw new UnsupportedException(node, overridden.ContainingType.SpecialType == SpecialType.System_Object
          ? $"overriding the object member {overridden.Name}"
          : $"overriding {overridden.ContainingType.Name}.{overridden.Name}, which the file does not declare");

      method = method with { Dispatch = IrDispatch.Override, Overrides = replaced, IsSealedOverride = symbol.IsSealed };
    } else if (symbol.IsAbstract) {
      method = method with { Dispatch = IrDispatch.Abstract };
    } else if (symbol.IsVirtual) {
      method = method with { Dispatch = IrDispatch.Virtual };
    }

    return method with { Implements = this.InterfaceMembers(symbol) };
  }

  /// <summary>Refuses a member that hides a member of the same name of a base class of the file, with or without <c>new</c>.</summary>
  private void NotHiding(ISymbol symbol, SyntaxNode node) {
    if (symbol.IsOverride)
      return;

    for (var ancestor = symbol.ContainingType.BaseType; this.ClassOf(ancestor) is not null; ancestor = ancestor!.BaseType)
      if (ancestor!.GetMembers(symbol.Name).Any(member => !member.IsImplicitlyDeclared))
        throw new UnsupportedException(node, "new member hiding");
  }

  /// <summary>The methods of the interfaces of the file that a method of a class implements, implicitly or explicitly.</summary>
  private List<IrMethod> InterfaceMembers(IMethodSymbol? symbol) {
    if (symbol is null)
      return [];

    var owner = symbol.ContainingType;
    return [.. owner.AllInterfaces.Where(this._interfaces.ContainsKey).SelectMany(contract => contract.GetMembers().OfType<IMethodSymbol>())
      .Where(member => SymbolEqualityComparer.Default.Equals(owner.FindImplementationForInterfaceMember(member), symbol))
      .Select(member => this._methods.GetValueOrDefault(member)).OfType<IrMethod>()];
  }

  /// <summary>How a call written as an access selects its method: <c>base.</c> statically, a method of an interface through it, a virtual method by the receiver's class.</summary>
  private IrMethodCallKind CallKind(ExpressionSyntax access, IrMethod method)
    => access is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } ? IrMethodCallKind.Base
      : this._signatures.Contains(method) ? IrMethodCallKind.Interface
      : method.IsVirtual ? IrMethodCallKind.Virtual
      : IrMethodCallKind.Direct;

  /// <summary>The source of a call, which a diagnostic about it points at.</summary>
  private IrMethodCall Site(IrMethodCall call, SyntaxNode node) {
    this._sites[call] = (path, node);
    return call;
  }

  /// <summary>
  /// The call of the constructor of the base class a constructor of a derived class begins with: the
  /// one <c>base(…)</c> names, or the base class's constructor without parameters; nothing for a
  /// class without a base class.
  /// </summary>
  private List<IrStmt> BaseCall(INamedTypeSymbol type, ConstructorInitializerSyntax? initializer, SyntaxNode node) {
    if (this.ClassOf(type)?.Base is null)
      return [];

    IMethodSymbol target;
    List<IrExpr> arguments = [];
    if (initializer is not null) {
      if (initializer.ArgumentList.Arguments.Any(argument => argument.NameColon is not null))
        throw new UnsupportedException(initializer, "a named argument");

      target = (IMethodSymbol)model.GetSymbolInfo(initializer).Symbol!;
      arguments = [.. initializer.ArgumentList.Arguments.Select(argument => this.Value(argument.Expression))];
    } else {
      target = type.BaseType!.InstanceConstructors.FirstOrDefault(constructor => constructor.Parameters.IsEmpty)
        ?? throw new UnsupportedException(node, "a constructor without base(...) in a class whose base class has no constructor without parameters");
    }

    return [new IrExprStmt(new IrCall(this._creators[target].Function, IrType.Void, [new IrLocalRef(this._receiver!), .. arguments]))];
  }

  /// <summary>A reference as its own type, for a type test or a cast.</summary>
  private IrExpr ObjectValue(ExpressionSyntax expression) {
    var value = this.Natural(expression, this.Map(model.GetTypeInfo(expression).Type, expression));
    return value.Type.IsObject ? value : throw new UnsupportedException(expression, $"a type test or a cast of a value of the type {value.Type}");
  }

  /// <summary>
  /// <c>base</c>, a type test, a cast or an <c>as</c> to a class or an interface of the file, or
  /// <see langword="null"/> for any other expression. An upcast is the value itself.
  /// </summary>
  private IrExpr? Conversion(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case BaseExpressionSyntax:
        return this._receiver is { } receiver ? new IrLocalRef(receiver) : null;
      case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression } test when this.ReferenceOf(model.GetTypeInfo(test.Right).Type) is { } tested:
        return new IrTypeTest(this.ObjectValue(test.Left), tested);
      case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } conversion when this.ReferenceOf(model.GetTypeInfo(conversion.Right).Type) is { } converted:
        return new IrAsCast(converted, this.ObjectValue(conversion.Left));
      case CastExpressionSyntax cast when this.ReferenceOf(model.GetTypeInfo(cast.Type).Type) is { } target:
        var value = this.ObjectValue(cast.Expression);
        return value.Type.IsAssignableTo(target) ? value : new IrCast(target, value);
      case IsPatternExpressionSyntax { Pattern: var pattern } test when this.PatternType(pattern) is { } tested:
        return new IrTypeTest(this.ObjectValue(test.Expression), tested);
      case IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: var pattern } } test when this.PatternType(pattern) is { } tested:
        return new IrUnary(IrUnaryOp.LogicalNot, new IrTypeTest(this.ObjectValue(test.Expression), tested));
      case IsPatternExpressionSyntax { Pattern: DeclarationPatternSyntax } pattern:
        throw new UnsupportedException(pattern, "a pattern declaration outside the condition of an if");
      default:
        return null;
    }
  }

  /// <summary>The class or interface of the file a pattern tests for - <c>T</c>, which the parser may read as a constant - or <see langword="null"/>.</summary>
  private IrType? PatternType(PatternSyntax pattern) => pattern switch {
    TypePatternSyntax type => this.ReferenceOf(model.GetTypeInfo(type.Type).Type),
    ConstantPatternSyntax constant when model.GetSymbolInfo(constant.Expression).Symbol is INamedTypeSymbol named => this.ReferenceOf(named),
    _ => null
  };

  /// <summary>
  /// <c>if (x is T t) …</c>: the test of <c>x</c>, and in the branch taken the variable <c>t</c>
  /// declared with <c>x</c> cast to <c>T</c>; says whether the statement was one. The variable may be
  /// used only in that branch.
  /// </summary>
  private bool PatternIf(IfStatementSyntax conditional, List<IrStmt> into) {
    if (conditional.Condition is not IsPatternExpressionSyntax { Pattern: DeclarationPatternSyntax { Designation: SingleVariableDesignationSyntax name } declaration } pattern)
      return false;

    var target = this.ReferenceOf(model.GetTypeInfo(declaration.Type).Type) ?? throw new UnsupportedException(declaration, "a pattern declaration of a type other than a class or an interface");
    var symbol = (ILocalSymbol)model.GetDeclaredSymbol(name)!;
    var scope = conditional.Parent!;
    if (scope.DescendantNodes().OfType<IdentifierNameSyntax>().Any(use => use.Identifier.Text == symbol.Name && !conditional.Statement.Contains(use)
        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(use).Symbol, symbol)))
      throw new UnsupportedException(name, "a pattern variable used outside the statement its if guards");

    var value = this.ObjectValue(pattern.Expression);
    if (value is not IrLocalRef) {
      var temporary = this.Local($"$t{this._localBase + this._locals.Count}", value.Type);
      into.Add(new IrDeclare(temporary, value));
      value = new IrLocalRef(temporary);
    }

    var variable = this.Local(symbol.Name, target);
    this._variables[symbol] = variable;
    var then = this.Body(conditional.Statement);
    var cast = value.Type.IsAssignableTo(target) ? value : new IrCast(target, value);
    into.Add(new IrIf(new IrTypeTest(value, target), new IrBlock([new IrDeclare(variable, cast), .. then.Statements]),
      conditional.Else is null ? null : this.Body(conditional.Else.Statement)));
    return true;
  }

  /// <summary>Refuses every virtual or interface call on the object a constructor is making, which the module would reach (see <see cref="IrConstruction"/>).</summary>
  private void Constructed(IrModule module) {
    foreach (var (_, call) in IrConstruction.VirtualCalls(module)) {
      var (file, node) = this._sites[call];
      path = file;
      this.Report(new UnsupportedException(node, "a virtual call on 'this' during construction"));
    }
  }

}
