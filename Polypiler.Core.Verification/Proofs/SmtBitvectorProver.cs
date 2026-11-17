using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Polypiler.Core.Verification.Proofs.Obligations;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>
/// Proves an obligation at full width with a bitvector solver: at each width, ascending, the solver is
/// asked whether some input satisfies the preconditions, keeps the before side valid and makes the
/// after side invalid or different (<see cref="SmtEncoder"/>). <c>unsat</c> at every width proves the
/// rewrite; a model refutes it, but only after the reference interpreter has evaluated both sides on
/// it and confirmed the difference; a timeout or <c>unknown</c> proves nothing. The refinement rule
/// is that of <see cref="ExhaustiveProver"/>.
/// </summary>
public sealed class SmtBitvectorProver : IEquivalenceProver {

  /// <summary>The proof class of every verdict of the prover.</summary>
  public const string ProofClass = "smt-bitvector";

  /// <summary>The environment variable naming a directory to write each query into.</summary>
  public const string DumpVariable = "POLYPILER_PROOF_DUMP";

  /// <summary>The reason of a verdict when the time of the budget ran out.</summary>
  public const string TimeoutReason = "timeout";

  /// <summary>The widths a prover tries when it is given none.</summary>
  public static readonly IReadOnlyList<int> DefaultWidths = [16, 32, 64];

  private readonly ISmtSolver? _solver;

  /// <summary>A prover at 16, 32 and 64 bits on Z3.</summary>
  public SmtBitvectorProver() : this(DefaultWidths) { }

  /// <summary>A prover at the given widths on Z3.</summary>
  /// <param name="widths">The widths, each 1 to 64; tried in ascending order.</param>
  /// <exception cref="ArgumentException">No width is given, or one is outside 1 to 64.</exception>
  public SmtBitvectorProver(IReadOnlyList<int> widths) : this(widths, null) { }

  /// <summary>A prover at the given widths on a solver.</summary>
  /// <param name="widths">The widths, each 1 to 64; tried in ascending order.</param>
  /// <param name="solver">The solver; <see langword="null"/> for a <see cref="Z3Solver"/>, made when the first query is asked.</param>
  /// <exception cref="ArgumentException">No width is given, or one is outside 1 to 64.</exception>
  public SmtBitvectorProver(IReadOnlyList<int> widths, ISmtSolver? solver) {
    ArgumentNullException.ThrowIfNull(widths);
    if (widths.Count == 0)
      throw new ArgumentException("The prover needs at least one width.", nameof(widths));
    if (widths.Any(width => width is < 1 or > 64))
      throw new ArgumentException("A width lies between 1 and 64.", nameof(widths));

    this.Widths = [.. widths.Distinct().Order()];
    this._solver = solver;
  }

  /// <inheritdoc/>
  public string Id => "smt";

  /// <summary>The widths tried, ascending.</summary>
  public IReadOnlyList<int> Widths { get; }

  /// <summary>
  /// The directory each query is written into as <c>&lt;rule&gt;-w&lt;width&gt;.smt2</c>, with the
  /// verdict and the time taken as a comment header; <see langword="null"/> writes none. By default the
  /// value of <see cref="DumpVariable"/>.
  /// </summary>
  public string? DumpDirectory { get; init; } = Environment.GetEnvironmentVariable(DumpVariable) is { Length: > 0 } directory ? directory : null;

  /// <inheritdoc/>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="ProverUnavailableException">Z3 cannot be loaded (<c>PLY0021</c>) or is not the version the lock pins (<c>PLY0022</c>); no query was asked.</exception>
  /// <exception cref="InvalidOperationException">The solver's model does not refute the rewrite when evaluated: the solver or the encoding is wrong, and nothing is claimed.</exception>
  public ProofResult Prove(ProofObligation obligation, ProofBudget budget) {
    ArgumentNullException.ThrowIfNull(obligation);
    ArgumentNullException.ThrowIfNull(budget);
    var timeout = budget.EffectiveSolverTimeout;
    if (timeout <= TimeSpan.Zero)
      return new ProofResult.Inconclusive(ProofClass, TimeoutReason);

    var solver = this._solver ?? new Z3Solver();
    var clock = Stopwatch.StartNew();
    var unsat = 0;
    string? unknown = null;
    ProofResult.Unsupported? unsupported = null;
    foreach (var width in this.Widths) {
      var left = timeout - clock.Elapsed;
      if (left <= TimeSpan.Zero) {
        unknown ??= TimeoutReason;
        break;
      }

      ObligationProgram program;
      IrObligation lowered;
      try {
        program = SmtEncoder.Parse(obligation, width);
        lowered = IrObligation.Lower(program);
      } catch (ObligationUnsupportedException exception) {
        unsupported ??= new ProofResult.Unsupported(ProofClass, exception.Operation) { Detail = $"at width {width.ToString(CultureInfo.InvariantCulture)}" + (exception.Detail is { } detail ? $": {detail}" : "") };
        continue;
      }

      var query = SmtEncoder.Encode(program);
      var started = clock.Elapsed;
      var answer = solver.Check(query, [.. program.Inputs.Select(input => input.Name)], left);
      this.Dump(obligation.RuleId, width, query, answer, clock.Elapsed - started, solver.Version);
      switch (answer.Status) {
        case SmtStatus.Unsat:
          ++unsat;
          break;
        case SmtStatus.Sat:
          return Confirmed(program, lowered, answer, width);
        default:
          unknown ??= answer.Reason is TimeoutReason ? TimeoutReason : $"solver returned unknown: {answer.Reason}";
          break;
      }
    }

    if (unsupported is not null)
      return unsupported;

    return unknown is not null ? new ProofResult.Inconclusive(ProofClass, unknown) : new ProofResult.Proved(ProofClass, unsat);
  }

  /// <summary>The model as a counterexample, after the reference interpreter confirmed it.</summary>
  private static ProofResult.Refuted Confirmed(ObligationProgram program, IrObligation lowered, SmtAnswer answer, int width) {
    var values = program.Inputs.Select(input => answer.Model.GetValueOrDefault(input.Name) & input.Type.Mask).ToArray();
    var arguments = lowered.Arguments(values);
    var holds = Enumerable.Range(0, lowered.PreconditionCount).Select(index => lowered.Evaluate(IrObligation.PreconditionFunction(index), arguments)).All(value => value.IsValid && value.Value != 0);
    var before = lowered.Evaluate(IrObligation.BeforeFunction, arguments);
    var after = lowered.Evaluate(IrObligation.AfterFunction, arguments);
    if (!holds || !before.IsValid || (after.IsValid && after.Value == before.Value))
      throw new InvalidOperationException($"The solver's model at width {width} ({string.Join(", ", program.Inputs.Select((input, index) => $"{input.Name}={ProofDiagnostics.Hex(values[index])}"))}) does not refute the rewrite when evaluated.");

    var inputs = program.Inputs.Select((input, index) => (input.Name, Value: values[index])).ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
    return new(ProofClass, inputs, before.Value, after.Value) { AfterFault = after.Fault, Width = width };
  }

  /// <summary>Writes a query with its verdict when a dump directory is set.</summary>
  private void Dump(string rule, int width, string query, SmtAnswer answer, TimeSpan elapsed, string version) {
    if (this.DumpDirectory is not { } directory)
      return;

    Directory.CreateDirectory(directory);
    var name = string.Concat(rule.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
    var verdict = answer.Status switch {
      SmtStatus.Unsat => "unsat",
      SmtStatus.Sat => "sat",
      _ => $"unknown ({answer.Reason})"
    };
    var text = new StringBuilder()
      .Append(CultureInfo.InvariantCulture, $"; rule: {rule}\n")
      .Append(CultureInfo.InvariantCulture, $"; width: {width}\n")
      .Append(CultureInfo.InvariantCulture, $"; verdict: {verdict}\n")
      .Append(CultureInfo.InvariantCulture, $"; elapsed: {(long)elapsed.TotalMilliseconds} ms\n")
      .Append(CultureInfo.InvariantCulture, $"; solver: {version}\n")
      .Append(query);
    File.WriteAllText(Path.Combine(directory, $"{name}-w{width.ToString(CultureInfo.InvariantCulture)}.smt2"), text.ToString());
  }

}
