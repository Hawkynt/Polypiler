using System;
using System.IO;
using System.Text;
using Polypiler.Core.Verification.Fuzzing;

namespace Polypiler.Cli.Fuzzing;

/// <summary>
/// The fuzz target <c>cli-args</c>: the input, split at zero bytes into UTF-8 arguments, is the command
/// line of <see cref="Program.Run"/>. The contract is that any command line ends with an exit code;
/// whatever else happens is a finding. The empty input is the empty command line.
/// </summary>
public sealed class CliArgsFuzzTarget : IFuzzTarget {

  /// <inheritdoc />
  public string Id => "cli-args";

  /// <inheritdoc />
  public void Execute(ReadOnlySpan<byte> input) => Program.Run(Arguments(input), TextWriter.Null, TextWriter.Null);

  /// <summary>The arguments an input stands for: the empty input none, every other one more than it has zero bytes.</summary>
  public static string[] Arguments(ReadOnlySpan<byte> input)
    => input.IsEmpty ? [] : Encoding.UTF8.GetString(input).Split('\0');

}
