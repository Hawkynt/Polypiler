using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// A fixture of <c>tests/fixtures/prototype/rust</c>: its source, and what <c>rustc</c> made of it
/// with overflow checks on and off, recorded in <c>recorded/&lt;fixture&gt;.&lt;mode&gt;.txt</c>. The
/// unit tests compare the interpreter with the recordings; the acceptance tests run <c>rustc</c>
/// again, hold the recordings to it and run the same calls through the C# and the JavaScript
/// emitted from the IR.
/// <para>
/// A Rust panic has no type. An outcome keeps of it the class the IR's traps have too: <c>panic:
/// overflow</c> for an arithmetic overflow, <c>panic: divide by zero</c> for a zero divisor, and
/// <c>panic</c> for every other one (<c>unwrap</c> of <c>None</c>, <c>expect</c>, <c>step_by(0)</c>,
/// <c>clamp</c> with its bounds crossed).
/// </para>
/// </summary>
internal static class RustFixture {

  /// <summary>The fixtures, which are also the names of their modules.</summary>
  public static readonly string[] Names = ["scalars", "control", "functions", "memory", "types", "closures", "edges", "strings", "traits", "collections", "objects", "generics"];

  /// <summary>
  /// The fixtures whose constructs the Rust backend does not write yet - arrays, slices, structs,
  /// enums, Option and Result - which the round trip through the Rust backend leaves out until it does.
  /// </summary>
  public static readonly string[] BackendPending = ["memory", "types", "closures", "edges", "strings", "traits", "collections", "objects", "generics"];

  /// <summary>Both overflow modes.</summary>
  public static readonly OverflowChecks[] Modes = [OverflowChecks.Panic, OverflowChecks.Wrap];

  /// <summary>The outcome of a call that panicked with an arithmetic overflow.</summary>
  public const string Overflow = "panic: overflow";

  /// <summary>The outcome of a call that panicked dividing by zero.</summary>
  public const string DivideByZero = "panic: divide by zero";

  /// <summary>The outcome of a call that panicked for another reason.</summary>
  public const string Panic = "panic";

  /// <summary>The directory of the Rust fixtures.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "rust");

  /// <summary>The path of a fixture's source file.</summary>
  public static string PathOf(string fixture) => Path.Combine(Directory, fixture + ".rs");

  /// <summary>The text of a fixture's source file.</summary>
  public static string SourceOf(string fixture) => File.ReadAllText(PathOf(fixture));

  /// <summary>The name of a mode as <c>--overflow-checks</c> and the recordings spell it.</summary>
  public static string NameOf(OverflowChecks mode) => mode == OverflowChecks.Panic ? "panic" : "wrap";

  /// <summary>The path of the file that records what rustc did for the calls of a fixture in a mode.</summary>
  public static string RecordingOf(string fixture, OverflowChecks mode) => Path.Combine(Directory, "recorded", $"{fixture}.{NameOf(mode)}.txt");

  /// <summary>Imports a source and fails with its diagnostics when it is refused.</summary>
  public static IrModule Import(string path, string source, OverflowChecks mode) {
    var result = RustFrontend.Import(path, source, mode);
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The Rust paths of the exported instances of the generic functions of a fixture, by IR name.</summary>
  public static IReadOnlyDictionary<string, string> InstancesOf(string fixture, OverflowChecks mode)
    => RustFrontend.Import(fixture + ".rs", SourceOf(fixture), mode).Instances;

  /// <summary>Imports a fixture in a mode.</summary>
  public static IrModule Import(string fixture, OverflowChecks mode) => Import(fixture + ".rs", SourceOf(fixture), mode);

  /// <summary>One line of a recording: the call and its outcome.</summary>
  public static string Line(PrototypeCall call, string outcome)
    => string.Concat($"{call} => {outcome}".Select(character => char.IsControl(character) || character is '\u2028' or '\u2029'
      ? $"\\u{{{(int)character:x}}}"
      : character.ToString()));

  /// <summary>The lines of a recording, whatever line ending the checkout gave the file.</summary>
  public static string[] RecordedLines(string fixture, OverflowChecks mode)
    => File.ReadAllText(RecordingOf(fixture, mode)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

  /// <summary>Writes the lines of a recording, creating its folder.</summary>
  public static void Record(string path, IEnumerable<string> lines) {
    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, string.Join('\n', lines) + "\n");
  }

  /// <summary>What the interpreter does for a call, a trap or a thrown exception as its panic class.</summary>
  public static string Interpreted(IrModule module, PrototypeCall call) {
    var arguments = call.FreshArguments();
    try {
      return PrototypeFixture.Outcome(IrInterpreter.Call(module, call.Method, arguments), arguments);
    } catch (IrTrapException trap) {
      return trap.Trap switch { IrTrap.IntegerOverflow => Overflow, IrTrap.DivideByZero => DivideByZero, _ => Panic };
    } catch (IrException thrown) {
      return ClassOf(thrown.Class.Name);
    }
  }

  /// <summary>The panic class of an exception class of the IR.</summary>
  public static string ClassOf(string exceptionClass) => exceptionClass switch {
    "OverflowException" => Overflow,
    "DivideByZeroException" => DivideByZero,
    _ => Panic
  };

  /// <summary>
  /// An outcome of the prototype's notation (<c>trap: integer overflow</c>, a message of the emitted
  /// JavaScript) or a Rust panic message as the panic class it is; a value as it is.
  /// </summary>
  public static string Normalized(string outcome) {
    string? message = outcome.StartsWith("trap: ", StringComparison.Ordinal) ? outcome["trap: ".Length..]
      : outcome.StartsWith("panic: ", StringComparison.Ordinal) && outcome is not (Overflow or DivideByZero) ? outcome["panic: ".Length..]
      : null;
    if (message is null)
      return outcome;

    return message.Contains("overflow", StringComparison.Ordinal) ? Overflow
      : message.Contains("divide by zero", StringComparison.Ordinal) || message.Contains("divisor of zero", StringComparison.Ordinal) ? DivideByZero
      : Panic;
  }

}
