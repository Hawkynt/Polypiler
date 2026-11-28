using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.BinaryFormats.Discovery;
using Polypiler.Core.MachineSemantics;
using Polypiler.Core.MachineSemantics.Ports;
using Polypiler.Core.MachineSemantics.Regions;

namespace Polypiler.Tooling.Fuzz;

/// <summary>
/// The toy instruction set of the discovery tests (#942), one-byte opcodes:
/// <c>00</c> no operation, <c>01</c> return, <c>02 rr</c> jump, <c>03 rr</c> conditional jump,
/// <c>04 rr</c> call (each target the address of the next instruction plus the signed <c>rr</c>),
/// <c>05</c> indirect jump, <c>06</c> trap (no return), <c>07 nn</c> followed by <c>nn</c> signed bytes
/// an indirect jump through an inline table (each target relative to the address after the table;
/// the instruction is <c>2 + nn</c> bytes long), anything else invalid.
/// </summary>
public sealed class ToyIsa : IDisassembler, IJumpTableResolver {

  /// <summary>The ISA's name.</summary>
  public const string Name = "toy";

  /// <inheritdoc/>
  public string Isa => Name;

  /// <summary>An image of toy code: one executable section of the bytes at an address, with its entries and function symbols.</summary>
  /// <param name="bytes">The code.</param>
  /// <param name="address">The address of its first byte.</param>
  /// <param name="entries">The container's entries.</param>
  /// <param name="symbols">Addresses with a function symbol, named <c>f_&lt;address&gt;</c>.</param>
  /// <returns>The binary.</returns>
  public static LoadedBinary Load(byte[] bytes, ulong address, IReadOnlyList<ulong> entries, params ulong[] symbols) {
    ArgumentNullException.ThrowIfNull(bytes);
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(symbols);
    var section = new ImageSection(".text", address, bytes, (ulong)bytes.Length, true, false, true);
    var image = new BinaryImage("raw", Name, Name, ByteOrder.Little, [section],
      [.. symbols.Select(symbol => new ImageSymbol($"f_{symbol:x}", symbol, 0, SymbolKind.Function, null))], [], [], [.. entries]);
    return new LoadedBinary(image, new AddressMap([]), [], null, null);
  }

  /// <summary>The bytes of a listing such as <c>04 02 01 FF</c>.</summary>
  /// <param name="hex">Two hex digits per byte, separated by white space.</param>
  public static byte[] Bytes(string hex) {
    ArgumentNullException.ThrowIfNull(hex);
    return [.. hex.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(pair => Convert.ToByte(pair, 16))];
  }

  /// <inheritdoc/>
  public DisassembledInstruction? Disassemble(IMachineCodeSource source, ulong address, string mode, IDiagnosticSink sink) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(sink);
    if (source.ByteAt(address) is not { } opcode)
      return null;

    DisassembledInstruction Make(int length, FlowKind flow, bool returns, string text, params ulong[] targets)
      => new(address, length, flow, [.. targets], returns, text, opcode);
    ulong Relative(ulong after, byte offset) => after + (ulong)(long)(sbyte)offset;

    switch (opcode) {
      case 0x00:
        return Make(1, FlowKind.Sequential, true, "nop");
      case 0x01:
        return Make(1, FlowKind.Return, false, "ret");
      case 0x05:
        return Make(1, FlowKind.IndirectJump, false, "jmp *");
      case 0x06:
        return Make(1, FlowKind.Trap, false, "trap");
      case 0x02 or 0x03 or 0x04:
        if (source.ByteAt(address + 1) is not { } offset)
          return null;
        var target = Relative(address + 2, offset);
        return opcode switch {
          0x02 => Make(2, FlowKind.Jump, false, $"jmp {MachineSyntax.Number(target)}", target),
          0x03 => Make(2, FlowKind.ConditionalJump, true, $"jcc {MachineSyntax.Number(target)}", target),
          _ => Make(2, FlowKind.Call, true, $"call {MachineSyntax.Number(target)}", target)
        };
      case 0x07:
        if (source.ByteAt(address + 1) is not { } count || Enumerable.Range(0, count).Any(index => source.ByteAt(address + 2 + (ulong)index) is null))
          return null;
        return Make(2 + count, FlowKind.IndirectJump, false, $"jmp table[{count}]");
      default:
        sink.Report(new Diagnostic("TOY0001", DiagnosticSeverity.Warning, $"byte {opcode:X2} at {MachineSyntax.Number(address)} is no toy instruction"));
        return null;
    }
  }

  /// <summary>The targets of an inline table: those its bounds-checked count of entries name; none for the bare indirect jump <c>05</c>.</summary>
  /// <inheritdoc/>
  public JumpTable? Resolve(BinaryImage image, IReadOnlyList<DisassembledInstruction> block, DisassembledInstruction jump) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(jump);
    if (!Equals(jump.Payload, (byte)0x07))
      return null;

    var count = image.ByteAt(jump.Address + 1)!.Value;
    var table = jump.Address + 2;
    var after = table + count;
    ImmutableArray<ulong> targets = [.. Enumerable.Range(0, count).Select(index => after + (ulong)(long)(sbyte)image.ByteAt(table + (ulong)index)!.Value)];
    return new JumpTable(targets, table, count, $"inline table of {count} entries bounded by its count");
  }

}

/// <summary>
/// Discovery on a toy image made of the input (#942): the bytes at 0x1000 with their first byte as the
/// entry. It must end within its budget and give the same result twice.
/// </summary>
public sealed class DiscoveryToyFuzzTarget : Polypiler.Core.Verification.Fuzzing.IFuzzTarget {

  /// <inheritdoc/>
  public string Id => "discovery-toy";

  /// <inheritdoc/>
  public void Execute(ReadOnlySpan<byte> input) {
    var binary = ToyIsa.Load(input.ToArray(), 0x1000, input.IsEmpty ? [] : [0x1000]);
    var options = new DiscoveryOptions(100_000);
    Same(Discovery.Run(binary, new ToyIsa(), new ToyIsa(), options), Discovery.Run(binary, new ToyIsa(), new ToyIsa(), options));
  }

  /// <summary>Fails the input when two runs on it gave different results.</summary>
  /// <param name="first">The first result.</param>
  /// <param name="second">The second result.</param>
  /// <exception cref="InvalidOperationException">The results differ.</exception>
  public static void Same(DiscoveryResult first, DiscoveryResult second) {
    ArgumentNullException.ThrowIfNull(first);
    if (!first.Equals(second))
      throw new InvalidOperationException("discovery gave two different results for one toy image");
  }

}
