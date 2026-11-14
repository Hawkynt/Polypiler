using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Core.Backends.JavaScript.Passes;

/// <summary>
/// The target passes of the JavaScript backend, in the order they run: helper inlining (O0500), then
/// the removal of what nothing references any more (O0501), so that a helper whose every call was
/// inlined is gone from the module.
/// </summary>
public static class JavaScriptTargetPasses {

  /// <summary>The pipeline the emitter runs between building the tree and printing it.</summary>
  public static TargetPipeline<JsProgram, JavaScriptProfile> Pipeline { get; } = new([new JavaScriptHelperInlining(), new JavaScriptUnusedDeclarations()]);

  /// <summary>
  /// The statements at the level of the module: those of the file, or of the function expression a
  /// script returns its exports from (<c>var G = (function () { ... })();</c>).
  /// </summary>
  /// <param name="program">The file.</param>
  /// <returns>The statements.</returns>
  public static IReadOnlyList<JsStatement> ModuleScope(JsProgram program) => Script(program) is { } function ? function.Body.Statements : program.Body;

  /// <summary>The file with the statements at the level of the module replaced.</summary>
  /// <param name="program">The file.</param>
  /// <param name="statements">The statements.</param>
  /// <returns>The file.</returns>
  public static JsProgram WithModuleScope(JsProgram program, IReadOnlyList<JsStatement> statements) {
    if (Script(program) is not { } function)
      return program with { Body = statements };

    var replacement = function with { Body = function.Body with { Statements = statements } };
    return JavaScriptSyntaxRewriter.Rewrite(program, node => ReferenceEquals(node, function) ? replacement : node);
  }

  /// <summary>The function expression of a script, or <see langword="null"/> for a module.</summary>
  private static JsFunctionExpression? Script(JsProgram program)
    => program.Body is [JsVariableDeclaration { Declarators: [{ Initializer: JsCall { Callee: JsParenthesized { Inner: JsFunctionExpression function } } }] }] ? function : null;

  /// <summary>How many nodes an expression has, parentheses not counted: the measure of a body the cost rule of the inlining compares.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The count.</returns>
  public static int Size(JsExpression expression) => JavaScriptSyntaxRewriter.DescendantsAndSelf(expression).Count(node => node is not JsParenthesized);

}

/// <summary>
/// O0500, helper inlining in JavaScript: a call of a helper whose body is one returned expression is
/// replaced by that expression with the arguments put in for the parameters, where the profile's cost
/// rule says the call is not cheaper (see <see cref="Budget"/>). The printer adds the parentheses the
/// substituted expressions need.
/// <para>
/// It is never inlined where that could change what the program does: a recursive or multi-statement
/// helper, a body that assigns, steps or names <c>this</c> or <c>arguments</c>, an argument that is no
/// variable or literal and is read more than once or can have an effect (a call, an assignment, a step,
/// an object made anew is no such argument), and a body that names a variable the calling function
/// declares.
/// </para>
/// </summary>
public sealed class JavaScriptHelperInlining : ITargetPass<JsProgram, JavaScriptProfile> {

  /// <inheritdoc/>
  public string Id => "O0500";

  /// <inheritdoc/>
  public string Name => "helper inlining";

  /// <summary>
  /// The cost rule: the largest body, in nodes, that is inlined in a profile. A call costs an engine
  /// that does not inline it about what a dozen operators cost, at every level; a larger body is left
  /// a call.
  /// </summary>
  /// <param name="profile">The profile.</param>
  /// <returns>The number of nodes.</returns>
  public static int Budget(JavaScriptProfile profile) => 16;

  /// <inheritdoc/>
  public JsProgram Run(JsProgram tree, JavaScriptProfile profile, Action<string> applied) {
    var scope = JavaScriptTargetPasses.ModuleScope(tree);
    var helpers = scope.OfType<JsFunctionDeclaration>().Select(function => (Function: function, Body: Inlinable(function, profile)))
      .Where(helper => helper.Body is not null)
      .ToDictionary(helper => helper.Function.Name, helper => (helper.Function, Body: helper.Body!), StringComparer.Ordinal);
    if (helpers.Count == 0)
      return tree;

    var rewritten = scope.Select(statement => statement switch {
      JsFunctionDeclaration function when !helpers.ContainsKey(function.Name) => this.Inline(function, function.Name, Locals(function), helpers, applied),
      JsExport { Declaration: JsFunctionDeclaration function } export => export with { Declaration = this.Inline(function, function.Name, Locals(function), helpers, applied) },
      JsFunctionDeclaration => statement,
      _ => this.Inline(statement, "the module", [], helpers, applied)
    }).ToList();
    return rewritten.SequenceEqual(scope, ReferenceEqualityComparer.Instance) ? tree : JavaScriptTargetPasses.WithModuleScope(tree, rewritten);
  }

  /// <summary>Every name a function declares for itself: its parameters and its variables.</summary>
  private static HashSet<string> Locals(JsFunctionDeclaration function)
    => [.. function.Parameters, .. JavaScriptSyntaxRewriter.DescendantsAndSelf(function.Body).OfType<JsDeclarator>().Select(declarator => declarator.Name)];

  /// <summary>The returned expression of a helper that may be inlined, or <see langword="null"/>.</summary>
  private static JsExpression? Inlinable(JsFunctionDeclaration function, JavaScriptProfile profile) {
    if (!function.IsHelper || function.Body.Statements is not [JsReturn { Value: { } body }] || JavaScriptTargetPasses.Size(body) > Budget(profile))
      return null;

    var nodes = JavaScriptSyntaxRewriter.DescendantsAndSelf(body).ToList();
    var forbidden = nodes.Any(node => node is JsAssignment or JsUpdate or JsFunctionExpression or JsIdentifier { Name: "this" or "arguments" }
      || node is JsIdentifier { Name: var name } && name == function.Name);
    return forbidden ? null : body;
  }

  private T Inline<T>(T statement, string where, HashSet<string> locals, Dictionary<string, (JsFunctionDeclaration Function, JsExpression Body)> helpers, Action<string> applied) where T : JsNode
    => JavaScriptSyntaxRewriter.Rewrite(statement, node => {
      if (node is not JsCall { Callee: JsIdentifier { Name: var name } } call || !helpers.TryGetValue(name, out var helper))
        return node;

      if (Substituted(helper.Function, helper.Body, call, locals) is not { } inlined)
        return node;

      applied($"'{name}' into '{where}'");
      return inlined;
    });

  /// <summary>The body of a helper with the arguments of a call in place of its parameters, or <see langword="null"/> where that is not allowed.</summary>
  private static JsExpression? Substituted(JsFunctionDeclaration helper, JsExpression body, JsCall call, HashSet<string> locals) {
    var parameters = helper.Parameters;
    if (call.Arguments.Count != parameters.Count)
      return null;

    var nodes = JavaScriptSyntaxRewriter.DescendantsAndSelf(body).ToList();
    // A name of the body that is no parameter means the same at the call only if the calling function declares no variable of it.
    if (nodes.OfType<JsIdentifier>().Any(identifier => !parameters.Contains(identifier.Name) && locals.Contains(identifier.Name)))
      return null;

    var replacements = new Dictionary<string, JsExpression>(StringComparer.Ordinal);
    for (var index = 0; index < parameters.Count; ++index) {
      var argument = call.Arguments[index];
      var uses = nodes.Count(node => node is JsIdentifier { Name: var name } && name == parameters[index]);
      if (!IsTrivial(argument) && (uses > 1 || !IsPure(argument)))
        return null;

      replacements[parameters[index]] = argument;
    }

    var substituted = JavaScriptSyntaxRewriter.Rewrite(body, node => node is JsIdentifier { Name: var name } && replacements.TryGetValue(name, out var argument) ? argument : node);
    return substituted with { Type = call.Type };
  }

  /// <summary>Whether an argument may be read any number of times, or not at all: a variable or a literal.</summary>
  private static bool IsTrivial(JsExpression expression) => expression is JsIdentifier or JsNumber or JsBigInt or JsString or JsBoolean;

  /// <summary>Whether an argument evaluates without an effect: operators, members and conditionals of variables and literals.</summary>
  private static bool IsPure(JsExpression expression) => expression switch {
    JsParenthesized parenthesized => IsPure(parenthesized.Inner),
    JsUnary { Operator: "-" or "+" or "!" or "~" } unary => IsPure(unary.Operand),
    JsBinary { Operator: not ("in" or "instanceof") } binary => IsPure(binary.Left) && IsPure(binary.Right),
    JsConditional choice => IsPure(choice.Test) && IsPure(choice.WhenTrue) && IsPure(choice.WhenFalse),
    JsMember member => IsPure(member.Target),
    JsIndex index => IsPure(index.Target) && IsPure(index.Index),
    _ => IsTrivial(expression)
  };

}

/// <summary>
/// O0501, unused declaration removal in JavaScript: a helper or a constant at the level of the module
/// that nothing in the file names any more is removed, again and again until every one left is named.
/// The emitter declares only the helpers the code calls; what this pass removes was made unused by the
/// passes before it, such as a helper whose every call was inlined. The empty lines in front of a
/// removed declaration stay in front of the next statement.
/// </summary>
public sealed class JavaScriptUnusedDeclarations : ITargetPass<JsProgram, JavaScriptProfile> {

  /// <inheritdoc/>
  public string Id => "O0501";

  /// <inheritdoc/>
  public string Name => "unused declaration removal";

  /// <inheritdoc/>
  public JsProgram Run(JsProgram tree, JavaScriptProfile profile, Action<string> applied) {
    while (true) {
      var scope = JavaScriptTargetPasses.ModuleScope(tree);
      var unused = scope.Where(statement => Removable(statement) is { } names && names.All(name => Count(tree, name) == Count(statement, name))).ToList();
      if (unused.Count == 0)
        return tree;

      var kept = new List<JsStatement>();
      var blank = 0;
      foreach (var statement in scope) {
        if (unused.Contains(statement)) {
          foreach (var name in Removable(statement)!)
            applied($"'{name}'");

          blank = Math.Max(blank, statement.Leading.BlankLines);
          continue;
        }

        kept.Add(blank > statement.Leading.BlankLines ? statement with { Leading = statement.Leading with { BlankLines = blank } } : statement);
        blank = 0;
      }

      tree = JavaScriptTargetPasses.WithModuleScope(tree, kept);
    }
  }

  /// <summary>The names a statement at the level of the module declares that this pass may remove: a helper's, or a variable's; <see langword="null"/> for any other statement.</summary>
  private static IReadOnlyList<string>? Removable(JsStatement statement) => statement switch {
    JsFunctionDeclaration { IsHelper: true } helper => [helper.Name],
    JsVariableDeclaration variable => [.. variable.Declarators.Select(declarator => declarator.Name)],
    _ => null
  };

  private static int Count(JsNode node, string name) => JavaScriptSyntaxRewriter.DescendantsAndSelf(node).Count(item => item is JsIdentifier { Name: var identifier } && identifier == name);

}
