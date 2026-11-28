using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Cli;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Python;
using Polypiler.Tests.Frontends.VisualBasic;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>
/// The fixtures of #1564 to #1567, each case a function of the module, translated to JavaScript and
/// Python: node and CPython print what the IR interpreter prints - VBA's text of numbers in the
/// locale of the import, its reading of strings, Currency and Date, bitwise Variants and Variant
/// arrays. JavaScript has no currency (a 64-bit payload), so <c>CurrencyDate.bas</c> runs in Python alone.
/// </summary>
[TestFixture, Layer("core")]
[Category("EndToEnd")]
public sealed class VisualBasicCaseTranslationTests {

  private string _directory = "";

  [SetUp]
  public void SetUp() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void TearDown() => Directory.Delete(this._directory, true);

  private static (IrModule Module, PrototypeCall[] Calls) Driven(string fixture, string cases, string locale) {
    VisualBasicLocale.TryParse(locale, out var parsed);
    var module = ClassicCases.Import(fixture, cases, parsed);
    return (module, [.. Enumerable.Range(0, ClassicCases.Lines(cases).Count).Select(index => new PrototypeCall(ClassicCases.Name(index), []))]);
  }

  [TestCase("Locale.bas", "Locale.cases.txt", "en-US")]
  [TestCase("Locale.bas", "Locale.de-DE.cases.txt", "de-DE")]
  [TestCase("Bitwise.bas", "Bitwise.cases.txt", "en-US")]
  [TestCase("Inference.bas", "Inference.cases.txt", "en-US")]
  [RequiresToolchain("node")]
  public void JavaScript_ComputesWhatTheInterpreterComputes(string fixture, string cases, string locale) {
    var (module, calls) = Driven(fixture, cases, locale);
    var legalized = JavaScriptEmitter.Legalize(module, () => PrecomputedImplementations.Module, JavaScriptProfile.Default, FloatMode.Accurate);
    var file = JavaScriptEmitter.Emit(legalized, JavaScriptProfile.Default).Single();
    File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);
    Assert.That(Toolchain.NodeOutcomes(this._directory, file.Path, calls), Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), file.Content);
  }

  internal static (IrModule Module, PrototypeCall[] Calls) Cases(string fixture, string cases, string locale) => Driven(fixture, cases, locale);

}

/// <summary>The fixtures of #1564 to #1567 translated to Python, whose CPython prints what the interpreter prints.</summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class VisualBasicCasePythonTests {

  [TestCase("Locale.bas", "Locale.cases.txt", "en-US")]
  [TestCase("Locale.bas", "Locale.de-DE.cases.txt", "de-DE")]
  [TestCase("CurrencyDate.bas", "CurrencyDate.cases.txt", "en-US")]
  [TestCase("Bitwise.bas", "Bitwise.cases.txt", "en-US")]
  [TestCase("Inference.bas", "Inference.cases.txt", "en-US")]
  [RequiresToolchain("python")]
  public async Task Python_ComputesWhatTheInterpreterComputes(string fixture, string cases, string locale) {
    var (module, calls) = VisualBasicCaseTranslationTests.Cases(fixture, cases, locale);
    var translated = PythonFixtures.Translate(module);
    Assert.That(translated.Problems, Is.Empty);
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = translated.Names![call.Method] })]);
    Assert.That(outcomes, Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), translated.Source);
  }

}
