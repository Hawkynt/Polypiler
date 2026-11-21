using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Polypiler.Core.Verification.Runners;

/// <summary>Resolves language runners and the optional capabilities they provide.</summary>
public sealed class LanguageRunners {

  private readonly Dictionary<string, ILanguageRunner> _runners = new(StringComparer.Ordinal);

  /// <summary>Registers a runner under its language identifier.</summary>
  /// <exception cref="InvalidOperationException">A runner for that language is already registered.</exception>
  public void Register(ILanguageRunner runner) {
    ArgumentNullException.ThrowIfNull(runner);
    if (!this._runners.TryAdd(runner.Language, runner))
      throw new InvalidOperationException($"a runner for '{runner.Language}' is already registered");
  }

  /// <summary>Returns the runner registered for the language.</summary>
  /// <exception cref="KeyNotFoundException">The language has no registered runner.</exception>
  public ILanguageRunner Resolve(string language) {
    if (this._runners.TryGetValue(language, out var runner))
      return runner;

    throw new KeyNotFoundException($"no runner is registered for '{language}'");
  }

  /// <summary>Returns whether the registered runner implements the requested capability.</summary>
  public bool TryGet<TCapability>(string language, [NotNullWhen(true)] out TCapability? capability) where TCapability : class {
    capability = this._runners.GetValueOrDefault(language) as TCapability;
    return capability is not null;
  }

}
