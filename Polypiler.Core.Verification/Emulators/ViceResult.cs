using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>How a VICE run ended.</summary>
public enum ViceStatus {

  /// <summary>The program ended and BASIC printed its <c>READY.</c> prompt again.</summary>
  ReturnedToBasic,

  /// <summary>The cycle limit was reached before BASIC was; never reported for a run that returned.</summary>
  CycleLimit,

  /// <summary>The emulator did not run the program to either end; the diagnostics say why.</summary>
  EmulatorFailed

}

/// <summary>Bytes read from the emulated machine's memory.</summary>
/// <param name="Start">The address of the first byte.</param>
/// <param name="Bytes">The bytes, in address order.</param>
public sealed record MemoryBlock(ushort Start, byte[] Bytes) {

  /// <summary>The bytes, in address order.</summary>
  public byte[] Bytes { get; } = Bytes ?? throw new ArgumentNullException(nameof(Bytes));

}

/// <summary>What a VICE run produced.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="ScreenText">
/// What the program printed to the screen between the autostart's <c>RUN</c> and BASIC's next
/// <c>READY.</c>, in the text form of <see cref="PetsciiText"/>: lines separated by <c>\n</c>, trailing
/// blanks and trailing empty lines removed.
/// </param>
/// <param name="Memory">The ranges of <see cref="ViceRequest.ReadAfterRun"/>, in request order; empty unless the run returned to BASIC.</param>
/// <param name="Diagnostics">Why the run failed; empty when it did not.</param>
public sealed record ViceResult(ViceStatus Status, string ScreenText, ImmutableArray<MemoryBlock> Memory, IReadOnlyList<Diagnostic> Diagnostics) {

  /// <summary>What the machine printed after its reset and before its first <c>READY.</c>: the start-up banner.</summary>
  public string Banner { get; init; } = "";

  /// <summary>The cycle at which BASIC was reached again, the cycle limit for <see cref="ViceStatus.CycleLimit"/>, or zero.</summary>
  public long Cycles { get; init; }

  /// <summary>The diagnostics.</summary>
  public IReadOnlyList<Diagnostic> Diagnostics { get; } = Diagnostics?.ToArray() ?? throw new ArgumentNullException(nameof(Diagnostics));

  /// <summary>Whether the run gave an answer an oracle may use: no diagnostics.</summary>
  public bool Succeeded => this.Diagnostics.Count == 0;

}
