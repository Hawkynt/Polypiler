using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Cli;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Python;
using Polypiler.Tests.Frontends.VisualBasic;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>
/// The Visual Basic .NET fixtures of <c>tests/fixtures/visualbasic/vbnet</c> imported by Roslyn and
/// translated to JavaScript: node prints, for every call, what the IR interpreter computes from the
/// import - the overflow, division and bound traps, GoTo, On Error and Err included.
/// </summary>
[TestFixture, Layer("core")]
[Category("EndToEnd")]
public sealed class VbNetJavaScriptTests {

  private string _directory = "";

  [SetUp]
  public void SetUp() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void TearDown() => Directory.Delete(this._directory, true);

  internal static IEnumerable<string> Fixtures() => VbNetCases.Names;

  /// <summary>A fixture's module and its calls; a counter past Integer.MaxValue is left to the Visual Basic oracle.</summary>
  internal static (IrModule Module, PrototypeCall[] Calls) Imported(string name) {
    var imported = VbNetFrontend.Import(name + ".vb", VbNetCases.Source(name));
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    return (imported.Module!, [.. VbNetCases.For(name).Where(call => call.Method != "PastTheEnd")]);
  }

  [TestCaseSource(nameof(Fixtures))]
  [RequiresToolchain("node")]
  public void JavaScript_ComputesWhatTheInterpreterComputes(string name) {
    var (module, calls) = Imported(name);
    var legalized = JavaScriptEmitter.Legalize(module, () => PrecomputedImplementations.Module, JavaScriptProfile.Default, FloatMode.Accurate);
    var file = JavaScriptEmitter.Emit(legalized, JavaScriptProfile.Default).Single();
    File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);
    Assert.That(Toolchain.NodeOutcomes(this._directory, file.Path, calls), Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), file.Content);
  }

}

/// <summary>The Visual Basic .NET fixtures translated to Python, whose CPython prints what the interpreter computes from the import.</summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class VbNetPythonTests {

  [TestCaseSource(typeof(VbNetJavaScriptTests), nameof(VbNetJavaScriptTests.Fixtures))]
  [RequiresToolchain("python")]
  public async Task Python_ComputesWhatTheInterpreterComputes(string name) {
    var (module, calls) = VbNetJavaScriptTests.Imported(name);
    var translated = PythonFixtures.Translate(module);
    Assert.That(translated.Problems, Is.Empty);
    var outcomes = await PythonDriver.OutcomesAsync(translated.Source!, [.. calls.Select(call => call with { Method = translated.Names![call.Method] })]);
    Assert.That(outcomes, Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), translated.Source);
  }

}
