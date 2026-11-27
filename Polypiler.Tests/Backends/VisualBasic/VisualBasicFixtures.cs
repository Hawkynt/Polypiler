using System.Collections.Generic;
using System.Linq;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Optimize.Passes;
using Polypiler.Core.Optimize.Target;
using Polypiler.Core.Recognize;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>A module translated to Visual Basic as <c>translate</c> does it, or the reasons it is not.</summary>
/// <param name="Source">The generated module, or <see langword="null"/> when the backend refused it.</param>
/// <param name="Tree">The tree the module was printed from, or <see langword="null"/>.</param>
/// <param name="Problems">The diagnostics of a refusal, as text.</param>
internal sealed record VisualBasicTranslation(string? Source, VbModule? Tree, IReadOnlyList<string> Problems);

/// <summary>Sources through the Visual Basic backend, by the pipeline <c>translate</c> runs.</summary>
internal static class VisualBasicFixtures {

  /// <summary>Imports C# methods of a snippet class and translates them.</summary>
  public static VisualBasicTranslation Snippet(string methods, VisualBasicProfile? profile = null)
    => Translate(PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods)), profile);

  /// <summary>Optimizes, legalizes, checks, builds and prints a module.</summary>
  public static VisualBasicTranslation Translate(IrModule imported, VisualBasicProfile? profile = null, TargetPassOptions? passes = null) {
    profile ??= VisualBasicProfile.Default;
    var optimized = Optimizer.Run(imported, new OptimizerOptions { Target = RuleTarget.Neutral }).Module;
    var legalized = VisualBasicEmitter.Legalize(optimized, () => PrecomputedImplementations.Module, profile, FloatMode.Accurate);
    var problems = VisualBasicEmitter.Check(legalized, profile);
    if (problems.Count > 0)
      return new(null, null, [.. problems.Select(problem => problem.ToString())]);

    var module = IrPassPipeline.Default.Run(legalized, new OptimizerOptions()).Module;
    var tree = VisualBasicEmitter.Build(module, profile, FloatMode.Accurate, null, passes ?? TargetPassOptions.Default).Tree;
    var text = VisualBasicEmitter.Emit(module, profile, FloatMode.Accurate, null, passes ?? TargetPassOptions.Default).Files.Single().Content;
    return new(text, tree, []);
  }

}
