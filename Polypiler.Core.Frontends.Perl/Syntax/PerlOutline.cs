using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>
/// The outline of a parsed file, in a form perl's own compiler gives as well (through <c>B</c>): the
/// subs the file defines, by qualified name, and the pad of every compilation unit - its lexicals in
/// order of declaration (<c>my $x</c>, <c>our @y</c>, <c>state %z</c>) with each anonymous sub's pad
/// nested in braces where perl allocates it, after the sub's body.
/// </summary>
internal static class PerlOutline {

  public static string Of(PerlDocument document) {
    var builder = new StringBuilder();
    builder.Append("main:").Append(Pad(document.Program)).Append('\n');
    var subs = new SortedDictionary<string, PerlSub>(StringComparer.Ordinal);
    foreach (var declaration in document.Subs)
      subs[declaration.Name] = declaration.Sub;
    foreach (var (name, sub) in subs)
      builder.Append("sub ").Append(name).Append(':').Append(Pad(sub.Unit)).Append('\n');
    return builder.ToString();
  }

  private static string Pad(PerlUnit unit) {
    var parts = unit.Pad.Select(entry => entry switch {
      PerlLexical lexical => $" {lexical.Kind.ToString().ToLowerInvariant()} {lexical.PadName}",
      PerlUnit nested => $" {{{Pad(nested)} }}",
      _ => ""
    });
    return string.Concat(parts);
  }

}
