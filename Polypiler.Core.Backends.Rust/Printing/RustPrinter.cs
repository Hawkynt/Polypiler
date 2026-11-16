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
  private const int StructLiteralWidth = 18;
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
        var head = $"{pad}{Visibility(constant.Visibility)}{(constant.IsStatic ? "static" : "const")} {constant.Name}: {constant.TypeName} =";
        text.Append(Right(head, constant.Value, indent)).Append(";\n");
        break;
      }
      case RsModuleDeclaration module:
        text.Append(pad).Append(Visibility(module.Visibility)).Append("mod ").Append(module.Name).Append(";\n");
        break;
      case RsUse use:
        text.Append(pad).Append(Visibility(use.Visibility)).Append("use ").Append(use.Path).Append(";\n");
        break;
      case RsEnum enumeration:
        text.Append(pad).Append(Visibility(enumeration.Visibility)).Append("enum ").Append(enumeration.Name).Append(enumeration.Variants.Count == 0 ? " {}\n" : " {\n");
        foreach (var variant in enumeration.Variants)
          text.Append(pad).Append(' ', Indent).Append(variant.IsDefault ? "#[default]\n" + pad + new string(' ', Indent) : "").Append(variant.Name).Append(variant.Discriminant is { } value ? " = " + value : "").Append(",\n");
        if (enumeration.Variants.Count > 0)
          text.Append(pad).Append("}\n");
        break;
      case RsInlineModule module:
        text.Append(pad).Append("mod ").Append(module.Name).Append(" {\n");
        Items(text, module.Items, indent + Indent, false);
        text.Append(pad).Append("}\n");
        break;
      case RsStruct { Fields.Count: 0 } unit:
        text.Append(pad).Append(Visibility(unit.Visibility)).Append("struct ").Append(unit.Name).Append(";\n");
        break;
      case RsStruct tuple when tuple.Fields.All(field => field.Name.Length == 0):
        text.Append(pad).Append(Visibility(tuple.Visibility)).Append("struct ").Append(tuple.Name).Append('(')
          .Append(string.Join(", ", tuple.Fields.Select(field => Visibility(field.Visibility) + field.TypeName))).Append(");\n");
        break;
      case RsStruct record:
        text.Append(pad).Append(Visibility(record.Visibility)).Append("struct ").Append(record.Name).Append(record.Generics).Append(" {\n");
        foreach (var field in record.Fields)
          text.Append(pad).Append(' ', Indent).Append(Visibility(field.Visibility)).Append(field.Name).Append(": ").Append(field.TypeName).Append(",\n");
        text.Append(pad).Append("}\n");
        break;
      case RsTrait trait:
        text.Append(pad).Append(Visibility(trait.Visibility)).Append("trait ").Append(trait.Name)
          .Append(trait.Supertraits.Count == 0 ? "" : ": " + string.Join(" + ", trait.Supertraits))
          .Append(trait.Items.Count == 0 ? " {}\n" : " {\n");
        if (trait.Items.Count > 0) {
          Items(text, trait.Items, indent + Indent, false);
          text.Append(pad).Append("}\n");
        }

        break;
      case RsImpl implementation:
        text.Append(pad).Append("impl").Append(implementation.Generics).Append(' ').Append(implementation.Trait is null ? "" : implementation.Trait + " for ").Append(implementation.TypeName)
          .Append(implementation.Items.Count == 0 ? " {}\n" : " {\n");
        if (implementation.Items.Count > 0) {
          Items(text, implementation.Items, indent + Indent, false);
          text.Append(pad).Append("}\n");
        }

        break;
      case RsTypeAlias alias:
        text.Append(pad).Append("type ").Append(alias.Name).Append(" = ").Append(alias.TypeName).Append(";\n");
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
    var parameters = string.Join(", ", function.Parameters.Select(Parameter));
    var empty = function.Body.Statements.Count == 0 && function.Body.Tail is null;
    var close = function.Body.IsDeclaration ? ";" : empty ? " {}" : " {";
    // rustfmt's where clause: the keyword on a line of its own, a clause on each line after it, the body's brace on the next.
    var clauses = function.Where.Count == 0 ? close : $"\n{pad}where\n{string.Concat(function.Where.Select(clause => $"{pad}{new string(' ', Indent)}{clause},\n"))}{pad}{(empty ? $"{{\n{pad}}}" : close.TrimStart())}";
    var line = head + parameters + ")" + result;
    if (line.Length + (function.Where.Count == 0 ? close.Length : 0) <= MaxWidth || function.Parameters.Count == 0)
      text.Append(line).Append(clauses);
    else {
      text.Append(head).Append('\n');
      foreach (var parameter in function.Parameters)
        text.Append(pad).Append(' ', Indent).Append(Parameter(parameter)).Append(",\n");
      text.Append(pad).Append(')').Append(result).Append(clauses);
    }

    if (empty) {
      text.Append('\n');
      return;
    }

    text.Append('\n');
    BlockBody(text, function.Body, indent + Indent);
    text.Append(pad).Append("}\n");
  }

  /// <summary>A parameter: <c>name: T</c>, or the receiver <c>&amp;self</c>, <c>&amp;mut self</c>, <c>self</c> without a type.</summary>
  private static string Parameter(RsParameter parameter) => parameter.TypeName.Length == 0 ? parameter.Name : $"{parameter.Name}: {parameter.TypeName}";

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
    // rustfmt gives the operands of a returned binary operator one column less than the line has.
    RsExpressionStatement { Expression: RsReturn { Value: RsBinary } expression } => Expression(expression, indent, indent, 2) + ";",
    RsExpressionStatement { Expression: var expression } => Expression(expression, indent, indent, 1) + ";",
    RsItemStatement { Item: var item } => NestedItem(item, indent),
    RsComment comment => "// " + comment.Text,
    _ => throw new ArgumentException($"There is no Rust statement '{statement.GetType().Name}'.", nameof(statement))
  };

  /// <summary>An item among the statements of a block, without the indentation of its first line and the line break after its last.</summary>
  private static string NestedItem(RsItem item, int indent) {
    var text = new StringBuilder();
    Item(text, item, indent);
    return text.ToString(indent, text.Length - indent - 1);
  }

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
    // A head over several lines (a chain broken before the operator) goes on from the end of its last one.
    var column = (head.Contains('\n') ? head.Length - head.LastIndexOf('\n') - 1 : indent + head.Length) + 1;
    var flat = Flat(value);
    if (flat is not null && column + flat.Length + 1 <= MaxWidth)
      return head + " " + flat;

    if (flat is not null && !IsBlockLike(value) && indent + Indent + flat.Length + 1 <= MaxWidth)
      return head + "\n" + new string(' ', indent + Indent) + flat;

    // rustfmt's choose_rhs: the value broken on the next line where that takes fewer lines by more
    // than one, or where on the head's line it would open a bracket the next line does not need.
    var same = Expression(value, indent, column, 1);
    if (!IsBlockLike(value) && value is not RsArray and not RsStructLiteral and not RsMacro { Bracketed: true }) {
      var next = Expression(value, indent + Indent, indent + Indent, 1);
      static int Lines(string text) => text.Count(c => c == '\n');
      static bool Opens(string text, char bracket) => text.Split('\n')[0].EndsWith(bracket);
      if (Lines(same) > Lines(next) + 1 || "([{".Any(bracket => Opens(same, bracket) && !Opens(next, bracket)))
        return head + "\n" + new string(' ', indent + Indent) + next;
    }

    return head + " " + same;
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
      case RsStructLiteral literal: {
        var fields = literal.Fields.Select(field => (field.Name, Value: Flat(field.Value))).ToList();
        if (fields.Any(field => field.Value is null))
          return null;

        var body = string.Join(", ", fields.Select(field => field.Value == field.Name ? field.Name : $"{field.Name}: {field.Value}"));
        return fields.Count == 0 ? literal.TypeName + " {}" : body.Length > StructLiteralWidth ? null : $"{literal.TypeName} {{ {body} }}";
      }
      case RsMethodCall call: {
        var (receiver, links) = Chain(call);
        if (Flat(receiver) is not { } start)
          return null;

        var flat = new StringBuilder(start);
        foreach (var link in links) {
          if (Link(link) is not { } text)
            return null;

          flat.Append(text);
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
        if (Flat(Guarded(choice.Condition)) is not { } condition || Flat(whenTrue) is not { } yes || Flat(whenFalse) is not { } no)
          return null;

        var flat = $"if {condition} {{ {yes} }} else {{ {no} }}";
        return flat.Length > IfElseWidth ? null : flat;
      }
      case RsBlockExpression { Block: { Statements.Count: 0, Tail: null } }:
        return "{}";
      case RsTuple tuple:
        return Arguments(tuple.Elements) is { } items ? $"({items})" : null;
      case RsTry attempt:
        return Flat(attempt.Operand) is { } tried ? tried + "?" : null;
      case RsLetCondition condition:
        return Flat(condition.Value) is { } matched ? $"let {condition.Pattern} = {matched}" : null;
      case RsClosure { Body: not RsBlockExpression } closure:
        return Flat(closure.Body) is { } closureBody ? ClosureHead(closure) + closureBody : null;
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

    // One argument stays on the line wherever the line has room (rustfmt's single-argument rule), but
    // a call within a call keeps to the width of a call's arguments, as rustfmt narrows a nested call.
    if (flat.Count == 1 && !(Called(arguments[0]) && flat[0]!.Length > CallWidth))
      return flat[0];

    var joined = string.Join(", ", flat);
    return joined.Length > CallWidth ? null : joined;
  }

  /// <summary>
  /// A method call as the receiver the chain starts from and the links after it, in order: the calls
  /// and the fields between them, which rustfmt counts as links of the chain as well.
  /// </summary>
  private static (RsExpression Receiver, List<RsExpression> Links) Chain(RsExpression call) {
    var links = new List<RsExpression>();
    RsExpression current = call;
    while (current is RsMethodCall or RsField) {
      links.Add(current);
      current = current is RsMethodCall link ? link.Receiver : ((RsField)current).Target;
    }

    links.Reverse();
    return (current, links);
  }

  /// <summary>
  /// rustfmt's overflow of the last link of a chain: the root and every link but the last on one line,
  /// the last call's arguments broken from there, where they take five lines or more, or no fewer than
  /// the last link on a line of its own would; <see langword="null"/> otherwise.
  /// </summary>
  private static string? Overflowing(string root, List<RsExpression> links, int indent, int column, int trailing) {
    if (links[^1] is not RsMethodCall last)
      return null;

    var prefix = new StringBuilder(root);
    foreach (var link in links.Take(links.Count - 1)) {
      if (Link(link) is not { } text)
        return null;

      prefix.Append(text);
    }

    var head = $"{prefix}.{last.Name}(";
    if (column + head.Length > MaxWidth)
      return null;

    var arguments = Overflowed(last.Arguments, indent, column + head.Length) ?? VerticalArguments(last.Arguments, indent);
    if (!arguments.Contains('\n'))
      return null;

    // The last link's first line has the room the chain has on one line: of more than one link, no more than chain_width.
    var budget = (links.Count == 1 ? MaxWidth - column : Math.Min(MaxWidth - column, ChainWidth)) - prefix.Length;
    if (head.Length - prefix.Length + arguments.Split('\n')[0].Length > budget)
      return null;

    static int Lines(string text) => text.Count(c => c == '\n') + 1;
    var overflowed = Lines(arguments);
    var own = Lines(BrokenLink(last, indent + Indent, indent + Indent, trailing));
    return overflowed >= 5 || own >= overflowed ? head + arguments + ")" : null;
  }

  /// <summary>A link of a chain on one line, <c>.field</c> or <c>.name(arguments)</c>, or <see langword="null"/>.</summary>
  private static string? Link(RsExpression link) => link switch {
    RsField field => "." + field.Name,
    RsMethodCall call when Arguments(call.Arguments) is { } arguments => $".{call.Name}({arguments})",
    _ => null
  };

  /// <summary>A link of a chain starting at a column: a field, or a call whose arguments are on its line, overflow it or stand one on each line.</summary>
  private static string BrokenLink(RsExpression link, int indent, int column, int trailing) {
    if (link is RsField field)
      return "." + field.Name;

    var call = (RsMethodCall)link;
    var head = $".{call.Name}(";
    var arguments = Arguments(call.Arguments);
    return head + (arguments is not null && column + head.Length + arguments.Length + 1 + trailing <= MaxWidth
      ? arguments
      : Overflowed(call.Arguments, indent, column + head.Length) ?? VerticalArguments(call.Arguments, indent)) + ")";
  }

  private static string Label(string? label) => label is null ? "" : $"'{label}: ";

  /// <summary>An expression over several lines, the way rustfmt breaks each kind.</summary>
  private static string Broken(RsExpression expression, int indent, int column, int trailing) {
    var pad = new string(' ', indent);
    var inner = new string(' ', indent + Indent);
    switch (expression) {
      case RsCall call: {
        var callee = Expression(call.Callee, indent, column);
        // A callee over several lines, the call of a call's result: the arguments go on from its last line where they fit there.
        if (callee.Contains('\n') && Arguments(call.Arguments) is { } after && callee.Length - callee.LastIndexOf('\n') - 1 + after.Length + 2 + trailing <= MaxWidth)
          return callee + "(" + after + ")";

        return callee + "(" + (Overflowed(call.Arguments, indent, column + callee.Length + 1, true) ?? VerticalArguments(call.Arguments, indent)) + ")";
      }
      case RsMacro { Bracketed: true } macro:
        return macro.Name + "!" + Expression(macro.Arguments[0], indent, column + macro.Name.Length + 1, trailing);
      case RsMacro macro:
        return macro.Name + "!(" + (Overflowed(macro.Arguments, indent, column + macro.Name.Length + 2) ?? VerticalArguments(macro.Arguments, indent)) + ")";
      case RsReference reference: {
        var prefix = reference.Mutable ? "&mut " : "&";
        return prefix + Expression(reference.Operand, indent, column + prefix.Length, trailing);
      }
      case RsStructLiteral literal: {
        var text = new StringBuilder(literal.TypeName).Append(" {");
        foreach (var field in literal.Fields) {
          var head = field.Value is RsPath { Name: var name } && name == field.Name ? field.Name : field.Name + ": ";
          text.Append('\n').Append(inner).Append(head);
          if (head != field.Name)
            text.Append(Expression(field.Value, indent + Indent, indent + Indent + head.Length, 1));
          text.Append(',');
        }

        return text.Append('\n').Append(pad).Append('}').ToString();
      }
      case RsMethodCall or RsField when expression is RsMethodCall || ((RsField)expression).Target is RsMethodCall or RsField || Expression(((RsField)expression).Target, indent, column).Contains('\n'): {
        var call = expression;
        // The root on the first line, then one link on each line one step in - or at the root's own
        // indentation where the root ends a block of lines.
        var (receiver, links) = Chain(call);
        var root = Expression(receiver, indent, column);
        if (!root.Contains('\n') && Overflowing(root, links, indent, column, trailing) is { } overflowing)
          return overflowing;

        var text = new StringBuilder(root);
        // After a root of several lines whose last line only closes brackets the links stand at its indentation (rustfmt's last_line_extendable).
        var rootEnd = root[(root.LastIndexOf('\n') + 1)..].Trim();
        var linkIndent = root.Contains('\n') && rootEnd.All(c => c is ')' or ']' or '}' or '?' or '>') ? indent : indent + Indent;
        var skip = 0;
        // rustfmt joins links to a root no wider than an indentation less the root's offset from it.
        while (skip < links.Count && !text.ToString().Contains('\n') && text.Length <= Indent - (column - indent)) {
          var written = BrokenLink(links[skip], indent, column + text.Length, skip == links.Count - 1 ? trailing : 0);
          text.Append(written);
          ++skip;
          // A link over several lines ends a block of lines: the links after it stand at the root's indentation.
          if (written.Contains('\n'))
            linkIndent = indent;
        }

        for (var index = skip; index < links.Count; ++index)
          text.Append('\n').Append(' ', linkIndent).Append(BrokenLink(links[index], linkIndent, linkIndent, index == links.Count - 1 ? trailing : 0));

        return text.ToString();
      }
      case RsIndex index: {
        // rustfmt's rewrite_index: the index on the target's line where it fits, else on the next line one step in.
        var target = Expression(index.Target, indent, column);
        if (!target.Contains('\n') && Flat(index.Index) is { } at) {
          if (column + target.Length + at.Length + 2 + trailing <= MaxWidth)
            return $"{target}[{at}]";
          if (indent + Indent + at.Length + 2 + trailing <= MaxWidth)
            return $"{target}\n{inner}[{at}]";
        }

        var opened = (target.Contains('\n') ? 0 : column) + target[(target.LastIndexOf('\n') + 1)..].Length + 1;
        return target + "[" + Expression(index.Index, indent, opened, trailing + 1) + "]";
      }
      case RsBinary binary: {
        var operands = new List<(string? Operator, RsExpression Operand)>();
        Flatten(binary, binary.Operator, operands);
        var text = new StringBuilder(Expression(operands[0].Operand, indent, column));
        foreach (var (op, operand) in operands.Skip(1)) {
          var head = op + " ";
          // rustfmt's rewrite_pairs_multiline: an operand goes on the line before it where that line
          // is no longer than the indentation of a new one would be, so as to leave no orphan.
          var written = text.ToString();
          var multiline = written.Contains('\n');
          var last = written[(written.LastIndexOf('\n') + 1)..];
          if (last.Length + (multiline ? 0 : column) <= indent + Indent) {
            var at = (multiline ? last.Length : column + last.Length) + head.Length + 1;
            text.Append(' ').Append(head).Append(Expression(operand, indent, at));
            continue;
          }

          text.Append('\n').Append(inner).Append(head).Append(Expression(operand, indent + Indent, indent + Indent + head.Length));
        }

        return text.ToString();
      }
      case RsUnary unary:
        return unary.Operator + Expression(unary.Operand, indent, column + unary.Operator.Length, trailing);
      case RsCast cast: {
        var operand = Expression(cast.Operand, indent, column);
        var last = operand[(operand.LastIndexOf('\n') + 1)..];
        // rustfmt's rewrite_pair measures the last line of an operand of several lines against the width left at the cast's own column.
        var end = column + last.Length + 4 + cast.Target.Length + trailing;
        return end <= MaxWidth ? operand + " as " + cast.Target : operand + "\n" + inner + "as " + cast.Target;
      }
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
      case RsField field:
        return Expression(field.Target, indent, column) + "." + field.Name;
      case RsRange range:
        return (range.Start is null ? "" : Expression(range.Start, indent, column)) + (range.Inclusive ? "..=" : "..") + (range.End is null ? "" : Expression(range.End, indent, column));
      case RsArray { Count: { } count } repeated:
        // [value; count] over several lines: the value and the count each on a line of their own.
        return "[\n" + inner + Expression(repeated.Elements[0], indent + Indent, indent + Indent, 1) + ";\n"
          + inner + Expression(count, indent + Indent, indent + Indent, 0) + "\n" + pad + "]";
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
      case RsClosure closure:
        return Closure(closure, indent, column, trailing);
      case RsTuple tuple:
        return "(" + VerticalArguments(tuple.Elements, indent) + ")";
      case RsTry attempt:
        return Expression(attempt.Operand, indent, column, trailing + 1) + "?";
      case RsLetCondition condition:
        return $"let {condition.Pattern} = " + Expression(condition.Value, indent, column + condition.Pattern.Length + 7, trailing);
      case RsWhile loop:
        return $"{Label(loop.Label)}while {Condition(loop.Condition, indent, column + Label(loop.Label).Length + 6, column)}{Block(loop.Body, indent)}";
      case RsLoop loop:
        return $"{Label(loop.Label)}loop {Block(loop.Body, indent)}";
      case RsFor loop:
        return $"{Label(loop.Label)}for {loop.Pattern} in {Expression(loop.Iterator, indent, column + 8 + loop.Pattern.Length, 2)} {Block(loop.Body, indent)}";
      case RsLabeledBlock labeled:
        return $"{Label(labeled.Label)}{Block(labeled.Body, indent)}";
      case RsUnsafe block:
        return "unsafe " + Block(block.Block, indent);
      case RsBlockExpression block:
        return Block(block.Block, indent);
      case RsMatch choice: {
        var text = new StringBuilder("match ").Append(Condition(choice.Scrutinee, indent, column + 6, column)).Append("{\n");
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

  /// <summary><c>move |a: T, b: U| </c>: what a closure starts with, its parameters typed.</summary>
  private static string ClosureHead(RsClosure closure)
    => (closure.Move ? "move " : "") + "|" + string.Join(", ", closure.Parameters.Select(Parameter)) + "| ";

  /// <summary>
  /// A closure over several lines (rustfmt's <c>rewrite_closure</c>): a block body as a block; an
  /// expression body that would take several lines stays bare only where it ends in a block itself
  /// (a <c>match</c>, a block, a loop, a struct), any other is put in a block of its own.
  /// </summary>
  private static string Closure(RsClosure closure, int indent, int column, int trailing) {
    var head = ClosureHead(closure);
    if (closure.Body is RsBlockExpression { Block: var block })
      return head + Block(block, indent);

    var body = Expression(closure.Body, indent, column + head.Length, trailing);
    if (!body.Contains('\n') || MultiLineBody(closure.Body))
      return head + body;

    return head + Block(new RsBlock([], closure.Body), indent);
  }

  /// <summary>Whether the body of a closure may take several lines without a block of its own (rustfmt's <c>allow_multi_line</c>).</summary>
  private static bool MultiLineBody(RsExpression body) => body switch {
    RsMatch or RsBlockExpression or RsLoop or RsLabeledBlock or RsStructLiteral => true,
    RsReference reference => MultiLineBody(reference.Operand),
    RsUnary unary => MultiLineBody(unary.Operand),
    RsCast cast => MultiLineBody(cast.Operand),
    _ => false
  };

  /// <summary>An <c>if</c> over several lines; an <c>else</c> that is another <c>if</c> follows as <c>else if</c>.</summary>
  private static string If(RsIf choice, int indent, int column) {
    var text = new StringBuilder("if ").Append(Condition(choice.Condition, indent, column + 3, column)).Append(Block(choice.Then, indent));
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
  /// line break, the block's brace standing alone at the indentation (rustfmt's <c>last_line_extendable</c>),
  /// as also where that last line is indented past the column the keyword starts at (<c>last_line_offsetted</c>).
  /// </summary>
  private static string Condition(RsExpression condition, int indent, int column, int start) {
    var text = Expression(Guarded(condition), indent, column, 2);
    var line = text[(text.LastIndexOf('\n') + 1)..];
    var last = line.Trim();
    // rustfmt's last_line_extendable and last_line_offsetted: the brace follows a last line of closing
    // delimiters only, and only where that line is not indented past the keyword.
    return text.Contains('\n') && (last.Any(c => c is not (')' or ']' or '}' or '?' or '>')) || line.Length - line.TrimStart().Length > start)
      ? text + "\n" + new string(' ', indent)
      : text + " ";
  }

  /// <summary>
  /// A condition of an <c>if</c> or a <c>while</c>, or a scrutinee, with every struct literal it holds outside
  /// parentheses, brackets and arguments parenthesized: <c>p.x == (P { x: 0, y: 0 }).x</c>. Rust reads the brace
  /// of a struct literal there as the opening of the block.
  /// </summary>
  private static RsExpression Guarded(RsExpression condition) => condition switch {
    RsStructLiteral literal => new RsParenthesized(literal) { Type = literal.Type },
    RsBinary binary => binary with { Left = Guarded(binary.Left), Right = Guarded(binary.Right) },
    RsUnary unary => unary with { Operand = Guarded(unary.Operand) },
    RsField field => field with { Target = Guarded(field.Target) },
    RsMethodCall call => call with { Receiver = Guarded(call.Receiver) },
    RsCast cast => cast with { Operand = Guarded(cast.Operand) },
    RsIndex index => index with { Target = Guarded(index.Target) },
    _ => condition
  };

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
  private static string? Overflowed(IReadOnlyList<RsExpression> arguments, int indent, int column, bool function = false) {
    if (arguments.Count == 0 || !Overflows(arguments[^1], arguments.Count, function))
      return null;

    var others = arguments.Take(arguments.Count - 1).Select(Flat).ToList();
    if (others.Any(other => other is null))
      return null;

    var head = string.Concat(others.Select(other => other + ", "));
    var last = Called(arguments[^1]) && Flat(arguments[^1]) is { Length: > CallWidth } ? Opened(arguments[^1], indent, column + head.Length) : Expression(arguments[^1], indent, column + head.Length, 1);
    // A closure that stays on the line yet makes the arguments wider than a call's may be takes a block body.
    if (arguments[^1] is RsClosure closure && !last.Contains('\n') && head.Length + last.Length > CallWidth)
      last = ClosureHead(closure) + (closure.Body is RsBlockExpression { Block: var body } ? Block(body, indent) : Block(new RsBlock([], closure.Body), indent));
    var first = last.Split('\n')[0];
    // A control-flow expression overflows only with its condition on its first line (rustfmt's try_overflow_last_item).
    if (arguments[^1] is RsIf or RsWhile or RsMatch or RsFor && last.Contains('\n') && !first.EndsWith('{'))
      return null;
    // Nor where the condition itself takes several lines, an if in the condition of an if.
    var leading = arguments[^1] switch { RsIf choice => choice.Condition, RsWhile loop => loop.Condition, RsMatch match => match.Scrutinee, _ => null };
    if (leading is not null && Flat(leading) is null)
      return null;
    // A control-flow expression overflows with its first line within the width of a call's arguments.
    if (leading is not null && last.Contains('\n') && first.Length > CallWidth)
      return null;

    return column + head.Length + first.Length <= MaxWidth ? head + last : null;
  }

  /// <summary>
  /// Whether the last of some arguments may overflow the call (rustfmt's <c>can_be_overflowed_expr</c>):
  /// a block or a control-flow expression; a call, a macro or an array only as the one argument; a
  /// method chain only as the one argument of a function's call, never of a method's or a macro's; a
  /// prefix operator, a borrow, a cast or a <c>?</c> as its operand may.
  /// </summary>
  private static bool Overflows(RsExpression last, int count, bool function = false) => last switch {
    RsIf or RsMatch or RsWhile or RsLoop or RsFor or RsLabeledBlock or RsBlockExpression or RsClosure => true,
    RsCall or RsMacro or RsArray or RsStructLiteral => count == 1,
    RsMethodCall or RsField => function && count == 1,
    RsTry attempt => Overflows(attempt.Operand, count, function),
    RsUnary unary => Overflows(unary.Operand, count, function),
    RsReference reference => Overflows(reference.Operand, count, function),
    RsCast cast => Overflows(cast.Operand, count, function),
    _ => false
  };

  /// <summary>Whether an argument is a call, a macro or the borrow of one: what rustfmt narrows as a nested call.</summary>
  private static bool Called(RsExpression argument) => argument switch {
    RsCall or RsMacro { Bracketed: false } => true,
    RsReference reference => Called(reference.Operand),
    _ => false
  };

  /// <summary>A nested call too wide for a call's arguments, broken even where the line would hold it.</summary>
  private static string Opened(RsExpression argument, int indent, int column) => argument switch {
    RsReference reference => (reference.Mutable ? "&mut " : "&") + Opened(reference.Operand, indent, column + (reference.Mutable ? 5 : 1)),
    _ => Broken(argument, indent, column, 1)
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
  public static string Quoted(string value) {
    var text = new StringBuilder("\"");
    foreach (var c in value)
      text.Append(c switch {
        '\\' => "\\\\",
        '"' => "\\\"",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ when c < ' ' || c == '\u007f' => $"\\u{{{(int)c:x}}}",
        _ => c.ToString()
      });

    return text.Append('"').ToString();
  }

}
