using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The steps of <c>tests/features/lang/rs/be-rs-result-helpers.feature</c> (#1415): C# methods through the
/// frontend and the Rust backend under the trap policy <c>result</c>, the module's text, the refusals,
/// and calls the generated driver makes with overflow checks on and off, compared with the table and the
/// IR interpreter; a trap in the table is <c>trap Name</c>, the error the export returns.
/// </summary>
[Binding, Scope(Feature = "Rust backend: helpers that return their traps under the result trap policy")]
public sealed class RustResultHelperSteps {

  private static readonly RustProfile Result = RustProfile.Default.WithTrapPolicy(RustTrapPolicy.Result);

  private string _source = "";
  private IrModule? _module;

  [Given("^the C# source \"(.*)\"$")]
  public void TheSource(string source) => this._source = source.Replace("\\\"", "\"", StringComparison.Ordinal);

  [When("^it is translated to Rust under the trap policy result$")]
  public void Translated() => this._module = RustCrates.Import(this._source);

  [Then("^the module contains \"(.*)\"$")]
  public void Contains(string text)
    => Assert.That(RustCrates.ModuleText(RustCrates.Emit(this._module!, Result)), Does.Contain(text.Replace("\\\"", "\"", StringComparison.Ordinal)));

  [Then("^it is refused with PLY1552 naming \"(.*)\"$")]
  public void Refused(string construct)
    => Assert.That(RustCrates.Check(this._module!, Result).Where(problem => problem.Code == RustEmitter.TrapPolicyLimit).Select(problem => problem.Message), Has.Some.Contains($"'{construct}'"));

  [Then("^with overflow checks panic and wrap the calls give$")]
  public async Task CallsGive(DataTable table) {
    var module = this._module!;
    var calls = table.Rows.Select(row => new PrototypeCall(row["call"], [int.Parse(row["arguments"], CultureInfo.InvariantCulture)])).ToList();
    var expected = table.Rows.Select(row => row["result"] is var result && result.StartsWith("trap ", StringComparison.Ordinal)
      ? PrototypeFixture.Trap(Enum.Parse<IrTrap>(result[5..])) : result).ToList();
    Assert.That(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)), Is.EqualTo(expected), "the IR interpreter");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks, Result), Is.EqualTo(expected), $"Rust with overflow checks {checks}");

    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(RustCrates.Emit(module, Result));
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
  }

}
