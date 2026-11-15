using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// LINQ to Objects (#1329): the members of <c>System.Linq.Enumerable</c> that <see cref="CSharpSequenceApi"/>
/// lists, called as extension methods or as static methods, are the operations on lazy sequences of
/// the IR (<see cref="IrSequence"/>), with the lambdas of #1326 as their functions; query syntax is
/// the same calls, as C# defines them; the array searches of <c>System.Array</c> are terminals on the
/// elements of the array. The members it does not list are refused by name.
/// </summary>
internal sealed partial class Lowering {

  private const string LinqClass = "System.Linq.Enumerable";

  /// <summary>
  /// A call of a member of LINQ or an array search of <c>System.Array</c> as the operation of the IR;
  /// <see langword="null"/> for a call of any other method.
  /// </summary>
  private IrExpr? SequenceCall(InvocationExpressionSyntax invocation, IMethodSymbol method, IrType type) {
    var definition = method.ReducedFrom ?? method;
    var container = definition.ContainingType.ToDisplayString();
    if (container is "System.Linq.ParallelEnumerable" or "System.Linq.Queryable")
      throw new UnsupportedException(invocation, $"{definition.ContainingType.Name}.{method.Name}, a parallel or a queryable sequence");

    if (container == "System.Array" && method.Name is "IndexOf" or "Find" or "FindIndex" or "Exists" or "TrueForAll")
      return this.ArraySearch(invocation, method);

    // Contains of an array the compiler converts to a span: .NET compares by Equals, as Enumerable.Contains does.
    if (container == "System.MemoryExtensions" && method.Name == "Contains" && method.ReducedFrom is not null && invocation.ArgumentList.Arguments.Count == 1
        && invocation.Expression is MemberAccessExpressionSyntax receiver && this.Read(receiver.Expression) is { Type.IsArray: true } array)
      return Operation(invocation, "MemoryExtensions.Contains", IrSequenceOp.Contains, Operation(invocation, "MemoryExtensions.Contains", IrSequenceOp.FromArray, this.Table(array, receiver.Expression)), this.Value(invocation.ArgumentList.Arguments[0].Expression));

    if (container != LinqClass)
      return null;

    if (invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null || !argument.RefKindKeyword.IsKind(SyntaxKind.None)))
      throw new UnsupportedException(invocation, $"Enumerable.{method.Name} with a named argument");

    var operands = new List<ExpressionSyntax>();
    if (method.ReducedFrom is not null)
      // A conditional access is refused before it reaches here: the receiver of an extension method is the expression before its dot.
      operands.Add(((MemberAccessExpressionSyntax)invocation.Expression).Expression);

    operands.AddRange(invocation.ArgumentList.Arguments.Select(argument => argument.Expression));
    return this.Linq(invocation, definition, operands);
  }

  /// <summary>Whether a call is one of a member of LINQ, which a collection receiver leaves to <see cref="SequenceCall"/>.</summary>
  private bool IsLinq(InvocationExpressionSyntax invocation)
    => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
      && (method.ReducedFrom ?? method).ContainingType.ToDisplayString() is LinqClass or "System.Linq.ParallelEnumerable" or "System.Linq.Queryable";

  /// <summary>
  /// The source of a LINQ member: an array is the sequence of its elements, a list the sequence of its
  /// elements as .NET enumerates them, a sequence itself; anything else is refused.
  /// </summary>
  private IrExpr LinqSource(ExpressionSyntax expression) {
    var value = this.Table(this.Read(expression), expression);
    return value.Type switch {
      { IsLazySequence: true } => value,
      { IsArray: true } => IrSequenceOps.Of(IrSequenceOp.FromArray, value),
      { IsList: true } => IrSequenceOps.Of(IrSequenceOp.FromList, value),
      _ => throw new UnsupportedException(expression, $"LINQ over a {model.GetTypeInfo(expression).Type?.ToDisplayString()}")
    };
  }

  /// <summary>A value that is no table of the module: a table handed to an operation would have a second name, under which it could be written.</summary>
  private IrExpr Table(IrExpr value, ExpressionSyntax expression)
    => value is IrConstRef table ? throw new UnsupportedException(expression, $"the static readonly array '{table.Constant.Name}' used as a value") : value;

  /// <summary>The operation of the IR, with the operands it is given, or a refusal naming the member where they do not fit it.</summary>
  private static IrSequence Operation(SyntaxNode node, string member, IrSequenceOp op, params IrExpr[] arguments) {
    var (type, problem) = IrSequenceOps.Signature(op, [.. arguments.Select(argument => argument.Type)]);
    return type is null ? throw new UnsupportedException(node, $"{member}: {problem}") : new IrSequence(op, type, arguments);
  }

  /// <summary>The number of parameters of the delegate a parameter of a member takes, or -1 for a parameter of another type.</summary>
  private static int Arity(IMethodSymbol definition, int parameter)
    => definition.Parameters.Length > parameter && definition.Parameters[parameter].Type is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke }
      ? invoke.Parameters.Length : -1;

  /// <summary>A member of <c>Enumerable</c> with its operands, the source first.</summary>
  private IrExpr Linq(InvocationExpressionSyntax invocation, IMethodSymbol definition, List<ExpressionSyntax> operands) {
    var name = $"Enumerable.{definition.Name}";
    if (definition.Name == "Range" && operands.Count == 2)
      return Operation(invocation, name, IrSequenceOp.Range, this.Value(operands[0]), this.Value(operands[1]));

    if (operands.Count == 0 || definition.Parameters.Length != operands.Count)
      throw new UnsupportedException(invocation, name);

    var source = this.LinqSource(operands[0]);
    IrExpr Operand(int index) => this.Value(operands[index]);
    IrSequence Of(IrSequenceOp op, params IrExpr[] rest) => Operation(invocation, name, op, [source, .. rest]);
    IrSequence Selected(IrSequenceOp op) => Operation(invocation, name, op, Operation(invocation, name, IrSequenceOp.Select, source, Operand(1)));

    switch (definition.Name, operands.Count) {
      case ("AsEnumerable", 1):
        return source;
      case ("Select", 2):
        return Of(Arity(definition, 1) == 2 ? IrSequenceOp.SelectIndexed : IrSequenceOp.Select, Operand(1));
      case ("Where", 2):
        return Of(Arity(definition, 1) == 2 ? IrSequenceOp.WhereIndexed : IrSequenceOp.Where, Operand(1));
      case ("SelectMany", 2) when Arity(definition, 1) == 1:
        return Of(IrSequenceOp.SelectMany, Operand(1));
      case ("SelectMany", 3) when Arity(definition, 1) == 1:
        return Of(IrSequenceOp.SelectManyResult, Operand(1), Operand(2));
      case ("Take" or "Skip", 2) when definition.Parameters[1].Type.SpecialType == SpecialType.System_Int32:
        return Of(definition.Name == "Take" ? IrSequenceOp.Take : IrSequenceOp.Skip, Operand(1));
      case ("Zip", 3):
        var second = this.Table(this.Read(operands[1]), operands[1]);
        var paired = second.Type switch {
          { IsArray: true } => Operation(invocation, name, IrSequenceOp.FromArray, second),
          { IsList: true } => Operation(invocation, name, IrSequenceOp.FromList, second),
          _ => throw new UnsupportedException(operands[1], "Zip with a second sequence that is no array and no list")
        };
        return Of(IrSequenceOp.Zip, paired, Operand(2));
      case ("OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending", 2 or 3):
        return this.Ordering(invocation, definition, source, operands);
      case ("ToArray", 1):
        return Of(IrSequenceOp.ToArray);
      case ("ToList", 1):
        return Of(IrSequenceOp.ToList);
      case ("Count" or "Any" or "First" or "FirstOrDefault", 1):
        return Of(Enum.Parse<IrSequenceOp>(definition.Name));
      case ("Count" or "Any" or "First" or "FirstOrDefault", 2) when Arity(definition, 1) == 1:
        return Of(Enum.Parse<IrSequenceOp>(definition.Name + "Where"), Operand(1));
      case ("All", 2):
        return Of(IrSequenceOp.All, Operand(1));
      case ("Sum" or "Min" or "Max", 1):
        return Of(Enum.Parse<IrSequenceOp>(definition.Name));
      case ("Sum" or "Min" or "Max", 2) when Arity(definition, 1) == 1:
        return Selected(Enum.Parse<IrSequenceOp>(definition.Name));
      case ("Aggregate", 2):
        return Of(IrSequenceOp.Aggregate, Operand(1));
      case ("Aggregate", 3):
        return Of(IrSequenceOp.AggregateSeed, Operand(1), Operand(2));
      case ("Aggregate", 4):
        var folded = Of(IrSequenceOp.AggregateSeed, Operand(1), Operand(2));
        var finish = Operand(3);
        return new IrInvoke(finish.Type.Signature!.Result, finish, [folded]);
      case ("Contains", 2):
        return Of(IrSequenceOp.Contains, Operand(1));
      default:
        throw new UnsupportedException(invocation, name);
    }
  }

  /// <summary>
  /// An ordering or a further key: the key a lambda computes, compared as .NET's default comparer
  /// compares numbers and truth values; a string key only with <c>StringComparer.Ordinal</c>, since the
  /// default comparer of strings depends on the culture. A further key follows an ordering in the same chain.
  /// </summary>
  private IrSequence Ordering(InvocationExpressionSyntax invocation, IMethodSymbol definition, IrExpr source, List<ExpressionSyntax> operands) {
    var name = $"Enumerable.{definition.Name}";
    var key = this.Value(operands[1]);
    var keyType = key.Type.Signature!.Result;
    if (operands.Count == 3) {
      var ordinal = model.GetSymbolInfo(operands[2]).Symbol is IPropertySymbol { Name: "Ordinal", ContainingType.Name: "StringComparer" };
      if (!ordinal || !keyType.IsString)
        throw new UnsupportedException(operands[2], $"{name} with a comparer other than StringComparer.Ordinal of a string key");
    } else if (keyType.IsString) {
      throw new UnsupportedException(invocation, $"{name} of a string key without a comparer, a culture-sensitive comparison");
    }

    var op = Enum.Parse<IrSequenceOp>(definition.Name);
    if (op is IrSequenceOp.ThenBy or IrSequenceOp.ThenByDescending && !(source is IrSequence { Op: var before } && IrSequenceOps.IsOrdering(before)))
      throw new UnsupportedException(invocation, $"{name} of an ordered sequence held in a variable");

    return Operation(invocation, name, op, source, key);
  }

  /// <summary>The array searches of <c>System.Array</c>: terminals on the elements of the array, as eager as .NET's.</summary>
  private IrExpr ArraySearch(InvocationExpressionSyntax invocation, IMethodSymbol method) {
    var name = $"Array.{method.Name}";
    var arguments = invocation.ArgumentList.Arguments;
    if (!method.IsGenericMethod || arguments.Count != 2 || arguments.Any(argument => argument.NameColon is not null))
      throw new UnsupportedException(invocation, name);

    // The generic searches take a T[]: the array itself, never a conversion of it.
    var array = this.Table(this.Read(arguments[0].Expression), arguments[0].Expression);
    var source = Operation(invocation, name, IrSequenceOp.FromArray, array);
    var op = method.Name switch {
      "IndexOf" => IrSequenceOp.IndexOf,
      "Find" => IrSequenceOp.FirstOrDefaultWhere,
      "FindIndex" => IrSequenceOp.FindIndex,
      "Exists" => IrSequenceOp.AnyWhere,
      _ => IrSequenceOp.All
    };
    return Operation(invocation, name, op, source, this.Value(arguments[1].Expression));
  }

  /// <summary>
  /// A query expression as C# defines it (ECMA-334 §12.20.3): <c>from x in s</c> is the source,
  /// <c>where</c> a <c>Where</c>, <c>orderby</c> an ordering and its further keys, <c>select</c> a
  /// <c>Select</c> - none for <c>select x</c>. A <c>let</c> and a second <c>from</c> introduce a variable:
  /// the rest of the query runs for each element as a <c>SelectMany</c> whose lambda has the element and
  /// the variable in scope - a <c>let</c> over the one-element array of its value - which calls the
  /// lambdas in the order the query does. <c>join</c>, <c>group</c>, <c>into</c> and an <c>orderby</c>
  /// after a <c>let</c> or a second <c>from</c> are refused.
  /// </summary>
  private IrExpr Query(QueryExpressionSyntax query) {
    foreach (var node in query.DescendantNodesAndSelf().Where(node => node is QueryClauseSyntax or OrderingSyntax or SelectOrGroupClauseSyntax))
      if (model.Inner.GetSymbolInfo(node).Symbol is IMethodSymbol { } bound && (bound.ReducedFrom ?? bound).ContainingType.ToDisplayString() != LinqClass)
        throw new UnsupportedException(node, $"a query over {(bound.ReducedFrom ?? bound).ContainingType.Name}");

    if (query.Body.Continuation is not null)
      throw new UnsupportedException(query.Body.Continuation, "into of a query");

    var from = query.FromClause;
    if (from.Type is not null)
      throw new UnsupportedException(from, "a typed range variable of a query, which casts");

    return this.QueryRest(this.LinqSource(from.Expression), model.Inner.GetDeclaredSymbol(from)!, query.Body, 0);
  }

  /// <summary>The clauses of a query from an index on, over a sequence of the values of one range variable.</summary>
  private IrExpr QueryRest(IrExpr sequence, IRangeVariableSymbol variable, QueryBodySyntax body, int start) {
    for (var index = start; index < body.Clauses.Count; ++index) {
      var clause = body.Clauses[index];
      switch (clause) {
        case WhereClauseSyntax filter:
          sequence = Operation(filter, "where", IrSequenceOp.Where, sequence, this.QueryLambda(variable, sequence.Type.Element!, IrType.Bool, statements => this.Initialized(filter.Condition, statements).Value));
          break;
        case OrderByClauseSyntax order:
          if (start > 0)
            throw new UnsupportedException(order, "an orderby after a let or a second from of a query");

          for (var level = 0; level < order.Orderings.Count; ++level) {
            var ordering = order.Orderings[level];
            var keyType = this.Map(model.GetTypeInfo(ordering.Expression).Type, ordering.Expression);
            if (keyType.IsString)
              throw new UnsupportedException(ordering, "orderby of a string key, a culture-sensitive comparison");

            var descending = ordering.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword);
            var op = (level == 0, descending) switch {
              (true, false) => IrSequenceOp.OrderBy,
              (true, true) => IrSequenceOp.OrderByDescending,
              (false, false) => IrSequenceOp.ThenBy,
              _ => IrSequenceOp.ThenByDescending
            };
            sequence = Operation(ordering, "orderby", op, sequence, this.QueryLambda(variable, sequence.Type.Element!, keyType, statements => this.Initialized(ordering.Expression, statements).Value));
          }

          break;
        case LetClauseSyntax let:
          var letVariable = model.Inner.GetDeclaredSymbol(let)!;
          var letType = this.Map(model.GetTypeInfo(let.Expression).Type, let.Expression);
          var next = index + 1;
          return Operation(let, "let", IrSequenceOp.SelectMany, sequence, this.QueryLambda(variable, sequence.Type.Element!, IrType.Sequence(this.QueryElement(body), true), statements => {
            var value = this.Initialized(let.Expression, statements).Value;
            var single = Operation(let, "let", IrSequenceOp.FromArray, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(letType), [value]));
            return this.QueryRest(single, letVariable, body, next);
          }));
        case FromClauseSyntax second:
          if (second.Type is not null)
            throw new UnsupportedException(second, "a typed range variable of a query, which casts");

          var secondVariable = model.Inner.GetDeclaredSymbol(second)!;
          var after = index + 1;
          return Operation(second, "from", IrSequenceOp.SelectMany, sequence, this.QueryLambda(variable, sequence.Type.Element!, IrType.Sequence(this.QueryElement(body), true),
            statements => this.QueryRest(this.LinqSource(second.Expression), secondVariable, body, after)));
        default:
          throw new UnsupportedException(clause, $"{clause.Kind()} of a query");
      }
    }

    // A group has a sequence of IGrouping as its type, which is refused before the query is lowered.
    var select = (SelectClauseSyntax)body.SelectOrGroup;
    // select x of the range variable is no call of Select: the sequence is the query's.
    if (select.Expression is IdentifierNameSyntax identifier && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, variable))
      return sequence;

    return Operation(select, "select", IrSequenceOp.Select, sequence, this.QueryLambda(variable, sequence.Type.Element!, this.QueryElement(body), statements => this.Initialized(select.Expression, statements).Value));
  }

  /// <summary>The element type of the sequence a query makes: the type of what it selects.</summary>
  private IrType QueryElement(QueryBodySyntax body)
    => this.Map(model.GetTypeInfo(((SelectClauseSyntax)body.SelectOrGroup).Expression).Type, body.SelectOrGroup);

  /// <summary>
  /// The lambda of a clause of a query: its parameter is the range variable, its body returns what the
  /// builder lowers - in the locals of the lambda, as a lambda of the source is lowered.
  /// </summary>
  private IrLambda QueryLambda(IRangeVariableSymbol variable, IrType element, IrType result, Func<List<IrStmt>, IrExpr> build) {
    var (locals, outerBase) = (this._locals.ToList(), this._localBase);
    this._localBase += this._locals.Count;
    this._locals.Clear();
    try {
      var parameter = new IrLocal(variable.Name, element);
      this._variables[variable] = parameter;
      var statements = new List<IrStmt>();
      var value = build(statements);
      statements.Add(new IrReturn(value));
      var lambda = IrLambda.Of([parameter], result, [.. this._locals], new IrBlock(statements));
      this._nested.Add(parameter);
      this._nested.AddRange(this._locals);
      return lambda;
    } finally {
      this._locals.Clear();
      this._locals.AddRange(locals);
      this._localBase = outerBase;
    }
  }

}
