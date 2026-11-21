using System.Collections.Generic;
using System.Text;

namespace Polypiler.Core.Backends.Perl;

/// <summary>Emits a standalone <c>Test::More</c> test file (<c>t/&lt;module&gt;.t</c>) for test-role functions.</summary>
internal static class PerlTestWriter {

  public static string Emit(string package, IReadOnlyList<string> exports, IReadOnlyList<(string Name, string Body)> tests) {
    var text = new StringBuilder();
    text.Append("use strict;\nuse warnings;\nuse Test::More;\n");
    text.Append("use ").Append(package);
    if (exports.Count > 0)
      text.Append(" qw(").AppendJoin(' ', exports).Append(')');
    text.Append(";\n\n");

    foreach (var (name, body) in tests) {
      var label = PerlNaming.StringLiteral(name);
      text.Append("subtest ").Append(label).Append(" => sub {\n");
      text.Append("    my $_ok = eval {\n");
      text.Append("        (sub {\n");
      foreach (var line in body.Split('\n')) {
        if (line.Length > 0)
          text.Append("        ").Append(line).Append('\n');
      }
      text.Append("        })->();\n");
      text.Append("        1;\n");
      text.Append("    };\n");
      text.Append("    ok($_ok, ").Append(label).Append(") or diag($@);\n");
      text.Append("};\n\n");
    }

    text.Append("done_testing;\n");
    return text.ToString();
  }

}
