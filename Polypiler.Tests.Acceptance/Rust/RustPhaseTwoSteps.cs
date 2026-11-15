using System;
using System.Collections.Generic;
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
/// The steps of <c>tests/features/lang/rs/fe-rs-memory.feature</c>, <c>fe-rs-types.feature</c> and
/// <c>fe-rs-functions.feature</c>: a Rust source is imported in an overflow mode, a function of it
/// is interpreted, and the same call compiled and run by <c>rustc</c> is the expectation; slices are
/// written <c>[1 2 3]</c> or <c>bytes "text"</c>, and their contents after the call are compared too.
/// </summary>
[Binding, Scope(Feature = "Rust frontend: arrays, slices, Vec, borrows and slice iterators")]
[Scope(Feature = "Rust frontend: structs, enums, Option, Result and the ? operator")]
[Scope(Feature = "Rust frontend: functions, modules, closures and iterator closures")]
[Scope(Feature = "Rust frontend: String, &str and char")]
public sealed class RustPhaseTwoSteps {

  private string _source = "";
  private OverflowChecks _mode = OverflowChecks.Panic;
  private bool? _debugAssertions;
  private ImportResult? _result;
  private PrototypeCall? _call;
  private string _outcome = "";

  private IrModule Module => this._result?.Module ?? throw new AssertionException(string.Join(Environment.NewLine, this._result!.Diagnostics));

  [Given("^the Rust source \"(.*)\"$")]
  public void TheSource(string source) => this._source = source.Replace("\\\"", "\"", StringComparison.Ordinal);

  [When("^it is imported with overflowChecks (panic|wrap)$")]
  public void Imported(string mode) {
    this._mode = mode == "panic" ? OverflowChecks.Panic : OverflowChecks.Wrap;
    this._result = RustFrontend.Import("snippet.rs", this._source, new RustImportOptions(this._mode) { DebugAssertions = this._debugAssertions });
  }

  [When("^it is imported with overflowChecks (panic|wrap) and debugAssertions (on|off)$")]
  public void ImportedWithDebugAssertions(string mode, string debug) {
    this._debugAssertions = debug == "on";
    this.Imported(mode);
  }

  [When("^\"(\\w+)\" is called with ?(.*)$")]
  public void Called(string function, string arguments) {
    Assert.That(IrVerifier.Verify(this.Module), Is.Empty);
    var parameters = this.Module.Functions.Single(candidate => candidate.Name == function).Parameters;
    this._call = new PrototypeCall(function, [.. Split(arguments).Select((argument, index) => Value(parameters[index].Type, argument))]);
    this._outcome = RustFixture.Interpreted(this.Module, this._call);
  }

  [Then("^the result is (.+)$")]
  public void TheResultIs(string expected) => Assert.That(this._outcome.Split(" | ")[0], Is.EqualTo(Shown(expected)));

  [Then("^the slices after the call are (.+)$")]
  public void TheSlicesAre(string expected)
    => Assert.That(string.Join(" | ", this._outcome.Split(" | ").Skip(1)), Is.EqualTo(string.Join(" | ", expected.Split(" and ").Select(list => Shown(list.Trim())))));

  [Then("^the interpreter reports the panic (overflow|divide by zero|panic)$")]
  public void ThePanicIs(string kind) => Assert.That(this._outcome, Is.EqualTo(Panic(kind)));

  [Then("^rustc does the same$")]
  public void RustcAgrees() {
    var directory = Directory.CreateTempSubdirectory("polypiler-rust-step-").FullName;
    try {
      var source = Path.Combine(directory, "snippet.rs");
      File.WriteAllText(source, this._source);
      Assert.That(RustOracle.Outcomes(Path.Combine(directory, "driver"), source, this.Module, [this._call!], this._mode).Single(), Is.EqualTo(this._outcome));
    } finally {
      Directory.Delete(directory, true);
    }
  }

  [Then(@"^the import fails with diagnostic (PLY\d+)$")]
  public void Fails(string code) => Assert.That(this._result!.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code));

  [Then(@"^the import fails with diagnostic (PLY\d+) at line (\d+)$")]
  public void FailsOnLine(string code, int line)
    => Assert.That(this._result!.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Does.Contain((code, line)));

  [Then(@"^the import fails with diagnostic (PLY\d+) at line (\d+), column (\d+)$")]
  public void FailsAt(string code, int line, int column)
    => Assert.That(this._result!.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line, diagnostic.Column)), Does.Contain((code, line, column)));

  [Then("^the message names \"(.*)\"$")]
  public void MessageNames(string text) => Assert.That(this._result!.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(text));

  [Then("^no IR is produced$")]
  public void NoModule() => Assert.That(this._result!.Module, Is.Null);

  [Then("^the parameters of \"(\\w+)\" are (.+)$")]
  public void ParametersAre(string function, string kinds)
    => Assert.That(this.Module.Functions.Single(candidate => candidate.Name == function).Parameters.Select(parameter => parameter.Type.Kind.ToString()), Is.EqualTo(kinds.Split(", ")));

  [Then("^\"(\\w+)\" returns its result and its receiver$")]
  public void ReturnsTheReceiver(string function)
    => Assert.That(this.Module.Functions.Single(candidate => candidate.Name == function).ReturnType.StructType!.Fields.Select(field => field.Name), Is.EqualTo(new[] { "result", "receiver" }));

  // ---------------------------------------------------------------- values

  /// <summary>The arguments of a step: split at the commas outside brackets and quotes.</summary>
  private static List<string> Split(string text) {
    var parts = new List<string>();
    var (depth, quoted, start) = (0, false, 0);
    for (var index = 0; index < text.Length; ++index) {
      var character = text[index];
      if (character == '"')
        quoted = !quoted;
      else if (!quoted && character == '[')
        ++depth;
      else if (!quoted && character == ']')
        --depth;
      else if (!quoted && depth == 0 && character == ',') {
        parts.Add(text[start..index].Trim());
        start = index + 1;
      }
    }

    if (text.Trim().Length > 0)
      parts.Add(text[start..].Trim());

    return parts;
  }

  private static object Value(IrType type, string text) {
    if (type.IsView) {
      var element = type.Element!;
      var items = text.StartsWith("bytes \"", StringComparison.Ordinal)
        ? text["bytes \"".Length..^1].Select(character => ((int)character).ToString(CultureInfo.InvariantCulture)).ToList()
        : text.Trim('[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
      var array = System.Array.CreateInstance(Value(element, "0").GetType(), items.Count);
      for (var index = 0; index < items.Count; ++index)
        array.SetValue(Value(element, items[index]), index);

      return array;
    }

    if (type == IrType.Bool)
      return text == "true";

    if (type.IsFloat) {
      var number = double.Parse(text, CultureInfo.InvariantCulture);
      return type == IrType.F32 ? (object)(float)number : number;
    }

    return RustCases.Value(type, Int128.Parse(Number(text), CultureInfo.InvariantCulture));
  }

  /// <summary>An expected value as an outcome shows it: hexadecimal in decimal, a list with commas.</summary>
  private static string Shown(string text) {
    if (text.StartsWith('[') || text.StartsWith("bytes \"", StringComparison.Ordinal)) {
      var items = text.StartsWith("bytes \"", StringComparison.Ordinal)
        ? text["bytes \"".Length..^1].Select(character => ((int)character).ToString(CultureInfo.InvariantCulture))
        : text.Trim('[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Number);
      return $"[{string.Join(",", items)}]";
    }

    if (text.StartsWith('('))
      return $"{{{string.Join(",", text.Trim('(', ')').Split(", ").Select(Number))}}}";

    return Number(text);
  }

  private static string Number(string text)
    => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : text;

  private static string Panic(string kind) => kind switch {
    "overflow" => RustFixture.Overflow,
    "divide by zero" => RustFixture.DivideByZero,
    _ => RustFixture.Panic
  };

}
