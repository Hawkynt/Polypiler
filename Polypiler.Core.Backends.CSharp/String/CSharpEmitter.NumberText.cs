using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  /// <summary>The line of the header that names the caveats of the import, or none for a module without one.</summary>
  private static IReadOnlyList<string> Caveats(IrModule module)
    => module.Caveats.Count == 0 ? [] : [$"Caveats of the import: {string.Join(", ", module.Caveats)}."];

  private sealed partial class Writer {

    /// <summary>
    /// The lines of the header that name the code a helper of the class was derived from, with its
    /// author and licence, as the licence asks of a copy in source form.
    /// </summary>
    private IReadOnlyList<string> Notices() {
      var lines = new List<string>();
      if (this._used.Contains(CSharpNumberTextHelpers.NumberText))
        lines.Add("NumberText finds the shortest digits with Ryu, Copyright 2018 Ulf Adams, under the Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0).");

      if (this._used.Contains(CSharpNumberTextHelpers.ParseText))
        lines.Add("ParseText reads parseInt of the radices other than 2, 4, 8, 10, 16 and 32 as V8 does, Copyright 2011 the V8 project authors, under the BSD-3-Clause licence of V8.");

      return lines;
    }

  }

}
