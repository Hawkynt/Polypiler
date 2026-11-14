using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;

namespace Polypiler.Tests.Diagnostics;

/// <summary>The types of ADR 0003: <see cref="SourceRange"/>, <see cref="ExitCode"/>, <see cref="DiagnosticDescriptor"/> and <see cref="Diagnostic.Create"/>.</summary>
[TestFixture]
public sealed class DiagnosticTypesTests {

  private static readonly DiagnosticDescriptor _export = new("PLY0013", DiagnosticSeverity.Error, ExitCode.InvalidSource, "export '{0}' not found in '{1}'", "cli.export");

  /// <summary>Test design 11: line 1 column 1 is the smallest range.</summary>
  [Test]
  public void SourceRange_LineOneColumnOne_IsAccepted() {
    var range = new SourceRange("a.cs", 1, 1, 1, 1);
    Assert.That((range.File, range.StartLine, range.StartColumn, range.EndLine, range.EndColumn), Is.EqualTo(("a.cs", 1, 1, 1, 1)));
  }

  /// <summary>Test design 11 and 12: a value below 1, and an end before the start, are refused.</summary>
  [TestCase(0, 1, 1, 1, TestName = "SourceRange_LineZero_IsRefused")]
  [TestCase(1, 0, 1, 1, TestName = "SourceRange_ColumnZero_IsRefused")]
  [TestCase(3, 5, 2, 9, TestName = "SourceRange_EndLineBeforeStart_IsRefused")]
  [TestCase(3, 5, 3, 4, TestName = "SourceRange_EndColumnBeforeStart_IsRefused")]
  [TestCase(3, 5, 4, 0, TestName = "SourceRange_EndColumnZeroOnALaterLine_IsRefused")]
  public void SourceRange_OutOfRange_IsRefused(int startLine, int startColumn, int endLine, int endColumn)
    => Assert.That(() => new SourceRange("a.cs", startLine, startColumn, endLine, endColumn), Throws.InstanceOf<ArgumentOutOfRangeException>());

  [Test]
  public void SourceRange_EndOnALaterLine_MayStartLeftOfTheStart()
    => Assert.That(new SourceRange("a.cs", 3, 5, 4, 1).EndColumn, Is.EqualTo(1));

  [Test]
  public void SourceRange_WithoutFile_IsRefused()
    => Assert.That(() => new SourceRange(null!, 1, 1, 1, 1), Throws.ArgumentNullException);

  /// <summary>The numbers of PRD §10.1 and the README: stable, one per category.</summary>
  [Test]
  public void ExitCode_HasTheDocumentedNumbers() {
    var numbers = Enum.GetValues<ExitCode>().ToDictionary(member => member.ToString(), member => (int)member);
    Assert.That(numbers, Is.EqualTo(new Dictionary<string, int> {
      ["Success"] = 0, ["EnvironmentError"] = 1, ["UsageError"] = 2, ["UnsupportedConstruct"] = 3, ["InvalidSource"] = 4,
      ["SemanticMismatch"] = 5, ["TargetBuildFailure"] = 6, ["OracleFailure"] = 7, ["LicenseBlocker"] = 8,
      ["MissingDependency"] = 9, ["BudgetExceeded"] = 10, ["InternalError"] = 70
    }));
  }

  [Test]
  public void Descriptor_KeepsWhatItWasGiven() {
    Assert.That((_export.Code, _export.Severity, _export.ExitCode, _export.MessageFormat, _export.Rule),
      Is.EqualTo(("PLY0013", DiagnosticSeverity.Error, (ExitCode?)ExitCode.InvalidSource, "export '{0}' not found in '{1}'", "cli.export")));
  }

  /// <summary>Test design 10: a code is PLY and exactly four digits.</summary>
  [TestCase("PLY0000", true)]
  [TestCase("PLY9999", true)]
  [TestCase("PLY10000", false)]
  [TestCase("PLY001", false)]
  [TestCase("ply0001", false)]
  [TestCase("PLY00a1", false)]
  [TestCase("", false)]
  [TestCase(null, false)]
  public void IsCode(string? text, bool expected) => Assert.That(DiagnosticDescriptor.IsCode(text), Is.EqualTo(expected));

  [Test]
  public void Descriptor_WithAMalformedCode_IsRefused()
    => Assert.That(() => new DiagnosticDescriptor("PLY10000", DiagnosticSeverity.Info, null, "x", "r"),
      Throws.ArgumentException.With.Message.StartsWith("'PLY10000' is not a diagnostic code"));

  [Test]
  public void Descriptor_ErrorWithoutExitCode_IsRefused()
    => Assert.That(() => new DiagnosticDescriptor("PLY0030", DiagnosticSeverity.Error, null, "x", "r"),
      Throws.ArgumentException.With.Message.StartsWith("PLY0030: an error needs an exit code"));

  [TestCase(DiagnosticSeverity.Warning)]
  [TestCase(DiagnosticSeverity.Info)]
  public void Descriptor_NonErrorWithExitCode_IsRefused(DiagnosticSeverity severity)
    => Assert.That(() => new DiagnosticDescriptor("PLY0009", severity, ExitCode.UsageError, "x", "r"),
      Throws.ArgumentException.With.Message.StartsWith("PLY0009: only errors carry an exit code"));

  [TestCase(null, "x", "r")]
  [TestCase("PLY0009", null, "r")]
  [TestCase("PLY0009", "x", null)]
  public void Descriptor_WithoutText_IsRefused(string? code, string? format, string? rule)
    => Assert.That(() => new DiagnosticDescriptor(code!, DiagnosticSeverity.Info, null, format!, rule!), Throws.ArgumentNullException);

  [Test]
  public void InternalError_IsPly0001WithExitCode70() {
    var descriptor = DiagnosticDescriptor.InternalError;
    Assert.That((descriptor.Code, descriptor.Severity, descriptor.ExitCode), Is.EqualTo(("PLY0001", DiagnosticSeverity.Error, (ExitCode?)ExitCode.InternalError)));
    Assert.That(Diagnostic.Create(descriptor, null, "boom").Message, Is.EqualTo("internal compiler error: boom"));
  }

  [Test]
  public void Create_FillsEveryFieldFromDescriptorAndRange() {
    var diagnostic = Diagnostic.Create(_export, new SourceRange("algo.cs", 3, 5, 3, 7), "Sum", "algo.cs");
    Assert.That(diagnostic.Code, Is.EqualTo("PLY0013"));
    Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error));
    Assert.That(diagnostic.Message, Is.EqualTo("export 'Sum' not found in 'algo.cs'"));
    Assert.That((diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.EndLine, diagnostic.EndColumn), Is.EqualTo(("algo.cs", 3, 5, 3, 7)));
    Assert.That(diagnostic.ExitCode, Is.EqualTo(ExitCode.InvalidSource));
    Assert.That(diagnostic.Rule, Is.EqualTo("cli.export"));
    Assert.That(diagnostic.Source, Is.EqualTo(new SourceRange("algo.cs", 3, 5, 3, 7)));
  }

  [Test]
  public void Create_FormatsWithTheInvariantCulture()
    => Assert.That(Diagnostic.Create(new DiagnosticDescriptor("PLY0099", DiagnosticSeverity.Info, null, "{0}", "r"), null, 1.5).Message, Is.EqualTo("1.5"));

  [Test]
  public void Create_WithoutRange_HasNoPosition() {
    var diagnostic = Diagnostic.Create(_export, null, "Sum", "algo.cs");
    Assert.That((diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.EndLine, diagnostic.EndColumn), Is.EqualTo(((string?)null, 0, 0, 0, 0)));
    Assert.That(diagnostic.Source, Is.Null);
  }

  [Test]
  public void Create_WithoutDescriptor_IsRefused()
    => Assert.That(() => Diagnostic.Create(null!, null), Throws.ArgumentNullException);

  [Test]
  public void Create_WithTooFewArguments_IsRefused()
    => Assert.That(() => Diagnostic.Create(_export, null, "Sum"), Throws.InstanceOf<FormatException>());

  /// <summary>A diagnostic of a stage that knows only its start has a range of one position; one without a file or a position has none.</summary>
  [TestCase("a.cs", 3, 7, true)]
  [TestCase(null, 3, 7, false)]
  [TestCase("a.cs", 0, 0, false)]
  [TestCase("a.cs", 3, 0, false)]
  public void Source_OfAPositionalDiagnostic(string? file, int line, int column, bool hasRange) {
    var source = new Diagnostic("PLY1002", DiagnosticSeverity.Error, "m", file, line, column).Source;
    Assert.That(source, hasRange ? Is.EqualTo(new SourceRange(file!, line, column, line, column)) : Is.Null);
  }

  [Test]
  public void ToString_NamesEverySeverity() {
    Assert.That(new Diagnostic("PLY0099", DiagnosticSeverity.Info, "m").ToString(), Is.EqualTo("info PLY0099: m"));
    Assert.That(new Diagnostic("PLY0009", DiagnosticSeverity.Warning, "m", "a.cs").ToString(), Is.EqualTo("a.cs: warning PLY0009: m"));
    Assert.That(new Diagnostic("PLY1002", DiagnosticSeverity.Error, "m", "a.cs", 3, 7).ToString(), Is.EqualTo("a.cs(3,7): error PLY1002: m"));
  }

}
