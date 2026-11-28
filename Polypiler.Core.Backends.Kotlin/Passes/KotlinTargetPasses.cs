using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Kotlin.Passes;

/// <summary>The target passes of the Kotlin backend, in the order they run.</summary>
public static class KotlinTargetPasses {

  /// <summary>The pipeline the emitter runs between building the tree and printing it.</summary>
  public static TargetPipeline<KtFile, KotlinProfile> Pipeline { get; } = new([new KotlinUnusedDeclarations()]);

  /// <summary>Every name an expression of a tree reads or calls.</summary>
  /// <param name="node">The tree.</param>
  /// <returns>The names.</returns>
  public static IEnumerable<string> Names(KtNode node) => KotlinSyntaxWalker.DescendantsAndSelf(node).OfType<KtName>().Select(name => name.Name);

}

/// <summary>
/// O0501, unused declaration removal in Kotlin: a helper - of the backend or copied in from the
/// libraries - that nothing outside itself names any more is removed. It runs until nothing changes,
/// so a helper only a removed helper called goes as well. A function of the module's own is never
/// removed: a private one is still part of what was translated.
/// </summary>
public sealed class KotlinUnusedDeclarations : ITargetPass<KtFile, KotlinProfile> {

  /// <inheritdoc/>
  public string Id => "O0501";

  /// <inheritdoc/>
  public string Name => "unused declaration removal";

  /// <inheritdoc/>
  public KtFile Run(KtFile tree, KotlinProfile profile, Action<string> applied) {
    ArgumentNullException.ThrowIfNull(tree);
    ArgumentNullException.ThrowIfNull(applied);
    var declarations = tree.Declarations.ToList();
    while (declarations.FirstOrDefault(declaration => declaration is KtFunction { IsHelper: true } helper
      && !declarations.Where(other => !ReferenceEquals(other, helper)).SelectMany(KotlinTargetPasses.Names).Contains(helper.Name)) is KtFunction unused) {
      applied($"removed the helper {unused.Name}, which nothing calls");
      declarations.Remove(unused);
    }

    return declarations.Count == tree.Declarations.Count ? tree : tree with { Declarations = declarations };
  }

}
