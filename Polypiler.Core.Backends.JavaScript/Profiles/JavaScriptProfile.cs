using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The JavaScript a module is emitted for: the language level, the module format, whether an engine
/// below ECMAScript 2015 has typed arrays, and the name of the global object of a script. A profile
/// is made by <see cref="Parse"/> or <see cref="TryParse"/> and is therefore always a valid combination.
/// </summary>
public sealed record JavaScriptProfile {

  private static readonly (string Name, JavaScriptLevel Level)[] _levels = [
    ("es5", JavaScriptLevel.Es5), ("es2015", JavaScriptLevel.Es2015), ("es2017", JavaScriptLevel.Es2017),
    ("es2020", JavaScriptLevel.Es2020), ("es2022", JavaScriptLevel.Es2022)
  ];

  private static readonly (string Name, JavaScriptModuleFormat Format, string Extension)[] _formats = [
    ("esm", JavaScriptModuleFormat.Esm, ".mjs"), ("cjs", JavaScriptModuleFormat.Cjs, ".cjs"), ("iife", JavaScriptModuleFormat.Iife, ".js")
  ];

  private JavaScriptProfile(JavaScriptLevel level, JavaScriptModuleFormat module, bool typedArrays, string? global) {
    this.Level = level;
    this.Module = module;
    this.TypedArrays = typedArrays;
    this.Global = global;
  }

  /// <summary>The profile of a command line that names none: ECMAScript 2022 as an ES module.</summary>
  public static JavaScriptProfile Default { get; } = new(JavaScriptLevel.Es2022, JavaScriptModuleFormat.Esm, true, null);

  /// <summary>The names <see cref="Parse"/> takes for a level, oldest first.</summary>
  public static IReadOnlyList<string> LevelNames { get; } = [.. _levels.Select(level => level.Name)];

  /// <summary>The names <see cref="Parse"/> takes for a module format.</summary>
  public static IReadOnlyList<string> ModuleNames { get; } = [.. _formats.Select(format => format.Name)];

  /// <summary>The language level.</summary>
  public JavaScriptLevel Level { get; }

  /// <summary>The module format.</summary>
  public JavaScriptModuleFormat Module { get; }

  /// <summary>
  /// Whether the engine has typed arrays and <c>DataView</c>. From ECMAScript 2015 on it always has;
  /// an ECMAScript 5 engine has them only when the profile says so.
  /// </summary>
  public bool TypedArrays { get; }

  /// <summary>The name of the global object of an <see cref="JavaScriptModuleFormat.Iife"/>; otherwise <see langword="null"/>.</summary>
  public string? Global { get; }

  /// <summary>The name of the level as <see cref="Parse"/> takes it, for example <c>es2017</c>.</summary>
  public string LevelName => _levels.First(level => level.Level == this.Level).Name;

  /// <summary>The name of the module format as <see cref="Parse"/> takes it, for example <c>cjs</c>.</summary>
  public string ModuleName => _formats.First(format => format.Format == this.Module).Name;

  /// <summary>The extension of a generated file, which tells <c>node</c> the module format: <c>.mjs</c>, <c>.cjs</c> or <c>.js</c>.</summary>
  public string Extension => _formats.First(format => format.Format == this.Module).Extension;

  /// <summary>Makes a profile from the values of the command line.</summary>
  /// <param name="level">The level, or <see langword="null"/> for ECMAScript 2022.</param>
  /// <param name="module">
  /// The module format, or <see langword="null"/> for an ES module — for CommonJS at ECMAScript 5,
  /// which has no modules of its own.
  /// </param>
  /// <param name="global">The name of the global object; needed by <c>iife</c> and by nothing else.</param>
  /// <param name="typedArrays">Whether an ECMAScript 5 engine has typed arrays; from ECMAScript 2015 on it has them anyway.</param>
  /// <returns>The profile.</returns>
  /// <exception cref="FormatException">A name is unknown or the combination is not valid; the message says which.</exception>
  public static JavaScriptProfile Parse(string? level = null, string? module = null, string? global = null, bool typedArrays = false)
    => TryParse(level, module, global, typedArrays, out var profile, out var error) ? profile : throw new FormatException(error);

  /// <summary>Makes a profile from the values of the command line, or says why there is none.</summary>
  /// <param name="level">The level, or <see langword="null"/> for ECMAScript 2022.</param>
  /// <param name="module">The module format, or <see langword="null"/> for the one the level starts with.</param>
  /// <param name="global">The name of the global object; needed by <c>iife</c> and by nothing else.</param>
  /// <param name="typedArrays">Whether an ECMAScript 5 engine has typed arrays.</param>
  /// <param name="profile">The profile, when there is one.</param>
  /// <param name="error">What is wrong, when there is none.</param>
  /// <returns>Whether the values make a profile.</returns>
  public static bool TryParse(
    string? level, string? module, string? global, bool typedArrays,
    [NotNullWhen(true)] out JavaScriptProfile? profile, [NotNullWhen(false)] out string? error) {
    profile = null;
    var chosen = _levels.Where(candidate => candidate.Name == (level ?? "es2022")).ToList();
    if (chosen.Count == 0) {
      error = $"unknown JavaScript profile '{level}'; the profiles are {Listed(LevelNames)}";
      return false;
    }

    var modern = chosen[0].Level >= JavaScriptLevel.Es2015;
    var format = _formats.Where(candidate => candidate.Name == (module ?? (modern ? "esm" : "cjs"))).ToList();
    if (format.Count == 0) {
      error = $"unknown module format '{module}'; the module formats are {Listed(ModuleNames)}";
      return false;
    }

    var script = format[0].Format == JavaScriptModuleFormat.Iife;
    error = !modern && format[0].Format == JavaScriptModuleFormat.Esm ? "the profile 'es5' has no ES modules; its module formats are 'cjs' and 'iife'"
      : script && global is null ? "the module format 'iife' needs the name of its global object: --global <name>"
      : !script && global is not null ? $"a global object belongs to the module format 'iife', not to '{format[0].Name}'"
      : global is not null && !JavaScriptWords.IsIdentifier(global) ? $"'{global}' is not a name a JavaScript global can have"
      : null;
    if (error is not null)
      return false;

    profile = new JavaScriptProfile(chosen[0].Level, format[0].Format, modern || typedArrays, global);
    return true;
  }

  /// <summary>The profile as the header of a generated file states it, for example <c>es5 with typed arrays, cjs</c>.</summary>
  public override string ToString() {
    var level = this.Level == JavaScriptLevel.Es5 && this.TypedArrays ? "es5 with typed arrays" : this.LevelName;
    return this.Global is null ? $"{level}, {this.ModuleName}" : $"{level}, {this.ModuleName} as the global {this.Global}";
  }

  private static string Listed(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));

}
