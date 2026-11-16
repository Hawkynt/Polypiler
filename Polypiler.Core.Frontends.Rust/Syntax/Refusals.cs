using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.Rust.Syntax;

/// <summary>
/// The refusals the parser recorded in a function body, found without checking it: the body of a
/// function whose signature names a type outside the subset cannot be checked, and what it uses
/// outside the subset is still reported.
/// </summary>
internal static class Refusals {

  public static IEnumerable<Refusal> In(Block block)
    => block.Statements.SelectMany(In).Concat(block.Tail is null ? [] : In(block.Tail));

  private static IEnumerable<Refusal> In(Statement statement) => statement switch {
    LetStatement let => In(let.Pattern).Concat(let.Initializer is null ? [] : In(let.Initializer)).Concat(let.Else is null ? [] : In(let.Else)),
    ExpressionStatement expression => In(expression.Expression),
    ConstStatement constant => In(constant.Item.Value),
    FunctionStatement function => In(function.Item.Body),
    RefusedStatement refused => [refused.Refusal],
    _ => []
  };

  private static IEnumerable<Refusal> In(Pattern pattern) => pattern switch {
    RefusedPattern refused => [refused.Refusal],
    OrPattern alternatives => alternatives.Alternatives.SelectMany(In),
    TuplePattern tuple => tuple.Elements.SelectMany(In),
    TupleStructPattern tupleStruct => tupleStruct.Elements.SelectMany(In),
    StructPattern structure => structure.Fields.SelectMany(field => In(field.Pattern)),
    ReferencePattern reference => In(reference.Inner),
    LiteralPattern literal => In(literal.Value),
    _ => []
  };

  private static IEnumerable<Refusal> In(Expr expression) => expression switch {
    RefusedExpr refused => [refused.Refusal],
    UnaryExpr unary => In(unary.Operand),
    ReferenceExpr reference => In(reference.Operand),
    DerefExpr deref => In(deref.Operand),
    BinaryExpr binary => In(binary.Left).Concat(In(binary.Right)),
    AssignExpr assign => In(assign.Target).Concat(In(assign.Value)),
    CastExpr cast => In(cast.Operand),
    MethodCallExpr call => In(call.Receiver).Concat(call.Arguments.SelectMany(In)),
    CallExpr call => In(call.Callee).Concat(call.Arguments.SelectMany(In)),
    FieldExpr field => In(field.Target),
    IndexExpr index => In(index.Target).Concat(In(index.Index)),
    TupleExpr tuple => tuple.Elements.SelectMany(In),
    ArrayExpr array => array.Elements.SelectMany(In),
    ArrayRepeatExpr array => In(array.Value).Concat(In(array.Count)),
    StructExpr structure => structure.Fields.SelectMany(field => In(field.Value)).Concat(structure.Base is null ? [] : In(structure.Base)),
    RangeExpr range => (range.Start is null ? [] : In(range.Start)).Concat(range.End is null ? [] : In(range.End)),
    BlockExpr block => In(block.Block),
    IfExpr conditional => (conditional.Pattern is null ? [] : In(conditional.Pattern)).Concat(In(conditional.Condition)).Concat(In(conditional.Then)).Concat(conditional.Else is null ? [] : In(conditional.Else)),
    MatchExpr match => In(match.Scrutinee).Concat(match.Arms.SelectMany(arm => In(arm.Pattern).Concat(arm.Guard is null ? [] : In(arm.Guard)).Concat(In(arm.Body)))),
    WhileExpr loop => (loop.Pattern is null ? [] : In(loop.Pattern)).Concat(In(loop.Condition)).Concat(In(loop.Body)),
    LoopExpr loop => In(loop.Body),
    ForExpr loop => In(loop.Pattern).Concat(In(loop.Iterable)).Concat(In(loop.Body)),
    BreakExpr jump when jump.Value is not null => In(jump.Value),
    ReturnExpr jump when jump.Value is not null => In(jump.Value),
    TryExpr attempt => In(attempt.Operand),
    ClosureExpr closure => closure.Parameters.SelectMany(parameter => In(parameter.Pattern)).Concat(In(closure.Body)),
    MacroExpr macro => macro.Arguments.SelectMany(In),
    _ => []
  };

}
