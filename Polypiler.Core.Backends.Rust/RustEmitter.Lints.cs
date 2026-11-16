using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>
  /// The lints a faithful translation can trigger and that are allowed by name in <c>src/lib.rs</c>,
  /// only those the crate's code has a shape of: an operation the source wrote with an identity or a
  /// zero operand (<c>identity_op</c>, <c>erasing_op</c>), two equal operands (<c>eq_op</c>), a value
  /// of the source stored and never read (<c>unused_assignments</c> of rustc), a minimum and a maximum
  /// whose bounds may be out of order, which <c>clamp</c> would panic on (<c>manual_clamp</c>, and
  /// <c>min_max</c> where both are constants), a
  /// comparison of an unsigned value with zero (<c>absurd_extreme_comparisons</c> of clippy and
  /// <c>unused_comparisons</c> of rustc), a range test written out (<c>manual_range_contains</c>), an
  /// ordering of floating-point numbers negated (<c>neg_cmp_op_on_partial_ord</c>, <c>nonminimal_bool</c>),
  /// a constant close to π or e (<c>approx_constant</c>), a loop that never ends (<c>empty_loop</c>), a
  /// value copied to a variable and straight back (<c>almost_swapped</c>) and
  /// a signature of the source with more than seven parameters (<c>too_many_arguments</c>), a public method
  /// whose name is one of a standard trait's (<c>should_implement_trait</c>: the name is the source's). The list is
  /// closed; no group and no <c>clippy::all</c> is ever allowed.
  /// </summary>
  public static class Lints {

    /// <summary>Every lint the backend may allow, in the order the attribute lists them.</summary>
    public static IReadOnlyList<string> Closed { get; } = [
      "unused_assignments", "unused_comparisons", "clippy::absurd_extreme_comparisons", "clippy::almost_swapped", "clippy::approx_constant", "clippy::empty_loop",
      "clippy::eq_op", "clippy::erasing_op", "clippy::identity_op", "clippy::manual_clamp", "clippy::manual_range_contains", "clippy::min_max",
      "clippy::neg_cmp_op_on_partial_ord", "clippy::nonminimal_bool", "clippy::too_many_arguments", "clippy::type_complexity",
      "clippy::manual_find", "clippy::unnecessary_literal_unwrap", "clippy::should_implement_trait"
    ];

    /// <summary>The method names of the standard traits clippy's <c>should_implement_trait</c> knows, which a method of the source may have.</summary>
    private static readonly HashSet<string> _traitMethods = new(StringComparer.Ordinal) {
      "add", "as_mut", "as_ref", "bitand", "bitor", "bitxor", "borrow", "borrow_mut", "clone", "cmp", "default", "deref", "deref_mut", "div", "drop",
      "eq", "from_iter", "from_str", "hash", "index", "index_mut", "into_iter", "mul", "neg", "next", "not", "rem", "shl", "shr", "sub"
    };

    /// <summary>The well-known constants of <c>std::f64::consts</c> clippy compares literals with.</summary>
    private static readonly double[] _constants = [
      Math.PI, Math.E, Math.Tau, Math.PI / 2, Math.PI / 3, Math.PI / 4, Math.PI / 6, Math.PI / 8, 1 / Math.PI, 2 / Math.PI,
      2 / Math.Sqrt(Math.PI), Math.Sqrt(2), 1 / Math.Sqrt(2), Math.Log(2), Math.Log(10), Math.Log2(Math.E), Math.Log10(Math.E),
      Math.Log2(10), Math.Log10(2)
    ];

    /// <summary>The lints a file's code needs allowed.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The lints, in the order of <see cref="Closed"/>.</returns>
    public static IReadOnlyList<string> Needed(RsFile file) {
      ArgumentNullException.ThrowIfNull(file);
      var needed = new HashSet<string>(StringComparer.Ordinal);
      foreach (var node in RustSyntaxRewriter.DescendantsAndSelf(file)) {
        switch (node) {
          case RsBinary binary:
            Binary(binary, needed);
            break;
          case RsUnary { Operator: "!", Operand: RsParenthesized { Inner: RsBinary { Operator: "<" or ">" or "<=" or ">=" } } }:
            needed.Add("clippy::neg_cmp_op_on_partial_ord");
            needed.Add("clippy::nonminimal_bool");
            break;
          case RsLiteral { Type.IsFloat: true } literal when IsApproximate(literal.Text):
            needed.Add("clippy::approx_constant");
            break;
          case RsLoop { Body: { Statements.Count: 0, Tail: null } }:
            needed.Add("clippy::empty_loop");
            break;
          case RsBlock block when IsManualFind(block):
            needed.Add("clippy::manual_find");
            break;
          case RsBlock block when block.Statements.Zip(block.Statements.Skip(1)).Any(pair => pair is (RsLet { Initializer: RsPath source } binding, RsExpressionStatement { Expression: RsAssign { Operator: "=", Target: RsPath target, Value: RsPath value } })
            && target.Name == source.Name && value.Name == binding.Name):
            needed.Add("clippy::almost_swapped");
            break;
          case RsImpl { Trait: null } impl when impl.Items.OfType<RsFunction>().Any(method => method.Visibility == "pub" && _traitMethods.Contains(method.Name)):
            needed.Add("clippy::should_implement_trait");
            break;
          case RsLet { TypeName: { } type } when IsComplex(type):
            needed.Add("clippy::type_complexity");
            break;
          case RsClosure closure when closure.Parameters.Any(parameter => IsComplex(parameter.TypeName)):
            needed.Add("clippy::type_complexity");
            break;
          case RsFunction function:
            if (function.Parameters.Count > 7)
              needed.Add("clippy::too_many_arguments");
            if (function.Parameters.Any(parameter => IsComplex(parameter.TypeName)) || function.Returns is { } returns && IsComplex(returns))
              needed.Add("clippy::type_complexity");
            if (RustLiveness.DeadStores(function).Count > 0)
              needed.Add("unused_assignments");
            if (UnwrapsALiteral(function))
              needed.Add("clippy::unnecessary_literal_unwrap");
            break;
          case RsIf { Condition: RsBinary { Operator: "<" or ">" or "<=" or ">=" } first, Else: RsIf { Condition: RsBinary { Operator: "<" or ">" or "<=" or ">=" } second, Else: RsBlockExpression { Block.Tail: { } last } } }
            when new[] { first.Left, first.Right }.Select(RustPrinter.Print).Intersect(new[] { second.Left, second.Right }.Select(RustPrinter.Print)).Contains(RustPrinter.Print(last)):
            needed.Add("clippy::manual_clamp");
            break;
          case RsMethodCall { Name: "min" or "max" } outer when outer.Receiver is RsMethodCall { Name: "min" or "max" } inner && inner.Name != outer.Name:
            needed.Add("clippy::manual_clamp");
            if (outer.Arguments[0] is RsLiteral or RsPath { Name: [>= 'A' and <= 'Z', ..] } && inner.Arguments[0] is RsLiteral or RsPath { Name: [>= 'A' and <= 'Z', ..] })
              needed.Add("clippy::min_max");
            break;
        }
      }

      return [.. Closed.Where(needed.Contains)];
    }

    private static void Binary(RsBinary binary, HashSet<string> needed) {
      var left = RustPrinter.Print(binary.Left);
      var right = RustPrinter.Print(binary.Right);
      bool Is(string text, params string[] values) => values.Contains(text, StringComparer.Ordinal);
      if ((binary.Operator is "+" or "-" or "|" or "^" or "<<" or ">>" && (Is(right, "0", "0.0") || (binary.Operator is "+" or "|" or "^" && Is(left, "0", "0.0"))))
        || (binary.Operator is "*" or "/" && (Is(right, "1", "1.0") || (binary.Operator == "*" && Is(left, "1", "1.0")))))
        needed.Add("clippy::identity_op");
      if (binary.Operator is "*" or "&" && (Is(left, "0", "0.0") || Is(right, "0", "0.0")))
        needed.Add("clippy::erasing_op");
      if (binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=" or "-" or "/" or "%" or "^" or "&" or "|" or "&&" or "||" && left == right)
        needed.Add("clippy::eq_op");
      if (binary.Operator is "<" or ">" or "<=" or ">=" && ((binary.Left.Type is { IsInteger: true, IsSigned: false } && Is(right, "0")) || (binary.Right.Type is { IsInteger: true, IsSigned: false } && Is(left, "0")))) {
        needed.Add("unused_comparisons");
        needed.Add("clippy::absurd_extreme_comparisons");
      }

      if (binary is { Operator: "&&" or "||", Left: RsBinary { Operator: "<" or ">" or "<=" or ">=" } first, Right: RsBinary { Operator: "<" or ">" or "<=" or ">=" } second }) {
        var operands = new[] { first.Left, first.Right }.Select(RustPrinter.Print).ToHashSet(StringComparer.Ordinal);
        if (operands.Overlaps(new[] { second.Left, second.Right }.Select(RustPrinter.Print)))
          needed.Add("clippy::manual_range_contains");
      }
    }

    /// <summary>
    /// Whether a block holds the search the source wrote as a loop, which clippy's <c>manual_find</c>
    /// would have as <c>find</c>: <c>for x in … { if c { return Some(x); } }</c> and then <c>None</c>.
    /// </summary>
    private static bool IsManualFind(RsBlock block) {
      for (var index = 0; index < block.Statements.Count; ++index) {
        if (block.Statements[index] is not RsExpressionStatement { Expression: RsFor { Body: { Statements: [RsExpressionStatement { Expression: RsIf { Else: null, Then: { Statements: [RsExpressionStatement { Expression: RsReturn { Value: RsCall { Callee: RsPath { Name: "Some" }, Arguments: [RsPath found] } } }], Tail: null } } }], Tail: null } } loop }
          || found.Name != loop.Pattern.TrimStart('&'))
          continue;

        var after = index + 1 < block.Statements.Count ? (block.Statements[index + 1] as RsExpressionStatement)?.Expression : block.Tail;
        if (after is RsPath { Name: "None" } or RsReturn { Value: RsPath { Name: "None" } })
          return true;
      }

      return false;
    }

    /// <summary>
    /// Whether a function unwraps an <c>Option</c> it bound to <c>Some(…)</c> or <c>None</c> and never
    /// changes - the source's optional value made of a value - which clippy's <c>unnecessary_literal_unwrap</c> sees through.
    /// </summary>
    private static bool UnwrapsALiteral(RsFunction function) {
      var literal = new HashSet<string>(StringComparer.Ordinal);
      foreach (var binding in RustSyntaxRewriter.DescendantsAndSelf(function.Body).OfType<RsLet>().Where(binding => !binding.Mutable))
        if (binding.Initializer is RsCall { Callee: RsPath { Name: "Some" } } or RsPath { Name: "None" } || binding.Initializer is RsPath { Name: var source } && literal.Contains(source))
          literal.Add(binding.Name);

      return RustSyntaxRewriter.DescendantsAndSelf(function.Body).Any(node => node is RsMethodCall { Receiver: RsPath { Name: var name }, Name: "unwrap_or_default" or "unwrap_or" or "unwrap_or_else" or "unwrap" or "expect" }
        && literal.Contains(name));
    }

    /// <summary>Whether a type is a function value that takes or gives another one, <c>Rc&lt;dyn Fn() -&gt; Rc&lt;dyn Fn() -&gt; i32&gt;&gt;</c>, which the source spelled as one name and clippy finds too complex.</summary>
    private static bool IsComplex(string type) {
      var first = type.IndexOf("dyn Fn", StringComparison.Ordinal);
      return first >= 0 && type.IndexOf("dyn Fn", first + 1, StringComparison.Ordinal) > first;
    }

    /// <summary>Whether a literal is close to a well-known constant: within a ten-thousandth, with four digits or more.</summary>
    private static bool IsApproximate(string text)
      => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && text.Count(char.IsAsciiDigit) >= 4
        && _constants.Any(constant => Math.Abs(Math.Abs(value) - constant) < 1e-4 * constant);

    /// <summary>The crate with the lints its module needs allowed in <c>src/lib.rs</c>.</summary>
    /// <param name="crate">The crate.</param>
    /// <returns>The crate.</returns>
    public static RustCrate Allowed(RustCrate crate) {
      ArgumentNullException.ThrowIfNull(crate);
      var needed = Needed(crate.Module);
      return needed.Count == 0 ? crate : crate with { Root = crate.Root with { InnerAttributes = [.. crate.Root.InnerAttributes, $"allow({string.Join(", ", needed)})"] } };
    }

  }

}
