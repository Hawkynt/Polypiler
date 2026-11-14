using System;
using System.Collections.Generic;
using Reqnroll;
using Reqnroll.BoDi;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// Gives every scenario its workspace and its command log before the first step, and after the last
/// one tells the observers how it went before the workspace is deleted.
/// </summary>
[Binding]
public sealed class ScenarioHooks(IObjectContainer container, ScenarioContext scenario) {

  /// <summary>Registers a fresh workspace and an empty command log for the scenario.</summary>
  [BeforeScenario(Order = 0)]
  public void CreateWorkspace() {
    container.RegisterInstanceAs(ScenarioWorkspace.Create());
    container.RegisterInstanceAs(new CommandLog());
  }

  /// <summary>Tells every registered observer about the scenario, then deletes its workspace.</summary>
  [AfterScenario(Order = int.MaxValue)]
  public void Complete() {
    var workspace = container.Resolve<ScenarioWorkspace>();
    var identity = container.Resolve<ScenarioIdentity>();
    var record = new ScenarioRecord(identity.FeatureSlug, identity.ScenarioSlug, identity.FeaturePath,
      scenario.ScenarioExecutionStatus == ScenarioExecutionStatus.OK && scenario.TestError is null,
      container.Resolve<CommandLog>().Commands, workspace.Path);
    Complete(record, container.ResolveAll<IScenarioObserver>(), workspace);
  }

  /// <summary>
  /// Calls every observer with the record of a scenario, then deletes its workspace, also when an
  /// observer throws; an observer that throws fails the scenario afterwards.
  /// </summary>
  /// <exception cref="AggregateException">One or more observers threw; it holds what they threw.</exception>
  public static void Complete(ScenarioRecord record, IEnumerable<IScenarioObserver> observers, ScenarioWorkspace workspace) {
    var failures = new List<Exception>();
    try {
      foreach (var observer in observers)
        try {
          observer.Completed(record);
        } catch (Exception exception) {
          failures.Add(exception);
        }
    } finally {
      workspace.Delete();
    }

    if (failures.Count > 0)
      throw new AggregateException($"an observer of the scenario {record.FeatureSlug}/{record.ScenarioSlug} failed", failures);
  }

}

/// <summary>The command lines a scenario ran, in order; the steps of a <see cref="Sweep"/> add to it side by side.</summary>
public sealed class CommandLog {

  private readonly List<CommandResult> _commands = [];

  /// <summary>Every command line run so far.</summary>
  public IReadOnlyList<CommandResult> Commands {
    get {
      lock (this._commands)
        return [.. this._commands];
    }
  }

  /// <summary>The last command line run.</summary>
  /// <exception cref="InvalidOperationException">No command line was run yet.</exception>
  public CommandResult Last {
    get {
      lock (this._commands)
        return this._commands.Count > 0 ? this._commands[^1] : throw new InvalidOperationException("no command line was run in this scenario");
    }
  }

  /// <summary>Adds the result of a command line and returns it.</summary>
  public CommandResult Add(CommandResult result) {
    lock (this._commands)
      this._commands.Add(result);

    return result;
  }

}
