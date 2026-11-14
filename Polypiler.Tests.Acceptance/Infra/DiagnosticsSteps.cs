using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Prototype;
using Polypiler.Tests.Acceptance.Support;
using Polypiler.Tooling.Diagnostics;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra;

/// <summary>
/// The steps of <c>tests/features/infra/adr-diagnostics.feature</c>. A registry named by a step is
/// copied into the scenario's workspace under the same relative path, where the real
/// <c>polypiler-tooling</c> executable reads it; the diagnostics of a step take their severity,
/// exit code and rule from the repository's <c>spec/diagnostics.yaml</c>, and are rendered and
/// counted in process.
/// </summary>
[Binding, Scope(Feature = "Diagnostics registry, renderings and exit codes")]
public sealed class DiagnosticsSteps(ScenarioWorkspace workspace, CommandLog log) {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;
  private static readonly Lazy<DiagnosticRegistry> _registry = new(() => DiagnosticRegistryValidator.Validate(Path.Combine(_root, DiagnosticRegistry.RepositoryPath)));

  private string _registryPath = "";
  private Diagnostic? _diagnostic;
  private string _line = "";
  private DiagnosticBag? _bag;

  /// <summary>Copies a registry of the repository into the workspace under the same path.</summary>
  [Given("^the registry " + StepText.Quoted + "$")]
  public void Registry(string path) {
    workspace.Copy(Path.Combine(_root, path), path);
    this._registryPath = path;
  }

  /// <summary>Copies a registry and checks that it lists a code twice.</summary>
  [Given("^the registry " + StepText.Quoted + @" listing (PLY\d{4}) twice$")]
  public void RegistryListingTwice(string path, string code) {
    this.Registry(path);
    Assert.That(Regex.Matches(File.ReadAllText(workspace.Resolve(path)), $@"code: {code}\b"), Has.Count.EqualTo(2));
  }

  /// <summary>Copies a registry and checks that it registers a code for an area.</summary>
  [Given("^the registry " + StepText.Quoted + @" registering (PLY\d{4}) for area " + StepText.Quoted + "$")]
  public void RegistryRegistering(string path, string code, string area) {
    this.Registry(path);
    Assert.That(DiagnosticRegistryValidator.Validate(workspace.Resolve(path)).Find(code)?.Area, Is.EqualTo(area));
  }

  /// <summary>Runs the validator on the registry of the scenario.</summary>
  [When("^I run the validator on it$")]
  public void RunValidator() => log.Add(Toolchain.Tooling(workspace.Path, "diagnostics", "validate", "--registry", this._registryPath));

  /// <summary>A diagnostic of a registered code with a message and a source range.</summary>
  [Given(@"^an? (?:error|warning|info) (PLY\d{4}) with message " + StepText.Quoted + @" at (\S+) (\d+):(\d+)-(\d+):(\d+)$")]
  public void DiagnosticAt(string code, string message, string file, int startLine, int startColumn, int endLine, int endColumn)
    => this._diagnostic = Diagnostic.Create(Descriptor(code), new SourceRange(file, startLine, startColumn, endLine, endColumn), StepText.Unescape(message));

  /// <summary>A diagnostic of a registered code without a source range, with every text field.</summary>
  [Given(@"^an? (?:error|warning|info) (PLY\d{4}) with message " + StepText.Quoted + ", rule " + StepText.Quoted + ", cause " + StepText.Quoted
    + ", workaround " + StepText.Quoted + " and command " + StepText.Quoted + "$")]
  public void DiagnosticWithFields(string code, string message, string rule, string cause, string workaround, string command)
    => this._diagnostic = Diagnostic.Create(Descriptor(code), null, StepText.Unescape(message)) with {
      Rule = StepText.Unescape(rule),
      Cause = StepText.Unescape(cause),
      Workaround = StepText.Unescape(workaround),
      Command = StepText.Unescape(command)
    };

  /// <summary>Renders the diagnostic of the scenario.</summary>
  [When("^it is rendered as (text|JSON)$")]
  public void Rendered(string format)
    => this._line = format == "text" ? DiagnosticRenderer.ToText(this._diagnostic!) : DiagnosticRenderer.ToJson(this._diagnostic!);

  /// <summary>Checks the rendered line.</summary>
  [Then("^the line is " + StepText.Quoted + "$")]
  public void LineIs(string expected) => Assert.That(this._line, Is.EqualTo(StepText.Unescape(expected)));

  /// <summary>Checks the rendered line against a doc string.</summary>
  [Then("^the line is$")]
  public void LineIsDocString(string expected) => Assert.That(this._line, Is.EqualTo(expected.Trim()));

  /// <summary>Reports one diagnostic of each registered code, in the order given.</summary>
  [Given(@"^the diagnostics (PLY\d{4}(?:, PLY\d{4})*) are reported in that order$")]
  public void Reported(string codes) {
    this._bag = new DiagnosticBag();
    foreach (var code in codes.Split(", "))
      this._bag.Report(Diagnostic.Create(Descriptor(code), null, "reported"));
  }

  /// <summary>Checks the exit code the bag derives.</summary>
  [Then(@"^the bag's exit code is (\d+)$")]
  public void BagExitCode(int expected) => Assert.That((int)this._bag!.ExitCode, Is.EqualTo(expected));

  /// <summary>The descriptor of a code as the repository's registry records it, with the message taken as it is.</summary>
  private static DiagnosticDescriptor Descriptor(string code) {
    var entry = _registry.Value.Find(code) ?? throw new AssertionException($"{code} is not registered in {DiagnosticRegistry.RepositoryPath}");
    return new(entry.Code, entry.Severity, entry.ExitCode, "{0}", entry.Rule ?? "");
  }

}
