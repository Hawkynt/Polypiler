using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The steps of <c>tests/features/lang/rs/fe-rs-scalars.feature</c> and <c>fe-rs-control.feature</c>:
/// a Rust function is imported in an overflow mode and interpreted, and the same call is compiled
/// and run by <c>rustc</c>, whose outcome is the expectation.
/// </summary>
[Binding, Scope(Feature = "Rust frontend: numeric literals, arithmetic, overflow methods and casts"), Scope(Feature = "Rust frontend: branches, match, loops and evaluation order")]
public sealed class RustFrontendSteps {

  private string _source = "";
  private OverflowChecks? _mode;
  private ImportResult? _result;
  private PrototypeCall? _call;
  private string _outcome = "";

  private IrModule Module => this._result?.Module ?? throw new AssertionException(string.Join(Environment.NewLine, this._result!.Diagnostics));

  [Given("^the Rust function \"(.*)\"$")]
  public void TheFunction(string source) => this._source = source;

  [When("^it is imported with overflowChecks (panic|wrap)$")]
  public void Imported(string mode) {
    this._mode = mode == "panic" ? OverflowChecks.Panic : OverflowChecks.Wrap;
    this._result = RustFrontend.Import("snippet.rs", this._source, this._mode);
  }

  [When("^it is imported without an overflow mode$")]
  public void ImportedWithoutMode() => this._result = RustFrontend.Import("snippet.rs", this._source, null);

  [When("^it is imported with overflowChecks (panic|wrap) and interpreted with ?(.*)$")]
  public void ImportedAndInterpreted(string mode, string arguments) {
    this.Imported(mode);
    Assert.That(IrVerifier.Verify(this.Module), Is.Empty);
    this._outcome = this.Interpret(arguments);
  }

  [Then("^the result is (.+)$")]
  public void TheResultIs(string expected) => Assert.That(this._outcome, Is.EqualTo(Number(expected)));

  [Then("^the interpreter reports the panic (overflow|divide by zero|panic)$")]
  public void ThePanicIs(string kind) => Assert.That(this._outcome, Is.EqualTo(Panic(kind)));

  [Then("^the same call compiled and run by rustc with the same overflow mode returns (.+)$")]
  public void RustcReturns(string expected) => Assert.That(this.Rustc(), Is.EqualTo(Number(expected)));

  [Then("^the same call compiled and run by rustc with the same overflow mode panics with (overflow|divide by zero|panic)$")]
  public void RustcPanics(string kind) => Assert.That(this.Rustc(), Is.EqualTo(Panic(kind)));

  [Then(@"^the IR contains the operator (\w+)$")]
  public void ContainsOperator(string op) => Assert.That(Operators(this.Module), Does.Contain(Enum.Parse<IrBinaryOp>(op)));

  [Then(@"^imported with overflowChecks wrap the IR contains the operator (\w+) and the result is (.+)$")]
  public void WrapContains(string op, string expected) {
    var module = RustFrontend.Import("snippet.rs", this._source, OverflowChecks.Wrap).Module!;
    Assert.That(Operators(module), Does.Contain(Enum.Parse<IrBinaryOp>(op)));
    Assert.That(RustFixture.Interpreted(module, this.Call(module, this._call!.Arguments)), Is.EqualTo(Number(expected)));
  }

  [Then("^interpreting with (.*) reports the panic (overflow|divide by zero|panic)$")]
  public void InterpretingPanics(string arguments, string kind) => Assert.That(this.Interpret(arguments), Is.EqualTo(Panic(kind)));

  [Then("^interpreting with (.*) returns (.+)$")]
  public void InterpretingReturns(string arguments, string expected) => Assert.That(this.Interpret(arguments), Is.EqualTo(Number(expected)));

  [Then("^the IR converts no value to u8 with a trapping contract$")]
  public void NoTrappingConversion()
    => Assert.That(IrTree.Nodes(this.Module.Functions[0].Body).OfType<IrConvert>().Where(convert => convert.Type == IrType.U8 && convert.Contract == IrConversion.Trap), Is.Empty);

  [Then(@"^the import fails with diagnostic (PLY\d+)$")]
  public void Fails(string code) => Assert.That(this._result!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code));

  [Then(@"^the import fails with diagnostic (PLY\d+) at line (\d+), column (\d+)$")]
  public void FailsAt(string code, int line, int column)
    => Assert.That(this._result!.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line, diagnostic.Column)), Does.Contain((code, line, column)));

  [Then(@"^the import fails with diagnostic (PLY\d+) at line (\d+)$")]
  public void FailsOnLine(string code, int line)
    => Assert.That(this._result!.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Does.Contain((code, line)));

  [Then("^the import fails with diagnostic (PLY\\d+) carrying \"(.*)\"$")]
  public void FailsCarrying(string code, string text)
    => Assert.That(this._result!.Diagnostics.Where(diagnostic => diagnostic.Code == code).Select(diagnostic => diagnostic.Message), Has.Some.Contains(text));

  [Then("^no IR function is produced$")]
  public void NoModule() => Assert.That(this._result!.Module, Is.Null);

  private string Interpret(string arguments) {
    this._call = this.Call(this.Module, arguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return RustFixture.Interpreted(this.Module, this._call);
  }

  /// <summary>A call of <c>f</c> with the arguments as the values of its parameter types.</summary>
  private PrototypeCall Call(IrModule module, object[] arguments) {
    var parameters = module.Functions.Single(function => function.Name == "f").Parameters;
    return new PrototypeCall("f", [.. arguments.Select((argument, index) => argument is string text ? Value(parameters[index].Type, text) : argument)]);
  }

  private static object Value(IrType type, string text) {
    if (type == IrType.Bool)
      return text == "true";

    if (type.IsFloat) {
      var number = double.Parse(text, CultureInfo.InvariantCulture);
      return type == IrType.F32 ? (object)(float)number : number;
    }

    return RustCases.Value(type, Int128.Parse(Number(text), CultureInfo.InvariantCulture));
  }

  /// <summary>A number of a feature file in decimal: a hexadecimal one is converted.</summary>
  private static string Number(string text)
    => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : text;

  private static string Panic(string kind) => kind switch {
    "overflow" => RustFixture.Overflow,
    "divide by zero" => RustFixture.DivideByZero,
    _ => RustFixture.Panic
  };

  private string Rustc() {
    var directory = Directory.CreateTempSubdirectory("polypiler-rust-step-").FullName;
    try {
      var source = Path.Combine(directory, "snippet.rs");
      File.WriteAllText(source, this._source);
      return RustOracle.Outcomes(Path.Combine(directory, "driver"), source, this.Module, [this._call!], this._mode!.Value).Single();
    } finally {
      Directory.Delete(directory, true);
    }
  }

  private static IrBinaryOp[] Operators(IrModule module)
    => [.. module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrBinary>().Select(binary => binary.Op)];

}
