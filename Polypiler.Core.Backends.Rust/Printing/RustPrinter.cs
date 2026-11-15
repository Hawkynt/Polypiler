using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core.Backends.Rust.Syntax;

namespace Polypiler.Core.Backends.Rust.Printing;

/// <summary>
/// Turns a Rust syntax tree into text, the only place where generated Rust becomes text. The text is
/// what <c>rustfmt</c> writes with its default configuration: four spaces of indentation, lines of at
/// most 100 columns, and its default width heuristics - a call's arguments on one line up to 60
/// columns (<c>fn_call_width</c>), a method chain up to 60 (<c>chain_width</c>), an <c>if</c>-<c>else</c>
/// on one line up to 50 (<c>single_line_if_else_max_width</c>), an array up to 60 (<c>array_width</c>).
/// Beyond them a call puts one argument on each line, a chain one method on each line, and a chain of
/// binary operators one operand on each line, the operator in front. Parentheses are nodes of the
/// tree; the printer adds none.
/// </summary>
public static class RustPrinter {

  /// <summary>The width rustfmt fills a line to.</summary>
  public const int MaxWidth = 100;

  private const int CallWidth = 60;
  private const int ChainWidth = 60;
  private const int IfElseWidth = 50;
  private const int ArrayWidth = 60;
  private const int ShortElement = 10;
  private const int AttributeWidth = 70;
  private const int Indent = 4;

  /// <summary>A file as rustfmt writes it, ending with one line break.</summary>
  /// <param name="file">The file.</param>
  /// <returns>The text.</returns>
  public static string Print(RsFile file) {
    ArgumentNullException.ThrowIfNull(file);
    var text = new StringBuilder();
    foreach (var line in file.InnerDocs)
      text.Append(line.Length == 0 ? "//!" : "//! " + line).Append('\n');
    foreach (var attribute in file.InnerAttributes)
      text.Append("#![").Append(Attribute(attribute, 0)).Append("]\n");

    Items(text, file.Items, 0, file.InnerDocs.Count + file.InnerAttributes.Count > 0);
    return text.ToString();
  }

  /// <summary>One item as rustfmt writes it at the outermost level, ending with a line break.</summary>
  /// <param name="item">The item.</param>
  /// <returns>The text.</returns>
  public static string Print(RsItem item) {
    ArgumentNullException.ThrowIfNull(item);
    var text = new StringBuilder();
    Item(text, item, 0);
    return text.ToString();
  }

  /// <summary>An expression on one line, as far as it can be: what a test or a diagnostic shows.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The text.</returns>
  public static string Print(RsExpression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    return Flat(expression) ?? Expression(expression, 0, 0);
  }

  /// <summary>
  /// Items one after the other: a blank line between two items, except between two module
  /// declarations or two <c>use</c> declarations, which stand as a group.
  /// </summary>
  private static void Items(StringBuilder text, IReadOnlyList<RsItem> items, int indent, bool blankFirst) {
    for (var index = 0; index < items.Count; ++index) {
      var grouped = index > 0 && items[index].GetType() == items[index - 1].GetType() && items[index] is RsModuleDeclaration or RsUse;
      if ((index > 0 || blankFirst) && !grouped)
        text.Append('\n');

      Item(text, items[index], indent);
    }
  }

  private static void Item(StringBuilder text, RsItem item, int indent) {
    var pad = new string(' ', indent);
    foreach (var doc in item.Docs)
      text.Append(pad).Append(doc.Length == 0 ? "///" : "/// " + doc).Append('\n');
    foreach (var attribute in item.Attributes)
      text.Append(pad).Append("#[").Append(Attribute(attribute, indent)).Append("]\n");

    switch (item) {
      case RsFunction function:
        Function(text, function, indent);
        break;
      case RsConst constant: {
        var head = $"{pad}{Visibility(constant.Visibility)}{(constant.IsStatic ? "static" : "const")} {constant.Name}: {constant.TypeName} = ";
        text.Append(head).Append(Expression(constant.Value, indent, head.Length, 1)).Append(";\n");
        break;
      }
      case RsModuleDeclaration module:
        text.Append(pad).Append(Visibility(module.Visibility)).Append("mod ").Append(module.Name).Append(";\n");
        break;
      case RsUse use:
        text.Append(pad).Append("use ").Append(use.Path).Append(";\n");
        break;
      case RsInlineModule module:
        text.Append(pad).Append("mod ").Append(module.Name).Append(" {\n");
        Items(text, module.Items, indent + Indent, false);
        text.Append(pad).Append("}\n");
        break;
      default:
        throw new ArgumentException($"There is no Rust item '{item.GetType().Name}'.", nameof(item));
    }
  }

  private static string Visibility(string visibility) => visibility.Length == 0 ? "" : visibility + " ";

  /// <summary>
  /// An attribute's content: <c>name(a, b)</c> on one line while its arguments are at most 70 columns
  /// wide (<c>attr_fn_like_width</c>), otherwise one argument on each line, without a trailing comma.
  /// </summary>
  private static string Attribute(string attribute, int indent) {
    var open = attribute.IndexOf('(', StringComparison.Ordinal);
    if (open < 0 || !attribute.EndsWith(')') || attribute.Length - open - 2 <= AttributeWidth)
      return attribute;

    var arguments = attribute[(open + 1)..^1].Split(", ");
    var inner = new string(' ', indent + Indent);
    return attribute[..(open + 1)] + "\n" + string.Join(",\n", arguments.Select(argument => inner + argument)) + "\n" + new string(' ', indent) + ")";
  }

  /// <summary>
  /// A function: its signature on one line when it fits, otherwise one parameter on each line with a
  /// trailing comma; an empty body as <c>{}</c>.
  /// </summary>
  private static void Function(StringBuilder text, RsFunction function, int indent) {
    var pad = new string(' ', indent);
    var head = $"{pad}{Visibility(function.Visibility)}fn {function.Name}{function.Generics}(";
    var result = function.Returns is null ? "" : " -> " + function.Returns;
    var parameters = string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Name}: {parameter.TypeName}"));
    var empty = function.Body.Statements.Count == 0 && function.Body.Tail is null;
    var line = head + parameters + ")" + result + (empty ? " {}" : " {");
    if (line.Length <= MaxWidth || function.Parameters.Count == 0)
      text.Append(line);
    else {
      text.Append(head).Append('\n');
      foreach (var parameter in function.Parameters)
        text.Append(pad).Append(' ', Indent).Append(parameter.Name).Append(": ").Append(parameter.TypeName).Append(",\n");
      text.Append(pad).Append(')').Append(result).Append(empty ? " {}" : " {");
    }

    if (empty) {
      text.Append('\n');
      return;
    }

    text.Append('\n');
    BlockBody(text, function.Body, indent + Indent);
    text.Append(pad).Append("}\n");
  }

  /// <summary>The statements and the tail of a block, each on its own lines at an indentation.</summary>
  private static void BlockBody(StringBuilder text, RsBlock block, int indent) {
    var pad = new string(' ', indent);
    foreach (var statement in block.Statements)
      text.Append(pad).Append(Statement(statement, indent)).Append('\n');
    if (block.Tail is { } tail)
      text.Append(pad).Append(Expression(tail, indent, indent)).Append('\n');
  }

  private static string Statement(RsStatement statement, int indent) => statement switch {
    RsLet binding => Let(binding, indent),
    RsExpressionStatement { Expression: var expression } when IsBlockLike(expression) => Expression(expression, indent, indent),
    RsExpressionStatement { Expression: RsAssign assign } => Right(Expression(assign.Target, indent, indent) + " " + assign.Operator, assign.Value, indent) + ";",
    RsExpressionStatement { Expression: var expression } => Expression(expression, indent, indent, 1) + ";",
    _ => throw new ArgumentException($"There is no Rust statement '{statement.GetType().Name}'.", nameof(statement))
  };

  private static string Let(RsLet binding, int indent) {
    var head = $"let {(binding.Mutable ? "mut " : "")}{binding.Name}{(binding.TypeName is null ? "" : ": " + binding.TypeName)}";
    return binding.Initializer is null ? head + ";" : Right(head + " =", binding.Initializer, indent) + ";";
  }

  /// <summary>
  /// The right-hand side of a <c>let</c> or an assignment after its head: on the head's line when it
  /// fits there whole; otherwise, where it fits whole on the next line one step in, there; otherwise
  /// broken from the head's line on - rustfmt's choice for a right-hand side.
  /// </summary>
  private static string Right(string head, RsExpression value, int indent) {
    var column = indent + head.Length + 1;
    var flat = Flat(value);
    if (flat is not null && column + flat.Length + 1 <= MaxWidth)
      return head + " " + flat;

    if (flat is not null && !IsBlockLike(value) && indent + Indent + flat.Length + 1 <= MaxWidth)
      return head + "\n" + new string(' ', indent + Indent) + flat;

    return head + " " + Expression(value, indent, column, 1);
  }

  /// <summary>Whether an expression ends in a block, so that as a statement it takes no semicolon.</summary>
  public static bool IsBlockLike(RsExpression expression)
    => expression is RsIf or RsWhile or RsLoop or RsFor or RsMatch or RsBlockExpression or RsLabeledBlock;

  /// <summary>
  /// An expression starting at a column: on one line when it fits there with <paramref name="trailing"/>
  /// columns after it and keeps to the heuristics, otherwise broken over lines below <paramref name="indent"/>.
  /// </summary>
  private static string Expression(RsExpression expression, int indent, int column, int trailing = 0) {
    if (Flat(expression) is { } flat && column + flat.Length + trailing <= MaxWidth)
      return flat;

    return Broken(expression, indent, column, trailing);
  }

  /// <summary>An expression on one line, or <see langword="null"/> where rustfmt would not write it on one.</summary>
  private static string? Flat(RsExpression expression) {
    switch (expression) {
      case RsLiteral literal:
        return literal.Text;
      case RsString text:
        return Quoted(text.Value);
      case RsPath path:
        return path.Name;
      case RsUnary unary:
        return Flat(unary.Operand) is { } operand ? unary.Operator + operand : null;
      case RsBinary binary:
        return Flat(binary.Left) is { } left && Flat(binary.Right) is { } right ? $"{left} {binary.Operator} {right}" : null;
      case RsCast cast:
        return Flat(cast.Operand) is { } value ? $"{value} as {cast.Target}" : null;
      case RsParenthesized parenthesized:
        return Flat(parenthesized.Inner) is { } inner ? $"({inner})" : null;
      case RsCall call:
        return Flat(call.Callee) is { } callee && Arguments(call.Arguments) is { } arguments ? $"{callee}({arguments})" : null;
      case RsMacro { Bracketed: true } macro:
        return Flat(macro.Arguments[0]) is { } bracketed ? $"{macro.Name}!{bracketed}" : null;
      case RsMacro macro:
        return Arguments(macro.Arguments) is { } macroArguments ? $"{macro.Name}!({macroArguments})" : null;
      case RsReference reference:
        return Flat(reference.Operand) is { } borrowed ? (reference.Mutable ? "&mut " : "&") + borrowed : null;
      case RsMethodCall call: {
        var (receiver, links) = Chain(call);
        if (Flat(receiver) is not { } start)
          return null;

        var flat = new StringBuilder(start);
        foreach (var link in links) {
          if (Arguments(link.Arguments) is not { } linkArguments)
            return null;

          flat.Append('.').Append(link.Name).Append('(').Append(linkArguments).Append(')');
        }

        return links.Count > 1 && flat.Length > ChainWidth ? null : flat.ToString();
      }
      case RsIndex index:
        return Flat(index.Target) is { } target && Flat(index.Index) is { } at ? $"{target}[{at}]" : null;
      case RsField field:
        return Flat(field.Target) is { } owner ? $"{owner}.{field.Name}" : null;
      case RsAssign assign:
        return Flat(assign.Target) is { } place && Flat(assign.Value) is { } value2 ? $"{place} {assign.Operator} {value2}" : null;
      case RsRange range: {
        var from = range.Start is null ? "" : Flat(range.Start);
        var to = range.End is null ? "" : Flat(range.End);
        return from is null || to is null ? null : $"{from}{(range.Inclusive ? "..=" : "..")}{to}";
      }
      case RsBreak exit: {
        var keyword = "break" + (exit.Label is null ? "" : " '" + exit.Label);
        return exit.Value is null ? keyword : Flat(exit.Value) is { } flatValue ? keyword + " " + flatValue : null;
      }
      case RsContinue next:
        return "continue" + (next.Label is null ? "" : " '" + next.Label);
      case RsReturn result:
        return result.Value is null ? "return" : Flat(result.Value) is { } returned ? "return " + returned : null;
      case RsArray { Count: { } count } repeated:
        return Flat(repeated.Elements[0]) is { } repeatedValue && Flat(count) is { } times ? $"[{repeatedValue}; {times}]" : null;
      case RsArray array: {
        var elements = array.Elements.Select(Flat).ToList();
        if (elements.Any(element => element is null))
          return null;

        var joined = string.Join(", ", elements);
        return joined.Length > ArrayWidth ? null : $"[{joined}]";
      }
      case RsIf { Then: { Statements.Count: 0, Tail: { } whenTrue }, Else: RsBlockExpression { Block: { Statements.Count: 0, Tail: { } whenFalse } } } choice: {
        if (Flat(choice.Condition) is not { } condition || Flat(whenTrue) is not { } yes || Flat(whenFalse) is not { } no)
          return null;

        var flat = $"if {condition} {{ {yes} }} else {{ {no} }}";
        return flat.Length > IfElseWidth ? null : flat;
      }
      case RsBlockExpression { Block: { Statements.Count: 0, Tail: null } }:
        return "{}";
      case RsLoop { Body: { Statements.Count: 0, Tail: null } } loop:
        return Label(loop.Label) + "loop {}";
      default:
        return null;
    }
  }

  /// <summary>Arguments on one line, or <see langword="null"/> when one cannot be or they are wider than a call's arguments may be.</summary>
  private static string? Arguments(IReadOnlyList<RsExpression> arguments) {
    var flat = arguments.Select(Flat).ToList();
    if (flat.Any(argument => argument is null))
      return null;

    var joined = string.Join(", ", flat);
    return joined.Length > CallWidth ? null : joined;
  }

  /// <summary>A method call as the receiver the chain starts from and the calls after it, in order.</summary>
  private static (RsExpression Receiver, List<RsMethodCall> Links) Chain(RsMethodCall call) {
    var links = new List<RsMethodCall>();
    RsExpression current = call;
    while (current is RsMethodCall link) {
      links.Add(link);
      current = link.Receiver;
    }

    links.Reverse();
    return (current, links);
  }

  private static string Label(string? label) => label is null ? "" : $"'{label}: ";

  /// <summary>An expression over several lines, the way rustfmt breaks each kind.</summary>
  private static string Broken(RsExpression expression, int indent, int column, int trailing) {
    var pad = new string(' ', indent);
    var inner = new string(' ', indent + Indent);
    switch (expression) {
      case RsCall call: {
        var callee = Expression(call.Callee, indent, column);
        return callee + "(" + (Overflowed(call.Arguments, indent, column + callee.Length + 1) ?? VerticalArguments(call.Arguments, indent)) + ")";
      }
      case RsMacro { Bracketed: true } macro:
        return macro.Name + "!" + Expression(macro.Arguments[0], indent, column + macro.Name.Length + 1, trailing);
      case RsMacro macro:
        return macro.Name + "!(" + (Overflowed(macro.Arguments, indent, column + macro.Name.Length + 2) ?? VerticalArguments(macro.Arguments, indent)) + ")";
      case RsReference reference: {
        var prefix = reference.Mutable ? "&mut " : "&";
        return prefix + Expression(reference.Operand, indent, column + prefix.Length, trailing);
      }
      case RsMethodCall call: {
        // The root on the first line, then one call on each line one step in - or at the root's own
        // indentation where the root ends a block of lines; a root no wider than one indentation
        // keeps the first call on its line.
        var (receiver, links) = Chain(call);
        var root = Expression(receiver, indent, column);
        var text = new StringBuilder(root);
        var linkIndent = root.Contains('\n') ? indent : indent + Indent;
        var skip = 0;
        if (!root.Contains('\n') && root.Length <= Indent) {
          var first = links[0];
          var firstArguments = Arguments(first.Arguments);
          var fits = firstArguments is not null && column + root.Length + first.Name.Length + firstArguments.Length + 3 + (links.Count == 1 ? trailing : 0) <= MaxWidth;
          var written = fits ? firstArguments! : Overflowed(first.Arguments, indent, column + root.Length + first.Name.Length + 2) ?? VerticalArguments(first.Arguments, indent);
          text.Append('.').Append(first.Name).Append('(').Append(written).Append(')');
          // The first call over several lines ends a block of lines: the calls after it stand at the root's indentation.
          if (written.Contains('\n'))
            linkIndent = indent;
          skip = 1;
        }

        for (var index = skip; index < links.Count; ++index) {
          var link = links[index];
          var head = $".{link.Name}(";
          var arguments = Arguments(link.Arguments);
          text.Append('\n').Append(' ', linkIndent).Append(head);
          text.Append(arguments is not null && linkIndent + head.Length + arguments.Length + 1 + (index == links.Count - 1 ? trailing : 0) <= MaxWidth
            ? arguments
            : Overflowed(link.Arguments, linkIndent, linkIndent + head.Length) ?? VerticalArguments(link.Arguments, linkIndent));
          text.Append(')');
        }

        return text.ToString();
      }
      case RsBinary binary: {
        var operands = new List<(string? Operator, RsExpression Operand)>();
        Flatten(binary, binary.Operator, operands);
        if (operands.Count == 2 && Paired(operands[0].Operand, binary.Operator, operands[1].Operand, indent, column, trailing) is { } paired)
          return paired;

        var text = new StringBuilder(Expression(operands[0].Operand, indent, column));
        foreach (var (op, operand) in operands.Skip(1)) {
          var head = op + " ";
          text.Append('\n').Append(inner).Append(head).Append(Expression(operand, indent + Indent, indent + Indent + head.Length));
        }

        return text.ToString();
      }
      case RsUnary unary:
        return unary.Operator + Expression(unary.Operand, indent, column + unary.Operator.Length, trailing);
      case RsCast cast:
        return Expression(cast.Operand, indent, column) + " as " + cast.Target;
      case RsParenthesized parenthesized:
        return "(" + Expression(parenthesized.Inner, indent, column + 1, trailing + 1) + ")";
      case RsAssign assign: {
        var place = Expression(assign.Target, indent, column);
        return place + " " + assign.Operator + " " + Expression(assign.Value, indent, column + place.Length + assign.Operator.Length + 2, trailing);
      }
      case RsReturn { Value: { } value }:
        return "return " + Expression(value, indent, column + 7, trailing);
      case RsBreak { Value: { } value } exit:
        var keyword = "break" + (exit.Label is null ? "" : " '" + exit.Label) + " ";
        return keyword + Expression(value, indent, column + keyword.Length, trailing);
      case RsIndex index:
        return Expression(index.Target, indent, column) + "[" + Expression(index.Index, indent, column) + "]";
      case RsField field:
        return Expression(field.Target, indent, column) + "." + field.Name;
      case RsRange range:
        return (range.Start is null ? "" : Expression(range.Start, indent, column)) + (range.Inclusive ? "..=" : "..") + (range.End is null ? "" : Expression(range.End, indent, column));
      case RsArray array: {
        var text = new StringBuilder("[");
        var flat = array.Elements.Select(Flat).ToList();
        if (array.Count is null && flat.All(element => element is not null && element.Length <= ShortElement)) {
          // Short elements fill the lines, as many on each as fit: rustfmt's mixed layout.
          var line = new StringBuilder();
          foreach (var element in flat) {
            if (line.Length > 0 && indent + Indent + line.Length + 1 + element!.Length + 1 >= MaxWidth) {
              text.Append('\n').Append(inner).Append(line);
              line.Clear();
            }

            line.Append(line.Length > 0 ? " " : "").Append(element).Append(',');
          }

          return text.Append('\n').Append(inner).Append(line).Append('\n').Append(pad).Append(']').ToString();
        }

        foreach (var element in array.Elements)
          text.Append('\n').Append(inner).Append(Expression(element, indent + Indent, indent + Indent, 1)).Append(',');
        return text.Append('\n').Append(pad).Append(']').ToString();
      }
      case RsIf choice:
        return If(choice, indent, column);
      case RsWhile loop:
        return $"{Label(loop.Label)}while {Condition(loop.Condition, indent, column + Label(loop.Label).Length + 6)}{Block(loop.Body, indent)}";
      case RsLoop loop:
        return $"{Label(loop.Label)}loop {Block(loop.Body, indent)}";
      case RsFor loop:
        return $"{Label(loop.Label)}for {loop.Pattern} in {Expression(loop.Iterator, indent, column + 8 + loop.Pattern.Length, 2)} {Block(loop.Body, indent)}";
      case RsLabeledBlock labeled:
        return $"{Label(labeled.Label)}{Block(labeled.Body, indent)}";
      case RsBlockExpression block:
        return Block(block.Block, indent);
      case RsMatch choice: {
        var text = new StringBuilder("match ").Append(Expression(choice.Scrutinee, indent, column + 6, 2)).Append(" {\n");
        foreach (var arm in choice.Arms) {
          var head = string.Join(" | ", arm.Patterns) + " => ";
          text.Append(inner).Append(head);
          if (arm.Body is RsBlockExpression { Block: var block })
            text.Append(Block(block, indent + Indent)).Append('\n');
          else
            text.Append(Expression(arm.Body, indent + Indent, indent + Indent + head.Length, 1)).Append(",\n");
        }

        return text.Append(pad).Append('}').ToString();
      }
      default:
        return Flat(expression) ?? throw new ArgumentException($"There is no Rust expression '{expression.GetType().Name}'.", nameof(expression));
    }
  }

  /// <summary>An <c>if</c> over several lines; an <c>else</c> that is another <c>if</c> follows as <c>else if</c>.</summary>
  private static string If(RsIf choice, int indent, int column) {
    var text = new StringBuilder("if ").Append(Condition(choice.Condition, indent, column + 3)).Append(Block(choice.Then, indent));
    switch (choice.Else) {
      case RsIf next:
        text.Append(" else ").Append(If(next, indent, column));
        break;
      case RsBlockExpression { Block: var block }:
        text.Append(" else ").Append(Block(block, indent));
        break;
    }

    return text.ToString();
  }

  /// <summary>
  /// The condition of an <c>if</c> or a <c>while</c> and what separates it from the block: a space,
  /// or, where the condition takes several lines and its last one is more than closing brackets, a
  /// line break, the block's brace standing alone at the indentation (rustfmt's <c>last_line_extendable</c>).
  /// </summary>
  private static string Condition(RsExpression condition, int indent, int column) {
    var text = Expression(condition, indent, column, 2);
    var last = text[(text.LastIndexOf('\n') + 1)..].Trim();
    return text.Contains('\n') && last.Any(c => c is not (')' or ']' or '}' or '?' or '>'))
      ? text + "\n" + new string(' ', indent)
      : text + " ";
  }

  /// <summary>
  /// Two operands of a binary operator as rustfmt's <c>rewrite_pair</c> writes them where they do not
  /// fit one line: the right one after the left one's last line where it is one line, or its first
  /// line opens a block, and fits; <see langword="null"/> otherwise, for one operand on each line.
  /// </summary>
  private static string? Paired(RsExpression left, string op, RsExpression right, int indent, int column, int trailing) {
    var lhs = Expression(left, indent, column);
    var lastLine = lhs[(lhs.LastIndexOf('\n') + 1)..];
    var lastColumn = lhs.Contains('\n') ? lastLine.Length : column + lhs.Length;
    var rhs = Expression(right, indent, lastColumn + op.Length + 2, trailing);
    var firstLine = rhs.Split('\n')[0];
    var sameLine = !rhs.Contains('\n') || lhs.Length <= Indent || firstLine.EndsWith('{');
    return sameLine && lastColumn + op.Length + 2 + firstLine.Length + (rhs.Contains('\n') ? 0 : trailing) <= MaxWidth ? $"{lhs} {op} {rhs}" : null;
  }

  /// <summary>A block whose braces stand at an indentation: <c>{}</c> when empty.</summary>
  private static string Block(RsBlock block, int indent) {
    if (block.Statements.Count == 0 && block.Tail is null)
      return "{}";

    var text = new StringBuilder("{\n");
    BlockBody(text, block, indent + Indent);
    return text.Append(new string(' ', indent)).Append('}').ToString();
  }

  /// <summary>
  /// Arguments whose last one ends in a block and whose others fit on the line: the last one
  /// overflows the call, starting on its line and closing at the call's indentation, as rustfmt lets
  /// it; <see langword="null"/> for any other arguments.
  /// </summary>
  private static string? Overflowed(IReadOnlyList<RsExpression> arguments, int indent, int column) {
    if (arguments.Count == 0 || !Overflows(arguments[^1], arguments.Count))
      return null;

    var others = arguments.Take(arguments.Count - 1).Select(Flat).ToList();
    if (others.Any(other => other is null))
      return null;

    var head = string.Concat(others.Select(other => other + ", "));
    var last = Expression(arguments[^1], indent, column + head.Length, 1);
    return column + head.Length + last.Split('\n')[0].Length <= MaxWidth ? head + last : null;
  }

  /// <summary>
  /// Whether the last of some arguments may overflow the call (rustfmt's <c>can_be_overflowed_expr</c>):
  /// a block or a control-flow expression; a call, a macro or an array only as the one argument, never a method chain; a
  /// prefix operator, a borrow or a cast as its operand may.
  /// </summary>
  private static bool Overflows(RsExpression last, int count) => last switch {
    RsIf or RsMatch or RsWhile or RsLoop or RsFor or RsLabeledBlock or RsBlockExpression => true,
    RsCall or RsMacro or RsArray => count == 1,
    RsUnary unary => Overflows(unary.Operand, count),
    RsReference reference => Overflows(reference.Operand, count),
    RsCast cast => Overflows(cast.Operand, count),
    _ => false
  };

  /// <summary>Arguments one on each line, each followed by a comma, the closing parenthesis back at the indentation.</summary>
  private static string VerticalArguments(IReadOnlyList<RsExpression> arguments, int indent) {
    if (arguments.Count == 0)
      return "";

    var inner = new string(' ', indent + Indent);
    var text = new StringBuilder();
    foreach (var argument in arguments)
      text.Append('\n').Append(inner).Append(Expression(argument, indent + Indent, indent + Indent, 1)).Append(',');
    return text.Append('\n').Append(new string(' ', indent)).ToString();
  }

  /// <summary>The operands of a chain of one binary operator, left-associated, with the operator in front of each but the first: rustfmt breaks a chain of one operator, not of one precedence.</summary>
  private static void Flatten(RsExpression expression, string op, List<(string?, RsExpression)> operands) {
    if (expression is RsBinary binary && binary.Operator == op) {
      Flatten(binary.Left, op, operands);
      operands.Add((binary.Operator, binary.Right));
      return;
    }

    operands.Add((null, expression));
  }

  /// <summary>
  /// How tightly an operator binds, from the Rust Reference's table of operator precedence: the
  /// higher, the tighter. A prefix operator, <c>as</c> and the postfix forms bind tighter than any binary operator.
  /// </summary>
  /// <param name="op">A binary operator, <c>..</c>, or <c>=</c> and the compound assignments.</param>
  /// <returns>The precedence.</returns>
  public static int Precedence(string op) => op switch {
    "*" or "/" or "%" => 11,
    "+" or "-" => 10,
    "<<" or ">>" => 9,
    "&" => 8,
    "^" => 7,
    "|" => 6,
    "==" or "!=" or "<" or ">" or "<=" or ">=" => 5,
    "&&" => 4,
    "||" => 3,
    ".." or "..=" => 2,
    _ => 1
  };

  /// <summary>A string literal: in double quotes, with a backslash before a quote and a backslash.</summary>
  public static string Quoted(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

}
