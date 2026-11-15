using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>The namespace of the task types.</summary>
  private const string TasksNamespace = "global::System.Threading.Tasks.";

  /// <summary>The modifiers of the member a function becomes, with <c>async</c> for an async function.</summary>
  private static IReadOnlyList<string> Asynchronous(IReadOnlyList<string> modifiers, IrFunction function)
    => function.IsAsync ? [.. modifiers, "async"] : modifiers;

  /// <summary>A profile without <c>async</c> and <c>await</c> cannot write a module with an async function: <see cref="Inexpressible"/>.</summary>
  private static IEnumerable<Diagnostic> AsyncProblems(IrModule module, CSharpProfile profile, CSharpCapabilities table)
    => module.Functions.Any(function => function.IsAsync) && !table.Has(CSharpFeature.AsyncAwait, profile)
      ? [InexpressibleDescriptor.At($"'async function' cannot be expressed in C# profile '{profile.Name}'")]
      : [];

  /// <summary>Whether an async function awaits nothing itself, which C# would warn of (CS1998).</summary>
  private static bool Unawaited(IrFunction function) => function.IsAsync && !IrTree.Nodes(function.Body).OfType<IrAwait>().Any();

  private sealed partial class Writer {

    /// <summary>Whether the profile writes a flagged async type as a <c>ValueTask</c>.</summary>
    private bool ValueTasks => this._table.HasApi(CSharpAsyncRows.ValueTask, this._profile);

    /// <summary>The C# type of an async type: <c>Task</c>, <c>Task&lt;T&gt;</c>, or for a flagged one <c>ValueTask</c> and <c>ValueTask&lt;T&gt;</c> where the profile has them.</summary>
    private string AsyncTypeName(IrType type) {
      var name = TasksNamespace + (type.IsValueTask && this.ValueTasks ? "ValueTask" : "Task");
      return type.Element == IrType.Void ? name : $"{name}<{this.TypeName(type.Element!)}>";
    }

    /// <summary>The line of the header that names the functions declared as a <c>ValueTask</c> the profile writes as a <c>Task</c>.</summary>
    private IEnumerable<string> AsyncNotes() {
      var changed = this._module.Functions.Where(function => function.ReturnType.IsValueTask).Select(function => this._members[function.Name]).ToList();
      return changed.Count > 0 && !this.ValueTasks
        ? [$"ValueTask is written as Task, which this profile has instead: {string.Join(", ", changed)}."]
        : [];
    }

    /// <summary>An async function that awaits nothing awaits the completed task first, which changes nothing and says it is async.</summary>
    private void Awaiting(IrFunction function, List<CsStatement> body) {
      if (Unawaited(function))
        body.Insert(0, new CsExpressionStatement(new CsAwait(this.Completed())));
    }

    /// <summary>The completed task: <c>Task.CompletedTask</c>, or before .NET Framework 4.6 <c>Task.FromResult(0)</c>.</summary>
    private CsExpression Completed()
      => this._table.HasApi(CSharpAsyncRows.CompletedTask, this._profile)
        ? CSharpFragments.Expression(TasksNamespace + "Task.CompletedTask") with { Type = IrType.Async(IrType.Void) }
        : new CsInvocation(CSharpFragments.Expression(TasksNamespace + "Task.FromResult"), [new CsIntegerLiteral(0) { Type = IrType.I32 }]) { Type = IrType.Async(IrType.I32) };

    /// <summary>
    /// The expressions of async functions: <c>await</c> of its operand, the completed task, <c>Task.FromResult&lt;T&gt;(v)</c>
    /// and <c>Task.WhenAll(…)</c>; <see langword="null"/> for any other expression.
    /// </summary>
    private (CsExpression Node, int Binding)? AsyncNode(IrExpr expression) {
      switch (expression) {
        case IrAwait awaited:
          return (new CsAwait(this.Operand(awaited.Operand, Unary)) { Type = awaited.Type }, Unary);
        case IrAsync { Op: IrAsyncOp.Completed }:
          return (this.Completed(), Primary);
        case IrAsync { Op: IrAsyncOp.FromResult } result:
          var value = result.Arguments[0];
          var from = CSharpFragments.Expression($"{TasksNamespace}Task.FromResult<{this.TypeName(value.Type)}>");
          return (new CsInvocation(from, [this.Stored(value)]) { Type = result.Type }, Primary);
        case IrAsync all:
          // Without an argument the result type of the array is named, which no argument gives.
          var element = all.Type.Element!;
          var method = all.Arguments.Count == 0 && element != IrType.Void ? $"Task.WhenAll<{this.TypeName(element.Element!)}>" : "Task.WhenAll";
          return (new CsInvocation(CSharpFragments.Expression(TasksNamespace + method), [.. all.Arguments.Select(this.Text)]) { Type = all.Type }, Primary);
        default:
          return null;
      }
    }

  }

}
