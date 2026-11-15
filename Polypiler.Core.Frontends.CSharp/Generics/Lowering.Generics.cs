using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Generic static methods of the static class and generic classes beside it, monomorphized: every
/// closed instantiation the module reaches - from its non-generic methods, from the bodies of other
/// instantiations, and from <c>--instantiate</c> - is lowered once with its type arguments in place
/// of its type parameters (<see cref="BoundModel"/>), as a function or a class of its own named
/// <c>Name&lt;arguments&gt;</c> that carries its <see cref="IrGenericOrigin"/>. A generic method is
/// lowered once more with its parameters as types of their own, the template a backend may write
/// back as one generic definition. The equality and the order a constraint brings
/// (<c>IEquatable&lt;T&gt;</c>, <c>IComparable&lt;T&gt;</c>, <c>EqualityComparer&lt;T&gt;.Default</c>,
/// <c>Comparer&lt;T&gt;.Default</c>), <c>default(T)</c> and <c>new T()</c> follow the type argument.
/// Generic math, <c>typeof(T)</c>, <c>is T</c>, generic delegates and polymorphic recursion are refused.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>How many instantiations one generic method may have before its instantiations are taken not to end.</summary>
  private const int InstantiationLimit = 64;

  /// <summary>The generic methods of the static class, by their definitions, with where they are declared.</summary>
  private readonly Dictionary<IMethodSymbol, (string Path, BoundModel Model, MethodDeclarationSyntax Syntax)> _genericMethods = new(SymbolEqualityComparer.Default);

  /// <summary>The generic classes of the file, by their definitions, with where they are declared.</summary>
  private readonly Dictionary<INamedTypeSymbol, (string Path, BoundModel Model, TypeDeclarationSyntax Syntax)> _genericClasses = new(SymbolEqualityComparer.Default);

  /// <summary>The instantiations of generic methods by their names in the IR, in the order they were reached.</summary>
  private readonly Dictionary<string, IMethodSymbol> _instances = [];

  /// <summary>The instantiations reached and not lowered yet.</summary>
  private readonly Queue<string> _pending = [];

  /// <summary>The instantiations <c>--instantiate</c> asks for.</summary>
  private IReadOnlyList<CSharpInstantiation> _requested = [];

  /// <summary>The generic method an instantiation of which is being lowered, or <see langword="null"/>.</summary>
  private IMethodSymbol? _definition;

  /// <summary>While a template is lowered, the type that stands for each of its type parameters; <see langword="null"/> otherwise.</summary>
  private Dictionary<ITypeParameterSymbol, IrType>? _standIns;

  /// <summary>While a template is lowered, the calls it makes of instantiations in terms of its parameters.</summary>
  private Dictionary<string, IrGenericCall> _templateCalls = [];

  /// <summary>The instantiations of generic methods that <c>--instantiate</c> names, which are lowered whether the module uses them or not.</summary>
  public IReadOnlyList<CSharpInstantiation> Requested {
    get => this._requested;
    init => this._requested = value;
  }

  /// <summary>Refused where a template is lowered, which a construct of it cannot be stated in terms of its parameters.</summary>
  private sealed class OpenException : Exception;

  /// <summary>Whether a type of the IR stands for a type parameter of the template being lowered.</summary>
  private bool IsStandIn(IrType type) => this._standIns?.ContainsValue(type) == true;

  /// <summary>
  /// What <see cref="Map"/> makes of a type that only a generic definition has: the stand-in of a
  /// type parameter in a template; a type parameter outside one is refused. <see langword="null"/> for every other type.
  /// </summary>
  private IrType? Open(ITypeSymbol? type, SyntaxNode node) => type switch {
    ITypeParameterSymbol parameter => this._standIns?.GetValueOrDefault(parameter) ?? throw new UnsupportedException(node, $"the type parameter {parameter.Name} outside a generic definition"),
    _ => null
  };

  /// <summary>Takes note of a generic method of the static class, whose instantiations are lowered when the module reaches them.</summary>
  private void Generic(MethodDeclarationSyntax method) {
    foreach (var modifier in method.Modifiers)
      if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.StaticKeyword or SyntaxKind.UnsafeKeyword))
        throw new UnsupportedException(method, modifier.Text);

    var symbol = model.GetDeclaredSymbol(method)!;
    this.Constraints(symbol.TypeParameters, method.TypeParameterList!);
    this._genericMethods[symbol] = (path, model, method);
  }

  /// <summary>
  /// Takes note of the generic classes among the declarations and returns the others: a generic class
  /// is declared once per instantiation the module reaches. It has no base class or interface.
  /// </summary>
  private List<(string Path, BoundModel Model, TypeDeclarationSyntax Type)> GenericClasses(IReadOnlyList<(string Path, BoundModel Model, TypeDeclarationSyntax Type)> declarations) {
    var others = new List<(string Path, BoundModel Model, TypeDeclarationSyntax Type)>();
    foreach (var (file, semantics, type) in declarations) {
      if (type is InterfaceDeclarationSyntax || type.TypeParameterList is null) {
        others.Add((file, semantics, type));
        continue;
      }

      path = file;
      model = semantics;
      try {
        if (type.BaseList is not null || type.Modifiers.Any(SyntaxKind.AbstractKeyword))
          throw new UnsupportedException(type, "a generic class in a hierarchy");

        var symbol = semantics.GetDeclaredSymbol(type)!;
        this.Constraints(symbol.TypeParameters, type.TypeParameterList);
        this._genericClasses[symbol.OriginalDefinition] = (file, semantics, type);
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
      }
    }

    return others;
  }

  /// <summary>The constraints of type parameters as the IR keeps them; a constraint outside the subset is refused.</summary>
  private List<IrGenericConstraint> Constraints(IEnumerable<ITypeParameterSymbol> parameters, SyntaxNode node) {
    var constraints = new List<IrGenericConstraint>();
    foreach (var parameter in parameters) {
      void Add(IrGenericConstraintKind kind, IrType? contract = null) => constraints.Add(new(parameter.Name, kind) { Interface = contract });
      if (parameter.HasReferenceTypeConstraint)
        Add(IrGenericConstraintKind.Class);

      if (parameter.HasUnmanagedTypeConstraint)
        Add(IrGenericConstraintKind.Unmanaged);
      else if (parameter.HasValueTypeConstraint)
        Add(IrGenericConstraintKind.Struct);

      if (parameter.HasNotNullConstraint)
        Add(IrGenericConstraintKind.NotNull);

      foreach (var type in parameter.ConstraintTypes) {
        var generic = type is INamedTypeSymbol { IsGenericType: true, TypeArguments: [var argument] } named && SymbolEqualityComparer.Default.Equals(argument, parameter)
          ? named.OriginalDefinition.ToDisplayString()
          : null;
        if (generic == "System.IEquatable<T>")
          Add(IrGenericConstraintKind.Equatable);
        else if (generic == "System.IComparable<T>")
          Add(IrGenericConstraintKind.Comparable);
        else if (type.ContainingNamespace?.ToDisplayString() == "System.Numerics")
          throw new UnsupportedException(node, $"generic math ({type.ToDisplayString()})");
        else if (type is INamedTypeSymbol { TypeKind: TypeKind.Interface } contract && this._interfaces.TryGetValue(contract, out var declared))
          Add(IrGenericConstraintKind.Interface, IrType.Object(declared));
        else
          throw new UnsupportedException(node, $"the constraint {type.ToDisplayString()}");
      }

      if (parameter.HasConstructorConstraint)
        Add(IrGenericConstraintKind.New);
    }

    return constraints;
  }

  /// <summary>The origin of an instantiation: the definition, its parameters, the arguments as the IR types them, and its constraints.</summary>
  private IrGenericOrigin Origin(string name, IReadOnlyList<ITypeParameterSymbol> parameters, IEnumerable<ITypeSymbol> arguments, SyntaxNode node)
    => new(name, [.. parameters.Select(parameter => parameter.Name)], [.. arguments.Select(argument => this.Map(argument, node))], this.Constraints(parameters, node));

  /// <summary>
  /// The name in the IR of the instantiation a call of a generic method of the static class reaches,
  /// which is lowered later when it has not been reached before. In a template a call whose type
  /// arguments name the template's parameters is named in their terms and noted.
  /// </summary>
  private string Instance(IMethodSymbol method, SyntaxNode site) {
    var definition = method.OriginalDefinition;
    // A call of the definition within it, with type arguments that contain its own type parameters, instantiates it ever anew.
    if (model.Inner.GetSymbolInfo(site).Symbol is IMethodSymbol { IsGenericMethod: true } written
        && SymbolEqualityComparer.Default.Equals(written.OriginalDefinition, this._definition)
        && written.TypeArguments.Any(argument => argument is not ITypeParameterSymbol && Mentions(argument, definition.TypeParameters)))
      throw new UnsupportedException(site, $"the instantiations of '{Display(definition)}' do not end");

    var name = IrGenerics.Mangle(method.Name, method.TypeArguments.Select(argument => this.Map(argument, site)));
    if (this._standIns is not null) {
      if (method.TypeArguments.Any(argument => Mentions(argument, this._standIns.Keys)))
        this._templateCalls[name] = new IrGenericCall(method.Name, [.. method.TypeArguments.Select(argument => this.Map(argument, site))]);

      return name;
    }

    if (this._instances.ContainsKey(name))
      return name;

    if (this._instances.Values.Count(instance => SymbolEqualityComparer.Default.Equals(instance.OriginalDefinition, definition)) >= InstantiationLimit)
      throw new UnsupportedException(site, $"the instantiations of '{Display(definition)}' do not end");

    this._instances[name] = method;
    this._pending.Enqueue(name);
    return name;
  }

  /// <summary>Whether a type names one of the type parameters, itself or within an array or a type argument.</summary>
  private static bool Mentions(ITypeSymbol type, IEnumerable<ITypeParameterSymbol> parameters) => type switch {
    ITypeParameterSymbol parameter => parameters.Contains(parameter, SymbolEqualityComparer.Default),
    IArrayTypeSymbol array => Mentions(array.ElementType, parameters),
    INamedTypeSymbol named => named.TypeArguments.Any(argument => Mentions(argument, parameters)),
    _ => false
  };

  /// <summary>Whether a type names a type parameter, itself or within an array or a type argument.</summary>
  private static bool IsOpen(ITypeSymbol type) => type switch {
    ITypeParameterSymbol => true,
    IArrayTypeSymbol array => IsOpen(array.ElementType),
    INamedTypeSymbol named => named.TypeArguments.Any(IsOpen),
    _ => false
  };

  /// <summary>A generic definition as the source names it, with its type parameters: <c>Depth&lt;T&gt;</c>.</summary>
  private static string Display(IMethodSymbol definition) => $"{definition.Name}<{string.Join(", ", definition.TypeParameters.Select(parameter => parameter.Name))}>";

  /// <summary>
  /// Lowers the instantiations the module reaches, those <c>--instantiate</c> names first, and the
  /// bodies of the classes they instantiate, until no new one is reached; refuses a public generic
  /// method nothing instantiates; and gives every instantiation the template of its definition.
  /// </summary>
  private List<IrFunction> Instantiations(List<IrFunction> functions) {
    foreach (var requested in this._requested)
      this.Request(requested);

    var lowered = new Dictionary<string, IrFunction>();
    while (this._pending.Count > 0) {
      while (this._pending.TryDequeue(out var name)) {
        var method = this._instances[name];
        // A generic method refused where it is declared has been reported there.
        if (!this._genericMethods.TryGetValue(method.OriginalDefinition, out var source))
          continue;

        var (file, semantics, syntax) = source;
        path = file;
        model = semantics.With(method.OriginalDefinition.TypeParameters.Zip(method.TypeArguments));
        this._definition = method.OriginalDefinition;
        try {
          var origin = this.Origin(method.Name, method.OriginalDefinition.TypeParameters, method.TypeArguments, syntax);
          lowered[name] = this.Method(syntax) with { Origin = origin };
        } catch (UnsupportedException unsupported) {
          this.Report(unsupported);
        } finally {
          this._definition = null;
        }
      }

      functions.AddRange(this.ClassBodies());
    }

    foreach (var (definition, (file, _, syntax)) in this._genericMethods)
      if (definition.DeclaredAccessibility == Accessibility.Public && !this._instances.Values.Any(instance => SymbolEqualityComparer.Default.Equals(instance.OriginalDefinition, definition))) {
        var position = syntax.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        this._diagnostics.Add(CSharpFrontend.UnsupportedDescriptor.At($"'{definition.Name}' has no instantiation; name one with --instantiate", file, position.Line + 1, position.Character + 1));
      }

    var templates = new Dictionary<IMethodSymbol, IrGenericTemplate?>(SymbolEqualityComparer.Default);
    var instantiated = this._instances.Where(entry => lowered.ContainsKey(entry.Key)).Select(entry => {
      var definition = entry.Value.OriginalDefinition;
      if (!templates.TryGetValue(definition, out var template))
        templates[definition] = template = this.Template(definition);

      var function = lowered[entry.Key];
      return function with { Origin = function.Origin! with { Template = template } };
    });

    // The instantiations stand where their definition stands among the methods of the static class.
    return [.. functions, .. instantiated];
  }

  /// <summary>The closed method of an instantiation <c>--instantiate</c> names; an unknown method or type is an error of the command line.</summary>
  private void Request(CSharpInstantiation requested) {
    var (definition, (_, semantics, syntax)) = this._genericMethods.FirstOrDefault(entry => entry.Key.Name == requested.Method);
    if (definition is null)
      throw new ArgumentException($"--instantiate names '{requested.Method}', which is no generic method of the class");

    if (requested.TypeArguments.Count != definition.TypeParameters.Length)
      throw new ArgumentException($"--instantiate gives '{requested.Method}' {requested.TypeArguments.Count} type arguments; it takes {definition.TypeParameters.Length}");

    var arguments = requested.TypeArguments.Select(text => {
      var bound = semantics.Inner.GetSpeculativeTypeInfo(syntax.SpanStart, SyntaxFactory.ParseTypeName(text), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
      return bound is null or IErrorTypeSymbol || bound.TypeKind == TypeKind.Error
        ? throw new ArgumentException($"--instantiate names the type '{text}', which neither C# nor the file declares")
        : bound;
    }).ToArray();
    var method = definition.Construct(arguments);
    path = semantics.Inner.SyntaxTree.FilePath;
    model = semantics;
    this.Instance(method, syntax);
  }

  /// <summary>
  /// The template of a generic method: its body lowered once more with each type parameter as an
  /// interface of its own name that no module declares; <see langword="null"/> where a construct of
  /// it has no form in terms of its parameters, such as an instantiation of a generic class.
  /// </summary>
  private IrGenericTemplate? Template(IMethodSymbol definition) {
    var (file, semantics, syntax) = this._genericMethods[definition];
    path = file;
    model = semantics;
    this._standIns = new(SymbolEqualityComparer.Default);
    this._templateCalls = [];
    foreach (var parameter in definition.TypeParameters) {
      var standIn = new IrInterfaceType(parameter.Name);
      standIn.DefineMembers([], [], []);
      this._standIns[parameter] = IrType.Object(standIn);
    }

    try {
      var function = this.Method(syntax);
      return new IrGenericTemplate(function, [.. definition.TypeParameters.Select(parameter => this._standIns[parameter])]) { Calls = this._templateCalls };
    } catch (Exception exception) when (exception is UnsupportedException or OpenException or ArgumentException or KeyNotFoundException) {
      return null;
    } finally {
      this._standIns = null;
      this._receiver = null;
    }
  }

  /// <summary>
  /// The class of an instantiation of a generic class of the file, declared the first time the module
  /// names it: its fields and members with the type arguments in place of the parameters, its bodies
  /// lowered with the other bodies. <see langword="null"/> for any other type.
  /// </summary>
  private IrClassType? Instantiated(INamedTypeSymbol type) {
    if (!type.IsGenericType || !this._genericClasses.TryGetValue(type.OriginalDefinition, out var source))
      return null;

    // Only a template names a generic class with a type parameter: it has no class in terms of its parameters.
    if (type.TypeArguments.Any(IsOpen))
      throw new OpenException();

    var (file, semantics, syntax) = source;
    var definition = type.OriginalDefinition;
    var origin = this.Origin(definition.Name, definition.TypeParameters, type.TypeArguments, syntax);
    var declared = new IrClassType(IrGenerics.Mangle(definition.Name, origin.TypeArguments)) {
      Visibility = VisibilityOf(definition), IsSealed = true, Origin = origin
    };
    this._classes[type] = declared;
    this._types.Add(declared);
    declared.Inherit(null, []);
    var (outerPath, outerModel, outerReceiver) = (path, model, this._receiver);
    path = file;
    model = semantics.With(definition.TypeParameters.Zip(type.TypeArguments));
    try {
      this.Class(syntax, declared, []);
    } catch (UnsupportedException unsupported) {
      this.Report(unsupported);
    } finally {
      (path, model, this._receiver) = (outerPath, outerModel, outerReceiver);
    }

    if (!declared.IsDefined)
      declared.Define([]);

    if (this._membersDefined)
      this.DefineMembers();

    return declared;
  }

  /// <summary>
  /// <c>default(T)</c> and <c>default</c> where the compiler gave it a type parameter: the default
  /// value of the type argument, <c>null</c> of the parameter in a template; <see langword="null"/> for anything else.
  /// </summary>
  private IrExpr? Defaulted(ExpressionSyntax expression, BoundType info) {
    if (expression is not (DefaultExpressionSyntax or LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression }) || !model.IsTypeParameter(expression))
      return null;

    var type = this.Map(info.Type, expression);
    if (type.IsString || type.IsArray || type.IsView)
      throw new UnsupportedException(expression, $"default of the type {type}, which is null");

    return Convert(IrGenerics.DefaultOf(type), this.Map(info.ConvertedType, expression));
  }

  /// <summary>
  /// What only a generic definition writes, as its natural value: <c>new T()</c> - a new object of
  /// the class made by its constructor without parameters, the default value of a value type -
  /// and the refusals of <c>typeof(T)</c>, <c>is T</c> and <c>as T</c>; <see langword="null"/> for anything else.
  /// </summary>
  private IrExpr? GenericNatural(ExpressionSyntax expression, IrType type) {
    switch (expression) {
      case BaseObjectCreationExpressionSyntax creation when model.IsTypeParameter(creation):
        if (this.IsStandIn(type))
          return new IrNew(type, IrGenerics.Parameterless, []);

        if (type.ClassType is not null && model.GetTypeInfo(creation).Type is INamedTypeSymbol created)
          return new IrNew(type, this._creators[created.InstanceConstructors.First(constructor => constructor.Parameters.IsEmpty)], []);

        // The compiler admits new T() only of a type with a constructor without parameters: a class, a struct, a number, a truth value.
        return IrGenerics.DefaultOf(type);
      default:
        this.RefuseOpen(expression);
        return null;
    }
  }

  /// <summary>Refuses what asks a type parameter for its type at run time: <c>typeof(T)</c>, <c>is T</c> and <c>as T</c>.</summary>
  private void RefuseOpen(ExpressionSyntax expression) {
    switch (expression) {
      case TypeOfExpressionSyntax typeOf when model.IsTypeParameter(typeOf.Type):
        throw new UnsupportedException(expression, "typeof(T)");
      case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression or (int)SyntaxKind.AsExpression } test when model.IsTypeParameter(test.Right):
        throw new UnsupportedException(expression, test.IsKind(SyntaxKind.IsExpression) ? "is T" : "as T");
      case IsPatternExpressionSyntax { Pattern: var pattern } when TestedType(pattern) is { } tested && model.IsTypeParameter(tested):
        throw new UnsupportedException(expression, "is T");
    }
  }

  /// <summary>The type a pattern tests for, or <see langword="null"/>.</summary>
  private static SyntaxNode? TestedType(PatternSyntax pattern) => pattern switch {
    TypePatternSyntax type => type.Type,
    DeclarationPatternSyntax declaration => declaration.Type,
    ConstantPatternSyntax constant => constant.Expression,
    UnaryPatternSyntax unary => TestedType(unary.Pattern),
    _ => null
  };

  /// <summary>
  /// The equality and the order of a generic definition: <c>Equals</c> of <c>IEquatable&lt;T&gt;</c>
  /// or of a number or truth value with one of its own type, <c>CompareTo</c> likewise of
  /// <c>IComparable&lt;T&gt;</c>, and <c>EqualityComparer&lt;T&gt;.Default.Equals</c> and
  /// <c>Comparer&lt;T&gt;.Default.Compare</c>; <see langword="null"/> for any other call.
  /// </summary>
  private IrExpr? GenericOperation(InvocationExpressionSyntax invocation, IMethodSymbol method) {
    var arguments = invocation.ArgumentList.Arguments;
    var owner = method.ContainingType.OriginalDefinition.ToDisplayString();
    var own = method.Parameters is [{ Type: var parameter }] && SymbolEqualityComparer.Default.Equals(parameter, method.ContainingType);
    var (equality, left, right) = (method.Name, arguments.Count, invocation.Expression) switch {
      ("Equals", 1, MemberAccessExpressionSyntax receiver) when owner == "System.IEquatable<T>" || (own && IsScalar(method.ContainingType)) => (true, receiver.Expression, arguments[0].Expression),
      ("CompareTo", 1, MemberAccessExpressionSyntax receiver) when owner == "System.IComparable<T>" || (own && IsScalar(method.ContainingType)) => (false, receiver.Expression, arguments[0].Expression),
      ("Equals", 2, _) when owner == "System.Collections.Generic.EqualityComparer<T>" => (true, arguments[0].Expression, arguments[1].Expression),
      ("Compare", 2, _) when owner == "System.Collections.Generic.Comparer<T>" => (false, arguments[0].Expression, arguments[1].Expression),
      _ => (false, (ExpressionSyntax?)null, (ExpressionSyntax?)null)
    };
    if (left is null)
      return null;

    var first = this.Value(left);
    var second = this.Value(right!);
    if (second.Type != first.Type || !(this.IsStandIn(first.Type) || (equality ? IrGenerics.Equates(first.Type) : IrGenerics.Orders(first.Type))))
      throw new UnsupportedException(invocation, $"{method.ContainingType.Name}.{method.Name} on {first.Type}");

    return equality ? new IrGenericEquals(first, second) : new IrGenericCompare(first, second);
  }

  /// <summary>Whether a type is one of the numbers or the truth value, whose <c>Equals</c> and <c>CompareTo</c> of their own type the IR has.</summary>
  private static bool IsScalar(ITypeSymbol type) => type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_Double;

}
