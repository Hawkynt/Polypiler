using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the capability table for function values (#1326). The forms follow the C# language
/// version history - anonymous methods in C# 2, lambdas in C# 3, the per-pass foreach variable in C# 5,
/// local functions in C# 7, static local functions in C# 8, static lambdas in C# 9 - and the "Applies
/// to" tables of the .NET API reference: <c>Action&lt;T&gt;</c> from .NET Framework 2.0, <c>Action</c>,
/// <c>Func</c> and <c>Action</c> of up to four parameters from 3.5, of up to sixteen from 4.0, whose
/// first profile here is .NET Framework 4.6. A function type no row covers is a delegate type the class
/// declares, which every profile can.
/// </summary>
internal static class CSharpFunctionRows {

  /// <summary>The name of the row of <c>Action&lt;T&gt;</c>.</summary>
  public const string ActionOfOne = "System.Action<T>";

  /// <summary>The name of the row of <c>Action</c> without parameters.</summary>
  public const string Action = "System.Action";

  /// <summary>The name of the row of <c>Func</c> of up to four parameters.</summary>
  public const string FuncOfFour = "System.Func<T1…T4, TResult>";

  /// <summary>The name of the row of <c>Action</c> of up to four parameters.</summary>
  public const string ActionOfFour = "System.Action<T1…T4>";

  /// <summary>The name of the row of <c>Func</c> of up to sixteen parameters.</summary>
  public const string FuncOfSixteen = "System.Func<T1…T16, TResult>";

  /// <summary>The name of the row of <c>Action</c> of up to sixteen parameters.</summary>
  public const string ActionOfSixteen = "System.Action<T1…T16>";

  /// <summary>Every row of the function values: the language, then the delegate types of the framework.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfLanguage("anonymous method", CSharpFeature.AnonymousMethod, new Version(2, 0)),
    CSharpCapability.OfLanguage("lambda expression", CSharpFeature.LambdaExpression, new Version(3, 0)),
    CSharpCapability.OfLanguage("foreach iteration variable of its own per pass", CSharpFeature.ForEachIterationVariable, new Version(5, 0)),
    CSharpCapability.OfLanguage("static anonymous function", CSharpFeature.StaticAnonymousFunction, new Version(9, 0)),
    CSharpCapability.OfApi(ActionOfOne, CSharpProfile.Net20),
    CSharpCapability.OfApi(Action, CSharpProfile.Net35),
    CSharpCapability.OfApi(FuncOfFour, CSharpProfile.Net35),
    CSharpCapability.OfApi(ActionOfFour, CSharpProfile.Net35),
    CSharpCapability.OfApi(FuncOfSixteen, CSharpProfile.Net46),
    CSharpCapability.OfApi(ActionOfSixteen, CSharpProfile.Net46)
  ];

}
