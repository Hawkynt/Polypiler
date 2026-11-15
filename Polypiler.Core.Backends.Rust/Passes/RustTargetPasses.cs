using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.Rust.Passes;

/// <summary>
/// The target passes of the Rust backend, in the order they run: helper inlining (O0500), then the
/// removal of what nothing names any more (O0501), so that a helper whose every call was inlined is
/// gone from the crate, where Rust would warn about it as dead code.
/// </summary>
public static class RustTargetPasses {

  /// <summary>The pipeline the emitter runs between building the crate and printing it.</summary>
  public static TargetPipeline<RustCrate, RustProfile> Pipeline { get; } = new([new RustHelperInlining(), new RustUnusedDeclarations()]);

  /// <summary>How many nodes an expression has, parentheses not counted: the measure the cost rule of the inlining compares.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The count.</returns>
  public static int Size(RsExpression expression) => RustSyntaxRewriter.DescendantsAndSelf(expression).Count(node => node is not RsParenthesized);

  /// <summary>
  /// A node without the parentheses that stand where any expression may: a value of a <c>let</c>, a
  /// tail, an argument, an assigned or returned value, a condition, an index, an element. An inlined
  /// call leaves its body in parentheses, which rustc's <c>unused_parens</c> warns about there.
  /// </summary>
  /// <typeparam name="T">The kind of the node.</typeparam>
  /// <param name="node">The node.</param>
  /// <returns>The node without them.</returns>
  public static T Unparenthesized<T>(T node) where T : RsNode {
    static RsExpression Bare(RsExpression expression) => expression is RsParenthesized { Inner: var inner } ? Bare(inner) : expression;
    static RsExpression Test(RsExpression expression) => expression is RsParenthesized { Inner: var inner } && !Printing.RustPrinter.IsBlockLike(inner) ? Test(inner) : expression;
    static bool Wrapped(RsExpression? expression) => expression is RsParenthesized;
    return RustSyntaxRewriter.Rewrite(node, item => item switch {
      RsLet { Initializer: RsParenthesized initializer } binding => binding with { Initializer = Bare(initializer) },
      RsBlock { Tail: RsParenthesized tail } block => block with { Tail = Bare(tail) },
      RsCall call when call.Arguments.Any(Wrapped) => call with { Arguments = [.. call.Arguments.Select(Bare)] },
      RsMethodCall call when call.Arguments.Any(Wrapped) => call with { Arguments = [.. call.Arguments.Select(Bare)] },
      RsMacro macro when macro.Arguments.Any(Wrapped) => macro with { Arguments = [.. macro.Arguments.Select(Bare)] },
      RsAssign assign when Wrapped(assign.Value) => assign with { Value = Bare(assign.Value) },
      RsReturn { Value: RsParenthesized value } result => result with { Value = Bare(value) },
      RsIf choice when Test(choice.Condition) != choice.Condition => choice with { Condition = Test(choice.Condition) },
      RsWhile loop when Test(loop.Condition) != loop.Condition => loop with { Condition = Test(loop.Condition) },
      RsMatch choice when Test(choice.Scrutinee) != choice.Scrutinee => choice with { Scrutinee = Test(choice.Scrutinee) },
      RsIndex index when Wrapped(index.Index) => index with { Index = Bare(index.Index) },
      RsArray array when array.Elements.Any(Wrapped) => array with { Elements = [.. array.Elements.Select(Bare)] },
      _ => item
    });
  }

  /// <summary>How often a name is named below a node: as a path or as the callee of a call.</summary>
  /// <param name="node">The node.</param>
  /// <param name="name">The name.</param>
  public static int Count(RsNode node, string name) => RustSyntaxRewriter.DescendantsAndSelf(node).Count(item => item is RsPath path && path.Name == name);

}

/// <summary>
/// O0500, helper inlining in Rust: a call of a helper whose body is one expression is replaced by that
/// expression with the arguments put in for the parameters, in parentheses, where the body has at
/// most <see cref="Budget"/> nodes. It is never inlined where that could change what the program does:
/// a recursive helper, one with statements, a body that assigns, returns, breaks or names a variable
/// the calling function has, and an argument that is no variable or literal, whose evaluation the
/// body could move past a trap of its own or repeat.
/// </summary>
public sealed class RustHelperInlining : ITargetPass<RustCrate, RustProfile> {

  /// <inheritdoc/>
  public string Id => "O0500";

  /// <inheritdoc/>
  public string Name => "helper inlining";

  /// <summary>The largest body, in nodes, that is inlined: rustc inlines small functions itself, so the cost rule only keeps the source short.</summary>
  public const int Budget = 16;

  /// <inheritdoc/>
  public RustCrate Run(RustCrate tree, RustProfile profile, Action<string> applied) {
    ArgumentNullException.ThrowIfNull(tree);
    ArgumentNullException.ThrowIfNull(applied);
    var helpers = tree.Module.Items.OfType<RsFunction>()
      .Select(function => (Function: function, Body: Inlinable(function)))
      .Where(helper => helper.Body is not null)
      .ToDictionary(helper => helper.Function.Name, helper => (helper.Function, Body: helper.Body!), StringComparer.Ordinal);
    if (helpers.Count == 0)
      return tree;

    var items = tree.Module.Items.Select(item => item is RsFunction function && !helpers.ContainsKey(function.Name) ? RustTargetPasses.Unparenthesized(Inline(function, helpers, applied)) : item).ToList();
    return items.SequenceEqual(tree.Module.Items, ReferenceEqualityComparer.Instance) ? tree : tree with { Module = tree.Module with { Items = items } };
  }

  /// <summary>The body of a helper that may be inlined, or <see langword="null"/>.</summary>
  private static RsExpression? Inlinable(RsFunction function) {
    if (!function.IsHelper || function.Body is not { Statements.Count: 0, Tail: { } body } || RustTargetPasses.Size(body) > Budget)
      return null;

    var nodes = RustSyntaxRewriter.DescendantsAndSelf(body).ToList();
    return nodes.Any(node => node is RsAssign or RsReturn or RsBreak or RsContinue or RsLet || node is RsPath path && path.Name == function.Name) ? null : body;
  }

  private static RsFunction Inline(RsFunction function, Dictionary<string, (RsFunction Function, RsExpression Body)> helpers, Action<string> applied) {
    var nodes = RustSyntaxRewriter.DescendantsAndSelf(function).ToList();
    var locals = nodes.OfType<RsLet>().Select(binding => binding.Name)
      .Concat(nodes.OfType<RsFunction>().SelectMany(inner => inner.Parameters).Concat(nodes.OfType<RsClosure>().SelectMany(closure => closure.Parameters)).Select(parameter => parameter.Name))
      .ToHashSet(StringComparer.Ordinal);
    return RustSyntaxRewriter.Rewrite(function, node => {
      if (node is not RsCall { Callee: RsPath { Name: var name } } call || !helpers.TryGetValue(name, out var helper) || Substituted(helper.Function, helper.Body, call, locals) is not { } inlined)
        return node;

      applied($"'{name}' into '{function.Name}'");
      return inlined;
    });
  }

  /// <summary>The body of a helper with the arguments of a call in place of its parameters, or <see langword="null"/> where that is not allowed.</summary>
  private static RsExpression? Substituted(RsFunction helper, RsExpression body, RsCall call, HashSet<string> locals) {
    var parameters = helper.Parameters.Select(parameter => parameter.Name).ToList();
    if (call.Arguments.Count != parameters.Count)
      return null;

    var nodes = RustSyntaxRewriter.DescendantsAndSelf(body).ToList();
    if (nodes.OfType<RsPath>().Any(path => !parameters.Contains(path.Name) && locals.Contains(path.Name)))
      return null;

    var replacements = new Dictionary<string, RsExpression>(StringComparer.Ordinal);
    for (var index = 0; index < parameters.Count; ++index) {
      // An argument that is no variable or literal could trap or be read twice where the call evaluated it once, first.
      var argument = call.Arguments[index];
      if (argument is not (RsPath or RsLiteral))
        return null;

      replacements[parameters[index]] = Precedence(argument) >= 15 ? argument : new RsParenthesized(argument) { Type = argument.Type };
    }

    var substituted = RustSyntaxRewriter.Rewrite(body, node => node switch {
      RsPath { Name: var name } when replacements.TryGetValue(name, out var argument) => argument,
      // A literal put in for a parameter that is cast: the literal of the type, which clippy's unnecessary_cast asks for.
      RsCast { Operand: RsLiteral { Text: var digits } literal, Target: var target } when Fits(digits, target) => literal with { Text = digits },
      _ => node
    });
    return RustEmitter.Precedence(substituted) >= 15 ? substituted : new RsParenthesized(substituted) { Type = call.Type };
  }

  private static int Precedence(RsExpression expression) => RustEmitter.Precedence(expression);

  /// <summary>Whether an integer literal is a value of an integer type, so that it may stand for its cast.</summary>
  private static bool Fits(string digits, string type) {
    if (!System.Int128.TryParse(digits, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value))
      return false;

    var (low, high) = type switch {
      "i8" => (sbyte.MinValue, sbyte.MaxValue),
      "u8" => (byte.MinValue, byte.MaxValue),
      "i16" => (short.MinValue, short.MaxValue),
      "u16" => (ushort.MinValue, ushort.MaxValue),
      "i32" => (int.MinValue, int.MaxValue),
      "u32" => (uint.MinValue, uint.MaxValue),
      "i64" => (long.MinValue, long.MaxValue),
      "u64" or "usize" => ((System.Int128)ulong.MinValue, (System.Int128)ulong.MaxValue),
      _ => ((System.Int128)1, (System.Int128)0)
    };
    return value >= low && value <= high;
  }

}

/// <summary>
/// O0501, unused declaration removal in Rust: a helper or a constant of the module that nothing in the
/// crate names any more is removed, again and again until every one left is named. Rust warns about
/// an unused private item as dead code.
/// </summary>
public sealed class RustUnusedDeclarations : ITargetPass<RustCrate, RustProfile> {

  /// <inheritdoc/>
  public string Id => "O0501";

  /// <inheritdoc/>
  public string Name => "unused declaration removal";

  /// <inheritdoc/>
  public RustCrate Run(RustCrate tree, RustProfile profile, Action<string> applied) {
    ArgumentNullException.ThrowIfNull(tree);
    ArgumentNullException.ThrowIfNull(applied);
    while (true) {
      var items = tree.Module.Items;
      var unused = items.Where(item => Removable(item) is { } name && RustTargetPasses.Count(tree.Module, name) == RustTargetPasses.Count(item, name)).ToList();
      if (unused.Count == 0)
        return tree;

      foreach (var item in unused)
        applied($"'{Removable(item)}'");

      tree = tree with { Module = tree.Module with { Items = [.. items.Where(item => !unused.Contains(item))] } };
    }
  }

  /// <summary>The name of an item this pass may remove: a helper's or a private constant's; <see langword="null"/> for any other item.</summary>
  private static string? Removable(RsItem item) => item switch {
    RsFunction { IsHelper: true } helper => helper.Name,
    RsConst { Visibility: "" } constant => constant.Name,
    _ => null
  };

}
