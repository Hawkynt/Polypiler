using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;
using Polypiler.Tests.Acceptance.Prototype;

namespace Polypiler.Tests.Acceptance.VisualBasic;

/// <summary>
/// <c>Variants.bas</c> translated to JavaScript, whose dynamic values are pairs of a tag and a payload,
/// computes in node what the IR interpreter computes - VBA's promotion, <c>Empty</c> and <c>Null</c>.
/// </summary>
[TestFixture, Layer("core")]
[Category("EndToEnd")]
public sealed class VisualBasicVariantTranslationTests {

  private string _directory = "";

  [SetUp]
  public void SetUp() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void TearDown() => Directory.Delete(this._directory, true);

  [Test, RequiresToolchain("node")]
  public void JavaScript_ComputesWhatTheInterpreterComputes() {
    var module = VisualBasicVariantCalls.Module();
    var legalized = JavaScriptEmitter.Legalize(module, () => PrecomputedImplementations.Module, JavaScriptProfile.Default, FloatMode.Accurate);
    var file = JavaScriptEmitter.Emit(legalized, JavaScriptProfile.Default).Single();
    File.WriteAllText(Path.Combine(this._directory, file.Path), file.Content);
    var calls = VisualBasicVariantCalls.Calls();
    Assert.That(Toolchain.NodeOutcomes(this._directory, file.Path, calls), Is.EqualTo(calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call))), file.Content);
  }

}
