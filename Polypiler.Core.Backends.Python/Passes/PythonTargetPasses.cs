using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Python.Passes;

/// <summary>The target passes of the Python backend, in the order they run.</summary>
public static class PythonTargetPasses {

  /// <summary>The pipeline the emitter runs between building the tree and printing it.</summary>
  public static TargetPipeline<PyModule, PythonProfile> Pipeline { get; } = new([new PythonUnusedDeclarations()]);

  /// <summary>Every name an expression of a tree reads or calls.</summary>
  /// <param name="node">The tree.</param>
  /// <returns>The names.</returns>
  public static IEnumerable<string> Names(PyNode node) => PythonSyntaxRewriter.DescendantsAndSelf(node).OfType<PyName>().Select(name => name.Name);

}

/// <summary>
/// O0501, unused declaration removal in Python: a helper - of the backend or copied in from the
/// libraries - that nothing outside itself names any more is removed, and with it an <c>import</c>
/// whose module nothing names. It runs until nothing changes, so a helper only a removed helper called
/// goes as well. A function of the module's own is never removed: a private one may still be called by
/// a caller that reaches into the module.
/// </summary>
public sealed class PythonUnusedDeclarations : ITargetPass<PyModule, PythonProfile> {

  /// <inheritdoc/>
  public string Id => "O0501";

  /// <inheritdoc/>
  public string Name => "unused declaration removal";

  /// <inheritdoc/>
  public PyModule Run(PyModule tree, PythonProfile profile, Action<string> applied) {
    ArgumentNullException.ThrowIfNull(tree);
    ArgumentNullException.ThrowIfNull(applied);
    var body = tree.Body.ToList();
    while (true) {
      var unused = body.FirstOrDefault(statement => statement is PyFunctionDef { IsHelper: true } helper
        && !body.Where(other => !ReferenceEquals(other, helper)).SelectMany(PythonTargetPasses.Names).Contains(helper.Name));
      unused ??= body.FirstOrDefault(statement => statement is PyImport import && !body.SelectMany(PythonTargetPasses.Names).Contains(import.Module));
      if (unused is null)
        break;

      applied(unused is PyFunctionDef function ? $"removed the helper {function.Name}, which nothing calls" : $"removed the import of {((PyImport)unused).Module}, which nothing uses");
      var index = body.IndexOf(unused);
      body.RemoveAt(index);
      // The statement that follows takes over the blank lines in front of the removed one.
      if (index < body.Count && index > 0)
        body[index] = body[index] with { Leading = body[index].Leading.BlankLines >= unused.Leading.BlankLines ? body[index].Leading : unused.Leading };
    }

    return body.Count == tree.Body.Count ? tree : tree with { Body = body };
  }

}
