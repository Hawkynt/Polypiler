using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The async methods of C# (#1330): <c>async Task</c>, <c>async Task&lt;T&gt;</c> and <c>async ValueTask&lt;T&gt;</c>
/// methods of the class are async functions, and <c>await</c> is an <see cref="IrAwait"/> of the call of one
/// of them, of <c>Task.CompletedTask</c>, of <c>Task.FromResult(v)</c> or of <c>Task.WhenAll</c> of such
/// calls; <c>ConfigureAwait(bool)</c> is dropped. Only the subset whose order of effects .NET and
/// JavaScript agree on is imported - every task is awaited where it is created - and every other use of
/// a task is refused by name (<see cref="CSharpAsyncApi"/>).
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The calls of <c>Task.WhenAll</c> lowered, with the file and the node, for the diagnostic of one whose order depends on timing.</summary>
  private readonly Dictionary<IrAsync, (string Path, SyntaxNode Node)> _whenAll = new(ReferenceEqualityComparer.Instance);

  /// <summary>Whether a type is a task of <c>System.Threading.Tasks</c>: <c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c> or <c>ValueTask&lt;T&gt;</c>.</summary>
  private static bool IsTask(ITypeSymbol? type)
    => type is INamedTypeSymbol { OriginalDefinition.MetadataName: "Task" or "Task`1" or "ValueTask" or "ValueTask`1" } named
      && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";

  /// <summary>Whether a type is a delegate whose result is a task.</summary>
  private static bool IsTaskDelegate(ITypeSymbol? type) => type is INamedTypeSymbol { DelegateInvokeMethod: { } invoke } && IsTask(invoke.ReturnType);

  /// <summary>Whether a type is <c>TaskCompletionSource</c> or <c>TaskCompletionSource&lt;T&gt;</c>.</summary>
  private static bool IsHandCompleted(ITypeSymbol? type)
    => type is INamedTypeSymbol { OriginalDefinition.MetadataName: "TaskCompletionSource" or "TaskCompletionSource`1" } named
      && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";

  /// <summary>
  /// The IR type of a task: <c>async&lt;T&gt;</c>, <c>async&lt;void&gt;</c> for <c>Task</c>, flagged for a
  /// <c>ValueTask</c>; <see langword="null"/> for any other type. A task a field holds is refused.
  /// </summary>
  private IrType? AsyncType(ITypeSymbol? type, SyntaxNode node) {
    if (!IsTask(type))
      return null;

    if (node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is not null)
      throw new UnsupportedException(node, IrAsyncFunctions.NotAwaited);

    var named = (INamedTypeSymbol)type!;
    var valueTask = named.Name == "ValueTask";
    if (named.TypeArguments.Length == 0)
      return IrType.Async(IrType.Void, valueTask);

    var result = this.Map(named.TypeArguments[0], node);
    return IrAsyncFunctions.IsResult(result) ? IrType.Async(result, valueTask) : throw new UnsupportedException(node, named.ToDisplayString());
  }

  /// <summary>
  /// Refuses, before anything of a member is lowered, every use of a task outside the subset: <c>async
  /// void</c>, <c>await foreach</c>, a task completed by hand, blocking on a task, the members of
  /// <c>Task</c> that schedule or continue work, and a task that is not awaited where it is created.
  /// </summary>
  private void RefuseUnordered(SyntaxNode member, IMethodSymbol symbol) {
    if (symbol.IsAsync && symbol.ReturnsVoid)
      throw new UnsupportedException(member, "async void");

    // No task can stand in a compilation that never names one: a method that returns one declares it.
    this._namesTasks ??= this.Unclosed.Compilation.SyntaxTrees.Any(tree => tree.ToString().Contains("Task", System.StringComparison.Ordinal));
    if (!this._namesTasks.Value)
      return;

    // The semantic model as the compiler bound the source: the check asks no question of an instantiation.
    var model = this.Unclosed;
    foreach (var node in member.DescendantNodes()) {
      switch (node) {
        case AnonymousFunctionExpressionSyntax { AsyncKeyword.RawKind: not 0 }:
          throw new UnsupportedException(node, "an async lambda");
        case ForEachStatementSyntax { AwaitKeyword.RawKind: not 0 } or ForEachVariableStatementSyntax { AwaitKeyword.RawKind: not 0 }:
          throw new UnsupportedException(node, "await foreach");
        case ParameterSyntax parameter when IsTask(this.ParameterType(parameter)):
          throw new UnsupportedException(node, IrAsyncFunctions.NotAwaited);
        case InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol called && IsTaskMember(called):
          if (called.Name is "Wait" or "WaitAll" or "WaitAny" or "GetAwaiter" or "RunSynchronously")
            throw new UnsupportedException(node, "blocking on a task");

          if (called.Name is not ("FromResult" or "WhenAll" or "ConfigureAwait"))
            throw new UnsupportedException(node, $"{called.ContainingType.Name}.{called.Name}");

          break;
        case MemberAccessExpressionSyntax { Name.Identifier.Text: "Result" } access when IsTask(model.GetTypeInfo(access.Expression).Type):
          throw new UnsupportedException(node, "blocking on a task");
        case ExpressionSyntax expression when IsHandCompleted(model.GetTypeInfo(expression).Type):
          throw new UnsupportedException(node, "a task completed by hand: .NET continues inside SetResult, JavaScript after it");
        // The name of a member access is checked as the access.
        case SimpleNameSyntax name when name.Parent is MemberAccessExpressionSyntax access && access.Name == name:
          break;
        case ExpressionSyntax expression when model.GetSymbolInfo(expression).Symbol is not (ITypeSymbol or INamespaceSymbol)
          && (IsTask(model.GetTypeInfo(expression).Type) && !IsAwaitedWhereCreated(expression) || IsTaskDelegate(model.GetTypeInfo(expression).ConvertedType)):
          throw new UnsupportedException(node, IrAsyncFunctions.NotAwaited);
      }
    }
  }

  /// <summary>Whether a file of the compilation names a task, which only then can stand anywhere; <see langword="null"/> before it is asked.</summary>
  private bool? _namesTasks;

  /// <summary>The semantic model of the compiler, without the instantiation of the generic definitions.</summary>
  private SemanticModel Unclosed => model.Inner;

  /// <summary>The type of a parameter, as declared.</summary>
  private ITypeSymbol? ParameterType(ParameterSyntax parameter) => parameter.Type is { } type ? this.Unclosed.GetTypeInfo(type).Type : null;

  /// <summary>Whether a method is a member of a task type or of <c>Task</c>'s static members, such as <c>Task.Run</c> or <c>t.ContinueWith</c>.</summary>
  private static bool IsTaskMember(IMethodSymbol method) => IsTask(method.ContainingType);

  /// <summary>
  /// Whether a task-valued expression stands where it is awaited: the operand of <c>await</c>, through
  /// parentheses and <c>ConfigureAwait</c>, or the call of a method given directly to an awaited
  /// <c>Task.WhenAll</c>.
  /// </summary>
  private bool IsAwaitedWhereCreated(ExpressionSyntax expression) {
    var place = Outer(expression);
    switch (place.Parent) {
      case AwaitExpressionSyntax:
        return true;
      case MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait" } access when access.Parent is InvocationExpressionSyntax configured:
        return Outer(configured).Parent is AwaitExpressionSyntax;
      case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax all } } when expression is InvocationExpressionSyntax
        && this.Unclosed.GetSymbolInfo(all).Symbol is IMethodSymbol { Name: "WhenAll" } whenAll && IsTask(whenAll.ContainingType):
        return this.IsAwaitedWhereCreated(all);
      default:
        return false;
    }
  }

  /// <summary>An expression with the parentheses around it.</summary>
  private static ExpressionSyntax Outer(ExpressionSyntax expression) {
    while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
      expression = parenthesized;

    return expression;
  }

  /// <summary>An expression without its parentheses and without a <c>ConfigureAwait(…)</c>, which says nothing in the subset.</summary>
  private ExpressionSyntax Configured(ExpressionSyntax expression) {
    while (true) {
      switch (expression) {
        case ParenthesizedExpressionSyntax parenthesized:
          expression = parenthesized.Expression;
          continue;
        case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait" } access, ArgumentList.Arguments: [var argument] }
          when model.GetSymbolInfo(expression).Symbol is IMethodSymbol { ContainingType: var owner } && IsTask(owner):
          // The argument is evaluated by .NET; dropped, it must have no effect.
          if (!model.GetConstantValue(argument.Expression).HasValue)
            throw new UnsupportedException(argument, "ConfigureAwait with an argument that is no constant");

          expression = access.Expression;
          continue;
        default:
          return expression;
      }
    }
  }

  /// <summary><c>await</c> of the call of an async method of the class, of <c>Task.CompletedTask</c>, of <c>Task.FromResult(v)</c> or of <c>Task.WhenAll</c> of such calls.</summary>
  private IrAwait Await(AwaitExpressionSyntax awaited) {
    var operand = this.Configured(awaited.Expression);
    var symbol = model.GetSymbolInfo(operand).Symbol;
    switch (symbol) {
      case IPropertySymbol { Name: "CompletedTask", ContainingType: var owner } when IsTask(owner) && owner.Name == "Task":
        return new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), []));
      case IMethodSymbol { Name: "FromResult", ContainingType: var owner } when IsTask(owner) && owner.Name == "Task":
        var value = this.Value(((InvocationExpressionSyntax)operand).ArgumentList.Arguments[0].Expression);
        return new IrAwait(new IrAsync(IrAsyncOp.FromResult, IrType.Async(value.Type), [value]));
      case IMethodSymbol { Name: "WhenAll", ContainingType: var owner } when IsTask(owner) && owner.Name == "Task":
        return new IrAwait(this.WhenAll((InvocationExpressionSyntax)operand));
      case IMethodSymbol called when SymbolEqualityComparer.Default.Equals(called.ContainingType, this._class) && IsTask(called.ReturnType):
        return new IrAwait(this.Value(operand));
      case IMethodSymbol { MethodKind: MethodKind.Constructor } called:
        throw new UnsupportedException(operand, $"new {called.ContainingType.Name}");
      case IMethodSymbol called:
        throw new UnsupportedException(operand, $"{called.ContainingType.Name}.{called.Name}");
      default:
        throw new UnsupportedException(operand, IrAsyncFunctions.NotAwaited);
    }
  }

  /// <summary><c>Task.WhenAll(F(a), G(b), …)</c> of calls of async methods of the class, each listed as an argument.</summary>
  private IrAsync WhenAll(InvocationExpressionSyntax invocation) {
    var arguments = new List<IrExpr>();
    // A collection of tasks has no type of the IR: an array or a list of them is refused where it is declared.
    foreach (var argument in invocation.ArgumentList.Arguments) {
      if (argument.Expression is not InvocationExpressionSyntax call || model.GetSymbolInfo(call).Symbol is not IMethodSymbol called
          || !SymbolEqualityComparer.Default.Equals(called.ContainingType, this._class))
        throw new UnsupportedException(argument, IrAsyncFunctions.NotAwaited);

      arguments.Add(this.Value(call));
    }

    var operation = new IrAsync(IrAsyncOp.WhenAll, this.Map(model.GetTypeInfo(invocation).Type, invocation), arguments);
    this._whenAll[operation] = (path, invocation);
    return operation;
  }

  /// <summary>
  /// Refuses every <c>Task.WhenAll</c> one of whose calls writes more than its own locals: the order in
  /// which .NET and JavaScript interleave the calls would show (<see cref="IrAsyncFunctions.Timed"/>).
  /// </summary>
  private void CheckWhenAll(string name, IReadOnlyList<IrFunction> functions) {
    foreach (var (_, operation, callee) in IrAsyncFunctions.Timed(new IrModule(name, functions))) {
      var (file, node) = this._whenAll.TryGetValue(operation, out var known) ? known : this._whenAll.Values.First();
      path = file;
      this.Report(new UnsupportedException(node, $"Task.WhenAll of '{callee}', which writes more than its own locals, whose order depends on timing"));
    }
  }

}
