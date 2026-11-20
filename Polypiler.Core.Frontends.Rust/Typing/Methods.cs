using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Rust.Typing;

/// <summary>What a method of a primitive type returns, relative to the receiver type <c>T</c>.</summary>
internal enum MethodResult {

  /// <summary><c>T</c>.</summary>
  Same,

  /// <summary><c>Option&lt;T&gt;</c>, of the <c>checked_*</c> methods.</summary>
  Option,

  /// <summary><c>(T, bool)</c>, of the <c>overflowing_*</c> methods.</summary>
  Tuple,

  /// <summary>The unsigned type of <c>T</c>'s width, of <c>abs_diff</c>.</summary>
  Unsigned,

  /// <summary><c>u32</c>, of the bit counts.</summary>
  U32,

  /// <summary><c>bool</c>.</summary>
  Bool,

  /// <summary>The unsigned integer of the float's width, of <c>to_bits</c>.</summary>
  Bits

}

/// <summary>What an argument of a method must be, relative to the receiver type <c>T</c>.</summary>
internal enum MethodArgument {

  /// <summary><c>T</c>.</summary>
  Same,

  /// <summary><c>u32</c>: a shift amount, a rotation or an exponent.</summary>
  U32

}

/// <summary>A method of a primitive type the frontend imports: its arguments and its result.</summary>
/// <param name="Name">The name.</param>
/// <param name="Arguments">The arguments after the receiver.</param>
/// <param name="Result">The result.</param>
/// <param name="SignedOnly">Whether only the signed integer types have it.</param>
internal sealed record MethodSignature(string Name, IReadOnlyList<MethodArgument> Arguments, MethodResult Result, bool SignedOnly = false);

/// <summary>
/// The inherent methods of the integer and floating-point types that the frontend imports, as the
/// <c>core</c> documentation of the primitive types declares them, and the methods of the optional
/// value of a <c>checked_*</c> method that consume it on the spot.
/// </summary>
internal static class Methods {

  private static readonly Dictionary<string, MethodSignature> _integer = Integer();

  private static readonly Dictionary<string, MethodSignature> _float = new() {
    ["abs"] = new("abs", [], MethodResult.Same),
    ["sqrt"] = new("sqrt", [], MethodResult.Same),
    ["to_bits"] = new("to_bits", [], MethodResult.Bits),
    ["is_nan"] = new("is_nan", [], MethodResult.Bool)
  };

  /// <summary>The methods that consume the optional value of a <c>checked_*</c> method.</summary>
  public static IReadOnlySet<string> Consumers { get; } = new HashSet<string> { "unwrap", "expect", "unwrap_or", "is_some", "is_none" };

  private static Dictionary<string, MethodSignature> Integer() {
    var methods = new Dictionary<string, MethodSignature>();
    void Add(MethodSignature signature) => methods.Add(signature.Name, signature);
    MethodArgument[] same = [MethodArgument.Same];
    MethodArgument[] amount = [MethodArgument.U32];
    // The four families and the operations each has (The Rust Reference and core's integer methods).
    foreach (var (prefix, result) in new[] { ("wrapping_", MethodResult.Same), ("checked_", MethodResult.Option), ("overflowing_", MethodResult.Tuple) }) {
      foreach (var op in new[] { "add", "sub", "mul", "div", "rem" })
        Add(new(prefix + op, same, result));

      Add(new(prefix + "neg", [], result));
      Add(new(prefix + "shl", amount, result));
      Add(new(prefix + "shr", amount, result));
      Add(new(prefix + "abs", [], result, true));
      Add(new(prefix + "pow", amount, result));
    }

    foreach (var op in new[] { "add", "sub", "mul", "div" })
      Add(new("saturating_" + op, same, MethodResult.Same));

    Add(new("saturating_neg", [], MethodResult.Same, true));
    Add(new("saturating_abs", [], MethodResult.Same, true));
    Add(new("saturating_pow", amount, MethodResult.Same));
    Add(new("abs", [], MethodResult.Same, true));
    Add(new("signum", [], MethodResult.Same, true));
    Add(new("pow", amount, MethodResult.Same));
    Add(new("abs_diff", same, MethodResult.Unsigned));
    Add(new("min", same, MethodResult.Same));
    Add(new("max", same, MethodResult.Same));
    Add(new("clamp", [MethodArgument.Same, MethodArgument.Same], MethodResult.Same));
    Add(new("div_euclid", same, MethodResult.Same));
    Add(new("rem_euclid", same, MethodResult.Same));
    Add(new("rotate_left", amount, MethodResult.Same));
    Add(new("rotate_right", amount, MethodResult.Same));
    Add(new("swap_bytes", [], MethodResult.Same));
    Add(new("reverse_bits", [], MethodResult.Same));
    foreach (var count in new[] { "count_ones", "count_zeros", "leading_zeros", "trailing_zeros", "leading_ones", "trailing_ones" })
      Add(new(count, [], MethodResult.U32));

    return methods;
  }

  /// <summary>The method of a resolved primitive type, or <see langword="null"/> when the subset does not import it.</summary>
  public static MethodSignature? Of(RType receiver, string name) {
    var type = receiver.Resolved;
    if (type.Kind == RKind.Float)
      return _float.GetValueOrDefault(name);

    if (type.Kind != RKind.Integer || !_integer.TryGetValue(name, out var method))
      return null;

    return method.SignedOnly && !type.IsSigned ? null : method;
  }

  /// <summary>The type of a method's result for a receiver type.</summary>
  public static RType ResultOf(MethodSignature method, RType receiver) => method.Result switch {
    MethodResult.Option => RType.Option(receiver),
    MethodResult.Tuple => RType.Pair(receiver),
    MethodResult.Unsigned => receiver.Unsigned,
    MethodResult.U32 => RType.U32,
    MethodResult.Bool => RType.Bool,
    MethodResult.Bits => receiver.Bits == 32 ? RType.U32 : RType.U64,
    _ => receiver
  };

  /// <summary>The type of an argument for a receiver type.</summary>
  public static RType ArgumentOf(MethodArgument argument, RType receiver) => argument == MethodArgument.U32 ? RType.U32 : receiver;

}
