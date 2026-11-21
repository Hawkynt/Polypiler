using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The steps of <c>tests/features/lang/rs/be-rs-exception-regions.feature</c> (#1414): the exception
/// fixtures and C# sources through the frontend and the Rust backend under a trap policy, the crate
/// under rustfmt and clippy, the calls of the generated driver compared with the table and with what
/// .NET computes of the fixture, and the regions that stay refused.
/// </summary>
[Binding, Scope(Feature = "Rust backend: exception regions with finally blocks")]
public sealed class RustExceptionRegionSteps {

  /// <summary>Held while the .NET build of a fixture runs, whose static state the scenarios share.</summary>
  private static readonly System.Threading.Lock _dotNet = new();

  private IrModule? _module;
  private string? _className;
  private RustProfile _profile = RustProfile.Default;
  private IReadOnlyList<Diagnostic> _problems = [];
  private IReadOnlyList<GeneratedFile> _files = [];

  [Given("^the exception fixture \"(.*)\"$")]
  public void TheFixture(string file) {
    this._module = ExceptionFixtures.Imported(file);
    this._className = file switch { "order" => "OrderDemo", "Regions" => "RegionsDemo", _ => ExceptionFixtures.CSharp.First(fixture => fixture.File == file).Class };
  }

  [Given("^the C# source \"(.*)\"$")]
  public void TheSource(string source) => this._module = PrototypeFixture.Import("Snippet.cs", source);

  [When("^it is translated to Rust under the trap policy (panic|result)$")]
  public void Translated(string policy) {
    this._profile = RustProfile.Default.WithTrapPolicy(policy == "result" ? RustTrapPolicy.Result : RustTrapPolicy.Panic);
    this._problems = RustCrates.Check(this._module!, this._profile);
    if (this._problems.Count == 0)
      this._files = RustCrates.Emit(this._module!, this._profile);
  }

  [Then("^nothing is refused$")]
  public void NothingRefused() => Assert.That(this._problems, Is.Empty);

  [Then("^it is refused with (PLY\\d{4}) naming \"(.*)\"$")]
  public void Refused(string code, string construct)
    => Assert.That(this._problems.Select(problem => $"{problem.Code} {problem.Message}"), Has.Some.StartsWith(code).And.Contain($"'{construct}'"));

  [Then("^the crate is clean under cargo fmt --check and cargo clippy -D warnings$")]
  public async Task Clean() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(this._files);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
  }

  [Then("^with overflow checks panic and wrap the calls give what \\.NET gives$")]
  public async Task CallsGive(DataTable table) {
    var module = this._module!;
    var calls = table.Rows.Select(row => {
      var method = ExceptionFixtures.Compiled(this._className!, row["call"]);
      object[] arguments = row["arguments"].Length == 0 ? []
        : [method.GetParameters()[0].ParameterType == typeof(bool) ? bool.Parse(row["arguments"]) : int.Parse(row["arguments"], CultureInfo.InvariantCulture)];
      return new PrototypeCall(row["call"], arguments) { Returns = method.ReturnType };
    }).ToList();
    var expected = table.Rows.Select(row => row["result"]).ToList();
    // The fixtures keep a log in a static field of this process: the scenarios of both policies run in
    // parallel, so the .NET calls take turns.
    List<string> dotNet;
    lock (_dotNet)
      dotNet = [.. calls.Select(call => PrototypeFixture.InvokedOutcome(ExceptionFixtures.Compiled(this._className!, call.Method), call))];
    Assert.That(dotNet, Is.EqualTo(expected), ".NET");
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(expected), "the IR interpreter");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks, this._profile), Is.EqualTo(expected), $"Rust with overflow checks {checks}");
  }

}
