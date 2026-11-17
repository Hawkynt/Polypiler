using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Rust;

namespace Polypiler.Cli;

/// <summary>A language identifier <c>--from</c> and <c>--to</c> take, with the profiles it has.</summary>
/// <param name="Id">The identifier, spelled as its registry spells it (ADR 0004, "Identifiers").</param>
/// <param name="Profiles">The profile identifiers; empty for a language no component reads or writes yet.</param>
/// <param name="DefaultProfile">The profile a missing <c>--source-profile</c> or <c>--target-profile</c> takes; <see langword="null"/> without profiles.</param>
public sealed record TranslateLanguage(string Id, IReadOnlyList<string> Profiles, string? DefaultProfile) {

  /// <summary>
  /// The normal spelling of a profile a language also takes in another one, such as Python's
  /// <c>3.11</c> for <c>py3.11</c>; <see langword="null"/> for a value it does not take. Without it
  /// only the identifiers of <see cref="Profiles"/> are profiles.
  /// </summary>
  public Func<string, string?>? Spelling { get; init; }

  /// <summary>The profile a command line names in its normal spelling, or the default one; <see langword="null"/> for one the language does not have.</summary>
  /// <param name="profile">The profile as typed, or <see langword="null"/> when it was not.</param>
  public string? Resolve(string? profile)
    => profile is null ? this.DefaultProfile
      : this.Profiles.Contains(profile, StringComparer.Ordinal) ? profile
      : this.Spelling?.Invoke(profile);

}

/// <summary>
/// The language identifiers of <c>polypiler translate</c>: the folders of <c>spec/languages/</c> and
/// <c>spec/targets/</c>, the languages a frontend or backend of the command serves, and the further
/// languages PRD §3.1 plans in both directions. An identifier outside the table is <c>PLY0006</c>; one
/// inside it that the command has no component for is <c>PLY0014</c>. The profiles of a language are
/// those its profile type parses, the default the one it takes when none is named.
/// </summary>
public static class TranslateLanguages {

  /// <summary>Every known language, by identifier.</summary>
  public static IReadOnlyList<TranslateLanguage> All { get; } = [
    new("csharp", [.. CSharpProfile.All.Select(profile => profile.Name)], CSharpProfile.Default.Name),
    new("javascript", JavaScriptProfile.LevelNames, JavaScriptProfile.Default.LevelName),
    new("python", PythonProfile.Names, PythonProfile.Default.Name) { Spelling = PythonSpelling },
    new("rust", [.. RustProfile.All.Select(profile => profile.Name)], RustProfile.Default.Name),
    .. new[] { "c", "cpp", "delphi", "freebasic", "go", "java", "kotlin", "perl", "php", "ruby", "typescript" }.Select(id => new TranslateLanguage(id, [], null))
  ];

  /// <summary>The language of an identifier, matched exactly.</summary>
  /// <param name="id">The identifier as typed.</param>
  /// <returns>The language; <see langword="null"/> for an identifier no registry knows.</returns>
  public static TranslateLanguage? Find(string id) => All.FirstOrDefault(language => language.Id == id);

  /// <summary>A Python profile by its version alone, <c>3.11</c>, in its normal spelling <c>py3.11</c>.</summary>
  private static string? PythonSpelling(string profile) => PythonProfile.TryParse(profile, out var parsed, out _) ? parsed!.Name : null;

}
