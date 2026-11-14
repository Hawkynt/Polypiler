using System;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core;

namespace Polypiler.Tests.Diagnostics;

/// <summary>
/// The text and JSON renderings of ADR 0003. The JSON line is parsed back with
/// <see cref="System.Text.Json"/>, the independent oracle, and compared field by field.
/// </summary>
[TestFixture]
public sealed class DiagnosticRendererTests {

  private static readonly DiagnosticDescriptor _option = new("PLY0003", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "CLI.options");

  /// <summary>Test design 2: a range renders in the MSBuild canonical form.</summary>
  [Test]
  public void Text_WithRange() {
    var diagnostic = Diagnostic.Create(new("PLY0013", DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "r"), new SourceRange("algo.cs", 3, 5, 3, 7), "export 'Sum' not found in 'algo.cs'");
    Assert.That(DiagnosticRenderer.ToText(diagnostic), Is.EqualTo("algo.cs(3,5): error PLY0013: export 'Sum' not found in 'algo.cs'"));
  }

  /// <summary>Test design 4 and 5: every severity, without a range.</summary>
  [TestCase(DiagnosticSeverity.Info, "polypiler: info PLY0099: m")]
  [TestCase(DiagnosticSeverity.Warning, "polypiler: warning PLY0099: m")]
  [TestCase(DiagnosticSeverity.Error, "polypiler: error PLY0099: m")]
  public void Text_WithoutRange(DiagnosticSeverity severity, string expected)
    => Assert.That(DiagnosticRenderer.ToText(new Diagnostic("PLY0099", severity, "m")), Is.EqualTo(expected));

  /// <summary>Test design 17: a line break in the message becomes a space.</summary>
  [TestCase("a\nb", "a b")]
  [TestCase("a\rb", "a b")]
  [TestCase("a\r\nb", "a  b")]
  public void Text_IsOneLine(string message, string expected)
    => Assert.That(DiagnosticRenderer.ToText(new Diagnostic("PLY0099", DiagnosticSeverity.Info, message)), Is.EqualTo("polypiler: info PLY0099: " + expected));

  /// <summary>The acceptance scenario's line, exactly.</summary>
  [Test]
  public void Json_WithoutRange() {
    var diagnostic = Diagnostic.Create(_option, null, "unknown option --frm") with {
      Cause = "the option is not defined for translate",
      Workaround = "use --from",
      Command = "polypiler translate a.cs --frm csharp"
    };
    Assert.That(DiagnosticRenderer.ToJson(diagnostic), Is.EqualTo(
      """{"code":"PLY0003","severity":"error","message":"unknown option --frm","source":null,"rule":"CLI.options","cause":"the option is not defined for translate","workaround":"use --from","requiredCapability":null,"command":"polypiler translate a.cs --frm csharp"}"""));
  }

  /// <summary>Test design 3: every field set, parsed back, equals the diagnostic.</summary>
  [Test]
  public void Json_RoundTrip_EveryField() {
    var diagnostic = Diagnostic.Create(_option, new SourceRange("dir/ä.cs", 2, 3, 4, 1), "quote \" backslash \\ tab \t bell \u0007 newline \n form \f back \b return \r é €") with {
      Cause = "c", Workaround = "w", RequiredCapability = "cap", Command = "polypiler x"
    };
    var line = DiagnosticRenderer.ToJson(diagnostic);
    Assert.That(line, Does.Not.Contain("\n").And.Contain("ä.cs").And.Contain("é €").And.Contain("\\u0007"));

    var root = JsonDocument.Parse(line).RootElement;
    Assert.That(Names(root), Is.EqualTo(new[] { "code", "severity", "message", "source", "rule", "cause", "workaround", "requiredCapability", "command" }));
    Assert.That(root.GetProperty("code").GetString(), Is.EqualTo(diagnostic.Code));
    Assert.That(root.GetProperty("severity").GetString(), Is.EqualTo("error"));
    Assert.That(root.GetProperty("message").GetString(), Is.EqualTo(diagnostic.Message));
    var source = root.GetProperty("source");
    Assert.That(Names(source), Is.EqualTo(new[] { "file", "startLine", "startColumn", "endLine", "endColumn" }));
    Assert.That((source.GetProperty("file").GetString(), source.GetProperty("startLine").GetInt32(), source.GetProperty("startColumn").GetInt32(), source.GetProperty("endLine").GetInt32(), source.GetProperty("endColumn").GetInt32()),
      Is.EqualTo(("dir/ä.cs", 2, 3, 4, 1)));
    Assert.That(root.GetProperty("rule").GetString(), Is.EqualTo("CLI.options"));
    Assert.That(root.GetProperty("cause").GetString(), Is.EqualTo("c"));
    Assert.That(root.GetProperty("workaround").GetString(), Is.EqualTo("w"));
    Assert.That(root.GetProperty("requiredCapability").GetString(), Is.EqualTo("cap"));
    Assert.That(root.GetProperty("command").GetString(), Is.EqualTo("polypiler x"));
  }

  /// <summary>Test design 4: each severity in the JSON rendering.</summary>
  [TestCase(DiagnosticSeverity.Info, "info")]
  [TestCase(DiagnosticSeverity.Warning, "warning")]
  [TestCase(DiagnosticSeverity.Error, "error")]
  public void Json_Severity(DiagnosticSeverity severity, string expected)
    => Assert.That(JsonDocument.Parse(DiagnosticRenderer.ToJson(new Diagnostic("PLY0099", severity, "m"))).RootElement.GetProperty("severity").GetString(), Is.EqualTo(expected));

  /// <summary>Test design 19: the command is carried as given, never re-quoted or normalised.</summary>
  [Test]
  public void Json_CommandWithAQuotedArgument_IsCarriedAsGiven() {
    const string command = "polypiler translate \"my file.cs\" --frm csharp";
    var line = DiagnosticRenderer.ToJson(new Diagnostic("PLY0003", DiagnosticSeverity.Error, "m") { Command = command });
    Assert.That(line, Does.EndWith("\"command\":\"polypiler translate \\\"my file.cs\\\" --frm csharp\"}"));
    Assert.That(JsonDocument.Parse(line).RootElement.GetProperty("command").GetString(), Is.EqualTo(command));
  }

  [Test]
  public void Renderers_RefuseNull() {
    Assert.That(() => DiagnosticRenderer.ToText(null!), Throws.ArgumentNullException);
    Assert.That(() => DiagnosticRenderer.ToJson(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void SeverityName_OfNoMember_IsRefused()
    => Assert.That(() => DiagnosticRenderer.SeverityName((DiagnosticSeverity)7), Throws.InstanceOf<ArgumentOutOfRangeException>());

  private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

}
