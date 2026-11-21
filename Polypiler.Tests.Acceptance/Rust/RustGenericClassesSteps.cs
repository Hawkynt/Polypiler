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
/// The steps of <c>tests/features/lang/rs/be-rs-generic-classes.feature</c> (#1421): a C# source with a
/// generic class through the frontend and the Rust backend, the module's text, the crate under clippy
/// and rustfmt, and calls of the static class the generated driver makes, compared with the C#
/// compiler's build of the same source.
/// </summary>
[Binding, Scope(Feature = "Rust backend: generic classes as Rust generic structs")]
public sealed class RustGenericClassesSteps {

  private string _source = "";
  private IrModule? _module;
  private string _text = "";
  private IReadOnlyList<GeneratedFile> _files = [];

  [Given("^the C# source \"(.*)\"$")]
  public void TheSource(string source) => this._source = source.Replace("\\\"", "\"", StringComparison.Ordinal);

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

  [Then("^the crate is clean under cargo fmt --check and cargo clippy -D warnings$")]
  public async Task Clean() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    var package = RustCrates.Package(this._files);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
  }

  [Then("^with overflow checks panic and wrap the crate gives what .NET gives for$")]
  public async Task GivesWhatDotNetGives(DataTable table) {
    var module = this._module!;
    var built = GeneratedCSharp.CompileSource(this._source);
    var rows = table.Rows.Select(row => (Function: module.Functions.Single(function => function.Name == row["call"]), Arguments: row["arguments"], Result: row["result"])).ToList();
    var calls = rows.Select(row => new PrototypeCall(row.Function.Name, [.. row.Arguments.Split(", ").Select((argument, index) => Value(row.Function.Parameters[index].Type, argument))])).ToList();
    var dotNet = rows.Zip(calls).Select(pair => PrototypeFixture.InvokedOutcome(built.Method(pair.First.Function.Name), pair.Second)).ToList();
    Assert.That(dotNet, Is.EqualTo(rows.Select(row => PrototypeFixture.Show(Value(row.Function.ReturnType, row.Result)))), ".NET gives the results of the table");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(dotNet), $"Rust with overflow checks {checks}");
  }

  /// <summary>A value of the table as the .NET value of an IR scalar.</summary>
  private static object Value(IrType type, string text) => type.IsFloat ? double.Parse(text, CultureInfo.InvariantCulture)
    : type == IrType.Bool ? bool.Parse(text)
    : RustCases.Value(type, Int128.Parse(text, CultureInfo.InvariantCulture));

}
