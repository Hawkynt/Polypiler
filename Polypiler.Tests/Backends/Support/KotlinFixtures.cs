using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core.Backends.Kotlin;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Backends.Testing;

/// <summary>A source translated to Kotlin as <c>translate</c> does it, or the reasons it is not.</summary>
/// <param name="Source">The generated file, or <see langword="null"/> when the backend refused the source.</param>
/// <param name="Tree">The tree the file was printed from, or <see langword="null"/>.</param>
/// <param name="Module">The module as it was emitted, legalized and optimized, or <see langword="null"/>.</param>
/// <param name="Names">The Kotlin name of every function by its name in the IR, or <see langword="null"/>.</param>
/// <param name="Problems">The diagnostics of a refusal, as text.</param>
internal sealed record KotlinTranslation(string? Source, KtFile? Tree, IrModule? Module, IReadOnlyDictionary<string, string>? Names, IReadOnlyList<string> Problems) {

  /// <summary>The package of the file, or <see langword="null"/> for a refusal.</summary>
  public string? Package => this.Tree?.Package;

}

/// <summary>The prototype fixtures through the Kotlin backend, by the pipeline <c>translate</c> runs.</summary>
internal static class KotlinFixtures {

  /// <summary>The prototype fixtures every backend is compared on.</summary>
  public static IReadOnlyList<string> Prototype => PrototypeFixture.Names;

  /// <summary>Translates a source to Kotlin: imported, optimized, legalized for the profile, checked, optimized again, built and printed.</summary>
  /// <param name="source">The text of the source.</param>
  /// <param name="file">Its file name, whose extension names the language: <c>.cs</c> or JavaScript.</param>
  /// <param name="profile">The profile; the default one when <see langword="null"/>.</param>
  /// <returns>The translation.</returns>
  /// <exception cref="InvalidOperationException">The frontend refuses the source.</exception>
  public static KotlinTranslation Translate(string source, string file, KotlinProfile? profile = null) {
    var imported = file.EndsWith(".cs", StringComparison.Ordinal)
      ? CSharpFrontend.Import(file, source).Module
      : JavaScriptFrontend.Import(file, source).Module;
    return Translate(imported ?? throw new InvalidOperationException($"the frontend refuses {file}"), profile);
  }

  /// <summary>Translates an imported module to Kotlin as <see cref="Translate(string, string, KotlinProfile?)"/> does.</summary>
  /// <param name="imported">The module as a frontend imported it.</param>
  /// <param name="profile">The profile; the default one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run; all of them when <see langword="null"/>.</param>
  /// <returns>The translation.</returns>
  public static KotlinTranslation Translate(IrModule imported, KotlinProfile? profile = null, TargetPassOptions? passes = null) {
    profile ??= KotlinProfile.Default;
    var optimized = Optimizer.Run(imported, new OptimizerOptions { Target = RuleTarget.Neutral }).Module;
    var legalized = KotlinEmitter.Legalize(optimized, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    var problems = KotlinEmitter.Check(legalized, profile);
    if (problems.Count > 0)
      return new(null, null, null, null, [.. problems.Select(problem => problem.ToString())]);

    var module = IrPassPipeline.Default.Run(legalized, new OptimizerOptions()).Module;
    var tree = KotlinEmitter.Build(module, profile, FloatMode.Accurate, null, passes ?? TargetPassOptions.Default).Tree;
    var text = KotlinEmitter.Emit(module, profile, FloatMode.Accurate, null, passes ?? TargetPassOptions.Default).Files.Single().Content;
    return new(text, tree, module, KotlinEmitter.FunctionNames(module), []);
  }

}
