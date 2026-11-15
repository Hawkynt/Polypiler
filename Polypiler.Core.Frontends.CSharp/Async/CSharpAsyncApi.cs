using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The construct table of async C# (#1330), which <c>docs/reference/async.md</c> prints and the tests
/// check against the lowering: what the frontend imports as an async function, an await or an async
/// value of the IR, and what it refuses, each with the words its diagnostic names it by. Only the subset
/// whose order of effects .NET and JavaScript agree on is imported.
/// </summary>
public static class CSharpAsyncApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<CSharpStringMember> Members { get; } = [
    new("async Task, async Task<T> methods of the class", "an async function, async<void> or async<T>"),
    new("async ValueTask, async ValueTask<T> methods of the class", "an async function whose type is flagged as a value task"),
    new("await F(…) of an async method of the class", "Await of the call"),
    new("await Task.CompletedTask", "Await of Completed"),
    new("await Task.FromResult(v)", "Await of FromResult"),
    new("await Task.WhenAll(F(…), G(…), …) of isolated async methods of the class", "Await of WhenAll"),
    new("x.ConfigureAwait(false), x.ConfigureAwait(true)", "x: dropped"),
    new("try, catch, finally around await", "the exceptions of #1323"),
    new("var t = F(); … await t; a task passed, returned or stored without await", "", "a task that is not awaited where it is created"),
    new("TaskCompletionSource, TaskCompletionSource<T>", "", "a task completed by hand"),
    new("t.Result, t.Wait(), t.GetAwaiter().GetResult(), Task.WaitAll", "", "blocking on a task"),
    new("Task.Run, Task.Delay, Task.Yield, t.ContinueWith, Task.WhenAny", "", "Task."),
    new("async void", "", "async void"),
    new("an async lambda, an async local function", "", "an async lambda"),
    new("await foreach, IAsyncEnumerable<T>", "", "await foreach"),
    new("Task.WhenAll of a method that writes more than its own locals", "", "whose order depends on timing"),
    new("Task.WhenAll over a collection of tasks, an array or a list of tasks", "", "Task<int>[]")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<CSharpStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<CSharpStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
