using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.VisualBasic;

/// <summary>
/// The classic Visual Basic a module is written for, named <c>visualbasic/&lt;dialect&gt;</c>:
/// <c>vb6</c> (Visual Basic 6.0), <c>vba6</c> (VBA 6 of Office 2000 to 2007), <c>vba7</c> (VBA 7 of
/// Office 2010 and later, code that compiles on its 32-bit and its 64-bit hosts) and <c>vba7-64</c>
/// (VBA 7 on a 64-bit host, which alone has <c>LongLong</c>). What each has is the
/// <see cref="VisualBasicCapabilities"/>; the language of a standard module is otherwise the same in
/// all four ([MS-VBAL] 1.0).
/// </summary>
public sealed record VisualBasicProfile {

  /// <summary>The prefix of every profile name.</summary>
  public const string Prefix = "visualbasic/";

  private VisualBasicProfile(string dialect, string title) {
    this.Dialect = dialect;
    this.Title = title;
  }

  /// <summary>The dialect: <c>vb6</c>, <c>vba6</c>, <c>vba7</c> or <c>vba7-64</c>.</summary>
  public string Dialect { get; }

  /// <summary>What the dialect is, as a header states it.</summary>
  public string Title { get; }

  /// <summary>The name of the profile, <c>visualbasic/&lt;dialect&gt;</c>.</summary>
  public string Name => Prefix + this.Dialect;

  /// <summary>Visual Basic 6.0.</summary>
  public static VisualBasicProfile Vb6 { get; } = new("vb6", "Visual Basic 6.0");

  /// <summary>Visual Basic for Applications 6 (Office 2000 to 2007).</summary>
  public static VisualBasicProfile Vba6 { get; } = new("vba6", "Visual Basic for Applications 6");

  /// <summary>Visual Basic for Applications 7, for its 32-bit and its 64-bit hosts alike.</summary>
  public static VisualBasicProfile Vba7 { get; } = new("vba7", "Visual Basic for Applications 7");

  /// <summary>Visual Basic for Applications 7 on a 64-bit host (Win64), which has <c>LongLong</c>.</summary>
  public static VisualBasicProfile Vba7x64 { get; } = new("vba7-64", "Visual Basic for Applications 7, 64-bit host");

  /// <summary>Every profile, oldest first.</summary>
  public static IReadOnlyList<VisualBasicProfile> All { get; } = [Vb6, Vba6, Vba7, Vba7x64];

  /// <summary>The names of every profile, oldest first.</summary>
  public static IReadOnlyList<string> Names { get; } = [.. All.Select(profile => profile.Name)];

  /// <summary>The profile the translation writes when none is named: VBA 7, which runs on every current Office host.</summary>
  public static VisualBasicProfile Default => Vba7;

  /// <summary>Reads a profile name: <c>visualbasic/vba7</c> or the dialect alone, <c>vba7</c>, in any case.</summary>
  /// <param name="name">The name; <see langword="null"/> for the default.</param>
  /// <param name="profile">The profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the name, or <see langword="null"/>.</param>
  /// <returns>Whether the name is a profile.</returns>
  public static bool TryParse(string? name, out VisualBasicProfile? profile, out string? error) {
    error = null;
    if (name is null) {
      profile = Default;
      return true;
    }

    var dialect = name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? name[Prefix.Length..] : name;
    profile = All.FirstOrDefault(candidate => string.Equals(candidate.Dialect, dialect, StringComparison.OrdinalIgnoreCase));
    if (profile is not null)
      return true;

    error = $"unknown Visual Basic profile '{name}'; the profiles are {string.Join(", ", Names)}";
    return false;
  }

  /// <summary>Reads a profile name as <see cref="TryParse"/> does.</summary>
  /// <param name="name">The name; the default profile for <see langword="null"/>.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="ArgumentException">The name is no profile.</exception>
  public static VisualBasicProfile Parse(string? name)
    => TryParse(name, out var profile, out var error) ? profile! : throw new ArgumentException(error, nameof(name));

  /// <summary>The name of the profile.</summary>
  public override string ToString() => this.Name;

}
