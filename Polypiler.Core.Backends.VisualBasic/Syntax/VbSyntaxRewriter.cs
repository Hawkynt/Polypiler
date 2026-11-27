using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Backends.VisualBasic.Syntax;

/// <summary>Walks a Visual Basic syntax tree: every node below one, and the shape two trees are compared by.</summary>
public static class VbSyntaxRewriter {

  /// <summary>The node and every node below it, parents before children.</summary>
  /// <param name="node">The tree.</param>
  /// <returns>The nodes.</returns>
  public static IEnumerable<VbNode> DescendantsAndSelf(VbNode node) {
    ArgumentNullException.ThrowIfNull(node);
    var pending = new Stack<VbNode>();
    pending.Push(node);
    while (pending.TryPop(out var current)) {
      yield return current;
      var children = Children(current).ToList();
      for (var index = children.Count - 1; index >= 0; --index)
        pending.Push(children[index]);
    }
  }

  /// <summary>Every name an expression of a tree reads or calls, a member's name not among them.</summary>
  /// <param name="node">The tree.</param>
  /// <returns>The names, as written.</returns>
  public static IEnumerable<string> Names(VbNode node) => DescendantsAndSelf(node).OfType<VbName>().Select(name => name.Name);

  /// <summary>The children of a node, in the order of the text.</summary>
  private static IEnumerable<VbNode> Children(VbNode node) {
    switch (node) {
      case VbModule module:
        return module.Declarations;
      case VbTypeDeclaration type:
        return type.Fields;
      case VbConst constant:
        return [constant.Type, constant.Value];
      case VbModuleVariable variable:
        return [variable.Variable];
      case VbProcedure procedure:
        return [.. procedure.Parameters, .. procedure.Returns is { } returns ? [returns] : Array.Empty<VbNode>(), .. procedure.Body];
      case VbParameter parameter:
        return [parameter.Type];
      case VbVariable variable:
        return [.. variable.Bounds, variable.Type];
      case VbBounds bounds:
        return [bounds.Lower, bounds.Upper];
      case VbDim dim:
        return dim.Variables;
      case VbStatic variables:
        return variables.Variables;
      case VbAssign assign:
        return [assign.Target, assign.Value];
      case VbCallStatement call:
        return [call.Target, .. call.Arguments];
      case VbIf conditional:
        return [.. conditional.Branches, .. conditional.Else ?? []];
      case VbIfBranch branch:
        return [branch.Condition, .. branch.Body];
      case VbSelect select:
        return [select.Value, .. select.Cases, .. select.Else ?? []];
      case VbCase section:
        return [.. section.Clauses, .. section.Body];
      case VbCaseClause clause:
        return clause.Upper is { } upper ? [clause.Value, upper] : [clause.Value];
      case VbFor loop:
        return [loop.Start, loop.End, .. loop.Step is { } step ? [step] : Array.Empty<VbNode>(), .. loop.Body];
      case VbDoLoop loop:
        return [.. loop.Pre is { } pre ? [pre] : Array.Empty<VbNode>(), .. loop.Body, .. loop.Post is { } post ? [post] : Array.Empty<VbNode>()];
      case VbLoopTest test:
        return [test.Condition];
      case VbReDim redim:
        return [redim.Target, redim.Bounds];
      case VbErase erase:
        return [erase.Target];
      case VbMember member:
        return [member.Target];
      case VbCall call:
        return [call.Target, .. call.Arguments];
      case VbUnary unary:
        return [unary.Operand];
      case VbBinary binary:
        return [binary.Left, binary.Right];
      default:
        return [];
    }
  }

  /// <summary>
  /// The structure of a tree as one text: every node's kind and the values that change its meaning,
  /// none of its layout. Two trees of one shape print alike up to their blank lines and comments.
  /// </summary>
  /// <param name="node">The tree.</param>
  /// <returns>The shape.</returns>
  public static string Shape(VbNode node) {
    ArgumentNullException.ThrowIfNull(node);
    var text = new StringBuilder();
    Write(text, node);
    return text.ToString();
  }

  private static void Write(StringBuilder text, VbNode node) {
    text.Append(node.GetType().Name[2..]);
    var detail = node switch {
      VbModule module => module.Name,
      VbInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture) + integer.Suffix + (integer.Hex ? "h" : ""),
      VbFloat number => BitConverter.DoubleToInt64Bits(number.Value).ToString(CultureInfo.InvariantCulture) + (number.IsSingle ? "!" : ""),
      VbString literal => literal.Value,
      VbBoolean boolean => boolean.Value ? "True" : "False",
      VbName name => name.Name,
      VbMember member => member.Name,
      VbUnary unary => unary.Operator,
      VbBinary binary => binary.Operator,
      VbTypeName type => type.Name,
      VbVariable variable => variable.Name + (variable.IsArray ? "()" : ""),
      VbParameter parameter => (parameter.ByVal ? "ByVal " : "") + parameter.Name + (parameter.IsArray ? "()" : ""),
      VbProcedure procedure => (procedure.IsPublic ? "Public " : "") + procedure.Name + (procedure.ReturnsArray ? "()" : ""),
      VbTypeDeclaration type => (type.IsPublic ? "Public " : "") + type.Name,
      VbConst constant => (constant.IsPublic ? "Public " : "") + constant.Name,
      VbModuleVariable variable => variable.IsPublic ? "Public" : "",
      VbOption option => option.Text,
      VbIf conditional => conditional.Else is null ? "" : "else",
      VbSelect select => select.Else is null ? "" : "else",
      VbCaseClause clause => clause.Is ?? (clause.Upper is null ? "" : "to"),
      VbFor loop => loop.Variable,
      VbDoLoop loop => (loop.Pre is null ? "" : "pre") + (loop.Post is null ? "" : "post"),
      VbLoopTest test => test.Until ? "Until" : "While",
      VbExit exit => exit.Kind,
      VbGoTo jump => jump.Label,
      VbLabel label => label.Name,
      VbOnError onError => onError.Label ?? "Resume Next",
      VbResume resume => resume.Target,
      VbReDim redim => redim.Preserve ? "Preserve" : "",
      _ => ""
    };
    text.Append('[').Append(detail).Append(']');
    var children = Children(node).ToList();
    if (children.Count == 0)
      return;

    text.Append('(');
    for (var index = 0; index < children.Count; ++index) {
      if (index > 0)
        text.Append(',');
      Write(text, children[index]);
    }

    text.Append(')');
  }

}
