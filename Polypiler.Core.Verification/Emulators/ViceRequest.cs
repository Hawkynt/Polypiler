using System;
using System.Collections.Immutable;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>A Commodore machine VICE emulates, with its memory configuration.</summary>
public enum CbmMachine {

  /// <summary>The Commodore 64: <c>x64sc</c>.</summary>
  C64,

  /// <summary>The VIC-20 without memory expansion: <c>xvic -memory none</c>.</summary>
  Vic20Unexpanded,

  /// <summary>The VIC-20 with the 3K expansion: <c>xvic -memory 3k</c>.</summary>
  Vic20Plus3K,

  /// <summary>The VIC-20 with an 8K expansion: <c>xvic -memory 8k</c>.</summary>
  Vic20Plus8K,

  /// <summary>The Commodore 128 in its own mode with the 40-column screen: <c>x128 -40col</c>.</summary>
  C128

}

/// <summary>A range of the emulated machine's address space.</summary>
/// <param name="Start">The first address.</param>
/// <param name="Length">The number of bytes, at least one, not reaching past <c>$FFFF</c>.</param>
public sealed record AddressRange(ushort Start, int Length) {

  /// <summary>The number of bytes.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The range is empty or reaches past <c>$FFFF</c>.</exception>
  public int Length { get; } = Length >= 1 && Start + Length <= 0x10000 ? Length : throw new ArgumentOutOfRangeException(nameof(Length), Length, "a range holds at least one byte and ends at $FFFF");

  /// <summary>The last address of the range.</summary>
  public ushort End => (ushort)(this.Start + this.Length - 1);

}

/// <summary>What one VICE run executes and reads back.</summary>
/// <param name="Machine">The machine the PRG runs on.</param>
/// <param name="Prg">The program file: a two-byte load address followed by a BASIC program. It is never modified.</param>
/// <param name="KeyboardInput">What is typed after the autostart's <c>RUN</c>: printable ASCII, letters as the unshifted keys, <c>\n</c> for RETURN; <see langword="null"/> for nothing.</param>
/// <param name="ReadAfterRun">The memory read when BASIC is reached again; a default array is empty.</param>
/// <param name="CycleLimit">The number of cycles after which the emulator stops, at least one.</param>
public sealed record ViceRequest(CbmMachine Machine, ReadOnlyMemory<byte> Prg, string? KeyboardInput, ImmutableArray<AddressRange> ReadAfterRun, long CycleLimit) {

  /// <summary>The machine the PRG runs on.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="CbmMachine"/>.</exception>
  public CbmMachine Machine { get; } = Enum.IsDefined(Machine) ? Machine : throw new ArgumentOutOfRangeException(nameof(Machine), Machine, "not a machine VICE is run as");

  /// <summary>What is typed after the autostart's <c>RUN</c>.</summary>
  /// <exception cref="ArgumentException">A character is neither printable ASCII other than <c>\</c> nor <c>\n</c>.</exception>
  public string? KeyboardInput { get; } = KeyboardInput is null || KeyboardInput.AsSpan().IndexOfAnyExcept(Typeable) < 0 ? KeyboardInput : throw new ArgumentException("keyboard input is printable ASCII without '\\', and '\\n' for RETURN", nameof(KeyboardInput));

  /// <summary>The memory read when BASIC is reached again.</summary>
  public ImmutableArray<AddressRange> ReadAfterRun { get; } = ReadAfterRun.IsDefault ? [] : ReadAfterRun;

  /// <summary>The number of cycles after which the emulator stops.</summary>
  /// <exception cref="ArgumentOutOfRangeException">The limit is not positive.</exception>
  public long CycleLimit { get; } = CycleLimit >= 1 ? CycleLimit : throw new ArgumentOutOfRangeException(nameof(CycleLimit), CycleLimit, "the cycle limit is at least one cycle");

  /// <summary>The program's name in a diagnostic; <c>PROG.PRG</c> unless given.</summary>
  public string Name { get; init; } = ViceRunner.ProgramFile;

  /// <summary>Whether a run that does not return to BASIC is a failure, <c>PLY3935</c>, rather than a <see cref="ViceStatus.CycleLimit"/> result. An oracle requires it.</summary>
  public bool RequireReturn { get; init; }

  /// <summary>The limits of the emulator's process; the runner's defaults unless given.</summary>
  public RunLimits Limits { get; init; } = RunLimits.Default;

  /// <summary>Where the program came from; untrusted unless given, so it runs only where the sandbox isolates it.</summary>
  public TrustLevel Trust { get; init; } = TrustLevel.Untrusted;

  private static readonly System.Buffers.SearchValues<char> Typeable = System.Buffers.SearchValues.Create("\n !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{|}~");

}
