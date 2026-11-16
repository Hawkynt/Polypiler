using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The seeded random calls of the Rust differentials of #464, #465 and #467: for every public function
/// of a fixture whose parameters are scalars or slices of scalars, a fixed number of argument tuples
/// drawn from one seed per issue. An integer is a boundary of its type, a small number or any bit
/// pattern, a quarter, a quarter and a half of the time; a float a special value or any bit pattern; a
/// slice 0 to 64 elements drawn so; a 64-bit integer of a function that sizes an array with a
/// computed length 0 to <see cref="MaximumSize"/>. A tuple whose call does not end within a budget of interpreter
/// steps - a loop counting to a random bound, a recursion as deep - is drawn again, so that every call
/// ends in <c>rustc</c>'s build as well; the same seed gives the same calls on every run and platform.
/// </summary>
internal static class RustRandomCalls {

  /// <summary>The seed of the functions, closures and iterator chains (#464).</summary>
  public const int FunctionSeed = 1530;

  /// <summary>The seed of the arrays, slices and <c>Vec</c> (#465).</summary>
  public const int SliceSeed = 1520;

  /// <summary>The seed of the structs, enums, <c>Option</c> and <c>Result</c> (#467).</summary>
  public const int TypeSeed = 1540;

  /// <summary>The argument tuples each function is called with.</summary>
  public const int PerFunction = 2000;

  /// <summary>The most interpreter steps a call may take; one that needs more is drawn again.</summary>
  public const long Budget = 200_000;

  /// <summary>The longest slice drawn.</summary>
  public const int MaximumLength = 64;

  /// <summary>
  /// The largest size drawn for a 64-bit integer parameter - Rust's <c>usize</c> - of a function that
  /// creates an array sized at run time. A size beyond memory aborts a Rust process at the allocation,
  /// which is no panic and has no outcome to compare.
  /// </summary>
  public const int MaximumSize = 4 * MaximumLength;

  /// <summary>Whether a function creates an array whose length is computed, not a constant.</summary>
  public static bool Allocates(IrFunction function)
    => function.Body.Statements.SelectMany(IrTree.Nodes).Any(node => node is IrNewArray { Length: not IrConst });

  /// <summary>The seed a fixture's calls are drawn from: the one of the issue it belongs to.</summary>
  public static int SeedOf(string fixture) => fixture switch {
    "memory" => SliceSeed,
    "types" => TypeSeed,
    _ => FunctionSeed
  };

  /// <summary>Whether a function can be called with drawn arguments: every parameter a scalar or a slice of scalars.</summary>
  public static bool Drawable(IrFunction function) => function.IsPublic && function.Parameters.All(parameter => IsScalar(parameter.Type) || parameter.Type.IsView && IsScalar(parameter.Type.Element!));

  private static bool IsScalar(IrType type) => type == IrType.Bool || type.IsInteger || type.IsFloat;

  /// <summary>
  /// The calls of a module: <paramref name="perFunction"/> per drawable function, in the module's order,
  /// each function with a generator of its own seeded from <paramref name="seed"/> and its position, so
  /// that adding a function does not change the calls of the others.
  /// </summary>
  /// <param name="module">The module, which declares no globals: a call within a budget needs that.</param>
  /// <param name="seed">The seed.</param>
  /// <param name="perFunction">The calls per function.</param>
  /// <param name="budget">The most interpreter steps a call may take.</param>
  public static IReadOnlyList<PrototypeCall> For(IrModule module, int seed, int perFunction = PerFunction, long budget = Budget) {
    var calls = new List<PrototypeCall>();
    var functions = module.Functions.Where(Drawable).ToList();
    for (var position = 0; position < functions.Count; ++position) {
      var function = functions[position];
      var random = new Random(unchecked(seed * 7919 + position));
      var sizes = Allocates(function);
      for (int drawn = 0, attempts = 0; drawn < perFunction; ++attempts) {
        if (attempts == perFunction * 20)
          throw new InvalidOperationException($"'{function.Name}' did not end within {budget} steps for {attempts} argument tuples");

        var call = new PrototypeCall(function.Name, [.. function.Parameters.Select(parameter => sizes && parameter.Type.IsInteger && parameter.Type.Bits == 64
          ? RustCases.Value(parameter.Type, random.Next(MaximumSize + 1))
          : Draw(parameter.Type, random))]);
        if (!Ends(module, call, budget))
          continue;

        calls.Add(call);
        ++drawn;
      }
    }

    return calls;
  }

  /// <summary>Whether a call ends within the budget, with a value or a trap.</summary>
  public static bool Ends(IrModule module, PrototypeCall call, long budget) {
    try {
      IrInterpreter.Call(module, call.Method, call.FreshArguments(), budget);
      return true;
    } catch (IrBudgetException) {
      return false;
    } catch (IrException) {
      return true;
    }
  }

  /// <summary>An argument of a parameter type: a scalar, or a slice of 0 to <see cref="MaximumLength"/> scalars.</summary>
  public static object Draw(IrType type, Random random) {
    if (!type.IsView)
      return Scalar(type, random);

    var element = type.Element!;
    var length = random.Next(MaximumLength + 1);
    var array = Array.CreateInstance(Scalar(element, random).GetType(), length);
    for (var index = 0; index < length; ++index)
      array.SetValue(Scalar(element, random), index);

    return array;
  }

  private static object Scalar(IrType type, Random random) {
    if (type == IrType.Bool || random.Next(4) >= 2)
      return RustCases.Random(type, random);

    if (type.IsFloat)
      return type == IrType.F32 ? (object)(float)_floats[random.Next(_floats.Length)] : _floats[random.Next(_floats.Length)];

    var (minimum, maximum) = (IrOperators.Minimum(type), IrOperators.Maximum(type));
    Int128[] boundaries = [minimum, minimum + 1, -1, 0, 1, maximum - 1, maximum];
    var value = random.Next(2) == 0
      ? boundaries[random.Next(boundaries.Length)]
      : random.Next(type.IsSigned ? -8 : 0, 41);
    return RustCases.Value(type, Int128.Clamp(value, minimum, maximum));
  }

  private static readonly double[] _floats = [0.0, -0.0, 1.0, -1.0, 0.5, -2.5, 3.0, 255.5, 1e10, -1e10, double.Epsilon, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity];

}
