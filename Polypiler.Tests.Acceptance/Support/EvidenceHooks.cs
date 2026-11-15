using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.Verification.Evidence;
using Polypiler.Prototype.Testing;
using Reqnroll;
using Reqnroll.BoDi;
using EvidenceIdentity = Polypiler.Core.Verification.Evidence.ScenarioIdentity;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// Gives every scenario an <see cref="EvidenceCollector"/> that its steps take by constructor
/// injection, and after the last step writes and validates its bundle under <see cref="Root"/>
/// through an <see cref="IScenarioObserver"/>, which <see cref="ScenarioHooks"/> calls before the
/// workspace goes. A step adds only what it alone knows; the version of the runtime and of Polypiler,
/// the values of an example row and every command line of the <see cref="CommandLog"/> are added
/// here.
/// </summary>
[Binding]
public sealed class EvidenceHooks(IObjectContainer container, ScenarioContext scenario) {

  /// <summary>The name the observer is registered under.</summary>
  public const string ObserverName = "evidence";

  /// <summary>The root of every bundle: <c>artifacts/evidence/</c> of the repository.</summary>
  public static string Root { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "artifacts", "evidence");

  /// <summary>Registers a fresh collector, filled with the values of the example row, and the observer that writes it.</summary>
  [BeforeScenario(Order = 1)]
  public void RegisterCollector() {
    var collector = new EvidenceCollector();
    foreach (DictionaryEntry argument in scenario.ScenarioInfo.Arguments)
      collector.AddFixtureValue((string)argument.Key, argument.Value?.ToString() ?? "");
    container.RegisterInstanceAs(collector);
    container.RegisterInstanceAs<IScenarioObserver>(new EvidenceObserver(collector, () => Identity(container.Resolve<ScenarioIdentity>(), scenario), Root), ObserverName);
  }

  /// <summary>The identity of a scenario as its bundle records it: titles, the tags of the scenario, its rule and its feature, the example row.</summary>
  public static EvidenceIdentity Identity(ScenarioIdentity identity, ScenarioContext scenario)
    => new(identity.FeatureTitle, identity.ScenarioTitle, scenario.ScenarioInfo.CombinedTags, identity.Row);

}

/// <summary>Writes and validates the evidence bundle of a scenario once it has run; an invalid bundle fails the scenario.</summary>
/// <param name="collector">What the scenario collected.</param>
/// <param name="identity">Gives the scenario's identity, which is known for sure only once it has started.</param>
/// <param name="root">The root of every bundle.</param>
public sealed class EvidenceObserver(EvidenceCollector collector, Func<EvidenceIdentity> identity, string root) : IScenarioObserver {

  /// <summary>The bundle directory once it was written.</summary>
  public string? Bundle { get; private set; }

  /// <summary>Adds the tool versions and command lines, writes the bundle and validates it.</summary>
  /// <exception cref="InvalidOperationException">The bundle is not valid; the message holds every <c>PLY0060</c>.</exception>
  public void Completed(ScenarioRecord record) {
    collector.AddToolVersion("dotnet-runtime", Environment.Version.ToString());
    collector.AddToolVersion("polypiler", ProductInfo.Version);
    for (var index = 0; index < record.Commands.Count; ++index) {
      var command = record.Commands[index];
      collector.AddProcess($"command-{(index + 1).ToString(CultureInfo.InvariantCulture)}", command.Stdout, command.Stderr, command.ExitCode);
    }

    this.Bundle = EvidenceWriter.Write(collector, identity(), record.Passed ? ScenarioOutcome.Passed : ScenarioOutcome.Failed, root);
    var diagnostics = new DiagnosticBag();
    if (!EvidenceValidator.Validate(this.Bundle, diagnostics))
      throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Items.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
  }

}
