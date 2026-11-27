using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Kotlin.Syntax;

/// <summary>Short constructors of the nodes the writer and the helpers build most often.</summary>
internal static class Kt {

  public static KtName Name(string name) => new(name);

  public static KtLiteral Literal(string text) => new(text);

  public static KtLiteral Int(long value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

  public static KtLiteral True { get; } = new("true");

  public static KtLiteral False { get; } = new("false");

  public static KtLiteral Null { get; } = new("null");

  public static KtBinary Binary(KtExpression left, string op, KtExpression right) => new(left, op, right);

  public static KtMember Member(KtExpression receiver, string name) => new(receiver, name);

  public static KtMember Member(string receiver, string name) => new(new KtName(receiver), name);

  public static KtCall Call(string function, params IReadOnlyList<KtExpression> arguments) => new(new KtName(function), arguments);

  public static KtCall Call(KtExpression callee, params IReadOnlyList<KtExpression> arguments) => new(callee, arguments);

  /// <summary><c>receiver.name(arguments)</c>.</summary>
  public static KtCall Method(KtExpression receiver, string name, params IReadOnlyList<KtExpression> arguments) => new(new KtMember(receiver, name), arguments);

  public static KtNamedType Type(string name) => new(name);

  public static KtNamedType Generic(string name, params KtType[] arguments) => new(name, arguments);

  public static KtPrefix Not(KtExpression operand) => new("!", operand);

  public static KtPrefix Negate(KtExpression operand) => new("-", operand);

  public static KtBlock Block(params IEnumerable<KtStatement> statements) => new([.. statements]);

  public static KtReturn Return(KtExpression value) => new(value);

  /// <summary><c>throw Class("message")</c>.</summary>
  public static KtThrow Throw(string type, string message) => new(Call(type, new KtString(message)));

  public static KtParameter Parameter(string name, KtType type) => new(name, type);

  /// <summary>A private function of the file with a block body, marked as a helper.</summary>
  public static KtFunction Helper(string name, IReadOnlyList<KtParameter> parameters, KtType? result, params IEnumerable<KtStatement> body)
    => new(name, parameters, result, Block(body)) { Modifiers = ["private"], IsHelper = true };

}
