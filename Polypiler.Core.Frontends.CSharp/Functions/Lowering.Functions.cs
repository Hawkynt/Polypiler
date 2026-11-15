using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// Delegates, lambdas, local functions and closures (#1326). A delegate type - <c>Func</c>, <c>Action</c>
/// or one the file declares - is the function type of its signature; a lambda, an anonymous method and
/// a local function are an <see cref="IrLambda"/> that captures the variables it uses; a method group is
/// an <see cref="IrFunctionRef"/> or an <see cref="IrBoundMethod"/>; a call through a delegate is an
/// <see cref="IrInvoke"/>. A local function is a variable of its block that holds its closure from the
/// earliest point every variable it captures is declared at, which C# demands of every call of it.
/// Multicast delegates, delegate equality other than with <c>null</c> and lambdas with <c>ref</c> or
/// <c>params</c> parameters are refused; an expression tree does not compile against the core library
/// the frontend binds to.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The number of locals of the functions around the lambda being lowered, so that the temporaries of each are named apart.</summary>
  private int _localBase;

  /// <summary>The delegate types declared beside the static class, each with the file and the model it is read with.</summary>
  private readonly List<(string Path, BoundModel Model, DelegateDeclarationSyntax Type)> _delegates = [];

  /// <summary>The parameters and locals of the lambdas of the function being lowered, which may hide a constant as its own locals may.</summary>
  private readonly List<IrLocal> _nested = [];

  /// <summary>Whether a type is a delegate type, which the IR has as the function type of its signature.</summary>
  private static bool IsDelegate(ITypeSymbol? type) => type is INamedTypeSymbol { TypeKind: TypeKind.Delegate };

  /// <summary>
  /// The function type of a delegate type: a <c>Func</c> or an <c>Action</c> of any arity, or a delegate
  /// type of the file that is not generic. A parameter passed by reference or a <c>params</c> array has no
  /// function type; <see langword="null"/> for a type that is no delegate.
  /// </summary>
  private IrType? FunctionType(ITypeSymbol? type, SyntaxNode node) {
    if (type is not INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke })
      return null;

    if (invoke.Parameters.FirstOrDefault(parameter => parameter.RefKind != RefKind.None || parameter.IsParams) is { } passed)
      throw new UnsupportedException(node, passed.IsParams ? "a params delegate" : $"a {passed.RefKind.ToString().ToLowerInvariant()} parameter of a delegate");

    if (invoke.ReturnsByRef || invoke.ReturnsByRefReadonly)
      throw new UnsupportedException(node, "a delegate that returns by reference");

    return IrType.Function(new IrFunctionType([.. invoke.Parameters.Select(parameter => this.Map(parameter.Type, node))], this.Map(invoke.ReturnType, node)));
  }

  /// <summary>
  /// A delegate type the file declares, beside or inside the static class: it is taken as the function
  /// type of its signature wherever it is named, and declares nothing of its own.
  /// </summary>
  private void DelegateDeclaration(DelegateDeclarationSyntax declaration) {
    if (declaration.TypeParameterList is not null)
      throw new UnsupportedException(declaration, "a generic delegate type");

    this.FunctionType(model.GetDeclaredSymbol(declaration), declaration);
  }

  /// <summary>
  /// A function value where the compiler converts an expression to a delegate type: a lambda, an
  /// anonymous method, a method group or <c>null</c>; <see langword="null"/> for any other expression.
  /// </summary>
  private IrExpr? FunctionValue(ExpressionSyntax expression, BoundType info) {
    if (!IsDelegate(info.ConvertedType))
      return null;

    var type = this.FunctionType(info.ConvertedType, expression)!;
    switch (expression) {
      case ParenthesizedExpressionSyntax parenthesized when model.GetTypeInfo(parenthesized.Expression).Type is null:
        return this.FunctionValue(parenthesized.Expression, info);
      case AnonymousFunctionExpressionSyntax function:
        return this.Lambda(function, (IMethodSymbol)model.GetSymbolInfo(function).Symbol!);
      case LiteralExpressionSyntax or DefaultExpressionSyntax when model.GetConstantValue(expression) is { HasValue: true, Value: null }:
        return new IrNull(type);
      // A conditional of two lambdas has no type of its own; the delegate it converts to is its type.
      case ConditionalExpressionSyntax when info.Type is null:
        return this.Natural(expression, type);
    }

    if (info.Type is not null || model.GetSymbolInfo(expression).Symbol is not IMethodSymbol method)
      return null;

    if (method.MethodKind == MethodKind.LocalFunction)
      return new IrLocalRef(this.LocalFunction(method));

    if (method.IsStatic && SymbolEqualityComparer.Default.Equals(method.ContainingType, this._class))
      return new IrFunctionRef(type, method.IsGenericMethod ? this.Instance(method, expression) : method.Name);

    if (!this._methods.TryGetValue(method, out var called))
      throw new UnsupportedException(expression, $"the method group {method.ContainingType.Name}.{method.Name}");

    var receiver = called.IsStatic ? null : this.Owner(expression, expression);
    return new IrBoundMethod(type, receiver, called) { Kind = this.CallKind(expression, called) };
  }

  /// <summary>
  /// A lambda or an anonymous method, lowered as a function of its own: its parameters, its locals and
  /// its body, which returns the value of an expression body. It captures what it uses of the
  /// functions around it.
  /// </summary>
  private IrLambda Lambda(AnonymousFunctionExpressionSyntax function, IMethodSymbol symbol) {
    if (function.AsyncKeyword.RawKind != 0)
      throw new UnsupportedException(function, "an async lambda");

    var body = function.Block is { } block ? (SyntaxNode)block : function.ExpressionBody!;
    return this.Nested(function, symbol, body as BlockSyntax, body as ExpressionSyntax);
  }

  /// <summary>The closure of a function inside a function: lowered with locals of its own, the outer ones restored after it.</summary>
  private IrLambda Nested(SyntaxNode node, IMethodSymbol symbol, BlockSyntax? block, ExpressionSyntax? expression) {
    var (locals, outerBase) = (this._locals.ToList(), this._localBase);
    this._localBase += this._locals.Count;
    this._locals.Clear();
    try {
      var parameters = new List<IrLocal>();
      for (var index = 0; index < symbol.Parameters.Length; ++index) {
        var parameter = symbol.Parameters[index];
        if (parameter.RefKind != RefKind.None || parameter.IsParams)
          throw new UnsupportedException(node, parameter.IsParams ? "a params parameter of a lambda" : $"a {parameter.RefKind.ToString().ToLowerInvariant()} parameter of a lambda");

        if (parameter.HasExplicitDefaultValue)
          throw new UnsupportedException(node, "a default parameter value");

        // An anonymous method without a parameter list has parameters without names.
        var variable = new IrLocal(parameter.Name.Length > 0 ? parameter.Name : $"$a{index}", this.Map(parameter.Type, node));
        this._variables[parameter] = variable;
        parameters.Add(variable);
      }

      var result = this.Map(symbol.ReturnType, node);
      var statements = new List<IrStmt>();
      if (block is not null)
        statements.AddRange(this.Block(block).Statements);
      else if (result == IrType.Void)
        this.Effect(expression!, statements);
      else
        statements.Add(new IrReturn(this.Initialized(expression!, statements).Value));

      var lambda = IrLambda.Of(parameters, result, [.. this._locals], new IrBlock(statements));
      this._nested.AddRange(parameters.Concat(this._locals));
      return lambda;
    } finally {
      this._locals.Clear();
      this._locals.AddRange(locals);
      this._localBase = outerBase;
    }
  }

  /// <summary>The variable that holds a local function, made when its block begins - before anything of the block names it; a generic one is refused there.</summary>
  private IrLocal LocalFunction(IMethodSymbol symbol) => this._variables[symbol.OriginalDefinition];

  /// <summary>
  /// The statements of a block that declares local functions: each is a variable declared first, and
  /// its closure is assigned to it once every variable of the block it captures is declared - before
  /// any call C# admits, since a call needs those variables assigned.
  /// </summary>
  private IrBlock BlockWithFunctions(BlockSyntax block, IReadOnlyList<LocalFunctionStatementSyntax> functions) {
    var statements = new List<IrStmt>();
    var held = new List<(LocalFunctionStatementSyntax Syntax, IMethodSymbol Symbol, IrLocal Variable)>();
    foreach (var function in functions) {
      foreach (var modifier in function.Modifiers)
        if (!modifier.IsKind(SyntaxKind.StaticKeyword))
          throw new UnsupportedException(function, modifier.IsKind(SyntaxKind.AsyncKeyword) ? "an async local function" : modifier.Text);

      if (function.TypeParameterList is not null)
        throw new UnsupportedException(function, "a generic local function");

      if (function.AttributeLists.Count > 0)
        throw new UnsupportedException(function, "an attribute on a local function");

      var symbol = model.GetDeclaredSymbol(function)!;
      var type = IrType.Function(new IrFunctionType([.. symbol.Parameters.Select(parameter => this.Map(parameter.Type, function))], this.Map(symbol.ReturnType, function)));
      var variable = this.Local(function.Identifier.Text, type);
      this._variables[symbol] = variable;
      statements.Add(new IrDeclare(variable, null));
      held.Add((function, symbol, variable));
    }

    var start = statements.Count;
    foreach (var statement in block.Statements.Where(statement => statement is not LocalFunctionStatementSyntax))
      this.Statement(statement, statements);

    foreach (var (syntax, symbol, variable) in held) {
      var lambda = this.Nested(syntax, symbol, syntax.Body, syntax.ExpressionBody?.Expression);
      var position = start;
      for (var index = start; index < statements.Count; ++index)
        if (statements[index] is IrDeclare declare && lambda.Captures.Contains(declare.Local))
          position = index + 1;

      statements.Insert(position, new IrAssign(variable, lambda));
    }

    return new IrBlock(statements);
  }

  /// <summary>
  /// A call through a delegate - <c>f(x)</c> and <c>f.Invoke(x)</c> - or of a local function: the callee,
  /// then the arguments; <see langword="null"/> for a call of anything else.
  /// </summary>
  private IrExpr? Invocation(InvocationExpressionSyntax invocation, IMethodSymbol method) {
    if (method.MethodKind is not (MethodKind.DelegateInvoke or MethodKind.LocalFunction))
      return null;

    var arguments = invocation.ArgumentList.Arguments;
    if (arguments.Any(argument => argument.NameColon is not null || argument.RefKindKeyword.RawKind != 0))
      throw new UnsupportedException(invocation, "a named or ref argument");

    IrExpr callee;
    if (method.MethodKind == MethodKind.LocalFunction)
      callee = new IrLocalRef(this.LocalFunction(method));
    else
      callee = this.Value(invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Invoke" } member && IsDelegate(model.GetTypeInfo(member.Expression).Type)
        ? member.Expression : invocation.Expression);

    var signature = callee.Type.Signature!;
    return new IrInvoke(signature.Result, callee, [.. arguments.Select(argument => this.Value(argument.Expression))]);
  }

  /// <summary>
  /// What an operator does to delegates: <c>==</c>, <c>!=</c> and <c>is</c> with <c>null</c> compare the
  /// reference; any other comparison and <c>+</c> and <c>-</c>, which combine invocation lists, are
  /// refused. <see langword="null"/> where no operand is a delegate.
  /// </summary>
  private IrExpr? DelegateOperator(ExpressionSyntax expression) {
    switch (expression) {
      case BinaryExpressionSyntax binary when IsDelegate(model.GetTypeInfo(binary.Left).Type) || IsDelegate(model.GetTypeInfo(binary.Right).Type):
        if (binary.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression)
          throw new UnsupportedException(binary, "a multicast delegate");

        if (binary.Kind() is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression) || !(IsNull(binary.Left) || IsNull(binary.Right)))
          throw new UnsupportedException(binary, "delegate equality other than with null");

        var compared = this.NullComparison(IsNull(binary.Left) ? binary.Right : binary.Left);
        return binary.IsKind(SyntaxKind.EqualsExpression) ? compared : new IrUnary(IrUnaryOp.LogicalNot, compared);
      case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax constant } pattern when IsNull(constant.Expression) && IsDelegate(model.GetTypeInfo(pattern.Expression).Type):
        return this.NullComparison(pattern.Expression);
      case IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax constant } } pattern when IsNull(constant.Expression) && IsDelegate(model.GetTypeInfo(pattern.Expression).Type):
        return new IrUnary(IrUnaryOp.LogicalNot, this.NullComparison(pattern.Expression));
      default:
        return null;
    }
  }

  /// <summary>Whether a delegate is <c>null</c>.</summary>
  private IrReferenceEquals NullComparison(ExpressionSyntax operand) {
    var value = this.Natural(operand, this.FunctionType(model.GetTypeInfo(operand).Type, operand)!);
    return new IrReferenceEquals(value, new IrNull(value.Type));
  }

  /// <summary>A compound assignment to a delegate, which adds to or removes from its invocation list: refused.</summary>
  private void RefuseMulticast(AssignmentExpressionSyntax assignment) {
    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && IsDelegate(model.GetTypeInfo(assignment.Left).Type))
      throw new UnsupportedException(assignment, "a multicast delegate");
  }

}
