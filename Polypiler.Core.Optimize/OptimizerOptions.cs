using System.Collections.Generic;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize;

/// <summary>How much the optimizer does.</summary>
public enum OptimizationLevel {

  /// <summary>Nothing: the module stays as the frontend made it.</summary>
  None,

  /// <summary>Every rule of the table that is not switched off.</summary>
  Default

}

/// <summary>What the optimizer runs and how far.</summary>
public sealed record OptimizerOptions {

  /// <summary>The number of passes when nothing else is said.</summary>
  public const int DefaultPassLimit = 8;

  /// <summary>Whether the verifier checks the module after every rule application unless said otherwise: in a Debug build.</summary>
#if DEBUG
  public const bool DefaultVerify = true;
#else
  public const bool DefaultVerify = false;
#endif

  /// <summary>How much the optimizer does.</summary>
  public OptimizationLevel Level { get; init; } = OptimizationLevel.Default;

  /// <summary>The numbers of the rules that do not run.</summary>
  public IReadOnlySet<string> Disabled { get; init; } = new HashSet<string>();

  /// <summary>The most passes over the module; the rules run until a pass changes nothing or this many have run.</summary>
  public int PassLimit { get; init; } = DefaultPassLimit;

  /// <summary>Whether the IR verifier checks the module after every rule application, failing loudly on a rule that breaks it.</summary>
  public bool Verify { get; init; } = DefaultVerify;

  /// <summary>What the target says about the costs some rules ask for.</summary>
  public RuleTarget Target { get; init; } = RuleTarget.Neutral;

  /// <summary>The rules.</summary>
  public RuleTable Rules { get; init; } = RuleTable.Default;

}
