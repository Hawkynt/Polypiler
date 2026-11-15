using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Backends.Testing;

/// <summary>A source translated to Python as <c>translate</c> does it, or the reasons it is not.</summary>
/// <param name="Source">The generated module, or <see langword="null"/> when the backend refused the source.</param>
/// <param name="Tree">The tree the module was printed from, or <see langword="null"/>.</param>
/// <param name="Names">The Python name of every function by its name in the IR, or <see langword="null"/>.</param>
/// <param name="Problems">The diagnostics of a refusal, as text.</param>
internal sealed record PythonTranslation(string? Source, PyModule? Tree, IReadOnlyDictionary<string, string>? Names, IReadOnlyList<string> Problems);

/// <summary>The prototype fixtures through the Python backend, by the pipeline <c>translate</c> runs.</summary>
internal static class PythonFixtures {

  /// <summary>The prototype fixtures every backend is compared on.</summary>
  public static IReadOnlyList<string> Prototype => PrototypeFixture.Names;

  /// <summary>
  /// Translates a source to Python: imported, optimized, legalized for the profile, checked, its
  /// legalized module optimized again, built with the target passes and printed.
  /// </summary>
  /// <param name="source">The text of the source.</param>
  /// <param name="file">Its file name, whose extension names the language: <c>.cs</c> or JavaScript.</param>
  /// <param name="profile">The profile; the default one when <see langword="null"/>.</param>
  /// <returns>The translation.</returns>
  /// <exception cref="InvalidOperationException">The frontend refuses the source.</exception>
  public static PythonTranslation Translate(string source, string file, PythonProfile? profile = null) {
    var imported = file.EndsWith(".cs", StringComparison.Ordinal)
      ? CSharpFrontend.Import(file, source).Module
      : JavaScriptFrontend.Import(file, source).Module;
    if (imported is null)
      throw new InvalidOperationException($"the frontend refuses {file}");

    return Translate(imported, profile);
  }

  /// <summary>Translates an imported module to Python as <see cref="Translate(string, string, PythonProfile?)"/> does.</summary>
  /// <param name="imported">The module as a frontend imported it.</param>
  /// <param name="profile">The profile; the default one when <see langword="null"/>.</param>
  /// <param name="passes">Which target passes run; all of them when <see langword="null"/>.</param>
  /// <returns>The translation.</returns>
  public static PythonTranslation Translate(IrModule imported, PythonProfile? profile = null, TargetPassOptions? passes = null) {
    profile ??= PythonProfile.Default;
    var optimized = Optimizer.Run(imported, new OptimizerOptions { Target = RuleTarget.Neutral }).Module;
    var legalized = PythonEmitter.Legalize(optimized, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    var problems = PythonEmitter.Check(legalized, profile);
    if (problems.Count > 0)
      return new(null, null, null, [.. problems.Select(problem => problem.ToString())]);

    var module = IrPassPipeline.Default.Run(legalized, new OptimizerOptions()).Module;
    var tree = PythonEmitter.Build(module, profile, FloatMode.Accurate, null, passes ?? TargetPassOptions.Default).Tree;
    var text = PythonEmitter.Emit(module, profile, FloatMode.Accurate, null, passes ?? TargetPassOptions.Default).Files.Single().Content;
    return new(text, tree, PythonEmitter.FunctionNames(module), []);
  }

}
