using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
/// The steps of <c>tests/features/lang/rs/be-rs-generics.feature</c> (#1394): C# methods through the
/// frontend and the Rust backend, the module's text, the crate under clippy and rustfmt, and calls the
/// generated driver makes - of an instantiation, which calls the generic function, or of a method -
/// compared with the C# compiler's build of the same methods, an instantiation called through
/// reflection with its type arguments.
/// </summary>
[Binding, Scope(Feature = "Rust backend: generic methods as Rust generics")]
public sealed class RustGenericsSteps {

  private string _methods = "";
  private IrModule? _module;
  private string _text = "";
  private IReadOnlyList<GeneratedFile> _files = [];

  [Given("^the C# methods \"(.*)\"$")]
  public void TheMethods(string methods) => this._methods = methods;

  [When("^they are translated to Rust$")]
  public void Translated() {
    this._module = RustCrates.Import(this._methods);
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
    var built = GeneratedCSharp.CompileSource(PrototypeFixture.Wrap(this._methods));
    var rows = table.Rows.Select(row => (Function: module.Functions.Single(function => function.Name == row["call"]), Arguments: row["arguments"], Result: row["result"])).ToList();
    var calls = rows.Select(row => new PrototypeCall(row.Function.Name, [.. row.Arguments.Split(", ").Select((argument, index) => Value(row.Function.Parameters[index].Type, argument))])).ToList();
    var dotNet = rows.Zip(calls).Select(pair => PrototypeFixture.InvokedOutcome(Method(built, pair.First.Function), pair.Second)).ToList();
    Assert.That(dotNet, Is.EqualTo(rows.Select(row => PrototypeFixture.Show(Value(row.Function.ReturnType, row.Result)))), ".NET gives the results of the table");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That(await RustRun.OutcomesAsync(module, calls, checks), Is.EqualTo(dotNet), $"Rust with overflow checks {checks}");
  }

  /// <summary>The method of the build a function of the module is: an instantiation is the generic method with its type arguments.</summary>
  private static MethodInfo Method(GeneratedCSharp built, IrFunction function) {
    var type = built.Type(built.ClassName);
    var method = type.GetMethod(function.Origin?.Name ?? function.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    return function.Origin is { } origin ? method.MakeGenericMethod([.. origin.TypeArguments.Select(DotNetType)]) : method;
  }

  private static Type DotNetType(IrType type) => type.Kind switch {
    IrTypeKind.U8 => typeof(byte),
    IrTypeKind.I32 => typeof(int),
    IrTypeKind.I64 => typeof(long),
    _ => typeof(double)
  };

  /// <summary>A value of the table as the .NET value of an IR scalar.</summary>
  private static object Value(IrType type, string text) => type.IsFloat
    ? double.Parse(text, CultureInfo.InvariantCulture)
    : RustCases.Value(type, Int128.Parse(text, CultureInfo.InvariantCulture));

}
