using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core;
using Polypiler.Permutation.Model;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// The text form of a network for tests, reports and the command line: <c>v1 = And(v0, 0x0F); v2 =
/// ShiftLeft(v1, 4); …</c>, one step per <c>;</c> or line. Operands are <c>vN</c>; an immediate is a
/// hexadecimal bit pattern (<c>0x…</c>), a decimal amount or a byte list <c>[b0,b1,…]</c>. A step's width
/// is its first operand's; a <c>Const</c> is as wide as the input unless a second argument gives its
/// width, and <c>ZeroExtend</c>/<c>Truncate</c> take the width they make as their second argument. The
/// result is the last value.
/// </summary>
public static class NetworkText {

  /// <summary>A bit pattern in hexadecimal, upper case, at least two digits: <c>0x0F</c>, <c>0x1FF</c>.</summary>
  /// <param name="value">The bits.</param>
  /// <returns>The text.</returns>
  public static string Hex(BigInteger value) {
    var digits = value.IsZero ? "0" : value.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
    return "0x" + (digits.Length < 2 ? digits.PadLeft(2, '0') : digits);
  }

  /// <summary>The text of a network.</summary>
  /// <param name="network">The network.</param>
  /// <returns>The steps, separated by <c>; </c>; empty for a network without steps.</returns>
  public static string Print(BitOperationNetwork network) {
    ArgumentNullException.ThrowIfNull(network);
    return string.Join("; ", network.Steps.Select((step, index) => $"v{(index + 1).ToString(CultureInfo.InvariantCulture)} = {Step(step, network)}"));
  }

  /// <summary>The right-hand side of one step: <c>And(v0, 0x0F)</c>.</summary>
  /// <param name="step">The step.</param>
  /// <param name="network">The network, for the widths a step's text leaves implicit; <see langword="null"/> to write them all.</param>
  /// <returns>The text.</returns>
  public static string Step(NetworkStep step, BitOperationNetwork? network = null) {
    ArgumentNullException.ThrowIfNull(step);
    var arguments = step.Operands.Select(operand => "v" + operand.ToString(CultureInfo.InvariantCulture)).ToList();
    if (step.Immediate is { } immediate)
      arguments.Add(immediate.ToString()!);
    if (step.Op is NetworkOp.ZeroExtend or NetworkOp.Truncate || step.Op == NetworkOp.Const && step.Width != network?.InputWidth)
      arguments.Add(step.Width.ToString(CultureInfo.InvariantCulture));

    return $"{step.Op}({string.Join(", ", arguments)})";
  }

  /// <summary>A network from its text.</summary>
  /// <param name="text">The text.</param>
  /// <param name="inputWidth">The width of the input.</param>
  /// <param name="outputWidth">The width of the result.</param>
  /// <returns>The network, which may be ill-formed: <see cref="NetworkVerifier"/> says so.</returns>
  /// <exception cref="PermutationModelException">PLY0810: a step that cannot be read.</exception>
  public static BitOperationNetwork Parse(string text, int inputWidth, int outputWidth) {
    ArgumentNullException.ThrowIfNull(text);
    var steps = new List<NetworkStep>();
    var lines = text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var line in lines) {
      var number = steps.Count + 1;
      var (target, body) = line.IndexOf('=') is var equals and > 0 ? (line[..equals].Trim(), line[(equals + 1)..].Trim()) : ("", line);
      if (target != "v" + number.ToString(CultureInfo.InvariantCulture))
        throw Refused(number, "?", $"'{line}' does not define v{number.ToString(CultureInfo.InvariantCulture)}");

      steps.Add(Read(body, number, inputWidth, steps));
    }

    return new BitOperationNetwork(inputWidth, outputWidth, steps, steps.Count);
  }

  /// <summary>Emits the steps of a text into a builder, the text's <c>v0</c> being a value of the builder.</summary>
  /// <param name="builder">The builder.</param>
  /// <param name="text">The steps, numbered from <c>v1</c> as <see cref="Parse"/> reads them.</param>
  /// <param name="input">The builder's value that stands for the text's <c>v0</c>.</param>
  /// <returns>The value of the last step; <paramref name="input"/> for no step.</returns>
  public static int Emit(NetworkBuilder builder, string text, int input = 0) {
    ArgumentNullException.ThrowIfNull(builder);
    var network = Parse(text, builder.WidthOf(input), builder.WidthOf(input));
    var values = new List<int> { input };
    foreach (var step in network.Steps)
      values.Add(builder.Emit(step with { Operands = [.. step.Operands.Select(operand => values[operand])] }));

    return values[^1];
  }

  private static NetworkStep Read(string body, int number, int inputWidth, List<NetworkStep> steps) {
    var open = body.IndexOf('(');
    if (open <= 0 || !body.EndsWith(')'))
      throw Refused(number, "?", $"'{body}' is no operation");

    var name = body[..open].Trim();
    if (!Enum.GetNames<NetworkOp>().Contains(name, StringComparer.Ordinal) || !Enum.TryParse<NetworkOp>(name, out var op))
      throw Refused(number, name, "is no operation");

    var operands = new List<int>();
    NetworkImmediate? immediate = null;
    int? width = null;
    foreach (var argument in Arguments(body[(open + 1)..^1])) {
      if (argument.StartsWith('v') && int.TryParse(argument[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var operand))
        operands.Add(operand);
      else if (argument.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && BigInteger.TryParse("0" + argument[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits))
        immediate = new NetworkImmediate.Bits(bits);
      else if (argument.StartsWith('[') && argument.EndsWith(']'))
        immediate = new NetworkImmediate.Bytes([.. argument[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
          .Select(item => byte.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : throw Refused(number, name, $"'{item}' is no byte"))]);
      else if (int.TryParse(argument, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
        if (op is NetworkOp.ZeroExtend or NetworkOp.Truncate || op == NetworkOp.Const && immediate is not null)
          width = amount;
        else
          immediate = new NetworkImmediate.Amount(amount);
      else
        throw Refused(number, name, $"'{argument}' is no operand or immediate");
    }

    // The width the text leaves implicit: the first operand's where it exists, else the input's.
    var implied = operands.Count > 0 && operands[0] >= 0 && operands[0] < number ? (operands[0] == 0 ? inputWidth : steps[operands[0] - 1].Width) : inputWidth;
    return new NetworkStep(op, width ?? implied, operands, immediate);
  }

  /// <summary>The arguments of an operation, split at the commas outside a byte list.</summary>
  private static IEnumerable<string> Arguments(string text) {
    var depth = 0;
    var start = 0;
    for (var index = 0; index <= text.Length; ++index) {
      if (index < text.Length && text[index] is '[' or '{')
        ++depth;
      else if (index < text.Length && text[index] is ']' or '}')
        --depth;
      else if (index == text.Length || text[index] == ',' && depth == 0) {
        if (text[start..index].Trim() is { Length: > 0 } argument)
          yield return argument;

        start = index + 1;
      }
    }
  }

  private static PermutationModelException Refused(int number, string op, string problem)
    => new(Diagnostic.Create(NetworkDiagnostics.IllFormed, null, number, op, problem));

}
