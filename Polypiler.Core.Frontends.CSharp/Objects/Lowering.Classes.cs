using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The classes of a file beside the static class: <c>sealed</c> classes, classes no class of the file
/// derives from, and <c>record</c>s - with instance and static fields, constructors (also chained with
/// <c>this(…)</c>), instance and static methods, auto-properties and computed properties. Each becomes
/// an <see cref="IrClassType"/>; its constructors and methods are functions of the module that take
/// the receiver first. Inheritance, interfaces, generics, events, indexers, operators, finalizers,
/// static constructors and <c>with</c> are refused by name.
/// </summary>
internal sealed partial class Lowering {

  private readonly Dictionary<INamedTypeSymbol, IrClassType> _classes = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<IMethodSymbol, IrMethod> _methods = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<IMethodSymbol, IrConstructor> _creators = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<IPropertySymbol, IrProperty> _properties = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<ISymbol, IrClassType> _staticOwners = new(SymbolEqualityComparer.Default);

  /// <summary>The members of every class whose bodies are lowered after the globals: what each function is made of.</summary>
  private readonly List<(string Path, SemanticModel Model, IrTypeDeclaration Owner, Func<IrFunction> Lower)> _bodies = [];

  /// <summary>The receiver of the member being lowered, <c>this</c>; <see langword="null"/> in a static function.</summary>
  private IrLocal? _receiver;

  /// <summary>Whether a member declares a class that holds objects - a class that is not static, or a record class - or an interface.</summary>
  private static bool IsClass(MemberDeclarationSyntax member)
    => member is ClassDeclarationSyntax type && !type.Modifiers.Any(SyntaxKind.StaticKeyword)
      || member is RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: not (int)SyntaxKind.StructKeyword }
      || member is InterfaceDeclarationSyntax;

  /// <summary>
  /// Declares the classes of every file: first a type for each, so that a field may have the type of a
  /// class declared later; then their fields and the members whose bodies are lowered later.
  /// </summary>
  private void DeclareClasses(IReadOnlyList<(string Path, SemanticModel Model, TypeDeclarationSyntax Type)> classes) {
    var names = new HashSet<string>(this._types.Select(type => type.Name));
    foreach (var (file, semantics, type) in classes) {
      path = file;
      model = semantics;
      var symbol = semantics.GetDeclaredSymbol(type)!;
      IrTypeDeclaration made = type is InterfaceDeclarationSyntax
        ? this._interfaces[symbol] = new IrInterfaceType(symbol.Name) { Visibility = VisibilityOf(symbol) }
        : this._classes[symbol] = new IrClassType(symbol.Name) {
          IsRecord = type is RecordDeclarationSyntax, Visibility = VisibilityOf(symbol), IsAbstract = symbol.IsAbstract, IsSealed = symbol.IsSealed
        };
      this._types.Add(made);
      if (!names.Add(symbol.Name))
        this.Report(new UnsupportedException(type, $"a second type named {symbol.Name}"));
    }

    var hierarchy = this.Hierarchy(classes);
    foreach (var (file, semantics, type) in hierarchy.OfType<InterfaceDeclarationSyntax>().Select(Find)) {
      path = file;
      model = semantics;
      try {
        this.Interface(type);
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
      }
    }

    (string, SemanticModel, TypeDeclarationSyntax) Find(TypeDeclarationSyntax type) => classes.First(entry => entry.Type == type);
    foreach (var (file, semantics, type) in hierarchy.Where(type => type is not InterfaceDeclarationSyntax).Select(Find)) {
      path = file;
      model = semantics;
      var declared = this._classes[semantics.GetDeclaredSymbol(type)!];
      try {
        this.Class(type, declared, classes.Select(entry => entry.Type));
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
      }

      // A class that is refused still has a type, so that its uses do not report it again.
      if (!declared.IsDefined)
        declared.Define([]);
    }
  }

  private static IrVisibility VisibilityOf(ISymbol symbol) => symbol.DeclaredAccessibility switch {
    Accessibility.Public => IrVisibility.Public,
    Accessibility.Private => IrVisibility.Private,
    Accessibility.Protected or Accessibility.ProtectedOrInternal or Accessibility.ProtectedAndInternal => IrVisibility.Protected,
    _ => IrVisibility.Internal
  };

  /// <summary>What a diagnostic calls a member a class of the subset cannot have.</summary>
  private static string? Refused(MemberDeclarationSyntax member) => member switch {
    EventDeclarationSyntax or EventFieldDeclarationSyntax => "an event",
    IndexerDeclarationSyntax => "an indexer",
    OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "an operator",
    DestructorDeclarationSyntax => "a finalizer",
    ConstructorDeclarationSyntax constructor when constructor.Modifiers.Any(SyntaxKind.StaticKeyword) => "a static constructor",
    BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => "a type nested in a class",
    FieldDeclarationSyntax or ConstructorDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax => null,
    _ => member.Kind().ToString()
  };

  private void Class(TypeDeclarationSyntax type, IrClassType declared, IEnumerable<TypeDeclarationSyntax> all) {
    var symbol = model.GetDeclaredSymbol(type)!;
    foreach (var modifier in type.Modifiers)
      if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.SealedKeyword or SyntaxKind.AbstractKeyword))
        throw new UnsupportedException(type, $"a {modifier.Text} class");

    if (type.AttributeLists.SelectMany(list => list.Attributes).FirstOrDefault() is { } attribute)
      throw new UnsupportedException(attribute, $"the attribute {attribute.Name}");

    if (type.TypeParameterList is not null)
      throw new UnsupportedException(type.TypeParameterList, "a generic class");

    if (type is RecordDeclarationSyntax && (type.BaseList is not null || all.Any(other => other.BaseList is not null && other.BaseList.Types.Any(@base
        => SymbolEqualityComparer.Default.Equals(model.Compilation.GetSemanticModel(other.SyntaxTree).GetTypeInfo(@base.Type).Type, symbol)))))
      throw new UnsupportedException(type, "a record in a hierarchy");

    if (type.Members.FirstOrDefault(member => Refused(member) is not null) is { } refused)
      throw new UnsupportedException(refused, Refused(refused)!);

    var fields = new List<IrField>();
    var initializers = new List<(IrField Field, ExpressionSyntax Value)>();
    var properties = new List<IrProperty>();
    var methods = new List<IrMethod>();
    var constructors = new List<IrConstructor>();
    var statics = new List<IFieldSymbol>();
    var overloads = new Dictionary<string, int>();
    string Unique(string name) {
      var count = overloads[name] = overloads.GetValueOrDefault(name) + 1;
      return count == 1 ? $"{declared.Name}.{name}" : $"{declared.Name}.{name}{count}";
    }

    if (type is RecordDeclarationSyntax { ParameterList: { } positional } record) {
      foreach (var parameter in positional.Parameters) {
        if (parameter.Modifiers.Count > 0 || parameter.Default is not null || parameter.AttributeLists.Count > 0)
          throw new UnsupportedException(parameter, "a positional parameter with a modifier, an attribute or a default value");

        var property = symbol.GetMembers(parameter.Identifier.Text).OfType<IPropertySymbol>().Single();
        var field = new IrField(property.Name, this.ClassFieldType(property.Type, parameter), false);
        this._fields[property] = field;
        fields.Add(field);
        properties.Add(new IrProperty(property.Name, field.Type) { BackingField = field, HasSetter = true, IsInitOnly = true });
      }

      var primary = symbol.InstanceConstructors.First(constructor => constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() == type));
      var function = Unique("ctor");
      var creator = new IrConstructor(function);
      constructors.Add(creator);
      this._creators[primary] = creator;
      var owner = declared;
      var components = fields.ToList();
      this.Body(declared, () => this.Function(function, false, primary, record, positional.Parameters, Receiver(owner), () => [
        .. this.Initializers(initializers),
        .. components.Select((field, index) => (IrStmt)new IrFieldStore(new IrLocalRef(this._receiver!), field, new IrLocalRef(this._variables[primary.Parameters[index]])))
      ], null, null));
    }

    foreach (var member in type.Members.OfType<FieldDeclarationSyntax>()) {
      foreach (var modifier in member.Modifiers)
        if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ReadOnlyKeyword
            or SyntaxKind.StaticKeyword or SyntaxKind.ConstKeyword))
          throw new UnsupportedException(member, $"a {modifier.Text} field");

      // A constant has been folded into every use of it by the compiler.
      if (member.Modifiers.Any(SyntaxKind.ConstKeyword))
        continue;

      foreach (var variable in member.Declaration.Variables) {
        var fieldSymbol = (IFieldSymbol)model.GetDeclaredSymbol(variable)!;
        var fieldType = this.ClassFieldType(fieldSymbol.Type, member.Declaration.Type);
        if (fieldSymbol.IsStatic) {
          this._staticOwners[fieldSymbol] = declared;
          this.State(member, variable, fieldSymbol, fieldType, true);
          statics.Add(fieldSymbol);
          continue;
        }

        var field = new IrField(fieldSymbol.Name, fieldType, fieldSymbol.IsReadOnly) { Visibility = VisibilityOf(fieldSymbol) };
        this._fields[fieldSymbol] = field;
        fields.Add(field);
        if (variable.Initializer is { } initializer)
          initializers.Add((field, initializer.Value));
      }
    }

    foreach (var member in type.Members.OfType<PropertyDeclarationSyntax>())
      properties.Add(this.Property(member, declared, fields, initializers, methods, Unique));

    declared.Define(fields);
    foreach (var member in type.Members.OfType<MethodDeclarationSyntax>()) {
      foreach (var modifier in member.Modifiers)
        if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.StaticKeyword or SyntaxKind.ProtectedKeyword
            or SyntaxKind.VirtualKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.OverrideKeyword or SyntaxKind.SealedKeyword))
          throw new UnsupportedException(member, modifier.IsKind(SyntaxKind.NewKeyword) ? "new member hiding" : $"a {modifier.Text} method");

      if (member.TypeParameterList is not null)
        throw new UnsupportedException(member.TypeParameterList, "a generic method");

      var methodSymbol = model.GetDeclaredSymbol(member)!;
      var method = this.Dispatched(methodSymbol, member, new IrMethod(this.MethodName(methodSymbol), Unique(this.MethodName(methodSymbol)), methodSymbol.IsStatic) {
        Visibility = methodSymbol.ExplicitInterfaceImplementations.Length > 0 ? IrVisibility.Public : VisibilityOf(methodSymbol)
      });
      methods.Add(method);
      this._methods[methodSymbol] = method;
      this.Body(declared, () => this.Function(method.Function, false, methodSymbol, member, member.ParameterList.Parameters,
        method.IsStatic ? null : Receiver(declared), null, member.Body, member.ExpressionBody?.Expression));
    }

    var explicitConstructors = type.Members.OfType<ConstructorDeclarationSyntax>().ToList();
    foreach (var constructor in explicitConstructors) {
      var constructorSymbol = model.GetDeclaredSymbol(constructor)!;
      var creator = new IrConstructor(Unique("ctor")) { Visibility = VisibilityOf(constructorSymbol) };
      constructors.Add(creator);
      this._creators[constructorSymbol] = creator;
    }

    foreach (var constructor in explicitConstructors) {
      var constructorSymbol = model.GetDeclaredSymbol(constructor)!;
      var creator = this._creators[constructorSymbol];
      this.Body(declared, () => this.Function(creator.Function, false, constructorSymbol, constructor, constructor.ParameterList.Parameters, Receiver(declared),
        () => this.Chain(constructor) ?? [.. this.BaseCall(symbol, constructor.Initializer, constructor), .. this.Initializers(initializers, declared)],
        constructor.Body, constructor.ExpressionBody?.Expression));
    }

    if (constructors.Count == 0) {
      var implicitConstructor = symbol.InstanceConstructors.Single();
      var creator = new IrConstructor(Unique("ctor")) { Visibility = VisibilityOf(implicitConstructor) };
      constructors.Add(creator);
      this._creators[implicitConstructor] = creator;
      this.Body(declared, () => this.Function(creator.Function, false, implicitConstructor, type, [], Receiver(declared),
        () => [.. this.BaseCall(symbol, null, type), .. this.Initializers(initializers, declared)], null, null));
    }

    this._members[declared] = (constructors, methods, properties, statics);
  }

  /// <summary>The members of each class, until the globals its static fields are have been made.</summary>
  private readonly Dictionary<IrClassType, (List<IrConstructor>, List<IrMethod>, List<IrProperty>, List<IFieldSymbol>)> _members = [];

  /// <summary>The receiver of a member of a class: the parameter <c>this</c>.</summary>
  private static IrLocal Receiver(IrClassType owner) => new("this", IrType.Object(owner));

  private void Body(IrTypeDeclaration owner, Func<IrFunction> lower) => this._bodies.Add((path, model, owner, lower));

  /// <summary>
  /// Gives every class its members and lowers their bodies, after the globals: a static field of a
  /// class is one of them, and a body may read any.
  /// </summary>
  private List<IrFunction> ClassBodies() {
    foreach (var (owner, (constructors, methods, properties, statics)) in this._members)
      owner.DefineMembers(constructors, methods, properties, [.. statics.Select(field => this._globals.GetValueOrDefault(field)).OfType<IrGlobal>()]);

    var functions = new List<IrFunction>();
    foreach (var (file, semantics, _, lower) in this._bodies) {
      path = file;
      model = semantics;
      try {
        functions.Add(lower());
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
        this._receiver = null;
      }
    }

    return functions;
  }

  /// <summary>The type of a field of a class: a truth value, a number, a struct, an array of numbers or a reference to an object.</summary>
  private IrType ClassFieldType(ITypeSymbol type, SyntaxNode node) {
    var mapped = this.Map(type, node);
    return mapped.IsString || mapped.Element is { IsNumeric: false } ? throw new UnsupportedException(node, $"a field of the type {mapped}") : mapped;
  }

  /// <summary>
  /// A property: an auto-property is a field of the class, written by its initializer at the start of
  /// every constructor that does not chain; a computed property is a pair of accessor methods.
  /// </summary>
  private IrProperty Property(PropertyDeclarationSyntax member, IrClassType owner, List<IrField> fields, List<(IrField Field, ExpressionSyntax Value)> initializers,
      List<IrMethod> methods, Func<string, string> unique) {
    var symbol = model.GetDeclaredSymbol(member)!;
    foreach (var modifier in member.Modifiers)
      if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ProtectedKeyword))
        throw new UnsupportedException(member, modifier.Kind() switch {
          SyntaxKind.StaticKeyword => "a static property",
          SyntaxKind.NewKeyword => "new member hiding",
          SyntaxKind.VirtualKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.OverrideKeyword or SyntaxKind.SealedKeyword => "a virtual, abstract or overriding property",
          _ => $"a {modifier.Text} property"
        });

    if (member.ExplicitInterfaceSpecifier is not null)
      throw new UnsupportedException(member, "an explicit implementation of a property");

    this.NotHiding(symbol, member);

    var type = this.ClassFieldType(symbol.Type, member.Type);
    var visibility = VisibilityOf(symbol);
    var accessors = member.AccessorList?.Accessors ?? [];
    if (accessors.FirstOrDefault(accessor => accessor.Body is null && accessor.ExpressionBody is null) is not null) {
      if (accessors.FirstOrDefault(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) is { } mixed)
        throw new UnsupportedException(mixed, "a property with an automatic and a written accessor");

      if (this.InterfaceMembers(symbol.GetMethod).Count > 0 || this.InterfaceMembers(symbol.SetMethod).Count > 0)
        throw new UnsupportedException(member, "an auto-property that implements a property of an interface");

      var setter = accessors.FirstOrDefault(accessor => accessor.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration);
      var field = new IrField(symbol.Name, type, setter is null) { Visibility = visibility };
      this._fields[symbol] = field;
      fields.Add(field);
      if (member.Initializer is { } initializer)
        initializers.Add((field, initializer.Value));

      return new IrProperty(symbol.Name, type) {
        BackingField = field, HasSetter = setter is not null, IsInitOnly = setter?.IsKind(SyntaxKind.InitAccessorDeclaration) == true, Visibility = visibility
      };
    }

    if (member.Initializer is not null)
      throw new UnsupportedException(member.Initializer, "an initializer of a property that is not automatic");

    IrMethod? Accessor(IMethodSymbol? accessorSymbol, IrMethodKind kind, SyntaxNode node, BlockSyntax? body, ExpressionSyntax? expression) {
      if (accessorSymbol is null)
        return null;

      var method = new IrMethod(symbol.Name, unique((kind == IrMethodKind.Getter ? "get_" : "set_") + symbol.Name), false) {
        Kind = kind, Visibility = visibility, Implements = this.InterfaceMembers(accessorSymbol)
      };
      methods.Add(method);
      this._methods[accessorSymbol] = method;
      this.Body(owner, () => this.Function(method.Function, false, accessorSymbol, node, [], Receiver(owner), null, body, expression));
      return method;
    }

    IrMethod? getter, written = null;
    if (member.ExpressionBody is { } arrow) {
      getter = Accessor(symbol.GetMethod, IrMethodKind.Getter, member, null, arrow.Expression);
    } else {
      var get = accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
      var set = accessors.FirstOrDefault(accessor => !accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
      if (accessors.FirstOrDefault(accessor => accessor.Modifiers.Count > 0) is { } restricted)
        throw new UnsupportedException(restricted, "an accessor with an accessibility of its own");

      if (set?.IsKind(SyntaxKind.InitAccessorDeclaration) == true)
        throw new UnsupportedException(set, "an init accessor that is not automatic");

      getter = get is null ? null : Accessor(symbol.GetMethod, IrMethodKind.Getter, get, get.Body, get.ExpressionBody?.Expression);
      written = set is null ? null : Accessor(symbol.SetMethod, IrMethodKind.Setter, set, set.Body, set.ExpressionBody?.Expression);
    }

    var property = new IrProperty(symbol.Name, type) { Getter = getter, Setter = written, Visibility = visibility };
    this._properties[symbol] = property;
    return property;
  }

  /// <summary>
  /// The field initializers of a class as stores into the new object, in declaration order. C# runs
  /// those of a derived class before the constructor of its base class, the IR after it: an
  /// initializer of a derived class may therefore have no effect and read nothing that could change.
  /// </summary>
  private List<IrStmt> Initializers(List<(IrField Field, ExpressionSyntax Value)> initializers, IrClassType? owner = null)
    => [.. initializers.Select(entry => {
      var value = Convert(this.Value(entry.Value), entry.Field.Type);
      if (owner?.Base is not null && !IrTree.Nodes(value).All(node => node is IrConst or IrStrConst or IrConstRef or IrUnary or IrBinary or IrConvert or IrConditional
          or IrNewArray or IrNull or IrStructNew or IrStructDefault or IrIntrinsic))
        throw new UnsupportedException(entry.Value, "an initializer of a field of a derived class that is not a constant");

      return (IrStmt)new IrFieldStore(new IrLocalRef(this._receiver!), entry.Field, value);
    })];

  /// <summary>The call of the constructor <c>this(…)</c> names, as the first statement; <see langword="null"/> for a constructor without one.</summary>
  private List<IrStmt>? Chain(ConstructorDeclarationSyntax constructor) {
    if (constructor.Initializer is not { } initializer)
      return null;

    if (initializer.IsKind(SyntaxKind.BaseConstructorInitializer))
      return null;

    if (initializer.ArgumentList.Arguments.Any(argument => argument.NameColon is not null))
      throw new UnsupportedException(initializer, "a named argument");

    var target = (IMethodSymbol)model.GetSymbolInfo(initializer).Symbol!;
    var arguments = initializer.ArgumentList.Arguments.Select(argument => this.Value(argument.Expression)).ToList();
    return [new IrExprStmt(new IrCall(this._creators[target].Function, IrType.Void, [new IrLocalRef(this._receiver!), .. arguments]))];
  }

  /// <summary>The class a type symbol names, or <see langword="null"/>.</summary>
  private IrClassType? ClassOf(ITypeSymbol? type) => type is INamedTypeSymbol named ? this._classes.GetValueOrDefault(named) : null;

  /// <summary>The reference type of a class or an interface of the file a type symbol names, or <see langword="null"/>.</summary>
  private IrType? ReferenceOf(ITypeSymbol? type)
    => this.ClassOf(type) is { } owner ? IrType.Object(owner)
      : type is INamedTypeSymbol named && this._interfaces.TryGetValue(named, out var contract) ? IrType.Object(contract)
      : null;

  /// <summary>Whether a type symbol is a class or an interface of the file.</summary>
  private bool IsFileType(ITypeSymbol? type) => this.ReferenceOf(type) is not null;

  /// <summary>The object a member is reached through: the target of a member access, or the receiver for a bare name.</summary>
  private IrExpr Owner(ExpressionSyntax access, SyntaxNode node) => access is MemberAccessExpressionSyntax member
    ? this.Value(member.Expression)
    : this._receiver is { } receiver ? new IrLocalRef(receiver) : throw new UnsupportedException(node, "an instance member used without an object");

  /// <summary>
  /// An expression about objects as its natural value, or <see langword="null"/> for any other: <c>this</c>,
  /// a field or a property of an object, a method of a class called, a new object, <c>is null</c> and
  /// <c>ReferenceEquals</c>.
  /// </summary>
  private IrExpr? ObjectNatural(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case ThisExpressionSyntax:
        return this._receiver is { } receiver ? new IrLocalRef(receiver) : null;
      case IdentifierNameSyntax or MemberAccessExpressionSyntax when model.GetSymbolInfo(expression).Symbol is { IsStatic: false } member
          && member.ContainingType is { } owner && this.IsFileType(owner):
        if (member is IPropertySymbol property && this._properties.TryGetValue(property, out var computed))
          return computed.Getter is { } getter
            ? this.Site(new IrMethodCall(type, getter, this.Owner(expression, expression), []) { Kind = this.CallKind(expression, getter) }, expression)
            : throw new UnsupportedException(expression, $"a read of the property {property.Name}, which has no getter");

        return this._fields.TryGetValue(member, out var field) ? new IrFieldLoad(this.Owner(expression, expression), field) : null;
      case InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method:
        if (method is { Name: "ReferenceEquals", ContainingType.SpecialType: SpecialType.System_Object } && invocation.ArgumentList.Arguments is [var first, var second])
          return this.Identity(first.Expression, second.Expression, invocation);

        if (method is { Name: "Equals", Parameters: [{ Type: var other }] } && this.ClassOf(method.ContainingType) is { IsRecord: true }
            && SymbolEqualityComparer.Default.Equals(other, method.ContainingType) && invocation.Expression is MemberAccessExpressionSyntax receiver2)
          return new IrStructEquals(this.Value(receiver2.Expression), this.Value(invocation.ArgumentList.Arguments[0].Expression));

        if (!this._methods.TryGetValue(method, out var called))
          return this.ClassOf(method.ContainingType) is null ? null : throw new UnsupportedException(invocation, $"{method.ContainingType.Name}.{method.Name}");

        if (invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null || argument.RefKindKeyword.RawKind != 0))
          throw new UnsupportedException(invocation, "a named or ref argument");

        var target = called.IsStatic ? null : this.Owner(invocation.Expression, invocation);
        return this.Site(new IrMethodCall(type, called, target, [.. invocation.ArgumentList.Arguments.Select(argument => this.Value(argument.Expression))]) {
          Kind = this.CallKind(invocation.Expression, called)
        }, invocation);
      case BaseObjectCreationExpressionSyntax creation when type.IsObject:
        if (creation.Initializer is not null)
          throw new UnsupportedException(creation, "an object initializer inside an expression");

        var constructor = (IMethodSymbol)model.GetSymbolInfo(creation).Symbol!;
        var arguments = creation.ArgumentList?.Arguments ?? [];
        if (arguments.Any(argument => argument.NameColon is not null))
          throw new UnsupportedException(creation, "a named argument");

        return new IrNew(type, this._creators[constructor], [.. arguments.Select(argument => this.Value(argument.Expression))]);
      case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax constant } pattern when IsNull(constant.Expression):
        return this.Identity(pattern.Expression, constant.Expression, pattern);
      case IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax constant } } pattern when IsNull(constant.Expression):
        return new IrUnary(IrUnaryOp.LogicalNot, this.Identity(pattern.Expression, constant.Expression, pattern));
      default:
        return this.Conversion(expression, type);
    }
  }

  /// <summary>Whether an expression is the literal <c>null</c>.</summary>
  private static bool IsNull(ExpressionSyntax expression) => expression.IsKind(SyntaxKind.NullLiteralExpression);

  /// <summary>A reference as its own type, not the <c>object</c> a comparison converts it to; <c>null</c> as the other side's type.</summary>
  private IrExpr Reference(ExpressionSyntax expression, ExpressionSyntax other, SyntaxNode node) {
    if (IsNull(expression))
      return this.ReferenceOf(model.GetTypeInfo(other).Type) is { } owner
        ? new IrNull(owner)
        : throw new UnsupportedException(node, "a comparison of null with null");

    var value = this.Natural(expression, this.Map(model.GetTypeInfo(expression).Type, expression));
    return value.Type.IsObject ? value : throw new UnsupportedException(node, $"a comparison of references of the type {value.Type}");
  }

  /// <summary>Whether two references name the same object.</summary>
  private IrExpr Identity(ExpressionSyntax left, ExpressionSyntax right, SyntaxNode node) {
    var first = this.Reference(left, right, node);
    var second = this.Reference(right, left, node);
    return first.Type.IsAssignableTo(second.Type) || second.Type.IsAssignableTo(first.Type)
      ? new IrReferenceEquals(first, second)
      : throw new UnsupportedException(node, $"a comparison of {first.Type} with {second.Type}");
  }

  /// <summary>
  /// <c>==</c> and <c>!=</c> where an operand is an object or <c>null</c>: identity, and for a record
  /// the field-wise equality its operators define; <see langword="null"/> for a comparison of anything else.
  /// </summary>
  private IrExpr? ObjectEquality(BinaryExpressionSyntax binary) {
    if (binary.Kind() is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression))
      return null;

    var owner = this.ReferenceOf(model.GetTypeInfo(binary.Left).Type) ?? this.ReferenceOf(model.GetTypeInfo(binary.Right).Type);
    if (owner is null)
      return null;

    IrExpr equal = owner.ClassType is { IsRecord: true }
      ? new IrStructEquals(this.Reference(binary.Left, binary.Right, binary), this.Reference(binary.Right, binary.Left, binary))
      : this.Identity(binary.Left, binary.Right, binary);
    return binary.IsKind(SyntaxKind.EqualsExpression) ? equal : new IrUnary(IrUnaryOp.LogicalNot, equal);
  }

  /// <summary>
  /// What an assignment to a field or a property of an object writes and, for a compound assignment,
  /// reads: the object is evaluated once into a temporary when it is read as well; <see langword="null"/> for any other target.
  /// </summary>
  private (Func<IrExpr> Read, Func<IrExpr, IrStmt> Write)? ObjectTarget(ExpressionSyntax target, List<IrStmt> into, bool alsoRead) {
    if (target is not (IdentifierNameSyntax or MemberAccessExpressionSyntax) || model.GetSymbolInfo(target).Symbol is not { IsStatic: false } member
        || member.ContainingType is not { } owner || !this.IsFileType(owner))
      return null;

    var computed = member is IPropertySymbol property ? this._properties.GetValueOrDefault(property) : null;
    if (computed is null && !this._fields.ContainsKey(member))
      return null;

    var holder = this.Owner(target, target);
    if (alsoRead)
      holder = this.Once(holder, into);

    if (computed is not null) {
      if (computed.Setter is not { } setter)
        throw new UnsupportedException(target, $"a write of the property {computed.Name}, which has no setter");

      return (() => computed.Getter is { } getter
          ? this.Site(new IrMethodCall(computed.Type, getter, holder, []) { Kind = this.CallKind(target, getter) }, target)
          : throw new UnsupportedException(target, $"a read of the property {computed.Name}, which has no getter"),
        value => new IrExprStmt(this.Site(new IrMethodCall(IrType.Void, setter, holder, [value]) { Kind = this.CallKind(target, setter) }, target)));
    }

    var field = this._fields[member];
    return (() => new IrFieldLoad(holder, field), value => new IrFieldStore(holder, field, value));
  }

  /// <summary>
  /// <c>new C(…) { P = v, … }</c> of a class: the object made into a temporary, or into the variable
  /// being declared, and the entries stored into it one after the other.
  /// </summary>
  private (IrExpr Value, List<IrStmt> After) ObjectInitialized(BaseObjectCreationExpressionSyntax creation, InitializerExpressionSyntax initializer, IrType type, List<IrStmt> into, IrLocal? declared) {
    var constructor = (IMethodSymbol)model.GetSymbolInfo(creation).Symbol!;
    var start = new IrNew(type, this._creators[constructor], [.. (creation.ArgumentList?.Arguments ?? []).Select(argument => this.Value(argument.Expression))]);
    var target = declared ?? this.Local($"$t{this._locals.Count}", type);
    var stores = new List<IrStmt>();
    foreach (var expression in initializer.Expressions) {
      if (expression is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: IdentifierNameSyntax name } entry
          || model.GetSymbolInfo(name).Symbol is not { } member)
        throw new UnsupportedException(expression, "an object initializer entry other than a member and its value");

      var value = this.Value(entry.Right);
      if (member is IPropertySymbol property && this._properties.TryGetValue(property, out var computed))
        stores.Add(computed.Setter is { } setter
          ? new IrExprStmt(new IrMethodCall(IrType.Void, setter, new IrLocalRef(target), [value]))
          : throw new UnsupportedException(expression, $"a write of the property {computed.Name}, which has no setter"));
      else
        stores.Add(new IrFieldStore(new IrLocalRef(target), this._fields[member], value));
    }

    if (declared is not null)
      return (start, stores);

    into.Add(new IrDeclare(target, start));
    into.AddRange(stores);
    return (new IrLocalRef(target), []);
  }

}
