using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// <c>/pattern/flags.test(s)</c> of a regular-expression literal (#1399): the IR's
/// <see cref="IrIntrinsicOp.RegexTest"/> of the subject as JavaScript writes it as text, with pattern
/// and flags as the literal has them. A pattern or a flag outside the subset of
/// <see cref="RegexOperations"/> is refused by name; any other use of a regular expression stays
/// refused as one.
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The test of a regular-expression literal, or <see langword="null"/> for any other expression.</summary>
  private IrExpr? RegexExpression(Node node) {
    if (node is not CallExpression { Callee: MemberExpression { Computed: false, Object: RegExpLiteral literal, Property: Identifier { Name: "test" } } } call)
      return null;

    var (pattern, flags) = (literal.RegExp.Pattern, literal.RegExp.Flags);
    if (RegexOperations.Refusal(pattern, flags) is { } refused)
      throw this.Unsupported(literal, $"a regular expression with {refused}");
    if (call.Arguments.Count != 1)
      throw this.Unsupported(call, $"test with {call.Arguments.Count} arguments");

    // test converts its argument to text first, as a template does.
    return RegexOperations.Test(this.Written(call.Arguments[0], "the subject of test"), pattern, flags);
  }

}
