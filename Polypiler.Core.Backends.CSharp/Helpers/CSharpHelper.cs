// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// A private static method a generated class gets when it uses it: what stands in for an API the
/// target framework lacks, or for a result no C# operator has. Every helper is written in C# 2 and
/// uses nothing but the base library of .NET Framework 2.0, so one text serves every profile; it
/// computes the same whatever overflow checking the consuming project sets.
/// </summary>
public sealed class CSharpHelper {

  /// <summary>Describes a helper.</summary>
  /// <param name="name">The name the method has unless a member of the class already has it.</param>
  /// <param name="returns">The return type.</param>
  /// <param name="parameters">The parameter list, without its parentheses.</param>
  /// <param name="body">The lines of the body, indented from the method by nothing.</param>
  /// <param name="typeParameters">The type parameter list with its angle brackets, or nothing.</param>
  public CSharpHelper(string name, string returns, string parameters, IReadOnlyList<string> body, string typeParameters = "") {
    this.Name = name;
    this.Returns = returns;
    this.Parameters = parameters;
    this.Body = body;
    this.TypeParameters = typeParameters;
  }

  /// <summary>The name the method has unless a member of the class already has it.</summary>
  public string Name { get; }

  /// <summary>The return type.</summary>
  public string Returns { get; }

  /// <summary>The parameter list, without its parentheses.</summary>
  public string Parameters { get; }

  /// <summary>The lines of the body.</summary>
  public IReadOnlyList<string> Body { get; }

  /// <summary>The type parameter list with its angle brackets, or nothing.</summary>
  public string TypeParameters { get; }

  /// <summary>The lines of the method under a name.</summary>
  /// <param name="name">The name the class gives the method.</param>
  /// <returns>The declaration, the body indented by two spaces, and the closing brace.</returns>
  public IReadOnlyList<string> Lines(string name) => [
    $"private static {this.Returns} {name}{this.TypeParameters}({this.Parameters}) {{",
    .. this.Body.Select(line => "  " + line),
    "}"
  ];

  /// <summary>The folder of the repository every helper's source lies in, all of it released for outputs under <see cref="EmittedHelper.OwnLicence"/>.</summary>
  public const string Folder = "Polypiler.Core.Backends.CSharp/Helpers";

  /// <summary>The licence of the helper in an output.</summary>
  public string Licence => EmittedHelper.OwnLicence;

  /// <summary>The source the helper is copied from.</summary>
  public string Origin => EmittedHelper.OriginOf(Folder);

  /// <summary>How the output manifest names the method under a name: the name, the type parameters and the parameter list, which tell overloads apart.</summary>
  /// <param name="name">The name the class gives the method.</param>
  public string Symbol(string name) => $"{name}{this.TypeParameters}({this.Parameters})";

}
