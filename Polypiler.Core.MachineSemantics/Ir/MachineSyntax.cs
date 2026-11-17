using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Polypiler.Core.MachineSemantics;

/// <summary>
/// The text form of MachineIR, printed and parsed with an exact round trip (print → parse → print gives
/// the same text). A module is a header — <c>machine &lt;profile&gt;</c>, <c>mode &lt;mode&gt;</c>,
/// <c>isa &lt;isa&gt;</c>, each optional — followed by functions:
/// <code>
/// machine test-m32
///
/// @f [root-registers] {
/// entry:
///   %v = Const.u8 0xFF
///   RegWrite.u8 @r0l, %v !at(0x00401000, "B0 FF")
///   Ret
/// }
/// </code>
/// A line break or <c>;</c> ends a statement, <c>#</c> starts a comment. Numbers are decimal or
/// <c>0x</c> hexadecimal; the printer writes values below 10 in decimal and the rest in upper-case hex.
/// </summary>
public static class MachineSyntax {

  /// <summary>Prints a number as the printer writes it: decimal below 10, else <c>0x</c> and upper-case hex.</summary>
  /// <param name="value">The value.</param>
  /// <returns>The text.</returns>
  public static string Number(ulong value) => value < 10 ? value.ToString(CultureInfo.InvariantCulture) : "0x" + value.ToString("X", CultureInfo.InvariantCulture);

  /// <summary>Prints a module.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The text, ending in a line break.</returns>
  public static string Print(MachineModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var text = new StringBuilder();
    if (module.Profile is { } profile)
      text.Append("machine ").Append(profile.Id).Append('\n');
    if (module.Mode != MachineModule.DefaultMode)
      text.Append("mode ").Append(module.Mode).Append('\n');
    if (module.Isa is { } isa && isa != module.Profile?.Id)
      text.Append("isa ").Append(isa).Append('\n');

    foreach (var function in module.Functions) {
      if (text.Length > 0)
        text.Append('\n');
      text.Append(Print(function, module));
    }

    return text.ToString();
  }

  /// <summary>Prints a function as it appears in its module.</summary>
  /// <param name="function">The function.</param>
  /// <param name="module">The module whose ISA and mode its origins are printed against, or <see langword="null"/>.</param>
  /// <returns>The text, ending in a line break.</returns>
  public static string Print(MachineFunction function, MachineModule? module = null) {
    ArgumentNullException.ThrowIfNull(function);
    var text = new StringBuilder("@").Append(function.Name);
    if (!function.Attributes.IsEmpty)
      text.Append(" [").Append(string.Join(", ", function.Attributes)).Append(']');
    text.Append(" {\n");
    foreach (var block in function.Blocks) {
      text.Append(block.Label).Append(":\n");
      foreach (var instruction in block.Instructions)
        text.Append("  ").Append(Print(instruction, module)).Append('\n');
    }

    return text.Append("}\n").ToString();
  }

  /// <summary>Prints one instruction.</summary>
  /// <param name="instruction">The instruction.</param>
  /// <param name="module">The module whose ISA and mode its origin is printed against, or <see langword="null"/>.</param>
  /// <returns>The text, without a line break.</returns>
  public static string Print(MachineInstruction instruction, MachineModule? module = null) {
    ArgumentNullException.ThrowIfNull(instruction);
    var text = new StringBuilder();
    if (instruction.Result is { } result)
      text.Append('%').Append(result).Append(" = ");
    text.Append(instruction.Opcode);
    if (instruction.Type != MachineType.Void)
      text.Append('.').Append(instruction.Type);

    if (instruction.Opcode == MachineOpcode.IndirectJump) {
      text.Append(' ').Append(instruction.Operands[0]).Append(" -> ");
      text.Append(instruction.Unresolved ? "unresolved" : string.Join(", ", instruction.Operands.Skip(1)));
    } else if (!instruction.Operands.IsEmpty) {
      text.Append(' ').Append(string.Join(", ", instruction.Operands));
    }

    if (instruction.Origin is { } origin) {
      text.Append(" !at(");
      var isa = module?.OriginIsa;
      var mode = module?.Mode ?? MachineModule.DefaultMode;
      if (origin.Isa != isa || origin.Mode != mode)
        text.Append(origin.Isa).Append(':').Append(origin.Mode).Append(", ");
      text.Append("0x").Append(origin.Address.ToString(origin.Address > uint.MaxValue ? "X16" : "X8", CultureInfo.InvariantCulture));
      text.Append(", \"").Append(origin.HexBytes).Append('"');
      if (origin.Text is { } disassembly)
        text.Append(", \"").Append(disassembly.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
      text.Append(')');
    }

    return text.ToString();
  }

  /// <summary>Parses a module.</summary>
  /// <param name="text">The text.</param>
  /// <param name="profiles">Finds a machine profile by ID; <see langword="null"/> for an unknown one.</param>
  /// <returns>The module. It is not verified: run <see cref="MachineVerifier.Verify"/>.</returns>
  /// <exception cref="MachineException">The text does not parse (<c>PLY4006</c>) or names an unknown profile (<c>PLY4003</c>).</exception>
  public static MachineModule Parse(string text, Func<string, IsaProfile?> profiles) {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(profiles);
    return new Parser(Lex(text), profiles).Module();
  }

  private enum TokenKind { Word, Value, Storage, Number, String, Symbol, End, Eof }

  private readonly record struct Token(TokenKind Kind, string Text, int Line) {
    public override string ToString() => this.Kind switch {
      TokenKind.End => "the end of the statement",
      TokenKind.Eof => "the end of the text",
      _ => $"'{this.Text}'"
    };
  }

  private static bool IsWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '$';

  private static List<Token> Lex(string text) {
    var tokens = new List<Token>();
    var line = 1;
    for (var index = 0; index < text.Length;) {
      var c = text[index];
      if (c is '\n' or ';') {
        tokens.Add(new(TokenKind.End, c.ToString(), line));
        if (c == '\n')
          ++line;
        ++index;
      } else if (c is ' ' or '\t' or '\r') {
        ++index;
      } else if (c == '#') {
        while (index < text.Length && text[index] != '\n')
          ++index;
      } else if (c is '%' or '@') {
        var start = ++index;
        while (index < text.Length && IsWordCharacter(text[index]))
          ++index;
        if (index == start)
          throw Error(line, $"'{c}' must be followed by a name");
        tokens.Add(new(c == '%' ? TokenKind.Value : TokenKind.Storage, text[start..index], line));
      } else if (c == '"') {
        var value = new StringBuilder();
        for (++index; ; ++index) {
          if (index >= text.Length || text[index] == '\n')
            throw Error(line, "a string is not closed");
          if (text[index] == '"')
            break;
          if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\')
            ++index;
          value.Append(text[index]);
        }

        ++index;
        tokens.Add(new(TokenKind.String, value.ToString(), line));
      } else if (c == '-' && index + 1 < text.Length && text[index + 1] == '>') {
        tokens.Add(new(TokenKind.Symbol, "->", line));
        index += 2;
      } else if (char.IsAsciiDigit(c)) {
        var start = index;
        while (index < text.Length && char.IsAsciiLetterOrDigit(text[index]))
          ++index;
        tokens.Add(new(TokenKind.Number, text[start..index], line));
      } else if (IsWordCharacter(c)) {
        var start = index;
        while (index < text.Length && IsWordCharacter(text[index]))
          ++index;
        tokens.Add(new(TokenKind.Word, text[start..index], line));
      } else if (c is ',' or ':' or '{' or '}' or '[' or ']' or '(' or ')' or '=' or '!') {
        tokens.Add(new(TokenKind.Symbol, c.ToString(), line));
        ++index;
      } else {
        throw Error(line, $"unexpected character '{c}'");
      }
    }

    tokens.Add(new(TokenKind.Eof, "", line));
    return tokens;
  }

  private static MachineException Error(int line, string reason) => new(MachineDiagnostics.Syntax.At($"line {line}: {reason}"));

  /// <summary>Reads a number written in decimal or with <c>0x</c>.</summary>
  internal static bool TryNumber(string text, out ulong value)
    => text.StartsWith("0x", StringComparison.Ordinal)
      ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && text.Length > 2
      : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

  private sealed class Parser(List<Token> tokens, Func<string, IsaProfile?> profiles) {

    private int _position;
    private IsaProfile? _profile;
    private string? _isa;
    private string _mode = MachineModule.DefaultMode;

    private Token Peek => tokens[this._position];

    private Token Next() => tokens[this._position++];

    private MachineException Error(Token at, string reason) => MachineSyntax.Error(at.Line, reason);

    private void SkipEnds() {
      while (this.Peek.Kind == TokenKind.End)
        ++this._position;
    }

    private bool IsSymbol(string symbol) => this.Peek is { Kind: TokenKind.Symbol } token && token.Text == symbol;

    private void Expect(string symbol) {
      var token = this.Next();
      if (token.Kind != TokenKind.Symbol || token.Text != symbol)
        throw this.Error(token, $"expected '{symbol}' but found {token}");
    }

    private string Word(string what) {
      var token = this.Next();
      return token.Kind == TokenKind.Word ? token.Text : throw this.Error(token, $"expected {what} but found {token}");
    }

    private void EndOfStatement() {
      var token = this.Next();
      if (token.Kind is not (TokenKind.End or TokenKind.Eof))
        throw this.Error(token, $"expected the end of the statement but found {token}");
    }

    public MachineModule Module() {
      var functions = ImmutableArray.CreateBuilder<MachineFunction>();
      var seenFunction = false;
      for (this.SkipEnds(); this.Peek.Kind != TokenKind.Eof; this.SkipEnds()) {
        var token = this.Peek;
        if (token.Kind == TokenKind.Storage) {
          seenFunction = true;
          functions.Add(this.Function());
          continue;
        }

        if (token.Kind != TokenKind.Word || seenFunction || token.Text is not ("machine" or "mode" or "isa"))
          throw this.Error(token, $"expected a function or a header line but found {token}");
        ++this._position;
        var value = this.Word($"the value of '{token.Text}'");
        this.EndOfStatement();
        switch (token.Text) {
          case "machine":
            this._profile = profiles(value) ?? throw new MachineException(MachineDiagnostics.InvalidProfile(value, "no such machine profile"));
            break;
          case "mode":
            this._mode = value;
            break;
          default:
            this._isa = value;
            break;
        }
      }

      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var function in functions)
        if (!names.Add(function.Name))
          throw new MachineException(MachineDiagnostics.Syntax.At($"the function '@{function.Name}' is defined twice"));

      return new(this._profile, functions.ToImmutable()) { Isa = this._isa, Mode = this._mode };
    }

    private MachineFunction Function() {
      var name = this.Next().Text;
      var attributes = ImmutableArray.CreateBuilder<string>();
      if (this.IsSymbol("[")) {
        ++this._position;
        do
          attributes.Add(this.Word("an attribute"));
        while (this.TryComma());
        this.Expect("]");
      }

      this.Expect("{");
      var blocks = ImmutableArray.CreateBuilder<MachineBlock>();
      string? label = null;
      var instructions = ImmutableArray.CreateBuilder<MachineInstruction>();
      for (this.SkipEnds(); !this.IsSymbol("}"); this.SkipEnds()) {
        var token = this.Peek;
        if (token.Kind == TokenKind.Eof)
          throw this.Error(token, $"the function '@{name}' is not closed with '}}'");
        if (token.Kind == TokenKind.Word && tokens[this._position + 1] is { Kind: TokenKind.Symbol, Text: ":" }) {
          if (label is not null)
            blocks.Add(new(label, instructions.ToImmutable()));
          label = token.Text;
          instructions.Clear();
          this._position += 2;
          continue;
        }

        if (label is null)
          throw this.Error(token, $"an instruction of '@{name}' stands before the first block label");
        instructions.Add(this.Instruction());
      }

      ++this._position;
      if (label is not null)
        blocks.Add(new(label, instructions.ToImmutable()));
      return new(name, blocks.ToImmutable()) { Attributes = attributes.ToImmutable() };
    }

    private bool TryComma() {
      if (!this.IsSymbol(","))
        return false;
      ++this._position;
      return true;
    }

    private MachineInstruction Instruction() {
      string? result = null;
      if (this.Peek.Kind == TokenKind.Value) {
        result = this.Next().Text;
        this.Expect("=");
      }

      var head = this.Next();
      if (head.Kind != TokenKind.Word)
        throw this.Error(head, $"expected an operation but found {head}");
      var dot = head.Text.IndexOf('.', StringComparison.Ordinal);
      var name = dot < 0 ? head.Text : head.Text[..dot];
      if (!Enum.TryParse<MachineOpcode>(name, false, out var opcode) || !Enum.IsDefined(opcode) || !char.IsAsciiLetterUpper(name[0]))
        throw this.Error(head, $"'{name}' is not a MachineIR operation");
      var type = MachineType.Void;
      if (dot >= 0 && !MachineType.TryParse(head.Text[(dot + 1)..], out type))
        throw this.Error(head, $"'{head.Text[(dot + 1)..]}' is not a machine type");
      if (MachineOpcodes.HasResult(opcode) != (result is not null))
        throw this.Error(head, result is null ? $"'{name}' defines a value and needs '%name ='" : $"'{name}' defines no value");

      var operands = ImmutableArray.CreateBuilder<MachineOperand>();
      var unresolved = false;
      if (this.Peek.Kind is not (TokenKind.End or TokenKind.Eof) && !this.IsSymbol("!")) {
        operands.Add(this.Operand());
        if (opcode == MachineOpcode.IndirectJump) {
          this.Expect("->");
          if (this.Peek is { Kind: TokenKind.Word, Text: "unresolved" }) {
            ++this._position;
            unresolved = true;
          } else {
            do
              operands.Add(new MachineLabel(this.Word("a target block")));
            while (this.TryComma());
          }
        } else {
          while (this.TryComma())
            operands.Add(this.Operand());
        }
      }

      MachineOrigin? origin = null;
      if (this.IsSymbol("!"))
        origin = this.Origin();
      this.EndOfStatement();
      return new(opcode, type, result, operands.ToImmutable()) { Origin = origin, Unresolved = unresolved };
    }

    private MachineOperand Operand() {
      var token = this.Next();
      return token.Kind switch {
        TokenKind.Value => new MachineValue(token.Text),
        TokenKind.Storage => new MachineStorage(token.Text),
        TokenKind.Number => TryNumber(token.Text, out var value) ? new MachineImmediate(value) : throw this.Error(token, $"'{token.Text}' is not a number"),
        TokenKind.Word => new MachineLabel(token.Text),
        _ => throw this.Error(token, $"expected an operand but found {token}")
      };
    }

    private MachineOrigin Origin() {
      ++this._position;
      var at = this.Word("'at'");
      if (at != "at")
        throw this.Error(tokens[this._position - 1], $"expected 'at' after '!' but found '{at}'");
      this.Expect("(");
      var isa = this._isa ?? this._profile?.Id ?? "unknown";
      var mode = this._mode;
      if (this.Peek.Kind == TokenKind.Word) {
        isa = this.Next().Text;
        this.Expect(":");
        mode = this.Word("a mode");
        this.Expect(",");
      }

      var addressToken = this.Next();
      if (addressToken.Kind != TokenKind.Number || !TryNumber(addressToken.Text, out var address))
        throw this.Error(addressToken, $"expected the address of the origin but found {addressToken}");
      this.Expect(",");
      var bytesToken = this.Next();
      if (bytesToken.Kind != TokenKind.String)
        throw this.Error(bytesToken, $"expected the bytes of the origin as a string but found {bytesToken}");
      var bytes = ImmutableArray.CreateBuilder<byte>();
      foreach (var pair in bytesToken.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        bytes.Add(pair.Length == 2 && byte.TryParse(pair, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
          ? value
          : throw this.Error(bytesToken, $"'{pair}' is not a byte in hex"));
      string? disassembly = null;
      if (this.TryComma()) {
        var textToken = this.Next();
        disassembly = textToken.Kind == TokenKind.String ? textToken.Text : throw this.Error(textToken, $"expected the disassembly as a string but found {textToken}");
      }

      this.Expect(")");
      return new(isa, mode, address, bytes.ToImmutable(), disassembly);
    }

  }

}
