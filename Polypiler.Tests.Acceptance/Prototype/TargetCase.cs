using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// One profile of one of the two backends as a test case, so that a test asked of every profile runs
/// as one case per profile, side by side: a C# profile by its name, or a JavaScript
/// <see cref="ProfileCase"/>.
/// </summary>
/// <param name="CSharp">The C# profile; <see langword="null"/> for JavaScript.</param>
/// <param name="JavaScript">The JavaScript profile; <see langword="null"/> for C#.</param>
public sealed record TargetCase(CSharpProfile? CSharp, ProfileCase? JavaScript) {

  /// <summary>Every C# profile.</summary>
  public static IReadOnlyList<TargetCase> CSharpProfiles { get; } = [.. CSharpProfile.All.Select(profile => new TargetCase(profile, null))];

  /// <summary>Every JavaScript profile.</summary>
  public static IReadOnlyList<TargetCase> JavaScriptProfiles { get; } = [.. ProfileCase.All.Select(profile => new TargetCase(null, profile))];

  /// <summary>Every C# profile, then every JavaScript profile.</summary>
  public static IReadOnlyList<TargetCase> All { get; } = [.. CSharpProfiles, .. JavaScriptProfiles];

  /// <summary>The value of <c>--to</c>.</summary>
  public string Language => this.CSharp is null ? "javascript" : "csharp";

  /// <summary>The options of the command line that choose the profile.</summary>
  public string[] Arguments => this.CSharp is { } profile ? ["--profile", profile.Name] : this.JavaScript!.Arguments;

  /// <summary>The target as a test name shows it.</summary>
  public override string ToString() => this.CSharp is { } profile ? profile.Name : this.JavaScript!.ToString();

}
