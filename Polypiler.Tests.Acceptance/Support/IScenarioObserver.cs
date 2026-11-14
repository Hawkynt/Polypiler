using System.Collections.Generic;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// Is told about every scenario once it has run, before its workspace is deleted: the port through
/// which evidence of a scenario is collected. Observers are registered in the Reqnroll container of
/// the scenario under any name.
/// </summary>
public interface IScenarioObserver {

  /// <summary>Called once per scenario, after its last step.</summary>
  void Completed(ScenarioRecord record);

}

/// <summary>What a scenario was and how it went.</summary>
/// <param name="FeatureSlug">The slug of the feature's title.</param>
/// <param name="ScenarioSlug">The slug of the scenario's title, with the row number for a row of an outline.</param>
/// <param name="FeaturePath">The feature file, relative to the repository and with <c>/</c> as separator.</param>
/// <param name="Passed">Whether every step passed.</param>
/// <param name="Commands">The command lines the steps ran, in order.</param>
/// <param name="WorkspacePath">The scenario's workspace, which still exists while the observers run.</param>
public sealed record ScenarioRecord(
  string FeatureSlug, string ScenarioSlug, string FeaturePath, bool Passed, IReadOnlyList<CommandResult> Commands, string WorkspacePath);
