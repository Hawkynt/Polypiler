using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.IR.Intrinsics;

namespace Polypiler.Core.Legalize;

/// <summary>
/// Binds the contracts of <c>spec/intrinsics/</c> to the code of Polypiler: each to the operation of the
/// IR it names, the interpreter's meaning of that operation as its evaluator, and as its decomposition
/// the helpers of the library - <see cref="IntegerLibrary"/> for an integer operation, the portable
/// implementation of <see cref="FloatLegalization"/> for a floating-point one. A contract whose
/// operation has neither is one the targets provide from their runtimes, and its document says so.
/// </summary>
public static class IntrinsicCatalog {

  /// <summary>The operation of the IR a contract names, by its name.</summary>
  /// <param name="contract">The contract.</param>
  /// <returns>The operation.</returns>
  /// <exception cref="IntrinsicRegistryException"><c>PLY0250</c>: the IR has no operation of the name.</exception>
  public static IrIntrinsicOp Operation(IntrinsicContract contract) {
    ArgumentNullException.ThrowIfNull(contract);
    return Enum.GetNames<IrIntrinsicOp>().Contains(contract.Id.Name, StringComparer.Ordinal)
      ? Enum.Parse<IrIntrinsicOp>(contract.Id.Name)
      : throw new IntrinsicRegistryException(IntrinsicDiagnostics.Unknown.At($"unknown intrinsic '{contract.Id}'"));
  }

  /// <summary>The decomposition the library has for an operation, or <see langword="null"/> when it has none.</summary>
  /// <param name="op">The operation.</param>
  /// <returns>The decomposition, or <see langword="null"/>.</returns>
  public static IIntrinsicDecomposition? Library(IrIntrinsicOp op)
    => IntegerOperations.Operations.Contains(op) ? new LibraryDecomposition(IntegerLibrary.Call)
      : FloatOperationTable.Of(op) is { DoubleName: not null, Traps: null } ? new LibraryDecomposition(FloatLegalization.Rewrite(_ => false))
      : null;

  /// <summary>The registry of the contracts, each bound as this class says.</summary>
  /// <param name="contracts">The contracts.</param>
  /// <returns>The registry.</returns>
  /// <exception cref="IntrinsicRegistryException">A contract names no operation (<c>PLY0250</c>), or the builder refuses the registry.</exception>
  public static IIntrinsicRegistry Build(IEnumerable<IntrinsicContract> contracts) {
    ArgumentNullException.ThrowIfNull(contracts);
    var builder = new IntrinsicRegistryBuilder();
    foreach (var contract in contracts) {
      var op = Operation(contract);
      builder.Add(contract, new InterpretedEvaluator(op), Library(op));
    }

    return builder.Build();
  }

  /// <summary>A rewrite of the library as a decomposition.</summary>
  private sealed class LibraryDecomposition(LibraryRewrite rewrite) : IIntrinsicDecomposition {

    public IrExpr? Expand(IrIntrinsic node, IIntrinsicLibrary library) => rewrite(node, (LibraryCalls)library);

  }

}
