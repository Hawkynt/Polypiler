using System;
using System.Globalization;

namespace Polypiler.Core.Verification.Emulators;

/// <summary>
/// The structure every Commodore BASIC PRG has: a two-byte load address, then lines that each start
/// with the address of the next line and a line number and end with a zero byte, and a next-line
/// address of zero after the last line. A PRG without that chain has no program BASIC could run, so an
/// oracle must not report what the emulator printed for it.
/// </summary>
public static class BasicProgram {

  /// <summary>Why a PRG is not a BASIC program, or <see langword="null"/> when it is.</summary>
  /// <param name="prg">The PRG.</param>
  /// <returns>The first problem in file order: the load address, a line that is cut off, or a next-line address that does not point forward into the file.</returns>
  public static string? Problem(ReadOnlySpan<byte> prg) {
    if (prg.Length < 2)
      return $"the PRG has {prg.Length.ToString(CultureInfo.InvariantCulture)} bytes, fewer than its load address needs";

    var load = prg[0] | (prg[1] << 8);
    var offset = 2;
    while (true) {
      var address = Hex(load + offset - 2);
      if (prg.Length - offset < 2)
        return $"the line at {address} is cut off before its next-line address";

      var next = prg[offset] | (prg[offset + 1] << 8);
      if (next == 0)
        return null;

      var end = prg.Length - offset < 4 ? -1 : prg[(offset + 4)..].IndexOf((byte)0);
      if (end < 0)
        return $"the line at {address} is cut off before its end";

      var following = next - load + 2;
      if (following <= offset || following > prg.Length)
        return $"the line at {address} links to {Hex(next)}, which is not a later address in the program";

      offset = following;
    }
  }

  private static string Hex(int address) => "$" + address.ToString("X4", CultureInfo.InvariantCulture);

}
