using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Z3;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Core.Verification.Proofs;

/// <summary>Thrown when a prover cannot run at all: its solver is missing or not the version the lock pins.</summary>
/// <param name="diagnostic">The diagnostic, <c>PLY0021</c> or <c>PLY0022</c>.</param>
public sealed class ProverUnavailableException(Diagnostic diagnostic) : Exception(diagnostic.Message) {

  /// <summary>Why the prover cannot run.</summary>
  public Diagnostic Diagnostic { get; } = diagnostic;

}

/// <summary>
/// Z3 through its managed binding, the NuGet package <c>Microsoft.Z3</c> (MIT), whose native library
/// travels with the package. Every query gets a context of its own with fixed seeds and one thread,
/// so the same query gets the same answer; the version of the binding must be the one
/// <c>tooling/toolchains.lock</c> pins under <c>microsoft-z3</c>.
/// </summary>
public sealed class Z3Solver : ISmtSolver {

  /// <summary>The key of the binding in <c>tooling/toolchains.lock</c>.</summary>
  public const string LockKey = "microsoft-z3";

  /// <summary><c>PLY0021</c>: the solver's native library cannot be loaded. Argument: the reason.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor NotInstalled = new("PLY0021", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "toolchains.installed");

  /// <summary><c>PLY0022</c>: the binding is not the version the lock pins. Argument: the reason.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor VersionMismatch = new("PLY0022", DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "toolchains.version");

  private static readonly object _global = new();
  private static bool _configured;

  /// <summary>Creates the solver against the lock this build was made with.</summary>
  /// <exception cref="ProverUnavailableException">The native library is missing (<c>PLY0021</c>) or the binding is another version (<c>PLY0022</c>).</exception>
  public Z3Solver() : this(EmbeddedLock()) { }

  /// <summary>Creates the solver against the text of a toolchain lock.</summary>
  /// <param name="lockText">The text of <c>tooling/toolchains.lock</c>.</param>
  /// <exception cref="ProverUnavailableException">The native library is missing (<c>PLY0021</c>) or the binding is not the version the lock pins (<c>PLY0022</c>).</exception>
  public Z3Solver(string lockText) : this(lockText, BindingVersion) { }

  /// <summary>Creates the solver against the text of a toolchain lock, with the version of the binding read by a probe.</summary>
  /// <param name="lockText">The text of <c>tooling/toolchains.lock</c>.</param>
  /// <param name="probe">Reads the version of the binding, loading its native library; <see cref="BindingVersion"/> does.</param>
  /// <exception cref="ProverUnavailableException">The probe cannot load the native library (<c>PLY0021</c>) or the binding is not the version the lock pins (<c>PLY0022</c>).</exception>
  public Z3Solver(string lockText, Func<string> probe) {
    ArgumentNullException.ThrowIfNull(lockText);
    ArgumentNullException.ThrowIfNull(probe);
    var pinned = Pinned(lockText);
    string actual;
    try {
      actual = probe();
    } catch (Exception exception) when (exception is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException) {
      throw new ProverUnavailableException(Diagnostic.Create(NotInstalled, null, $"the native library of {LockKey} cannot be loaded: {exception.Message}"));
    }

    if (actual != pinned)
      throw new ProverUnavailableException(Diagnostic.Create(VersionMismatch, null, $"{LockKey} is {actual}, but tooling/toolchains.lock pins {pinned}"));

    this.Version = $"z3 {actual}";
    lock (_global) {
      if (!_configured) {
        Global.SetParameter("smt.random_seed", "0");
        Global.SetParameter("sat.random_seed", "0");
        _configured = true;
      }
    }
  }

  /// <inheritdoc/>
  public string Version { get; }

  /// <summary>The version of the binding as <c>major.minor.build</c>, read from its native library.</summary>
  /// <returns>The version, such as <c>4.11.2</c>.</returns>
  public static string BindingVersion() => $"{Microsoft.Z3.Version.Major}.{Microsoft.Z3.Version.Minor}.{Microsoft.Z3.Version.Build}";

  /// <summary>The version a lock pins for the binding.</summary>
  /// <param name="lockText">The text of the lock.</param>
  /// <returns>The version, such as <c>4.11.2</c>.</returns>
  /// <exception cref="ProverUnavailableException">The lock names no version under <see cref="LockKey"/> (<c>PLY0022</c>).</exception>
  public static string Pinned(string lockText) {
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(lockText));
      if (stream.Documents.Count > 0
        && stream.Documents[0].RootNode is YamlMappingNode root
        && root.Children.TryGetValue(new YamlScalarNode("toolchains"), out var toolchains) && toolchains is YamlMappingNode entries
        && entries.Children.TryGetValue(new YamlScalarNode(LockKey), out var entry) && entry is YamlMappingNode fields
        && fields.Children.TryGetValue(new YamlScalarNode("version"), out var version) && version is YamlScalarNode { Value: { } pinned })
        return pinned;
    } catch (YamlDotNet.Core.YamlException) {
      // Reported below like a lock without the entry.
    }

    throw new ProverUnavailableException(Diagnostic.Create(VersionMismatch, null, $"tooling/toolchains.lock pins no version of {LockKey}"));
  }

  /// <inheritdoc/>
  public SmtAnswer Check(string query, IReadOnlyList<string> constants, TimeSpan timeout) {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(constants);
    using var context = new Context(new Dictionary<string, string> { ["model"] = "true" });
    var solver = context.MkSolver();
    var parameters = context.MkParams();
    parameters.Add("timeout", (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue));
    parameters.Add("random_seed", 0u);
    solver.Parameters = parameters;

    // The API's parser reads declarations and assertions; the commands of the script are the solver's calls here.
    var script = string.Join('\n', query.Split('\n').Where(line => !line.StartsWith("(set-logic", StringComparison.Ordinal) && !line.StartsWith("(check-sat", StringComparison.Ordinal)));
    solver.Assert(context.ParseSMTLIB2String(script));
    switch (solver.Check()) {
      case Status.UNSATISFIABLE:
        return new(SmtStatus.Unsat, new Dictionary<string, ulong>());
      case Status.SATISFIABLE:
        var model = solver.Model;
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var name in constants) {
          var declaration = model.ConstDecls.FirstOrDefault(candidate => candidate.Name.ToString().Trim('|') == name);
          values[name] = declaration is null ? 0 : Value(model.ConstInterp(declaration));
        }

        return new(SmtStatus.Sat, values);
      default:
        var reason = solver.ReasonUnknown;
        return new(SmtStatus.Unknown, new Dictionary<string, ulong>(), reason is "timeout" or "canceled" ? "timeout" : reason);
    }
  }

  private static ulong Value(Expr value) => value switch {
    BitVecNum number => ulong.Parse(number.BigInteger.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
    _ => value.IsTrue ? 1UL : 0UL
  };

  private static string EmbeddedLock() {
    using var stream = typeof(Z3Solver).Assembly.GetManifestResourceStream("Polypiler.Core.Verification.Proofs.toolchains.lock")
      ?? throw new InvalidOperationException("The toolchain lock is not embedded in the assembly.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

}
