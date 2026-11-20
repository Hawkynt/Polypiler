using System;
using System.Collections.Generic;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Core.Verification.Runners;

/// <summary>Source files and the call contract for the program a runner receives.</summary>
/// <param name="Files">Source bytes keyed by relative path.</param>
/// <param name="Export">The callable export.</param>
/// <param name="Signature">The export's typed call signature.</param>
/// <param name="Profile">The source or target profile.</param>
/// <param name="Trust">The trust level used to select process isolation.</param>
public sealed record RunnerInput(
  IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files,
  string Export,
  CallSignature Signature,
  string Profile,
  TrustLevel Trust
);

/// <summary>The outcome of a build or syntax check.</summary>
/// <param name="Outcome">How the build ended.</param>
/// <param name="Directory">The directory of the built program.</param>
/// <param name="Diagnostics">Build diagnostics.</param>
/// <param name="Build">The underlying build result.</param>
public sealed record CompileResult(RunOutcome Outcome, string Directory, IReadOnlyList<Diagnostic> Diagnostics, RunResult Build);

/// <summary>Counts and failures from a language's test runner.</summary>
/// <param name="Total">The total number of tests.</param>
/// <param name="Passed">The number of passing tests.</param>
/// <param name="Failed">The number of failing tests.</param>
/// <param name="Skipped">The number of skipped tests.</param>
/// <param name="Failures">The details of the failing tests.</param>
public sealed record TestReport(int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures);

/// <summary>A failed test reported by the native test runner.</summary>
/// <param name="Name">The native test name.</param>
/// <param name="Message">The failure message.</param>
public sealed record TestFailure(string Name, string Message);
