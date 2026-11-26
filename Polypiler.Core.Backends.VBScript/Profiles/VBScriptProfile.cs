using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.VBScript;

/// <summary>The VBScript target profile a module is emitted for.</summary>
/// <param name="Name">The canonical profile name (<c>vbscript/5.8</c>).</param>
public sealed record VBScriptProfile(string Name) {

  /// <summary>The VBScript 5.8 profile (<c>vbscript/5.8</c>).</summary>
  public static VBScriptProfile Vbs58 { get; } = new("vbscript/5.8");

  /// <summary>The default VBScript profile (<c>vbscript/5.8</c>).</summary>
  public static VBScriptProfile Default => Vbs58;

  /// <summary>Every named VBScript profile.</summary>
  public static IReadOnlyList<string> Names { get; } = ["vbscript/5.8"];

  /// <summary>Parses a VBScript profile name, or returns <see cref="Default"/> when <see langword="null"/>.</summary>
  /// <param name="profile">The profile name, or <see langword="null"/> for default.</param>
  /// <returns>The parsed profile.</returns>
  /// <exception cref="ArgumentException">The profile name is unknown.</exception>
  public static VBScriptProfile Parse(string? profile = null)
    => TryParse(profile, out var result, out var error) ? result! : throw new ArgumentException(error, nameof(profile));

  /// <summary>Tries to parse a VBScript profile name.</summary>
  /// <param name="profile">The profile name (<c>vbscript/5.8</c> or <see langword="null"/> for default).</param>
  /// <param name="result">The parsed profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the profile name, or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when the profile is valid.</returns>
  public static bool TryParse(string? profile, out VBScriptProfile? result, out string? error) {
    if (profile is null || string.Equals(profile, Vbs58.Name, StringComparison.Ordinal)) {
      result = Vbs58;
      error = null;
      return true;
    }

    result = null;
    error = $"unknown VBScript profile '{profile}'; the supported profile is {Vbs58.Name}";
    return false;
  }

  /// <inheritdoc />
  public override string ToString() => this.Name;

}

/// <summary>Options controlling script file naming for VBScript emission.</summary>
public sealed record VBScriptEmitOptions {

  /// <summary>Default emission options.</summary>
  public static VBScriptEmitOptions Default { get; } = new();

  /// <summary>Overrides the generated script stem (without <c>.vbs</c>); <see langword="null"/> derives it from the module name.</summary>
  public string? ScriptName { get; init; }

}
