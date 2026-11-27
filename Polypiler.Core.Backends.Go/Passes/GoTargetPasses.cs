using System;
using System.Linq;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Go.Passes;

/// <summary>The target passes of the Go backend, in the order they run.</summary>
public static class GoTargetPasses {

  /// <summary>The pipeline the emitter runs between building the tree and printing it.</summary>
  public static TargetPipeline<GoFile, GoProfile> Pipeline { get; } = new([new GoUnusedDeclarations()]);

}

/// <summary>
/// O0501, unused declaration removal in Go: a helper - of the backend or copied in from the
/// libraries - that nothing outside itself names any more is removed. It runs until nothing
/// changes, so a helper only a removed helper called goes as well. A function of the module's own is
/// never removed. The imports follow the declarations that are left, so none is unused: Go refuses
/// a package with an unused import.
/// </summary>
public sealed class GoUnusedDeclarations : ITargetPass<GoFile, GoProfile> {

  /// <inheritdoc/>
  public string Id => "O0501";

  /// <inheritdoc/>
  public string Name => "unused declaration removal";

  /// <inheritdoc/>
  public GoFile Run(GoFile tree, GoProfile profile, Action<string> applied) {
    ArgumentNullException.ThrowIfNull(tree);
    ArgumentNullException.ThrowIfNull(applied);
    var declarations = tree.Declarations.ToList();
    while (true) {
      var unused = declarations.OfType<GoFuncDecl>().FirstOrDefault(helper => helper.IsHelper
        && !declarations.Where(other => !ReferenceEquals(other, helper)).SelectMany(GoSyntaxRewriter.Names).Contains(helper.Name));
      if (unused is null)
        break;

      applied($"removed the helper {unused.Name}, which nothing calls");
      declarations.Remove(unused);
    }

    return declarations.Count == tree.Declarations.Count ? tree : tree with { Declarations = declarations };
  }

}
