using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The structs of a file: <c>struct</c>, <c>readonly struct</c>, <c>record struct</c> and
/// <c>readonly record struct</c>, at the top of the file or nested in the static class. A struct has
/// fields and constructors that give every field a parameter or a constant, nothing else; a record
/// struct has its positional parameters and the equality the compiler gives it. Everything that
/// makes a struct more than its fields - methods, properties, initializers of fields, layouts - is
/// refused by name.
/// </summary>
internal sealed partial class Lowering {

  private readonly Dictionary<INamedTypeSymbol, IrStructType> _structs = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<ISymbol, IrField> _fields = new(SymbolEqualityComparer.Default);
  private readonly Dictionary<IMethodSymbol, IReadOnlyList<FieldSource>> _constructors = new(SymbolEqualityComparer.Default);
  private readonly List<IrTypeDeclaration> _types = [];

  /// <summary>Where a constructor takes the value of one field from: a parameter by its position, or a constant.</summary>
  /// <param name="Parameter">The position of the parameter, or -1.</param>
  /// <param name="Constant">The constant, converted to the type of the field, when there is no parameter.</param>
  private sealed record FieldSource(int Parameter, IrExpr? Constant);

  /// <summary>Whether a member declares a struct or a record struct.</summary>
  private static bool IsStruct(MemberDeclarationSyntax member)
    => member is StructDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: (int)SyntaxKind.StructKeyword };

  /// <summary>
  /// Declares the structs of every file: first a type for each, so that a field may have the type of
  /// a struct declared later, then their fields and constructors.
  /// </summary>
  private void DeclareStructs(IReadOnlyList<(string Path, SemanticModel Model, TypeDeclarationSyntax Type)> structs) {
    var names = new HashSet<string>();
    foreach (var (file, semantics, type) in structs) {
      path = file;
      model = semantics;
      var symbol = semantics.GetDeclaredSymbol(type)!;
      var declared = new IrStructType(symbol.Name) { IsEquatable = type is RecordDeclarationSyntax };
      this._structs[symbol] = declared;
      this._types.Add(declared);
      if (!names.Add(symbol.Name))
        this.Report(new UnsupportedException(type, $"a second struct named {symbol.Name}"));
    }

    foreach (var (file, semantics, type) in structs) {
      path = file;
      model = semantics;
      var declared = this._structs[semantics.GetDeclaredSymbol(type)!];
      try {
        this.Struct(type, declared);
      } catch (UnsupportedException unsupported) {
        this.Report(unsupported);
        // A struct that is refused still has a type, so that its uses do not report it again.
        if (!declared.IsDefined)
          declared.Define([]);
      }
    }
  }

  private void Struct(TypeDeclarationSyntax type, IrStructType declared) {
    foreach (var modifier in type.Modifiers)
      if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ReadOnlyKeyword))
        throw new UnsupportedException(type, modifier.IsKind(SyntaxKind.RefKeyword) ? "ref struct" : $"a {modifier.Text} struct");

    if (type.AttributeLists.SelectMany(list => list.Attributes).FirstOrDefault() is { } attribute)
      throw new UnsupportedException(attribute, $"the attribute {attribute.Name}");

    if (type.TypeParameterList is not null)
      throw new UnsupportedException(type.TypeParameterList, "a generic struct");

    if (type.BaseList is not null)
      throw new UnsupportedException(type.BaseList, "a struct that implements an interface");

    var readOnly = type.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
    var fields = new List<IrField>();
    var symbol = model.GetDeclaredSymbol(type)!;
    if (type is RecordDeclarationSyntax { ParameterList: { } positional }) {
      foreach (var parameter in positional.Parameters) {
        if (parameter.Modifiers.Count > 0 || parameter.Default is not null || parameter.AttributeLists.Count > 0)
          throw new UnsupportedException(parameter, "a positional parameter with a modifier, an attribute or a default value");

        var property = symbol.GetMembers(parameter.Identifier.Text).OfType<IPropertySymbol>().Single();
        var field = new IrField(property.Name, this.FieldType(property.Type, parameter), readOnly);
        this._fields[property] = field;
        fields.Add(field);
      }

      var primary = symbol.InstanceConstructors.First(constructor => constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() == type));
      this._constructors[primary] = [.. fields.Select((_, index) => new FieldSource(index, null))];
    }

    foreach (var member in type.Members.OfType<FieldDeclarationSyntax>()) {
      foreach (var modifier in member.Modifiers)
        if (modifier.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ReadOnlyKeyword))
          throw new UnsupportedException(member, $"a {modifier.Text} field of a struct");

      foreach (var variable in member.Declaration.Variables) {
        if (variable.Initializer is not null)
          throw new UnsupportedException(variable.Initializer, "a field initializer of a struct");

        var fieldSymbol = (IFieldSymbol)model.GetDeclaredSymbol(variable)!;
        var field = new IrField(fieldSymbol.Name, this.FieldType(fieldSymbol.Type, member.Declaration.Type), readOnly || fieldSymbol.IsReadOnly);
        this._fields[fieldSymbol] = field;
        fields.Add(field);
      }
    }

    declared.Define(fields);
    foreach (var member in type.Members.Where(member => member is not FieldDeclarationSyntax))
      if (member is ConstructorDeclarationSyntax constructor)
        this.Constructor(constructor, declared);
      else
        throw new UnsupportedException(member, Describe(member));
  }

  /// <summary>What a diagnostic calls a member of a struct that is not a field or a constructor.</summary>
  private static string Describe(MemberDeclarationSyntax member) => member switch {
    MethodDeclarationSyntax method => $"the method {method.Identifier.Text} of a struct",
    PropertyDeclarationSyntax property => $"the property {property.Identifier.Text} of a struct",
    OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "an operator of a struct",
    BaseTypeDeclarationSyntax => "a type nested in a struct",
    _ => $"a {member.Kind()} of a struct"
  };

  /// <summary>The type of a field: a truth value, a number, a struct, or an array of numbers.</summary>
  private IrType FieldType(ITypeSymbol type, SyntaxNode node) {
    var mapped = this.Map(type, node);
    return mapped.Element is { IsStruct: true } ? throw new UnsupportedException(node, $"an array of {mapped.Element} as a field") : mapped;
  }

  /// <summary>
  /// A constructor: every statement gives one field a parameter or a constant, and every field is
  /// given a value exactly once. Calls of it become an <see cref="IrStructNew"/>.
  /// </summary>
  private void Constructor(ConstructorDeclarationSyntax constructor, IrStructType declared) {
    if (constructor.Modifiers.Any(SyntaxKind.StaticKeyword) || constructor.Initializer is not null)
      throw new UnsupportedException(constructor, constructor.Initializer is null ? "a static constructor" : "a constructor that calls another");

    var symbol = model.GetDeclaredSymbol(constructor)!;
    foreach (var parameter in constructor.ParameterList.Parameters)
      if (parameter.Modifiers.Count > 0 || parameter.Default is not null)
        throw new UnsupportedException(parameter, "a constructor parameter with a modifier or a default value");

    IEnumerable<SyntaxNode> statements = constructor.ExpressionBody is { } body ? [body.Expression] : constructor.Body!.Statements;
    var sources = new FieldSource?[declared.Fields.Count];
    foreach (var statement in statements) {
      var (field, source) = this.FieldAssignment(statement, symbol);
      var index = declared.Fields.ToList().IndexOf(field);
      if (sources[index] is not null)
        throw new UnsupportedException(statement, $"a constructor that assigns the field {field.Name} twice");

      sources[index] = source;
    }

    if (sources.Contains(null))
      throw new UnsupportedException(constructor, "a constructor that does not assign every field");

    this._constructors[symbol] = [.. sources.Select(source => source!)];
  }

  /// <summary>One statement of a constructor, or its expression body: a field of the struct given a parameter or a constant.</summary>
  private (IrField Field, FieldSource Source) FieldAssignment(SyntaxNode statement, IMethodSymbol constructor) {
    const string logic = "a constructor with logic beyond giving each field a parameter or a constant";
    var expression = statement is ExpressionStatementSyntax { Expression: var inner } ? inner : statement;
    if (expression is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment
        || assignment.Left is not (IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }))
      throw new UnsupportedException(statement, logic);

    var field = this._fields[model.GetSymbolInfo(assignment.Left).Symbol!];
    var value = assignment.Right is ParenthesizedExpressionSyntax parenthesized ? parenthesized.Expression : assignment.Right;
    if (model.GetSymbolInfo(value).Symbol is IParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, constructor)) {
      var from = this.Map(parameter.Type, value);
      if (from != field.Type && !(from.IsNumeric && field.Type.IsNumeric))
        throw new UnsupportedException(statement, logic);

      return (field, new FieldSource(parameter.Ordinal, null));
    }

    if (model.GetConstantValue(value) is { HasValue: true })
      return (field, new FieldSource(-1, this.Value(value)));

    throw new UnsupportedException(statement, logic);
  }

  /// <summary>The struct a field of the subset belongs to, as a member access names it, or <see langword="null"/>.</summary>
  private IrField? FieldOf(ExpressionSyntax access) => model.GetSymbolInfo(access).Symbol is { } symbol ? this._fields.GetValueOrDefault(symbol) : null;

  /// <summary>
  /// <c>new T(…)</c>, <c>new T()</c>, <c>new()</c> and <c>new T { F = v }</c> of a struct. The
  /// parameterless form is the default value; a constructor is inlined into the value it builds.
  /// </summary>
  private IrExpr Creation(BaseObjectCreationExpressionSyntax creation, IrType type) {
    var arguments = creation.ArgumentList?.Arguments ?? [];
    if (creation.Initializer is { } initializer) {
      if (arguments.Count > 0)
        throw new UnsupportedException(creation, "a constructor call with an object initializer");

      return this.Built(type, this.Entries(initializer), creation);
    }

    var constructor = (IMethodSymbol)model.GetSymbolInfo(creation).Symbol!;
    if (constructor.IsImplicitlyDeclared && constructor.Parameters.Length == 0)
      return this.Default(type, creation);

    if (arguments.Any(argument => argument.NameColon is not null))
      throw new UnsupportedException(creation, "a named argument");

    var values = arguments.Select(argument => this.Value(argument.Expression)).ToList();
    var sources = this._constructors[constructor];
    var fields = type.StructType!.Fields;
    var taken = sources.Where(source => source.Parameter >= 0).Select(source => source.Parameter).ToList();
    // The arguments are evaluated left to right, the fields of the IR in declaration order: where the
    // two orders differ or an argument is not used, only arguments without effects can be moved.
    var inOrder = taken.SequenceEqual(Enumerable.Range(0, values.Count));
    if (!inOrder && !values.All(Simple))
      throw new UnsupportedException(creation, "a constructor call whose arguments the constructor does not take in field order");

    return new IrStructNew(type, [.. sources.Select((source, index) => source.Constant ?? Convert(values[source.Parameter], fields[index].Type))]);
  }

  /// <summary>The fields an object initializer gives values, in source order.</summary>
  private List<(IrField Field, ExpressionSyntax Value)> Entries(InitializerExpressionSyntax initializer) {
    var entries = new List<(IrField Field, ExpressionSyntax Value)>();
    foreach (var expression in initializer.Expressions) {
      if (expression is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax target } entry || this.FieldOf(target) is not { } field)
        throw new UnsupportedException(expression, "an object initializer entry other than a field and its value");

      if (entry.Right is InitializerExpressionSyntax)
        throw new UnsupportedException(entry.Right, "a nested object initializer");

      entries.Add((field, entry.Right));
    }

    return entries;
  }

  /// <summary>
  /// An object initializer where it stands inside an expression: the value built from all fields at
  /// once, every field the initializer leaves out at its default. The entries are evaluated in
  /// source order and the fields in declaration order, so where the two differ only values without
  /// effects can stand.
  /// </summary>
  private IrExpr Built(IrType type, List<(IrField Field, ExpressionSyntax Value)> entries, SyntaxNode node) {
    var fields = type.StructType!.Fields;
    var values = entries.Select(entry => Convert(this.Value(entry.Value), entry.Field.Type)).ToList();
    var positions = entries.Select(entry => fields.ToList().IndexOf(entry.Field)).ToList();
    if (!positions.SequenceEqual(positions.Order()) && !values.All(Simple))
      throw new UnsupportedException(node, "an object initializer with effects in an order other than that of the fields");

    return new IrStructNew(type, [.. fields.Select((field, index) => positions.IndexOf(index) is var at and >= 0 ? values[at] : this.Zero(field.Type, node))]);
  }

  /// <summary>
  /// Lowers the value of a declaration, an assignment or a <c>return</c>. An object initializer of a
  /// struct is, as C# defines it, the default value with the entries stored into it one after the
  /// other: into the variable a declaration declares - the stores then follow the declaration -
  /// otherwise into a temporary declared in front of the statement.
  /// </summary>
  private (IrExpr Value, List<IrStmt> After) Initialized(ExpressionSyntax value, List<IrStmt> into, IrLocal? declared = null) {
    if (value is not BaseObjectCreationExpressionSyntax { Initializer: { } initializer } creation
        || creation.ArgumentList is { Arguments.Count: > 0 }
        || this.Map(model.GetTypeInfo(creation).Type, creation) is not { IsStruct: true } type)
      return (this.Value(value), []);

    var start = this.Default(type, creation);
    var target = declared ?? this.Local($"$t{this._locals.Count}", type);
    var stores = this.Entries(initializer)
      .Select(entry => (IrStmt)new IrFieldStore(new IrLocalRef(target), entry.Field, Convert(this.Value(entry.Value), entry.Field.Type)))
      .ToList();
    if (declared is not null)
      return (start, stores);

    into.Add(new IrDeclare(target, start));
    into.AddRange(stores);
    return (new IrLocalRef(target), []);
  }

  /// <summary>The default value of a struct type, which one with a field of an array type has none of.</summary>
  private IrExpr Default(IrType type, SyntaxNode node)
    => type.StructType!.HasDefault ? new IrStructDefault(type) : throw new UnsupportedException(node, $"the default value of {type}, which has a field of an array type");

  /// <summary>The zero of a field's type: a constant, or the default of a struct.</summary>
  private IrExpr Zero(IrType type, SyntaxNode node)
    => type.IsStruct ? this.Default(type, node) : type.IsArray ? throw new UnsupportedException(node, $"an object initializer that leaves an array field without a value") : new IrConst(type, 0);

  /// <summary>Whether an expression can be evaluated in another order without a difference: a constant, a variable or a field of one.</summary>
  private static bool Simple(IrExpr expression) => expression switch {
    IrConst or IrConstRef or IrLocalRef or IrStructDefault => true,
    IrFieldLoad load => Simple(load.Target),
    IrConvert conversion => conversion.Contract == IrConversion.Wrap && Simple(conversion.Operand),
    _ => false
  };

  /// <summary>
  /// A place a field is written in: a variable, an array element or a field of a place. When the
  /// place is also read, an array and an index that are more than a variable or a constant are
  /// evaluated once.
  /// </summary>
  private IrExpr Place(ExpressionSyntax expression, List<IrStmt> into, bool alsoRead) {
    switch (expression) {
      case ParenthesizedExpressionSyntax parenthesized:
        return this.Place(parenthesized.Expression, into, alsoRead);
      case IdentifierNameSyntax when this.Value(expression) is IrLocalRef variable:
        return variable;
      case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element:
        var array = this.Written(element.Expression);
        var index = this.Index(element.ArgumentList.Arguments[0].Expression);
        return alsoRead ? new IrArrayLoad(this.Once(array, into), this.Once(index, into)) : new IrArrayLoad(array, index);
      case MemberAccessExpressionSyntax member when this.FieldOf(member) is { } field:
        return new IrFieldLoad(this.Place(member.Expression, into, alsoRead), field);
      default:
        throw new UnsupportedException(expression, $"a field written of {expression.Kind()}");
    }
  }

  /// <summary>
  /// A method called on a struct other than <c>Equals(T)</c> of a record struct - <c>ToString</c>,
  /// <c>GetHashCode</c>, <c>Equals(object)</c> - by the type that declares it and its name, or
  /// <see langword="null"/>. It is named before its result, which may be no type of the subset, is looked at.
  /// </summary>
  private string? MemberOfStruct(ExpressionSyntax expression)
    => expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call
      && model.GetTypeInfo(access.Expression).Type is INamedTypeSymbol receiver && this._structs.ContainsKey(receiver)
      && model.GetSymbolInfo(call).Symbol is IMethodSymbol method
      && !(method is { Name: "Equals", Parameters: [{ Type: var other }] } && SymbolEqualityComparer.Default.Equals(other, receiver))
        ? $"{method.ContainingType.Name}.{method.Name}"
        : null;

  /// <summary>
  /// <c>==</c> and <c>!=</c> of two values of a record struct, which compare field by field; a plain
  /// struct has no such operator.
  /// </summary>
  private IrExpr Equality(IrBinaryOp op, IrExpr left, IrExpr right, SyntaxNode node) {
    if (op is not (IrBinaryOp.Equal or IrBinaryOp.NotEqual) || left.Type != right.Type || left.Type.StructType is not { IsEquatable: true })
      throw new UnsupportedException(node, $"{node.Kind()} on {left.Type} and {right.Type}");

    var equal = new IrStructEquals(left, right);
    return op == IrBinaryOp.Equal ? equal : new IrUnary(IrUnaryOp.LogicalNot, equal);
  }

}
