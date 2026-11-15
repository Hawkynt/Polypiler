using System;
using System.Collections.Generic;
using System.Globalization;

namespace Polypiler.Core.Verification.Runners;

/// <summary>What an integer operation whose result does not fit does in a build: Cargo's <c>overflow-checks</c>.</summary>
public enum OverflowChecks {

  /// <summary>It panics (<c>overflow-checks = true</c>, the <c>dev</c> profile's default).</summary>
  Panic,

  /// <summary>It wraps around (<c>overflow-checks = false</c>, the <c>release</c> profile's default).</summary>
  Wrap

}

/// <summary>
/// How every crate of a <see cref="RustRunner"/> is built, independent of the optimization level: the
/// <c>[profile.dev]</c> keys of The Cargo Book, given as their <c>CARGO_PROFILE_DEV_*</c> environment
/// variables. Neither switch has a default: the runner never guesses which build mode a result is for.
/// </summary>
/// <param name="OverflowChecks">What an overflowing integer operation does.</param>
/// <param name="DebugAssertions">Whether <c>debug_assert!</c> runs.</param>
/// <param name="OptLevel">The optimization level, 0 to 3.</param>
public sealed record RustBuildOptions(OverflowChecks OverflowChecks, bool DebugAssertions, int OptLevel = 0) {

  /// <summary>The optimization level, 0 to 3.</summary>
  public int OptLevel { get; } = OptLevel is >= 0 and <= 3 ? OptLevel : throw new ArgumentOutOfRangeException(nameof(OptLevel), OptLevel, "the optimization level is 0, 1, 2 or 3");

  /// <summary>Cargo's <c>dev</c> defaults, stated: overflow checks on, debug assertions on, level 0.</summary>
  public static RustBuildOptions Dev { get; } = new(OverflowChecks.Panic, true);

  /// <summary>The environment variables that give the switches to Cargo.</summary>
  public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>(StringComparer.Ordinal) {
    ["CARGO_PROFILE_DEV_OVERFLOW_CHECKS"] = this.OverflowChecks == OverflowChecks.Panic ? "true" : "false",
    ["CARGO_PROFILE_DEV_DEBUG_ASSERTIONS"] = this.DebugAssertions ? "true" : "false",
    ["CARGO_PROFILE_DEV_OPT_LEVEL"] = this.OptLevel.ToString(CultureInfo.InvariantCulture)
  };

}
