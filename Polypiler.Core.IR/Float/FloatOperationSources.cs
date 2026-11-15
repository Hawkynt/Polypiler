using System;
using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>
/// The source text of <see cref="FloatOperations"/>, one file per family, as this assembly carries it.
/// It is written in the subset of C# the C# frontend imports, so the portable implementations can be
/// translated into every target: a backend that lacks a native function emits the one of the source.
/// Every file states its licence in an <c>SPDX-License-Identifier</c> header on its first line: MIT
/// for Polypiler's own code and for what is converted from CORE-MATH, musl and .NET, with the
/// BSD-2-Clause added where FreeBSD's msun is converted too.
/// </summary>
public static class FloatOperationSources {

  /// <summary>The folder of the repository the files lie in.</summary>
  public const string Folder = "Polypiler.Core.IR/Float/Operations";

  /// <summary>Every file as its name and its text, in the order of the names.</summary>
  public static IReadOnlyList<(string Name, string Text)> All => _sources.All;

  /// <summary>The file that declares a function of the portable implementations, with the licence its header states.</summary>
  /// <param name="function">The name of the function in <see cref="FloatOperations"/>.</param>
  /// <returns>The path of the file in the repository and its licence; <see langword="null"/> for a name no file declares.</returns>
  public static (string Path, string Licence)? Origin(string function) => _sources.Origin(function);

  /// <summary>A portable implementation as a backend wrote it into a file, as a helper of the file.</summary>
  /// <param name="name">The name the function has in the file, which is also its symbol.</param>
  /// <param name="implementation">The name of the function in <see cref="FloatOperations"/>.</param>
  /// <param name="text">Its text, as it stands in the file.</param>
  /// <exception cref="ArgumentException">No file declares <paramref name="implementation"/>.</exception>
  public static HelperText Helper(string name, string implementation, string text) {
    var (path, licence) = Origin(implementation) ?? throw new ArgumentException($"No file of '{Folder}' declares '{implementation}'.", nameof(implementation));
    return new(name, name, licence, EmittedHelper.OriginOf(path), text);
  }

  /// <summary>Which file of a set declares each static method, with the licence the header of the file states.</summary>
  /// <param name="sources">The files, as their names and their texts.</param>
  /// <returns>Each method name with the path of its file in the repository and its licence; the first file that declares a name wins.</returns>
  /// <exception cref="InvalidDataException">A file has no <c>SPDX-License-Identifier</c> header on its first line.</exception>
  public static IReadOnlyDictionary<string, (string Path, string Licence)> Origins(IEnumerable<(string Name, string Text)> sources)
    => LibrarySources.Origins(Folder, sources);

  /// <summary>
  /// The notices a file of the portable implementations obliges an output to keep, by the file's name:
  /// <c>Arithmetic.Single.cs</c> is converted from FreeBSD's <c>lib/msun/src/s_fmaf.c</c>, whose
  /// BSD-2-Clause asks every redistribution of its source to retain the copyright notice, the
  /// conditions and the disclaimer (<c>THIRD-PARTY-NOTICE.musl.txt</c> beside the folder).
  /// </summary>
  private static readonly LibrarySources _sources = new(Folder, "Polypiler.Core.IR.Float.Operations.", new Dictionary<string, string[]>(StringComparer.Ordinal) {
    ["Arithmetic.Single.cs"] = [
      "FusedMultiplyAddF is converted from FreeBSD's lib/msun/src/s_fmaf.c, which carries this notice:",
      "Copyright (c) 2005-2011 David Schultz <das@FreeBSD.ORG>",
      "All rights reserved.",
      "Redistribution and use in source and binary forms, with or without",
      "modification, are permitted provided that the following conditions",
      "are met:",
      "1. Redistributions of source code must retain the above copyright",
      "   notice, this list of conditions and the following disclaimer.",
      "2. Redistributions in binary form must reproduce the above copyright",
      "   notice, this list of conditions and the following disclaimer in the",
      "   documentation and/or other materials provided with the distribution.",
      "THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND CONTRIBUTORS ``AS IS'' AND",
      "ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE",
      "IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE",
      "ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR OR CONTRIBUTORS BE LIABLE",
      "FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL",
      "DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS",
      "OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)",
      "HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT",
      "LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY",
      "OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF",
      "SUCH DAMAGE."
    ]
  });

  /// <summary>
  /// The lines of the notices an output keeps for the portable implementations it carries: each
  /// notice of the files that declare them once, in the order of the file names; none for a file
  /// under MIT alone, whose notice the grant of <c>LICENSE-OUTPUT.md</c> the header names already is.
  /// </summary>
  /// <param name="implementations">The names of the functions in <see cref="FloatOperations"/> the output carries.</param>
  public static IReadOnlyList<string> Notices(IEnumerable<string> implementations) {
    ArgumentNullException.ThrowIfNull(implementations);
    return _sources.Notices(implementations);
  }

}
