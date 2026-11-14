using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The IR as text, one statement per line, for assertions and failure messages: an operation of the
/// IR as <c>Op&lt;type&gt;(arguments)</c>, an operator in parentheses, a conversion as
/// <c>Contract&lt;type&gt;(operand)</c>.
/// </summary>
internal static class IrText {

  /// <summary>Every function of a module.</summary>
  public static string Of(IrModule module) {
    var text = new StringBuilder();
    foreach (var function in module.Functions) {
      text.Append($"fn {function.Name}({string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}"))}) : {function.ReturnType}\n");
      Block(text, function.Body, 1);
    }

    return text.ToString();
  }

  private static void Block(StringBuilder text, IrBlock block, int depth) {
    foreach (var statement in block.Statements)
      Statement(text, statement, depth);
  }

  private static void Statement(StringBuilder text, IrStmt statement, int depth) {
    var indent = new string(' ', 2 * depth);
    switch (statement) {
      case IrBlock block:
        text.Append(indent).Append("{\n");
        Block(text, block, depth + 1);
        text.Append(indent).Append("}\n");
        break;
      case IrIf conditional:
        text.Append(indent).Append($"if {Of(conditional.Condition)}\n");
        Block(text, conditional.Then, depth + 1);
        if (conditional.Else is not null) {
          text.Append(indent).Append("else\n");
          Block(text, conditional.Else, depth + 1);
        }

        break;
      case IrWhile loop:
        text.Append(indent).Append($"while {Of(loop.Condition)}\n");
        Block(text, loop.Body, depth + 1);
        break;
      default:
        text.Append(indent).Append(statement switch {
          IrDeclare declare => $"var {declare.Local.Type} {declare.Local.Name}" + (declare.Initializer is null ? "" : " = " + Of(declare.Initializer)),
          IrAssign assign => $"{assign.Local.Name} = {Of(assign.Value)}",
          IrStore store => $"{Of(store.Array)}[{Of(store.Index)}] = {Of(store.Value)}",
          IrExprStmt expression => Of(expression.Expression),
          IrReturn returned => "return" + (returned.Value is null ? "" : " " + Of(returned.Value)),
          _ => statement.GetType().Name
        }).Append('\n');
        break;
    }
  }

  /// <summary>An expression.</summary>
  public static string Of(IrExpr expression) => expression switch {
    IrConst constant => $"{constant.Value}:{constant.Type}",
    IrLocalRef reference => reference.Local.Name,
    IrConstRef reference => reference.Constant.Name,
    IrUnary unary => $"{unary.Op}({Of(unary.Operand)})",
    IrBinary binary => $"({Of(binary.Left)} {binary.Op} {Of(binary.Right)})",
    IrConvert conversion => $"{conversion.Contract}<{conversion.Type}>({Of(conversion.Operand)})",
    IrConditional choice => $"({Of(choice.Condition)} ? {Of(choice.WhenTrue)} : {Of(choice.WhenFalse)})",
    IrArrayLoad load => $"{Of(load.Array)}[{Of(load.Index)}]",
    IrArrayLength length => $"len({Of(length.Array)})",
    IrCall call => $"call {call.Function}({string.Join(", ", call.Arguments.Select(Of))})",
    IrIntrinsic intrinsic => $"{intrinsic.Op}<{intrinsic.Type}>({string.Join(", ", intrinsic.Arguments.Select(Of))})",
    IrStep step => $"step({Of(step.Target)})",
    _ => expression.GetType().Name
  };

}
