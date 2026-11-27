using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core;

namespace Polypiler.Permutation.Networks;

/// <summary>
/// Checks that a network is well-formed, each problem a <c>PLY0810: network step {n} ({op}): {problem}</c>:
/// <list type="bullet">
/// <item>N1 - every operand refers to an earlier value;</item>
/// <item>N2 - operand widths equal the step's width, except that <c>ZeroExtend</c> widens and <c>Truncate</c> narrows;</item>
/// <item>N3 - the immediate has the kind and range the operation requires, and a constant fits its width;</item>
/// <item>N4 - the result value exists and has the output width, and every width is positive;</item>
/// <item>N5 - every step is used (<c>is unused</c>);</item>
/// <item>N6 - the operation is registered.</item>
/// </list>
/// The problems come in the order of the steps; a problem of the network as a whole is step 0.
/// </summary>
public static class NetworkVerifier {

  /// <summary>The problems of a network; empty for a well-formed one.</summary>
  /// <param name="network">The network.</param>
  /// <param name="registry">The semantics of the operations; <see cref="NetworkOpRegistry.Default"/> when <see langword="null"/>.</param>
  /// <returns>The diagnostics.</returns>
  public static IReadOnlyList<Diagnostic> Check(BitOperationNetwork network, NetworkOpRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(network);
    registry ??= NetworkOpRegistry.Default;
    var problems = new List<Diagnostic>();
    void Report(int number, string op, string problem) => problems.Add(Diagnostic.Create(NetworkDiagnostics.IllFormed, null, number, op, problem));

    if (network.InputWidth < 1)
      Report(0, "input", $"width {network.InputWidth.ToString(CultureInfo.InvariantCulture)} is not positive");

    var used = Used(network);
    for (var number = 1; number <= network.Steps.Count; ++number) {
      var step = network.Steps[number - 1];
      var op = step.Op.ToString();
      if (!registry.TryGet(step.Op, out var semantics)) {
        Report(number, op, "is not registered");
        continue;
      }

      if (step.Width < 1)
        Report(number, op, $"width {step.Width.ToString(CultureInfo.InvariantCulture)} is not positive");
      else if (step.Operands.FirstOrDefault(operand => operand < 0 || operand >= number, -1) is var undefined && undefined >= 0)
        Report(number, op, $"operand v{undefined.ToString(CultureInfo.InvariantCulture)} is not defined before use");
      else
        foreach (var problem in semantics.Check(step, network))
          Report(number, op, problem);

      if (!used.Contains(number))
        Report(number, op, "is unused");
    }

    if (network.Result < 0 || network.Result > network.Steps.Count)
      Report(0, "result", $"result v{network.Result.ToString(CultureInfo.InvariantCulture)} is not defined");
    else if (network.WidthOf(network.Result) != network.OutputWidth)
      Report(0, "result", $"result v{network.Result.ToString(CultureInfo.InvariantCulture)} has {network.WidthOf(network.Result).ToString(CultureInfo.InvariantCulture)} bits, not {network.OutputWidth.ToString(CultureInfo.InvariantCulture)}");

    return problems;
  }

  /// <summary>The steps the result depends on, through operands that refer to earlier values.</summary>
  private static HashSet<int> Used(BitOperationNetwork network) {
    var used = new HashSet<int>();
    var pending = new Stack<int>();
    if (network.Result > 0 && network.Result <= network.Steps.Count)
      pending.Push(network.Result);

    while (pending.Count > 0) {
      var value = pending.Pop();
      if (!used.Add(value))
        continue;

      foreach (var operand in network.Steps[value - 1].Operands)
        if (operand > 0 && operand < value)
          pending.Push(operand);
    }

    return used;
  }

}
