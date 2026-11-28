using System.Collections.Generic;
using System.Collections.Immutable;
using Polypiler.Core.MachineSemantics.Ports;

namespace Polypiler.Core.BinaryFormats.Discovery;

/// <summary>Finds the functions, blocks and code bytes of a loaded binary (PRD §21.4).</summary>
public interface IProgramBoundaryResolver {

  /// <summary>Discovers what control flow reaches from the binary's proven entries.</summary>
  /// <param name="binary">The binary.</param>
  /// <param name="disassembler">Decodes its instructions.</param>
  /// <param name="resolver">Resolves indirect jumps through tables, or <see langword="null"/>.</param>
  /// <param name="options">The budget and whether heuristic candidates are traversed; the defaults when <see langword="null"/>.</param>
  /// <returns>The functions, the byte map and the diagnostics.</returns>
  DiscoveryResult Resolve(LoadedBinary binary, IDisassembler disassembler, IJumpTableResolver? resolver, DiscoveryOptions? options = null);

}

/// <summary>Finds the validated targets of an indirect jump.</summary>
public interface IJumpTableResolver {

  /// <summary>The table an indirect jump dispatches through, or <see langword="null"/> when no pattern proves one.</summary>
  /// <param name="image">The image.</param>
  /// <param name="block">The instructions of the block so far, the jump last.</param>
  /// <param name="jump">The indirect jump.</param>
  /// <returns>The table.</returns>
  JumpTable? Resolve(BinaryImage image, IReadOnlyList<DisassembledInstruction> block, DisassembledInstruction jump);

}

/// <summary>The validated targets of an indirect jump and where its table lies.</summary>
/// <param name="Targets">The targets, in table order.</param>
/// <param name="TableAddress">The address of the table's first byte.</param>
/// <param name="TableBytes">The number of the table's bytes, which become data.</param>
/// <param name="Evidence">Why the targets are the only ones, such as the bounds check the resolver read.</param>
public sealed record JumpTable(ImmutableArray<ulong> Targets, ulong TableAddress, int TableBytes, string Evidence);
