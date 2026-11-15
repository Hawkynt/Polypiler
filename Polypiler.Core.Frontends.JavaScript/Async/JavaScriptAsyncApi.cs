using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The construct table of async JavaScript (#1330), which <c>docs/reference/async.md</c> prints and the
/// tests check against the lowering: what the frontend imports as an async function, an await or an
/// async value of the IR, and what it refuses, each with the words its diagnostic names it by. Only the
/// subset whose order of effects JavaScript and .NET agree on is imported.
/// </summary>
public static class JavaScriptAsyncApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<JavaScriptArrayMember> Members { get; } = [
    new("async function f(…) { … } of the module", "an async function, async<T> of the type its returns are inferred to"),
    new("await f(…) of an async function of the module", "Await of the call"),
    new("await Promise.resolve(), await Promise.resolve(v)", "Await of Completed, of FromResult"),
    new("await v of a value that is no promise", "Await of FromResult"),
    new("await Promise.all([f(…), g(…), …]) of isolated async functions of which at most one can fault", "Await of WhenAll"),
    new("try, catch, finally around await", "the exceptions of #1323"),
    new("const p = f(); … await p; a promise passed, returned or stored without await", "", "a task that is not awaited where it is created"),
    new("new Promise(…)", "", "a task completed by hand"),
    new("o.then, o.catch, o.finally of an object of a class of the module that declares the method", "a method call"),
    new("p.then, p.catch, p.finally of a promise, or of any receiver when no class of the module declares the method", "", ".then"),
    new("an object of a class with then, returned from an async function or awaited", "", "a thenable object"),
    new("setTimeout, setInterval, setImmediate, queueMicrotask", "", "setTimeout"),
    new("Promise.race, Promise.any, Promise.allSettled, Promise.reject", "", "Promise."),
    new("an async arrow function, an async function nested in a function, an async method", "", "an async lambda"),
    new("an async generator, for await", "", "an async generator"),
    new("Promise.all of an array that is no literal of calls", "", "Promise.all over a collection of promises"),
    new("Promise.all of which two calls can fault", "", "Promise.all whose rejection depends on timing"),
    new("Promise.all of a function that writes more than its own locals", "", "whose order depends on timing")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<JavaScriptArrayMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<JavaScriptArrayMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
