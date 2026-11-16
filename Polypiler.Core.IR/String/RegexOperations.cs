using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Polypiler.Core.IR;

/// <summary>
/// The regular expressions of the IR (#1399): ECMA-262's <c>RegExp.prototype.test</c> of a pattern
/// and flags as ECMAScript writes them, and the one conversion of such a pattern to the syntax of .NET
/// that matches exactly what ECMAScript matches, which the interpreter and the C# backend share.
/// </summary>
/// <remarks>
/// The subset: literal characters, the escapes <c>\d \w \s \D \W \S \t \n \v \f \r \0 \xHH \uHHHH \cX</c>
/// and those of a character that is no letter or digit, classes with ranges and negation, <c>.</c>,
/// <c>^</c>, <c>$</c>, the quantifiers <c>* + ? {n} {n,} {n,m}</c> greedy and lazy, groups <c>( )</c> and
/// <c>(?: )</c>, alternation, and the flag <c>i</c> for patterns whose own characters are ASCII. The
/// conversion writes every set of characters out, so that nothing depends on what .NET's classes,
/// anchors or case folding mean: <c>$</c> is <c>\z</c>, which does not match before a final line feed
/// as .NET's <c>$</c> does; <c>.</c> leaves out the four line terminators of ECMAScript, not only the
/// line feed; <c>\s</c> is ECMAScript's white space and line terminators, U+FEFF among them;
/// <c>\d</c> and <c>\w</c> are ASCII; and the flag <c>i</c> adds the other case of each ASCII letter,
/// which is what ECMAScript's <c>Canonicalize</c> without <c>u</c> does to a pattern of ASCII characters
/// (§22.2.2.7.3) - no <c>RegexOptions.IgnoreCase</c>, which would also match the Kelvin sign for a
/// <c>k</c>. The flags <c>g</c> and <c>y</c> carry state between calls, <c>m s u v d</c> change the
/// meaning, and backreferences, lookaround, named groups, <c>\b</c> and the property escapes are
/// outside the subset: each is refused by name.
/// </remarks>
public static class RegexOperations {

  /// <summary>The options every converted pattern is compiled with.</summary>
  public const RegexOptions Options = RegexOptions.CultureInvariant;

  /// <summary>The regular-expression operations of the IR.</summary>
  public static IReadOnlyCollection<IrIntrinsicOp> Operations { get; } = [IrIntrinsicOp.RegexTest];

  private static readonly ConcurrentDictionary<(string Pattern, string Flags), Regex> _compiled = new();

  /// <summary>Whether an operation is one of the regular-expression operations.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for <see cref="IrIntrinsicOp.RegexTest"/>.</returns>
  public static bool IsRegex(IrIntrinsicOp op) => op == IrIntrinsicOp.RegexTest;

  /// <summary>The node that tests a subject against a regular expression.</summary>
  /// <param name="subject">The string tested.</param>
  /// <param name="pattern">The pattern, as ECMAScript writes it between the slashes.</param>
  /// <param name="flags">The flags, as ECMAScript writes them behind the pattern.</param>
  /// <returns>The <c>bool</c> node.</returns>
  public static IrIntrinsic Test(IrExpr subject, string pattern, string flags)
    => new(IrIntrinsicOp.RegexTest, IrType.Bool, [subject, new IrStrConst(pattern), new IrStrConst(flags)]);

  /// <summary>The pattern and the flags of a node of <see cref="IrIntrinsicOp.RegexTest"/> that <see cref="Problem"/> accepts.</summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>Pattern and flags.</returns>
  public static (string Pattern, string Flags) Of(IrIntrinsic intrinsic)
    => (((IrStrConst)intrinsic.Arguments[1]).Value, ((IrStrConst)intrinsic.Arguments[2]).Value);

  /// <summary>Whether a regular-expression operation takes the types, or <see langword="null"/> for an operation that is none.</summary>
  internal static bool? Accepts(IrIntrinsicOp op, IrType result, IReadOnlyList<IrType> arguments)
    => IsRegex(op) ? result == IrType.Bool && arguments.Count == 3 && arguments.All(argument => argument.IsString) : null;

  /// <summary>
  /// What is wrong with a well-typed node of a regular-expression operation beyond its types, or
  /// <see langword="null"/>: pattern and flags are string constants, and within the subset.
  /// </summary>
  /// <param name="intrinsic">The node.</param>
  /// <returns>The problem, or <see langword="null"/>.</returns>
  public static string? Problem(IrIntrinsic intrinsic) {
    if (!IsRegex(intrinsic.Op) || intrinsic.Arguments.Count != 3)
      return null;

    if (intrinsic.Arguments[1] is not IrStrConst || intrinsic.Arguments[2] is not IrStrConst)
      return $"the pattern and the flags of {intrinsic.Op} are not string constants";

    var (pattern, flags) = Of(intrinsic);
    return Refusal(pattern, flags) is { } refused ? $"{intrinsic.Op} of a regular expression with {refused}" : null;
  }

  /// <summary>What of a regular expression is outside the subset, or <see langword="null"/> when nothing is.</summary>
  /// <param name="pattern">The pattern, as ECMAScript writes it between the slashes.</param>
  /// <param name="flags">The flags.</param>
  /// <returns>The construct or flag, as a diagnostic names it after "a regular expression with": <c>the flag g</c>, <c>a backreference</c>.</returns>
  public static string? Refusal(string pattern, string flags) {
    try {
      ToDotNet(pattern, flags);
      return null;
    } catch (ArgumentException refused) {
      return refused.Message;
    }
  }

  /// <summary>The pattern in the syntax of .NET that, compiled with <see cref="Options"/>, matches exactly what the ECMAScript pattern matches.</summary>
  /// <param name="pattern">The pattern, as ECMAScript writes it between the slashes.</param>
  /// <param name="flags">The flags.</param>
  /// <returns>The .NET pattern.</returns>
  /// <exception cref="ArgumentException">The pattern or a flag is outside the subset; the message names it.</exception>
  public static string ToDotNet(string pattern, string flags) {
    var ignoreCase = false;
    foreach (var flag in flags)
      if (flag != 'i' || ignoreCase)
        throw new ArgumentException($"the flag {flag}");
      else
        ignoreCase = true;

    return new Converter(pattern, ignoreCase).Run();
  }

  /// <summary>ECMA-262's <c>RegExp.prototype.test</c>: whether the pattern matches anywhere in the subject.</summary>
  /// <param name="subject">The string tested.</param>
  /// <param name="pattern">The pattern.</param>
  /// <param name="flags">The flags.</param>
  /// <returns>Whether it matches.</returns>
  /// <exception cref="ArgumentException">The pattern or a flag is outside the subset.</exception>
  public static bool Test(string subject, string pattern, string flags)
    => _compiled.GetOrAdd((pattern, flags), key => new Regex(ToDotNet(key.Pattern, key.Flags), Options)).IsMatch(subject);

  /// <summary>Evaluates a node of a well-typed module on the values of its arguments, as the interpreter carries them.</summary>
  internal static object Evaluate(object[] arguments) => Test((string)arguments[0], (string)arguments[1], (string)arguments[2]);

  /// <summary>A set of UTF-16 code units, as sorted ranges that neither overlap nor touch.</summary>
  private sealed class Units {

    private readonly List<(char First, char Last)> _ranges = [];

    public static Units Of(params (char First, char Last)[] ranges) {
      var units = new Units();
      foreach (var (first, last) in ranges)
        units.Add(first, last);

      return units;
    }

    public static Units Single(char unit) => Of((unit, unit));

    public IReadOnlyList<(char First, char Last)> Ranges => this._ranges;

    public void Add(char first, char last) {
      var ranges = this._ranges.Append((First: first, Last: last)).OrderBy(range => range.First).ToList();
      this._ranges.Clear();
      foreach (var range in ranges)
        if (this._ranges.Count > 0 && range.First <= this._ranges[^1].Last + 1)
          this._ranges[^1] = (this._ranges[^1].First, (char)Math.Max(this._ranges[^1].Last, range.Last));
        else
          this._ranges.Add(range);
    }

    public void Add(Units other) {
      foreach (var (first, last) in other._ranges)
        this.Add(first, last);
    }

    public Units Complement() {
      var complement = new Units();
      var next = 0;
      foreach (var (first, last) in this._ranges) {
        if (first > next)
          complement._ranges.Add(((char)next, (char)(first - 1)));

        next = last + 1;
      }

      if (next <= char.MaxValue)
        complement._ranges.Add(((char)next, char.MaxValue));

      return complement;
    }

    /// <summary>The set with the other case of each ASCII letter it holds.</summary>
    public Units BothCases() {
      var both = new Units();
      both.Add(this);
      foreach (var (first, last) in this._ranges)
        foreach (var (low, high, other) in new[] { ('A', 'Z', 'a' - 'A'), ('a', 'z', 'A' - 'a') })
          if (first <= high && last >= low)
            both.Add((char)(Math.Max(first, low) + other), (char)(Math.Min(last, high) + other));

      return both;
    }

  }

  /// <summary>A reader of an ECMAScript pattern without the flag <c>u</c> that writes it in .NET's syntax as it goes.</summary>
  private sealed class Converter(string pattern, bool ignoreCase) {

    private static readonly Units _digits = Units.Of(('0', '9'));
    private static readonly Units _word = Units.Of(('0', '9'), ('A', 'Z'), ('_', '_'), ('a', 'z'));
    private static readonly Units _space = Spaces();
    private static readonly Units _terminators = Units.Of(('\n', '\n'), ('\r', '\r'), ('\u2028', '\u2029'));

    private readonly StringBuilder _written = new();
    private int _at;

    public string Run() {
      this.Disjunction();
      if (this._at < pattern.Length)
        throw new ArgumentException("an unmatched )");

      return this._written.ToString();
    }

    private static Units Spaces() {
      var units = new Units();
      foreach (var unit in StringOperations.EcmaWhitespace)
        units.Add(unit, unit);

      return units;
    }

    private bool At(char unit) => this._at < pattern.Length && pattern[this._at] == unit;

    private void Disjunction() {
      this.Alternative();
      while (this.At('|')) {
        ++this._at;
        this._written.Append('|');
        this.Alternative();
      }
    }

    private void Alternative() {
      while (this._at < pattern.Length && pattern[this._at] is not ('|' or ')'))
        this.Term();
    }

    private void Term() {
      var unit = pattern[this._at++];
      switch (unit) {
        case '^':
          this._written.Append('^');
          return;
        case '$':
          this._written.Append(@"\z");
          return;
        case '(':
          this.Group();
          break;
        case '.':
          this.Write(_terminators.Complement());
          break;
        case '[':
          this.Write(this.Class());
          break;
        case '\\':
          this.Write(this.Escape(false));
          break;
        case '*' or '+' or '?' or '{':
          throw new ArgumentException($"a quantifier {unit} without anything to repeat");
        default:
          this.Write(this.Literal(unit));
          break;
      }

      this.Quantifier();
    }

    private void Group() {
      if (this.At('?')) {
        if (this._at + 1 >= pattern.Length)
          throw new ArgumentException("an unclosed (");

        var kind = pattern[this._at + 1];
        var what = kind switch {
          ':' => null,
          '=' or '!' => "a lookahead",
          '<' when this._at + 2 < pattern.Length && pattern[this._at + 2] is '=' or '!' => "a lookbehind",
          '<' => "a named group",
          _ => "a group (?" + kind
        };
        if (what is not null)
          throw new ArgumentException(what);

        this._at += 2;
        this._written.Append("(?:");
      } else {
        this._written.Append('(');
      }

      this.Disjunction();
      if (!this.At(')'))
        throw new ArgumentException("an unclosed (");

      ++this._at;
      this._written.Append(')');
    }

    private void Quantifier() {
      if (this._at >= pattern.Length)
        return;

      switch (pattern[this._at]) {
        case '*' or '+' or '?':
          this._written.Append(pattern[this._at++]);
          break;
        case '{':
          var close = pattern.IndexOf('}', this._at);
          var bounds = close < 0 ? null : pattern[(this._at + 1)..close].Split(',');
          if (bounds is null || bounds.Length > 2 || !Count(bounds[0], out var least)
            || (bounds.Length == 2 && bounds[1].Length > 0 && (!Count(bounds[1], out var most) || most < least)))
            throw new ArgumentException("a { that starts no quantifier");

          this._written.Append(pattern, this._at, close + 1 - this._at);
          this._at = close + 1;
          break;
        default:
          return;
      }

      if (this.At('?')) {
        ++this._at;
        this._written.Append('?');
      }
    }

    private static bool Count(string digits, out int count) {
      count = 0;
      return digits.Length > 0 && digits.All(char.IsAsciiDigit) && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }

    /// <summary>A character of the pattern itself, with its other case under the flag <c>i</c>.</summary>
    private Units Literal(char unit) {
      if (ignoreCase && unit > 0x7F)
        throw new ArgumentException($"the flag i and the character U+{(int)unit:X4} outside ASCII");

      var units = Units.Single(unit);
      return ignoreCase ? units.BothCases() : units;
    }

    /// <summary>A class: its members, each range and character with its other case under <c>i</c>, then the complement where it is negated.</summary>
    private Units Class() {
      var negated = this.At('^');
      if (negated)
        ++this._at;

      var members = new Units();
      while (!this.At(']')) {
        if (this._at >= pattern.Length)
          throw new ArgumentException("an unclosed [");

        var first = this.ClassAtom();
        if (!this.At('-') || this._at + 1 >= pattern.Length || pattern[this._at + 1] == ']') {
          members.Add(first.Ranges is [var (unit, end)] && unit == end ? this.Literal(unit, unit) : first);
          continue;
        }

        ++this._at;
        var last = this.ClassAtom();
        if (first.Ranges is not [var (low, lowEnd)] || low != lowEnd || last.Ranges is not [var (high, highEnd)] || high != highEnd)
          throw new ArgumentException("a range of a class escape");
        if (high < low)
          throw new ArgumentException("a range out of order");

        members.Add(this.Literal(low, high));
      }

      ++this._at;
      return negated ? members.Complement() : members;
    }

    /// <summary>A range of the pattern's own characters, with the other case under the flag <c>i</c>.</summary>
    private Units Literal(char first, char last) {
      if (ignoreCase && last > 0x7F)
        throw new ArgumentException($"the flag i and the character U+{(int)last:X4} outside ASCII");

      var units = Units.Of((first, last));
      return ignoreCase ? units.BothCases() : units;
    }

    /// <summary>One member of a class: a character, or the set of a class escape, which always holds more than one.</summary>
    private Units ClassAtom() {
      var unit = pattern[this._at++];
      if (unit != '\\')
        return Units.Single(unit);

      if (this.At('b')) {
        ++this._at;
        return Units.Single('\b');
      }

      return this.Escape(true);
    }

    /// <summary>An escape behind its backslash: a class escape as its set, any other as its character - the case of the pattern's own characters added under <c>i</c> outside a class only, where a class adds it itself.</summary>
    private Units Escape(bool inClass) {
      if (this._at >= pattern.Length)
        throw new ArgumentException("a \\ at the end");

      var unit = pattern[this._at++];
      Units Character(char value) => inClass ? Units.Single(value) : this.Literal(value);
      switch (unit) {
        case 'd':
          return _digits;
        case 'D':
          return _digits.Complement();
        case 'w':
          return _word;
        case 'W':
          return _word.Complement();
        case 's':
          return _space;
        case 'S':
          return _space.Complement();
        case 't':
          return Character('\t');
        case 'n':
          return Character('\n');
        case 'v':
          return Character('\v');
        case 'f':
          return Character('\f');
        case 'r':
          return Character('\r');
        case '0' when !(this._at < pattern.Length && char.IsAsciiDigit(pattern[this._at])):
          return Character('\0');
        case >= '0' and <= '9':
          throw new ArgumentException(unit == '0' || inClass ? "an octal escape" : "a backreference");
        case 'x':
          return Character(this.Hex(2, unit));
        case 'u':
          return Character(this.Hex(4, unit));
        case 'c' when this._at < pattern.Length && char.IsAsciiLetter(pattern[this._at]):
          return Character((char)(pattern[this._at++] % 32));
        case 'b' or 'B':
          throw new ArgumentException($"a word boundary \\{unit}");
        case 'k':
          throw new ArgumentException("a named backreference");
        case 'p' or 'P':
          throw new ArgumentException($"a property escape \\{unit}");
        case var letter when char.IsAsciiLetterOrDigit(letter):
          throw new ArgumentException($"the escape \\{letter}");
        default:
          return Character(unit);
      }
    }

    private char Hex(int digits, char escape) {
      if (this._at + digits > pattern.Length || !pattern.AsSpan(this._at, digits).ToString().All(char.IsAsciiHexDigit))
        throw new ArgumentException($"an escape \\{escape} without {digits} hexadecimal digits");

      var value = (char)int.Parse(pattern.AsSpan(this._at, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
      this._at += digits;
      return value;
    }

    /// <summary>A set as .NET's class, every character but an ASCII letter, a digit and the underscore as its <c>\u</c> escape; a single character as itself or its escape.</summary>
    private void Write(Units units) {
      static string Unit(char unit) => char.IsAsciiLetterOrDigit(unit) || unit == '_' ? unit.ToString() : $"\\u{(int)unit:X4}";
      if (units.Ranges is [var (single, end)] && single == end) {
        this._written.Append(Unit(single));
        return;
      }

      if (units.Ranges.Count == 0) {
        // A class that matches nothing; .NET has no empty class.
        this._written.Append(@"[^\u0000-\uFFFF]");
        return;
      }

      this._written.Append('[');
      foreach (var (first, last) in units.Ranges) {
        this._written.Append(Unit(first));
        if (last > first)
          this._written.Append(last > first + 1 ? "-" : "").Append(Unit(last));
      }

      this._written.Append(']');
    }

  }

}
