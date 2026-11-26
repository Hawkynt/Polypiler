using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Runners;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The steps of <c>tests/features/infra/infra-rust-failure-message.feature</c> (#1429): formatting a failed
/// Rust execution keeps every diagnostic and every generated file without throwing when a crate holds
/// several modules or none.
/// </summary>
[Binding, Scope(Feature = "Rust failures retain their diagnostics")]
public sealed class RustFailureMessageSteps {

  private IReadOnlyList<Diagnostic> _diagnostics = [];
  private IReadOnlyList<GeneratedFile> _files = [];
  private string? _formatted;
  private Exception? _thrown;

  [Given(@"^PLY1592 " + StepText.Quoted + " and files " + StepText.Quoted + ", " + StepText.Quoted + " and " + StepText.Quoted + "$")]
  public void DiagnosticAndThreeFiles(string message, string first, string second, string third) {
    this._diagnostics = [Diagnostic.Create(RustRunnerDiagnostics.Crashed, null, 1, message)];
    this._files = [
      new(first, "pub mod a;\npub mod b;\n"),
      new(second, "pub fn a() -> i32 { 1 }\n"),
      new(third, "pub fn b() -> i32 { 2 }\n")
    ];
  }

  [Given(@"^PLY1592 " + StepText.Quoted + " and no generated files$")]
  public void DiagnosticAndNoFiles(string message) {
    this._diagnostics = [Diagnostic.Create(RustRunnerDiagnostics.Crashed, null, 1, message)];
    this._files = [];
  }

  [When("^the Rust assertion failure message is formatted$")]
  public void Format() {
    try {
      this._formatted = RustRun.FailureMessage(this._diagnostics, this._files);
      this._thrown = null;
    } catch (Exception exception) {
      this._thrown = exception;
    }
  }

  [Then(@"^it contains PLY1592 and " + StepText.Quoted + "$")]
  public void ContainsDiagnostic(string message) {
    Assert.That(this._formatted, Is.Not.Null);
    Assert.That(this._formatted, Does.Contain("PLY1592").And.Contain(message));
  }

  [Then("^it contains every file path and content$")]
  public void ContainsEveryFile() {
    Assert.That(this._formatted, Is.Not.Null);
    foreach (var file in this._files)
      Assert.That(this._formatted, Does.Contain(file.Path).And.Contain(file.Content));
  }

  [Then("^no exception is thrown$")]
  public void NoException() => Assert.That(this._thrown, Is.Null);

}

/// <summary>
/// Fast formatter tests for <see cref="RustRun.FailureMessage"/> (#1429): every row of the test-design
/// table — multi-file crates, diagnostics with causes, file-count boundaries, manifest/root/child order,
/// and empty inputs.
/// </summary>
[TestFixture]
[Layer("rust")]
public sealed class RustFailureMessageTests {

  private static Diagnostic Crashed(string detail, string? cause = null)
    => Diagnostic.Create(RustRunnerDiagnostics.Crashed, null, 101, detail) with { Cause = cause };

  /// <summary>Test design 1 and 4: multi-file crate with manifest, root and child modules keeps diagnostic and file order.</summary>
  [Test]
  public void FailureMessage_RetainsDiagnosticAndEveryFileInOrder() {
    GeneratedFile[] files = [
      new("Cargo.toml", "[package]\nname = \"modgraph\"\n"),
      new("src/lib.rs", "pub mod a;\npub mod b;\n"),
      new("src/a.rs", "pub fn alpha() -> i32 { 1 }\n"),
      new("src/b.rs", "pub fn beta() -> i32 { 2 }\n")
    ];
    var message = RustRun.FailureMessage([Crashed("driver failed")], files);
    Assert.That(message, Does.Contain("PLY1592").And.Contain("driver failed"));
    var positions = files.Select(file => message.IndexOf(file.Path, StringComparison.Ordinal)).ToList();
    Assert.That(positions, Is.All.GreaterThanOrEqualTo(0));
    Assert.That(positions, Is.Ordered.Ascending);
    foreach (var file in files)
      Assert.That(message, Does.Contain(file.Content));
  }

  /// <summary>Test design 2: a diagnostic carrying a cause retains both diagnostic text and cause without throwing.</summary>
  [Test]
  public void FailureMessage_RetainsDiagnosticCause() {
    var message = RustRun.FailureMessage(
      [Crashed("toolchain missing", "error: toolchain '1.99.0-x86_64-pc-windows-msvc' is not installed")],
      [new("src/lib.rs", "pub mod m;\n"), new("src/m.rs", "pub fn f() {}\n")]);
    Assert.That(message, Does.Contain("PLY1592")
      .And.Contain("toolchain missing")
      .And.Contain("1.99.0-x86_64-pc-windows-msvc"));
  }

  /// <summary>Test design 3: zero, one, two and three files format cleanly with no sequence exception.</summary>
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  public void FailureMessage_HandlesEveryFileCount(int count) {
    var files = Enumerable.Range(0, count).Select(index => new GeneratedFile($"src/m{index}.rs", $"pub fn f{index}() {{}}\n")).ToList();
    var message = RustRun.FailureMessage([Crashed("driver failed")], files);
    Assert.That(message, Does.Contain("PLY1592").And.Contain("driver failed"));
    foreach (var file in files)
      Assert.That(message, Does.Contain(file.Path).And.Contain(file.Content));
  }

  /// <summary>Test design 5: no diagnostics, empty file content, and empty file list all format cleanly; null inputs throw.</summary>
  [Test]
  public void FailureMessage_HandlesEmptyInputsAndRejectsNull() {
    Assert.That(RustRun.FailureMessage([], []), Is.EqualTo(string.Empty));
    Assert.That(RustRun.FailureMessage([], [new("src/empty.rs", "")]), Is.EqualTo("--- src/empty.rs ---\n"));
    Assert.That(RustRun.FailureMessage([Crashed("failed")], []), Does.Contain("PLY1592").And.Contain("failed"));
    Assert.Throws<ArgumentNullException>(() => RustRun.FailureMessage(null!, []));
    Assert.Throws<ArgumentNullException>(() => RustRun.FailureMessage([], null!));
  }

}
