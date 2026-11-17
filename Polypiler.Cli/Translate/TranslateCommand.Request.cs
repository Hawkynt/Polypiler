using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Cli;

/// <summary>
/// What <c>translate</c> resolves and records before and after its pipeline (#180): the language and
/// profile identifiers (<c>PLY0006</c>), the components that serve them (<c>PLY0014</c>), the exports
/// (<c>PLY0013</c>), the vectors <c>--verify</c> compares with, and the canonical command line the
/// manifest stores as <c>requestedConfig</c>.
/// </summary>
public sealed partial class TranslateCommand {

  private const string SourceProfile = "--source-profile";
  private const string LegacyProfile = "--profile";
  private const string Verify = "--verify";
  private const string VectorsOption = "--vectors";

  /// <summary>The options of the request every pair of languages takes.</summary>
  private static readonly OptionSpec[] _request = [
    new(SourceProfile, "<name>"), new(LegacyProfile, "<name>"), new(Export, "<name>[,<name>...]"), new(Verify, null), new(VectorsOption, "<file>")
  ];

  /// <summary>The options of a command line that change how a run reports, not what it produces; <c>requestedConfig</c> leaves them out with <c>--output</c>.</summary>
  private static readonly string[] _unrecorded = [Output, ExplainOpt, ExplainTypes];

  /// <summary>The list options, which <c>requestedConfig</c> records as sorted sets.</summary>
  private static readonly HashSet<string> _lists = new(StringComparer.Ordinal) { Export, DisableOpt, Instantiate, Bind };

  /// <summary>
  /// Resolves the request before anything is read: <c>--profile</c> as the former spelling of
  /// <c>--target-profile</c>, the languages and their profiles, the components that serve them, and
  /// the vectors of <c>--verify</c>.
  /// </summary>
  /// <param name="file">The input as typed.</param>
  /// <param name="options">The options of the command line by name; <c>--profile</c> becomes <c>--target-profile</c>.</param>
  /// <param name="context">Receives the diagnostics.</param>
  /// <param name="verification">What <c>--verify</c> compares with; <see langword="null"/> without it.</param>
  /// <returns>The exit code of a refusal; <see langword="null"/> when the request can be carried out.</returns>
  private int? Resolve(string file, Dictionary<string, string> options, CliContext context, out VerificationRequest? verification) {
    verification = null;
    if (options.ContainsKey(LegacyProfile) && options.ContainsKey(Profile)) {
      context.Report(Diagnostic.Create(CliDiagnostics.ExclusiveOptions, null, LegacyProfile, Profile));
      return (int)ExitCode.UsageError;
    }

    if (options.Remove(LegacyProfile, out var legacy))
      options[Profile] = legacy;

    if (Language(options, From, SourceProfile, context) is not { } source || Language(options, To, Profile, context) is not { } target)
      return (int)ExitCode.UsageError;

    if (_frontends.All(frontend => frontend.Name != source.Id))
      return Unserved(TranslateDiagnostics.NoFrontend, source.Id, options.GetValueOrDefault(SourceProfile, "default"), context);

    if (this._backendsOfThis.All(backend => backend.Name != target.Id))
      return Unserved(TranslateDiagnostics.NoBackend, target.Id, options.GetValueOrDefault(Profile, "default"), context);

    return Vectors(file, options, context, out verification);
  }

  /// <summary>The options of an emission outside a command line with <c>--profile</c> under its present name, <c>--target-profile</c>.</summary>
  private static Dictionary<string, string> Spelled(IReadOnlyDictionary<string, string> options)
    => options.ToDictionary(option => option.Key == LegacyProfile ? Profile : option.Key, option => option.Value, StringComparer.Ordinal);

  /// <summary>The language an option names, with a profile it has; <see langword="null"/> after a <c>PLY0006</c>.</summary>
  private static TranslateLanguage? Language(IReadOnlyDictionary<string, string> options, string option, string profileOption, CliContext context) {
    var id = options[option];
    if (TranslateLanguages.Find(id) is not { } language) {
      context.Report(Diagnostic.Create(TranslateDiagnostics.UnknownIdentifier, null, string.Format(CultureInfo.InvariantCulture, TranslateDiagnostics.UnknownLanguage, id)));
      return null;
    }

    // A language no component serves has no profiles yet; what it lacks is PLY0014.
    if (language.Profiles.Count == 0)
      return language;

    var typed = options.GetValueOrDefault(profileOption);
    if (language.Resolve(typed) is null) {
      context.Report(Diagnostic.Create(TranslateDiagnostics.UnknownIdentifier, null, string.Format(CultureInfo.InvariantCulture, TranslateDiagnostics.UnknownProfile, id, typed)));
      return null;
    }

    return language;
  }

  private static int Unserved(string format, string language, string profile, CliContext context) {
    context.Report(Diagnostic.Create(TranslateDiagnostics.NoComponent, null, string.Format(CultureInfo.InvariantCulture, format, language, profile)) with {
      RequiredCapability = $"{language} {profile}"
    });
    return (int)ExitCode.UnsupportedConstruct;
  }

  /// <summary>
  /// Checks <c>--verify</c> and <c>--vectors</c> and loads the vectors: <c>--vectors</c> needs
  /// <c>--verify</c>; <c>--verify</c> needs a target whose output the command can run, and a vector
  /// file that names a function with a signature the driver passes.
  /// </summary>
  private static int? Vectors(string file, Dictionary<string, string> options, CliContext context, out VerificationRequest? verification) {
    verification = null;
    var verify = options.ContainsKey(Verify);
    if (!verify)
      return options.ContainsKey(VectorsOption) ? Refuse(context, $"option '{VectorsOption}' requires {Verify}") : null;

    if (options[To] != "javascript")
      return Refuse(context, $"option '{Verify}' is not available for the target language '{options[To]}'; the command builds and runs JavaScript");

    if (JavaScriptProfileOf(options).Profile?.Module == Core.Backends.JavaScript.JavaScriptModuleFormat.Iife)
      return Refuse(context, $"option '{Verify}' takes a module: the module format 'iife' declares a global object, which the driver does not import");

    var exports = options.TryGetValue(Export, out var names) ? Names(names) : [];
    if (!options.TryGetValue(VectorsOption, out var path)) {
      verification = new(file.Replace('\\', '/'), null, exports.FirstOrDefault() ?? "");
      return null;
    }

    if (!CliPaths.RequireInput(path, context))
      return (int)ExitCode.EnvironmentError;

    VectorFile vectors;
    try {
      vectors = VectorFile.Load(context.Resolve(path));
    } catch (ImageException invalid) {
      context.Report(Diagnostic.Create(ImageDiagnostics.Invalid, null, invalid.Message));
      return (int)ExitCode.UsageError;
    } catch (FormatException invalid) {
      return Refuse(context, invalid.Message);
    }

    if (vectors.Signature is not { } signature)
      return Refuse(context, $"the vector file '{path}' names no export and signature; {Verify} calls a function");

    if (JavaScriptDriver.Unsupported(signature) is { } unsupported)
      return Refuse(context, $"the vector file '{path}': {unsupported}");

    if (vectors.Exactness is not ExactnessPolicy.Exact)
      return Refuse(context, $"the vector file '{path}' compares within a tolerance; {Verify} compares the integer and bool results of the driver exactly");

    verification = new(vectors.Fixture, vectors, vectors.Export!);
    return null;
  }

  /// <summary>
  /// The exports of an imported module: every public function, or with <c>--export</c> only the ones
  /// it names, the others kept as functions of the module. The JavaScript frontend reads
  /// <c>--export</c> itself. An input without any public function, and a name it does not have, are
  /// <c>PLY0013</c>; so is the function <c>--verify</c> calls when the module does not export it.
  /// </summary>
  /// <returns>The module with its exports; <see langword="null"/> after a <c>PLY0013</c>.</returns>
  private static IrModule? Exported(string file, string language, IrModule module, IReadOnlyDictionary<string, string> options, VerificationRequest? verification, CliContext context) {
    var exported = module.Functions.Where(function => function.IsPublic).Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
    if (exported.Count == 0)
      return NotExported(TranslateDiagnostics.NothingToExport, context, file);

    string[] names = language != "javascript" && options.TryGetValue(Export, out var list) ? Names(list) : [];
    var called = verification is { Export.Length: > 0 } ? [verification.Export] : Array.Empty<string>();
    if (names.Concat(called).FirstOrDefault(name => !exported.Contains(name)) is { } missing)
      return NotExported(TranslateDiagnostics.ExportNotFound, context, missing, file);

    return names.Length == 0
      ? module
      : module with { Functions = [.. module.Functions.Select(function => function.IsPublic && !names.Contains(function.Name) ? function with { IsPublic = false } : function)] };
  }

  private static IrModule? NotExported(string format, CliContext context, params object[] arguments) {
    context.Report(Diagnostic.Create(TranslateDiagnostics.Export, null, string.Format(CultureInfo.InvariantCulture, format, arguments)));
    return null;
  }

  /// <summary>
  /// The canonical command line of a request (ADR 0004): the input as typed, the own options with the
  /// effective profiles of both languages, typed or their defaults, then <c>--opt</c> with its effective level and <c>--disable-opt</c>; the
  /// output directory and the options that only change how the run reports are left out.
  /// </summary>
  private static string Requested(string file, IReadOnlyDictionary<string, string> options) {
    var recorded = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    foreach (var (name, value) in options.Where(option => !_unrecorded.Contains(option.Key)))
      recorded[name] = IsFlag(name) ? [] : [value];

    recorded[SourceProfile] = [TranslateLanguages.Find(options[From])!.Resolve(options.GetValueOrDefault(SourceProfile))!];
    recorded[Profile] = [TranslateLanguages.Find(options[To])!.Resolve(options.GetValueOrDefault(Profile))!];
    recorded[Opt] = [options.GetValueOrDefault(Opt, "default")];
    return RequestedConfig.Build("translate", [file], recorded, _lists);
  }

  /// <summary>Whether an option is a flag: one of the request, the pipeline or a language that takes no value.</summary>
  private static bool IsFlag(string name)
    => _request.Concat(_pipeline).Concat(_frontends.SelectMany(frontend => frontend.Options)).Concat(_backends.SelectMany(backend => backend.Options))
      .Any(option => option.Name == name && option.Value is null);

}
