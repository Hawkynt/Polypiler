using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The source text of <see cref="HelperOperations"/>, one file per family, as this assembly carries
/// it: the helpers a backend binds into a module for an operation its target has no native form of.
/// The files are written once, in the subset of C# the C# frontend imports, so every backend emits the
/// same helper through its ordinary path and every pass of the IR sees it. Every file states its
/// licence in an <c>SPDX-License-Identifier</c> header on its first line.
/// </summary>
public static class HelperOperationSources {

  /// <summary>The folder of the repository the files lie in.</summary>
  public const string Folder = "Polypiler.Core.IR/Helpers/Operations";

  private static readonly LibrarySources _sources = new(Folder, "Polypiler.Core.IR.Helpers.Operations.", new Dictionary<string, string[]>(StringComparer.Ordinal));

  /// <summary>
  /// The methods of the library that stand for an operation of the IR C# has no method for, by their
  /// names: the C# frontend imports a call of one as the operation, and the method as no function.
  /// </summary>
  public static IReadOnlyDictionary<string, IrIntrinsicOp> Intrinsics { get; } = new Dictionary<string, IrIntrinsicOp>(StringComparer.Ordinal) {
    [nameof(HelperOperations.IntrinsicRoundHalfUp)] = IrIntrinsicOp.RoundHalfUp
  };

  /// <summary>Every file as its name and its text, in the order of the names.</summary>
  public static IReadOnlyList<(string Name, string Text)> All => _sources.All;

  /// <summary>The file that declares a helper, with the licence its header states.</summary>
  /// <param name="function">The name of the helper in <see cref="HelperOperations"/>.</param>
  /// <returns>The path of the file in the repository and its licence; <see langword="null"/> for a name no file declares.</returns>
  public static (string Path, string Licence)? Origin(string function) => _sources.Origin(function);

  /// <summary>The lines of the notices an output keeps for the helpers it carries; none for a file under MIT alone.</summary>
  /// <param name="implementations">The names of the helpers the output carries.</param>
  public static IReadOnlyList<string> Notices(IEnumerable<string> implementations) {
    ArgumentNullException.ThrowIfNull(implementations);
    return _sources.Notices(implementations);
  }

}
