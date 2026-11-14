using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;

namespace Polypiler.Tests.Diagnostics;

/// <summary>The exit-code rule of ADR 0003: the first error decides, an internal compiler error overrides, warnings and infos count for nothing.</summary>
[TestFixture]
public sealed class DiagnosticBagTests {

  private static Diagnostic Error(string code, ExitCode exitCode) => Diagnostic.Create(new(code, DiagnosticSeverity.Error, exitCode, "m", "r"), null);

  private static Diagnostic Warning(string code) => Diagnostic.Create(new(code, DiagnosticSeverity.Warning, null, "m", "r"), null);

  private static Diagnostic Info(string code) => Diagnostic.Create(new(code, DiagnosticSeverity.Info, null, "m", "r"), null);

  private static Diagnostic Internal() => Diagnostic.Create(DiagnosticDescriptor.InternalError, null, "boom");

  /// <summary>Test design 7: an empty bag succeeds and counts nothing.</summary>
  [Test]
  public void EmptyBag_Succeeds() {
    var bag = new DiagnosticBag();
    Assert.That((bag.ExitCode, bag.ErrorCount, bag.WarningCount, bag.Items.Count), Is.EqualTo((ExitCode.Success, 0, 0, 0)));
  }

  [Test]
  public void WarningsAndInfos_NeverChangeTheExitCode() {
    var bag = new DiagnosticBag();
    bag.Report(Warning("PLY0009"));
    bag.Report(Info("PLY0099"));
    Assert.That((bag.ExitCode, bag.ErrorCount, bag.WarningCount), Is.EqualTo((ExitCode.Success, 0, 1)));
  }

  /// <summary>Test design 6: every member of <see cref="ExitCode"/> as the first error is the exit code.</summary>
  [TestCaseSource(nameof(ErrorExitCodes))]
  public void FirstError_DecidesTheExitCode(ExitCode exitCode) {
    var bag = new DiagnosticBag();
    bag.Report(Warning("PLY0009"));
    bag.Report(Error("PLY0098", exitCode));
    bag.Report(Error("PLY0051", ExitCode.SemanticMismatch));
    Assert.That((bag.ExitCode, bag.ErrorCount), Is.EqualTo((exitCode, 2)));
  }

  private static ExitCode[] ErrorExitCodes() => [.. Enum.GetValues<ExitCode>().Where(member => member != ExitCode.Success)];

  /// <summary>The first error decides, not the highest number.</summary>
  [Test]
  public void TheFirstError_NotTheHighestNumber() {
    var bag = new DiagnosticBag();
    bag.Report(Error("PLY0051", ExitCode.SemanticMismatch));
    bag.Report(Error("PLY0030", ExitCode.TargetBuildFailure));
    Assert.That(bag.ExitCode, Is.EqualTo(ExitCode.SemanticMismatch));
  }

  /// <summary>Test design 18: PLY0001 anywhere in the bag makes the exit code 70.</summary>
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  public void InternalCompilerError_AnywhereIs70(int position) {
    var reported = new[] { Error("PLY0030", ExitCode.TargetBuildFailure), Error("PLY0051", ExitCode.SemanticMismatch) }.ToList();
    reported.Insert(position, Internal());
    var bag = new DiagnosticBag();
    foreach (var diagnostic in reported)
      bag.Report(diagnostic);
    Assert.That((bag.ExitCode, bag.ErrorCount), Is.EqualTo((ExitCode.InternalError, 3)));
  }

  [Test]
  public void AnErrorWithoutExitCode_CountsAsInternalError() {
    var bag = new DiagnosticBag();
    bag.Report(new Diagnostic("PLY1002", DiagnosticSeverity.Error, "m"));
    Assert.That(bag.ExitCode, Is.EqualTo(ExitCode.InternalError));
  }

  [Test]
  public void Items_KeepEmissionOrder() {
    var (first, second, third) = (Warning("PLY0009"), Error("PLY0030", ExitCode.TargetBuildFailure), Info("PLY0099"));
    var bag = new DiagnosticBag();
    IDiagnosticSink sink = bag;
    sink.Report(first);
    sink.Report(second);
    sink.Report(third);
    Assert.That(bag.Items, Is.EqualTo(new[] { first, second, third }));
  }

  [Test]
  public void Report_WithoutDiagnostic_IsRefused()
    => Assert.That(() => new DiagnosticBag().Report(null!), Throws.ArgumentNullException);

}
