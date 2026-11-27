using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.Go;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Recognize;

namespace Polypiler.Backends.Testing;

/// <summary>A module translated to Go as <c>translate</c> does it, or the reasons it is not.</summary>
/// <param name="Files">The files of the generated module, or <see langword="null"/> when the backend refused it.</param>
/// <param name="Module">The legalized module the files were written from, or <see langword="null"/>.</param>
/// <param name="Names">The Go name of every function by its name in the IR, or <see langword="null"/>.</param>
/// <param name="Problems">The diagnostics of a refusal, as text.</param>
internal sealed record GoTranslation(IReadOnlyList<GeneratedFile>? Files, IrModule? Module, IReadOnlyDictionary<string, string>? Names, IReadOnlyList<string> Problems);

/// <summary>Modules through the Go backend, by the pipeline <c>translate</c> runs.</summary>
internal static class GoFixtures {

  /// <summary>
  /// Translates an imported module to Go: optimized, legalized for the profile, its legalized
  /// module optimized again and emitted with the target passes.
  /// </summary>
  /// <param name="imported">The module as a frontend imported it.</param>
  /// <param name="profile">The profile; the default one when <see langword="null"/>.</param>
  /// <returns>The translation.</returns>
  public static GoTranslation Translate(IrModule imported, GoProfile? profile = null) {
    profile ??= GoProfile.Default;
    var optimized = Optimizer.Run(imported, new OptimizerOptions { Target = RuleTarget.Neutral }).Module;
    var legalized = GoEmitter.Legalize(optimized, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    var module = IrPassPipeline.Default.Run(legalized, new OptimizerOptions()).Module;
    var problems = GoEmitter.Check(module, profile);
    if (problems.Count > 0)
      return new(null, null, null, [.. problems.Select(problem => problem.ToString())]);

    var files = GoEmitter.Emit(module, profile, FloatMode.Accurate, null, TargetPassOptions.Default).Files;
    return new(files, module, GoEmitter.FunctionNames(module), []);
  }

}
