using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.VisualBasic.Passes;

/// <summary>The target passes of the Visual Basic backend, in the order they run.</summary>
public static class VisualBasicTargetPasses {

  /// <summary>The pipeline the emitter runs between building the tree and printing it.</summary>
  public static TargetPipeline<VbModule, VisualBasicProfile> Pipeline { get; } = new([new VisualBasicUnusedDeclarations()]);

}

/// <summary>
/// O0501, unused declaration removal in Visual Basic: a helper - of the backend or copied in from the
/// libraries - that nothing outside itself names any more is removed. It runs until nothing changes,
/// so a helper only a removed helper called goes as well. A procedure of the module's own is never
/// removed. Names are compared without case, as Visual Basic compares them.
/// </summary>
public sealed class VisualBasicUnusedDeclarations : ITargetPass<VbModule, VisualBasicProfile> {

  /// <inheritdoc/>
  public string Id => "O0501";

  /// <inheritdoc/>
  public string Name => "unused declaration removal";

  /// <inheritdoc/>
  public VbModule Run(VbModule tree, VisualBasicProfile profile, Action<string> applied) {
    ArgumentNullException.ThrowIfNull(tree);
    ArgumentNullException.ThrowIfNull(applied);
    var body = tree.Declarations.ToList();
    while (true) {
      var unused = body.OfType<VbProcedure>().FirstOrDefault(helper => helper.IsHelper
        && !body.Where(other => !ReferenceEquals(other, helper)).SelectMany(VbSyntaxRewriter.Names).Contains(helper.Name, StringComparer.OrdinalIgnoreCase));
      if (unused is null)
        break;

      applied($"removed the helper {unused.Name}, which nothing calls");
      var index = body.IndexOf(unused);
      body.RemoveAt(index);
      if (index < body.Count && index > 0)
        body[index] = body[index] with { Leading = body[index].Leading.BlankLines >= unused.Leading.BlankLines ? body[index].Leading : unused.Leading };
    }

    return body.Count == tree.Declarations.Count ? tree : tree with { Declarations = body };
  }

}
