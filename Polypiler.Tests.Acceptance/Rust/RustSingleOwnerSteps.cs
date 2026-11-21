using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The steps of <c>tests/features/lang/rs/be-rs-single-owner.feature</c> (#1393) and <c>be-rs-enum-globals.feature</c> (#1424): C# classes through the
/// frontend and the Rust backend, the module's text, the crate under clippy and rustfmt, calls the
/// generated driver makes compared with the table and the IR interpreter, and the Rust fixtures that
/// come back through the backend compared with what rustc recorded.
/// </summary>
[Binding, Scope(Feature = "Rust backend: single-owner classes as plain structs"), Scope(Feature = "Rust backend: enum globals and nullable objects converted to their base")]
public sealed class RustSingleOwnerSteps {

  private static readonly string[] _fixtures = ["types", "edges", "reassign"];

  private string _source = "";
  private IrModule? _module;
  private string _text = "";
  private IReadOnlyList<GeneratedFile> _files = [];

  [Given("^the C# source \"(.*)\"$")]
  public void TheSource(string source) => this._source = source;

  [When("^it is translated to Rust$")]
  public void Translated() {
    this._module = PrototypeFixture.Import("Snippet.cs", this._source);
    this._files = RustCrates.Emit(this._module);
    this._text = RustCrates.ModuleText(this._files);
  }

  [Then("^the module contains \"(.*)\"$")]
  public void Contains(string text) => Assert.That(this._text, Does.Contain(text));

  [Then("^the module contains no \"(.*)\"$")]
  public void ContainsNo(string text) => Assert.That(this._text, Does.Not.Contain(text));

  [Then("^the class C is (plain|shared)$")]
  public void Form(string form) {
    Assert.That(this._text.Contains("Rc<C>", StringComparison.Ordinal), Is.EqualTo(form == "shared"));
    Assert.That(this._text, Does.Contain("pub struct C"));
  }

  [Then("^the crate is clean under cargo fmt --check and cargo clippy -D warnings$")]
  public async Task Clean() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(this._files);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
  }

  [Then("^with overflow checks panic and wrap the calls give$")]
  public async Task CallsGive(DataTable table) {
    var module = this._module!;
    var rows = table.Rows.Select(row => (Function: module.Functions.Single(function => function.Name == row["call"]), Arguments: row["arguments"], Result: row["result"])).ToList();
    var calls = rows.Select(row => new PrototypeCall(row.Function.Name, [.. row.Arguments.Split(", ").Select((argument, index) => RustCases.Value(row.Function.Parameters[index].Type, Int128.Parse(argument, CultureInfo.InvariantCulture)))])).ToList();
    var expected = rows.Select(row => row.Result).ToList();
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(expected), "the IR interpreter");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(expected), $"Rust with overflow checks {checks}");
  }

  [Given("^the Rust fixtures types, edges and reassign$")]
  public static void TheFixtures() => Assert.That(RustFixture.BackendPending, Has.None.AnyOf(_fixtures));

  [When("^each is imported with overflow checks on and off and emitted back to Rust$")]
  public static void Emitted() {
    foreach (var fixture in _fixtures)
      foreach (var mode in RustFixture.Modes)
        Assert.That(RustCrates.Check(RustFixture.Import(fixture, mode)), Is.Empty, $"{fixture} ({RustFixture.NameOf(mode)})");
  }

  [Then("^the crate builds with overflow checks on and off and every call gives what rustc recorded$")]
  public static async Task GivesWhatRustcRecorded() {
    foreach (var fixture in _fixtures) {
      foreach (var mode in RustFixture.Modes) {
        var module = RustFixture.Import(fixture, mode);
        var calls = RustCases.For(fixture, module);
        var recorded = RustFixture.RecordedLines(fixture, mode).Select(line => line[(line.IndexOf(" => ", StringComparison.Ordinal) + 4)..]).ToList();
        foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
          Assert.That((await RustRun.OutcomesAsync(module, calls, checks)).Select(RustFixture.Normalized), Is.EqualTo(recorded), $"{fixture} ({RustFixture.NameOf(mode)}), crate built with overflow checks {checks}");
      }
    }
  }

}
