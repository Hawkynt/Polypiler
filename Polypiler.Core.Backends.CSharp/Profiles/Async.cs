using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the async functions of #1330: <c>async</c> and <c>await</c> came with C# 5 and the builders of
/// .NET Framework 4.5, which every profile from net4.6 on has; net2.0 and net3.5 refuse a module with an
/// async function, for a task type of the class's own would be no API a caller of .NET recognises.
/// <c>Task.CompletedTask</c> came with .NET Framework 4.6, <c>ValueTask&lt;T&gt;</c> with .NET Standard 2.1 and
/// .NET Core 2.1 (in the framework, without a package); below, a flagged function returns <c>Task&lt;T&gt;</c>.
/// </summary>
internal static class CSharpAsyncRows {

  /// <summary>The name of the row of <c>async</c> and <c>await</c>.</summary>
  public const string AsyncAwait = "async/await";

  /// <summary>The name of the row of <c>Task.CompletedTask</c>.</summary>
  public const string CompletedTask = "Task.CompletedTask";

  /// <summary>The name of the row of <c>ValueTask&lt;T&gt;</c>.</summary>
  public const string ValueTask = "ValueTask<T>";

  /// <summary>Every row of the async functions.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfLanguage(AsyncAwait, CSharpFeature.AsyncAwait, new Version(5, 0)),
    CSharpCapability.OfApi(CompletedTask, CSharpProfile.Net46),
    CSharpCapability.OfApi(ValueTask, CSharpProfile.NetStandard21)
  ];

}
