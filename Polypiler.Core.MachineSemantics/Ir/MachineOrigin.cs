using System;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// Where an instruction came from in machine code: the ISA and mode it was decoded in, its address and
/// its bytes, and the disassembly text when one is known. Any binary or bytecode frontend may attach it,
/// with or without a machine profile (MSIL, JVM and WebAssembly name their code offset as the address);
/// every rewrite keeps it on what it produces. Text form: <c>!at(0x00401000, "0F C8")</c>.
/// </summary>
/// <param name="Isa">The ISA, for example <c>x86</c> or <c>wasm</c>.</param>
/// <param name="Mode">The decoding mode, for example <c>protected32</c> or <c>thumb</c>.</param>
/// <param name="Address">The address of the first byte, or the code offset.</param>
/// <param name="Bytes">The bytes of the instruction.</param>
/// <param name="Text">The disassembly, or <see langword="null"/>.</param>
public sealed record MachineOrigin(string Isa, string Mode, ulong Address, ImmutableArray<byte> Bytes, string? Text) {

  /// <inheritdoc/>
  public bool Equals(MachineOrigin? other)
    => other is not null && this.Isa == other.Isa && this.Mode == other.Mode && this.Address == other.Address
      && this.Bytes.SequenceEqual(other.Bytes) && this.Text == other.Text;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Isa, this.Mode, this.Address, this.Bytes.Length, this.Text);

  /// <summary>The bytes as upper-case hex pairs separated by spaces: <c>0F C8</c>.</summary>
  public string HexBytes => string.Join(' ', this.Bytes.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

}
