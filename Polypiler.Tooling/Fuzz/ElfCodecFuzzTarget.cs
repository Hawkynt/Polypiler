using System;
using Polypiler.Core.BinaryFormats.Elf;
using Polypiler.Core.Verification.Fuzzing;

namespace Polypiler.Tooling.Fuzz;

/// <summary>Runs the ELF codec over arbitrary bytes and treats an escaping exception as a finding.</summary>
public sealed class ElfCodecFuzzTarget : IFuzzTarget {

  /// <inheritdoc/>
  public string Id => "elf-codec";

  /// <inheritdoc/>
  public void Execute(ReadOnlySpan<byte> input) => _ = new ElfCodec().Read(input.ToArray(), null);

}
