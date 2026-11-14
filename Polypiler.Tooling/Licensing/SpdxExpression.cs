using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Tooling.Licensing;

/// <summary>What the policy lets a licence be used for, worst first, so that a larger value is a better status.</summary>
public enum LicenceStatus {

  /// <summary>Neither a source nor an oracle.</summary>
  Denied = 0,

  /// <summary>An oracle under rung 2 of the sourcing ladder, never a source of code that ships.</summary>
  ReferenceOnly = 1,

  /// <summary>A source of code that ships.</summary>
  Allowed = 2

}

/// <summary>What a scan checks files for.</summary>
public enum LicenceMode {

  /// <summary>Files read as sources or oracles: a reference-only licence passes.</summary>
  Corpus,

  /// <summary>Files that are shipped: only an allowed licence passes, and restricted assets are refused.</summary>
  Distribution

}

/// <summary>
/// An SPDX licence expression (SPDX specification, annex "SPDX license expressions"): licence
/// identifiers joined by <c>AND</c> and <c>OR</c>, a licence followed by <c>WITH</c> and an exception,
/// and parentheses. <c>WITH</c> binds tighter than <c>AND</c>, and <c>AND</c> tighter than <c>OR</c>;
/// the operators are matched case-sensitively, as the specification asks. An identifier is made of
/// letters, digits, <c>.</c> and <c>-</c>, optionally prefixed by a <c>DocumentRef-…:</c> and followed
/// by <c>+</c>.
/// </summary>
public abstract record SpdxExpression {

  private static readonly Regex _identifier = new(@"^(DocumentRef-[A-Za-z0-9.\-]+:)?[A-Za-z0-9.\-]+\+?$", RegexOptions.CultureInvariant);

  /// <summary>The licence identifiers of the expression, in reading order, exceptions excluded.</summary>
  public abstract IEnumerable<string> Licences { get; }

  /// <summary>
  /// The status of the expression: <c>OR</c> takes the better operand, <c>AND</c> the worse, and
  /// <c>WITH</c> the licence's own status when the exception is accepted, <see cref="LicenceStatus.Denied"/> otherwise.
  /// </summary>
  /// <param name="licence">The status of one licence identifier.</param>
  /// <param name="exception">Whether an exception identifier is accepted.</param>
  public abstract LicenceStatus Evaluate(Func<string, LicenceStatus> licence, Func<string, bool> exception);

  /// <summary>Reads an expression.</summary>
  /// <param name="text">The expression.</param>
  /// <exception cref="FormatException">The text is not an SPDX expression; the message says why.</exception>
  public static SpdxExpression Parse(string text) {
    var parser = new Parser(Tokenize(text));
    var expression = parser.Or();
    if (parser.Next is { } extra)
      throw new FormatException($"unexpected '{extra}'");
    return expression;
  }

  private static List<string> Tokenize(string text) {
    var tokens = new List<string>();
    var start = -1;
    for (var index = 0; index <= text.Length; ++index) {
      var character = index < text.Length ? text[index] : ' ';
      var separator = char.IsWhiteSpace(character) || character is '(' or ')';
      if (!separator) {
        if (start < 0)
          start = index;
        continue;
      }

      if (start >= 0) {
        tokens.Add(text[start..index]);
        start = -1;
      }

      if (character is '(' or ')')
        tokens.Add(character.ToString());
    }

    if (tokens.Count == 0)
      throw new FormatException("the expression is empty");
    return tokens;
  }

  private static bool IsOperator(string token) => token is "AND" or "OR" or "WITH" or "(" or ")";

  /// <summary>A precedence-climbing parser over the tokens: or := and ("OR" and)*, and := with ("AND" with)*, with := primary ("WITH" id)?.</summary>
  private sealed class Parser(List<string> tokens) {

    private int _position;

    public string? Next => this._position < tokens.Count ? tokens[this._position] : null;

    public SpdxExpression Or() {
      var left = this.And();
      while (this.Next == "OR") {
        ++this._position;
        left = new SpdxOr(left, this.And());
      }

      return left;
    }

    private SpdxExpression And() {
      var left = this.With();
      while (this.Next == "AND") {
        ++this._position;
        left = new SpdxAnd(left, this.With());
      }

      return left;
    }

    private SpdxExpression With() {
      var operand = this.Primary();
      if (this.Next != "WITH")
        return operand;
      if (operand is not SpdxLicence licence)
        throw new FormatException("'WITH' must follow a licence identifier, not a parenthesized expression");

      ++this._position;
      return new SpdxWith(licence, this.Identifier("an exception"));
    }

    private SpdxExpression Primary() {
      if (this.Next != "(")
        return new SpdxLicence(this.Identifier("a licence"));

      ++this._position;
      var inner = this.Or();
      if (this.Next != ")")
        throw new FormatException(this.Next is null ? "missing ')'" : $"expected ')' but found '{this.Next}'");
      ++this._position;
      return inner;
    }

    private string Identifier(string what) {
      var token = this.Next;
      var previous = this._position > 0 ? $" after '{tokens[this._position - 1]}'" : "";
      if (token is null)
        throw new FormatException($"expected {what}{previous} but the expression ends");
      if (IsOperator(token))
        throw new FormatException($"expected {what}{previous} but found '{token}'");
      if (!_identifier.IsMatch(token))
        throw new FormatException($"'{token}' is not an SPDX identifier");

      ++this._position;
      return token;
    }

  }

}

/// <summary>One licence identifier.</summary>
/// <param name="Id">The identifier, e.g. <c>MIT</c>.</param>
public sealed record SpdxLicence(string Id) : SpdxExpression {

  /// <inheritdoc />
  public override IEnumerable<string> Licences => [this.Id];

  /// <inheritdoc />
  public override LicenceStatus Evaluate(Func<string, LicenceStatus> licence, Func<string, bool> exception) => licence(this.Id);

  /// <inheritdoc />
  public override string ToString() => this.Id;

}

/// <summary>A licence with an exception: <c>GPL-2.0-only WITH Classpath-exception-2.0</c>.</summary>
/// <param name="Licence">The licence.</param>
/// <param name="Exception">The exception identifier.</param>
public sealed record SpdxWith(SpdxLicence Licence, string Exception) : SpdxExpression {

  /// <inheritdoc />
  public override IEnumerable<string> Licences => this.Licence.Licences;

  /// <inheritdoc />
  public override LicenceStatus Evaluate(Func<string, LicenceStatus> licence, Func<string, bool> exception)
    => exception(this.Exception) ? this.Licence.Evaluate(licence, exception) : LicenceStatus.Denied;

  /// <inheritdoc />
  public override string ToString() => $"{this.Licence} WITH {this.Exception}";

}

/// <summary>Both operands apply: the worse status wins.</summary>
/// <param name="Left">The first operand.</param>
/// <param name="Right">The second operand.</param>
public sealed record SpdxAnd(SpdxExpression Left, SpdxExpression Right) : SpdxExpression {

  /// <inheritdoc />
  public override IEnumerable<string> Licences => this.Left.Licences.Concat(this.Right.Licences);

  /// <inheritdoc />
  public override LicenceStatus Evaluate(Func<string, LicenceStatus> licence, Func<string, bool> exception)
    => (LicenceStatus)Math.Min((int)this.Left.Evaluate(licence, exception), (int)this.Right.Evaluate(licence, exception));

  /// <inheritdoc />
  public override string ToString() => $"{Operand(this.Left)} AND {Operand(this.Right)}";

  private static string Operand(SpdxExpression operand) => operand is SpdxOr ? $"({operand})" : operand.ToString();

}

/// <summary>Either operand may be chosen: the better status wins.</summary>
/// <param name="Left">The first operand.</param>
/// <param name="Right">The second operand.</param>
public sealed record SpdxOr(SpdxExpression Left, SpdxExpression Right) : SpdxExpression {

  /// <inheritdoc />
  public override IEnumerable<string> Licences => this.Left.Licences.Concat(this.Right.Licences);

  /// <inheritdoc />
  public override LicenceStatus Evaluate(Func<string, LicenceStatus> licence, Func<string, bool> exception)
    => (LicenceStatus)Math.Max((int)this.Left.Evaluate(licence, exception), (int)this.Right.Evaluate(licence, exception));

  /// <inheritdoc />
  public override string ToString() => $"{this.Left} OR {this.Right}";

}
