using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Polypiler.Core.Frontends.Cpp.Syntax;

internal sealed class CppParser {

  private static readonly HashSet<string> _primitiveStarters = [
    "void", "bool", "char", "signed", "unsigned", "short", "int", "long", "float", "double",
    "int8_t", "uint8_t", "int16_t", "uint16_t", "int32_t", "uint32_t", "int64_t", "uint64_t",
    "size_t", "ptrdiff_t", "ssize_t", "string", "vector", "span", "array", "optional"
  ];

  private readonly string _file;
  private readonly IReadOnlyList<CppToken> _tokens;
  private readonly HashSet<string> _knownTypes = [];
  private int _pos;
  private bool _pendingGreater;

  private CppParser(string file, IReadOnlyList<CppToken> tokens) {
    this._file = file;
    this._tokens = tokens;
  }

  public static CppTranslationUnit Parse(string file, IReadOnlyList<CppToken> tokens)
    => new CppParser(file, tokens).ParseTranslationUnit();

  private CppToken Current => this._tokens[Math.Min(this._pos, this._tokens.Count - 1)];

  private CppToken Peek(int offset) => this._tokens[Math.Min(this._pos + offset, this._tokens.Count - 1)];

  private CppToken Advance() {
    var tok = this.Current;
    if (this._pos < this._tokens.Count - 1)
      ++this._pos;

    return tok;
  }

  private bool IsKeyword(string kw) => this.Current.Kind == CppTokenKind.Identifier && this.Current.Text == kw;

  private bool Match(CppTokenKind kind) {
    if (this.Current.Kind != kind)
      return false;

    this.Advance();
    return true;
  }

  private bool MatchKeyword(string kw) {
    if (!this.IsKeyword(kw))
      return false;

    this.Advance();
    return true;
  }

  private CppToken Expect(CppTokenKind kind, string expected) {
    if (this.Current.Kind == kind)
      return this.Advance();

    throw this.SyntaxError(this.Current, $"expected {expected}, got '{this.Current.Text}'");
  }

  private CppToken ExpectIdentifier(string expected) {
    if (this.Current.Kind == CppTokenKind.Identifier)
      return this.Advance();

    throw this.SyntaxError(this.Current, $"expected {expected}, got '{this.Current.Text}'");
  }

  private CppDiagnosticException SyntaxError(CppToken tok, string message)
    => new(Diagnostic.Create(CppFrontend.SyntaxError, tok.Span(this._file), message));

  private CppDiagnosticException Unsupported(CppToken tok, string construct)
    => new(Diagnostic.Create(CppFrontend.UnsupportedConstruct, tok.Span(this._file), construct));

  private CppTranslationUnit ParseTranslationUnit() {
    var decls = new List<CppDecl>();
    while (this.Current.Kind != CppTokenKind.EndOfFile) {
      if (this.Match(CppTokenKind.Semicolon))
        continue;

      decls.Add(this.ParseTopLevelDecl());
    }

    return new(decls);
  }

  private CppDecl ParseTopLevelDecl() {
    var start = this.Current;
    if (this.MatchKeyword("using"))
      return this.ParseUsing(start);

    if (this.MatchKeyword("namespace"))
      return this.ParseNamespace(start);

    if (this.IsKeyword("class") || this.IsKeyword("union"))
      throw this.Unsupported(start, start.Text);

    if (this.MatchKeyword("struct"))
      return this.ParseStruct(start);

    if (this.MatchKeyword("enum"))
      return this.ParseEnum(start);

    List<string> typeParams = [];
    if (this.MatchKeyword("template")) {
      this.Expect(CppTokenKind.Less, "'<'");
      do {
        if (!this.MatchKeyword("typename") && !this.MatchKeyword("class"))
          throw this.Unsupported(this.Current, "non-type template parameter");

        var paramName = this.ExpectIdentifier("template type parameter name").Text;
        typeParams.Add(paramName);
        this._knownTypes.Add(paramName);
      } while (this.Match(CppTokenKind.Comma));

      this.ExpectClosingAngle();
      if (this.IsKeyword("struct") || this.IsKeyword("class"))
        throw this.Unsupported(this.Current, "template struct/class");
    }

    var isStatic = false;
    var isConst = false;
    while (true) {
      if (this.MatchKeyword("static"))
        isStatic = true;
      else if (this.MatchKeyword("inline"))
        continue;
      else if (this.MatchKeyword("constexpr") || this.MatchKeyword("const"))
        isConst = true;
      else
        break;
    }

    var type = this.ParseTypeRef(isConst);
    var name = this.ParseQualifiedName();

    if (this.Current.Kind == CppTokenKind.LParen) {
      var @params = this.ParseParamList();
      this.MatchKeyword("noexcept");
      CppBlockStmt? body = null;
      if (this.Current.Kind == CppTokenKind.LBrace)
        body = this.ParseBlock();
      else
        this.Expect(CppTokenKind.Semicolon, "';' or '{'");

      return new CppFuncDecl(typeParams, type, name, @params, body, isStatic, start.Span(this._file));
    }

    if (typeParams.Count > 0)
      throw this.Unsupported(start, "variable template");

    if (this.Match(CppTokenKind.LBracket)) {
      int? arrLen = null;
      if (this.Current.Kind != CppTokenKind.RBracket)
        arrLen = (int)this.ParseConstantSize();

      this.Expect(CppTokenKind.RBracket, "']'");
      type = type with { ArraySize = arrLen ?? -1 };
    }

    CppExpr? init = null;
    if (this.Match(CppTokenKind.Assign))
      init = this.ParseExpression();
    else if (this.Current.Kind == CppTokenKind.LBrace)
      init = this.ParseInitList(null);

    this.Expect(CppTokenKind.Semicolon, "';'");
    return new CppGlobalVarDecl(type, name, isConst || type.IsConst, init, start.Span(this._file));
  }

  private CppDecl ParseUsing(CppToken start) {
    if (this.MatchKeyword("namespace")) {
      var nsName = this.ParseQualifiedName();
      this.Expect(CppTokenKind.Semicolon, "';'");
      return new CppUsingNamespaceDecl(nsName, start.Span(this._file));
    }

    var alias = this.ExpectIdentifier("type alias name").Text;
    this.Expect(CppTokenKind.Assign, "'='");
    var target = this.ParseTypeRef(false);
    this.Expect(CppTokenKind.Semicolon, "';'");
    this._knownTypes.Add(alias);
    return new CppTypeAliasDecl(alias, target, start.Span(this._file));
  }

  private CppNamespaceDecl ParseNamespace(CppToken start) {
    string? name = null;
    if (this.Current.Kind == CppTokenKind.Identifier)
      name = this.ParseQualifiedName();

    this.Expect(CppTokenKind.LBrace, "'{'");
    var decls = new List<CppDecl>();
    while (this.Current.Kind != CppTokenKind.RBrace && this.Current.Kind != CppTokenKind.EndOfFile) {
      if (this.Match(CppTokenKind.Semicolon))
        continue;

      decls.Add(this.ParseTopLevelDecl());
    }

    this.Expect(CppTokenKind.RBrace, "'}'");
    return new(name, decls, start.Span(this._file));
  }

  private CppStructDecl ParseStruct(CppToken start) {
    var name = this.ExpectIdentifier("struct name").Text;
    this._knownTypes.Add(name);
    if (this.Current.Kind == CppTokenKind.Colon)
      throw this.Unsupported(this.Current, "struct inheritance");

    this.Expect(CppTokenKind.LBrace, "'{'");
    var fields = new List<CppFieldDecl>();
    var hasDefaultEquals = false;
    while (this.Current.Kind != CppTokenKind.RBrace && this.Current.Kind != CppTokenKind.EndOfFile) {
      if (this.Match(CppTokenKind.Semicolon))
        continue;

      if (this.IsKeyword("public") || this.IsKeyword("private") || this.IsKeyword("protected")) {
        this.Advance();
        this.Expect(CppTokenKind.Colon, "':'");
        continue;
      }

      if (this.IsKeyword("virtual"))
        throw this.Unsupported(this.Current, "virtual method");

      var fieldStart = this.Current;
      var fieldType = this.ParseTypeRef(false);
      if (this.MatchKeyword("operator")) {
        if (!this.Match(CppTokenKind.EqualEqual))
          throw this.Unsupported(this.Current, "operator overload");

        this.ParseParamList();
        this.MatchKeyword("const");
        this.MatchKeyword("noexcept");
        this.Expect(CppTokenKind.Assign, "'='");
        if (!this.MatchKeyword("default"))
          throw this.Unsupported(this.Current, "non-default operator==");

        this.Expect(CppTokenKind.Semicolon, "';'");
        hasDefaultEquals = true;
        continue;
      }

      var fieldName = this.ExpectIdentifier("field name").Text;
      if (this.Current.Kind == CppTokenKind.LParen)
        throw this.Unsupported(fieldStart, $"member function '{name}::{fieldName}'");

      CppExpr? defaultVal = null;
      if (this.Match(CppTokenKind.Assign))
        defaultVal = this.ParseExpression();
      else if (this.Current.Kind == CppTokenKind.LBrace)
        defaultVal = this.ParseInitList(null);

      this.Expect(CppTokenKind.Semicolon, "';'");
      fields.Add(new(fieldType, fieldName, defaultVal, fieldStart.Span(this._file)));
    }

    this.Expect(CppTokenKind.RBrace, "'}'");
    this.Expect(CppTokenKind.Semicolon, "';'");
    return new(name, fields, hasDefaultEquals, start.Span(this._file));
  }

  private CppEnumDecl ParseEnum(CppToken start) {
    var isScoped = this.MatchKeyword("class") || this.MatchKeyword("struct");
    var name = this.ExpectIdentifier("enum name").Text;
    this._knownTypes.Add(name);
    CppTypeRef? underlying = null;
    if (this.Match(CppTokenKind.Colon))
      underlying = this.ParseTypeRef(false);

    this.Expect(CppTokenKind.LBrace, "'{'");
    var members = new List<CppEnumMemberDecl>();
    while (this.Current.Kind != CppTokenKind.RBrace && this.Current.Kind != CppTokenKind.EndOfFile) {
      var mTok = this.ExpectIdentifier("enum member name");
      CppExpr? val = null;
      if (this.Match(CppTokenKind.Assign))
        val = this.ParseExpression();

      members.Add(new(mTok.Text, val, mTok.Span(this._file)));
      if (!this.Match(CppTokenKind.Comma))
        break;
    }

    this.Expect(CppTokenKind.RBrace, "'}'");
    this.Expect(CppTokenKind.Semicolon, "';'");
    return new(name, isScoped, underlying, members, start.Span(this._file));
  }

  private List<CppParamDecl> ParseParamList() {
    this.Expect(CppTokenKind.LParen, "'('");
    var @params = new List<CppParamDecl>();
    if (this.Current.Kind != CppTokenKind.RParen) {
      do {
        var pStart = this.Current;
        var pType = this.ParseTypeRef(false);
        var pName = this.Current.Kind == CppTokenKind.Identifier ? this.Advance().Text : "";
        if (this.Match(CppTokenKind.LBracket)) {
          int? size = this.Current.Kind != CppTokenKind.RBracket ? (int)this.ParseConstantSize() : -1;
          this.Expect(CppTokenKind.RBracket, "']'");
          pType = pType with { ArraySize = size };
        }

        @params.Add(new(pType, pName, pStart.Span(this._file)));
      } while (this.Match(CppTokenKind.Comma));
    }

    this.Expect(CppTokenKind.RParen, "')'");
    return @params;
  }

  private CppTypeRef ParseTypeRef(bool leadingConst) {
    var start = this.Current;
    var isConst = leadingConst;
    if (this.MatchKeyword("const"))
      isConst = true;

    string name;
    var typeArgs = new List<CppTypeArg>();
    if (this.IsMultiWordPrimitiveStarter()) {
      name = this.ReadPrimitiveTypeName();
    } else {
      name = this.ParseQualifiedName();
      if (this.Match(CppTokenKind.Less)) {
        do {
          if (this.Current.Kind == CppTokenKind.IntLiteral)
            typeArgs.Add(new(null, this.ParseConstantSize()));
          else
            typeArgs.Add(new(this.ParseTypeRef(false), null));
        } while (this.Match(CppTokenKind.Comma));

        this.ExpectClosingAngle();
      }
    }

    if (this.MatchKeyword("const"))
      isConst = true;

    var isPointer = false;
    var isReference = false;
    if (this.Match(CppTokenKind.Star)) {
      isPointer = true;
      this.MatchKeyword("const");
    } else if (this.Match(CppTokenKind.Amp)) {
      isReference = true;
    }

    return new(name, typeArgs, isConst, isPointer, isReference, null, start.Span(this._file));
  }

  private bool IsMultiWordPrimitiveStarter()
    => this.Current.Kind == CppTokenKind.Identifier
      && this.Current.Text is "signed" or "unsigned" or "short" or "long" or "int" or "char" or "bool" or "void" or "float" or "double";

  private string ReadPrimitiveTypeName() {
    var parts = new List<string> { this.Advance().Text };
    while (this.Current.Kind == CppTokenKind.Identifier
        && this.Current.Text is "char" or "short" or "int" or "long" or "double" or "signed" or "unsigned")
      parts.Add(this.Advance().Text);

    return string.Join(' ', parts);
  }

  private string ParseQualifiedName() {
    var builder = new StringBuilder();
    if (this.Match(CppTokenKind.ColonColon))
      builder.Append("::");

    builder.Append(this.ExpectIdentifier("identifier").Text);
    while (this.Match(CppTokenKind.ColonColon)) {
      builder.Append("::");
      builder.Append(this.ExpectIdentifier("identifier").Text);
    }

    return builder.ToString();
  }

  private void ExpectClosingAngle() {
    if (this._pendingGreater) {
      this._pendingGreater = false;
      return;
    }

    if (this.Match(CppTokenKind.Greater))
      return;

    if (this.Current.Kind == CppTokenKind.ShiftRight) {
      this.Advance();
      this._pendingGreater = true;
      return;
    }

    throw this.SyntaxError(this.Current, $"expected '>', got '{this.Current.Text}'");
  }

  private long ParseConstantSize() {
    var tok = this.Expect(CppTokenKind.IntLiteral, "integer size");
    var (val, _, _) = this.ParseIntToken(tok);
    return (long)val;
  }

  private CppBlockStmt ParseBlock() {
    var start = this.Expect(CppTokenKind.LBrace, "'{'");
    var stmts = new List<CppStmt>();
    while (this.Current.Kind != CppTokenKind.RBrace && this.Current.Kind != CppTokenKind.EndOfFile) {
      if (this.Match(CppTokenKind.Semicolon))
        continue;

      stmts.Add(this.ParseStatement());
    }

    this.Expect(CppTokenKind.RBrace, "'}'");
    return new(stmts, start.Span(this._file));
  }

  private CppStmt ParseStatement() {
    var start = this.Current;
    if (start.Kind == CppTokenKind.LBrace)
      return this.ParseBlock();

    if (this.MatchKeyword("if"))
      return this.ParseIf(start);

    if (this.MatchKeyword("while"))
      return this.ParseWhile(start);

    if (this.MatchKeyword("do"))
      return this.ParseDoWhile(start);

    if (this.MatchKeyword("for"))
      return this.ParseFor(start);

    if (this.MatchKeyword("switch"))
      return this.ParseSwitch(start);

    if (this.MatchKeyword("break")) {
      this.Expect(CppTokenKind.Semicolon, "';'");
      return new CppBreakStmt(start.Span(this._file));
    }

    if (this.MatchKeyword("continue")) {
      this.Expect(CppTokenKind.Semicolon, "';'");
      return new CppContinueStmt(start.Span(this._file));
    }

    if (this.MatchKeyword("return")) {
      CppExpr? val = null;
      if (this.Current.Kind != CppTokenKind.Semicolon)
        val = this.ParseExpression();

      this.Expect(CppTokenKind.Semicolon, "';'");
      return new CppReturnStmt(val, start.Span(this._file));
    }

    if (this.MatchKeyword("try"))
      return this.ParseTry(start);

    if (this.MatchKeyword("throw")) {
      CppExpr? val = null;
      if (this.Current.Kind != CppTokenKind.Semicolon)
        val = this.ParseExpression();

      this.Expect(CppTokenKind.Semicolon, "';'");
      return new CppThrowStmt(val, start.Span(this._file));
    }

    if (this.IsKeyword("goto") || this.IsKeyword("asm") || this.IsKeyword("co_await") || this.IsKeyword("co_yield") || this.IsKeyword("co_return"))
      throw this.Unsupported(start, start.Text);

    if (this.LooksLikeVarDecl())
      return this.ParseVarDecl(expectSemi: true);

    var expr = this.ParseExpression();
    this.Expect(CppTokenKind.Semicolon, "';'");
    return new CppExprStmt(expr, start.Span(this._file));
  }

  private CppIfStmt ParseIf(CppToken start) {
    this.MatchKeyword("constexpr");
    this.Expect(CppTokenKind.LParen, "'('");
    var cond = this.ParseExpression();
    this.Expect(CppTokenKind.RParen, "')'");
    var thenStmt = this.ParseStatement();
    CppStmt? elseStmt = this.MatchKeyword("else") ? this.ParseStatement() : null;
    return new(cond, thenStmt, elseStmt, start.Span(this._file));
  }

  private CppWhileStmt ParseWhile(CppToken start) {
    this.Expect(CppTokenKind.LParen, "'('");
    var cond = this.ParseExpression();
    this.Expect(CppTokenKind.RParen, "')'");
    var body = this.ParseStatement();
    return new(cond, body, start.Span(this._file));
  }

  private CppDoWhileStmt ParseDoWhile(CppToken start) {
    var body = this.ParseStatement();
    if (!this.MatchKeyword("while"))
      throw this.SyntaxError(this.Current, "expected 'while'");

    this.Expect(CppTokenKind.LParen, "'('");
    var cond = this.ParseExpression();
    this.Expect(CppTokenKind.RParen, "')'");
    this.Expect(CppTokenKind.Semicolon, "';'");
    return new(body, cond, start.Span(this._file));
  }

  private CppStmt ParseFor(CppToken start) {
    this.Expect(CppTokenKind.LParen, "'('");
    if (this.LooksLikeRangeFor()) {
      var isConst = this.MatchKeyword("const");
      CppTypeRef? varType = null;
      if (this.MatchKeyword("auto")) {
        this.Match(CppTokenKind.Amp);
      } else {
        varType = this.ParseTypeRef(isConst);
      }

      var varName = this.ExpectIdentifier("loop variable").Text;
      this.Expect(CppTokenKind.Colon, "':'");
      var iterable = this.ParseExpression();
      this.Expect(CppTokenKind.RParen, "')'");
      var rBody = this.ParseStatement();
      return new CppRangeForStmt(varType, varName, iterable, rBody, start.Span(this._file));
    }

    CppStmt? init = null;
    if (this.Current.Kind != CppTokenKind.Semicolon) {
      init = this.LooksLikeVarDecl()
        ? this.ParseVarDecl(expectSemi: false)
        : new CppExprStmt(this.ParseExpression(), this.Current.Span(this._file));
    }

    this.Expect(CppTokenKind.Semicolon, "';'");
    CppExpr? cond = this.Current.Kind != CppTokenKind.Semicolon ? this.ParseExpression() : null;
    this.Expect(CppTokenKind.Semicolon, "';'");
    CppExpr? step = this.Current.Kind != CppTokenKind.RParen ? this.ParseExpression() : null;
    this.Expect(CppTokenKind.RParen, "')'");
    var body = this.ParseStatement();
    return new CppForStmt(init, cond, step, body, start.Span(this._file));
  }

  private bool LooksLikeRangeFor() {
    var saved = this._pos;
    var savedGreater = this._pendingGreater;
    try {
      this.MatchKeyword("const");
      if (this.MatchKeyword("auto"))
        this.Match(CppTokenKind.Amp);
      else if (this.IsTypeStart())
        this.ParseTypeRef(false);
      else
        return false;

      if (this.Current.Kind != CppTokenKind.Identifier)
        return false;

      this.Advance();
      return this.Current.Kind == CppTokenKind.Colon;
    } catch (CppDiagnosticException) {
      return false;
    } finally {
      this._pos = saved;
      this._pendingGreater = savedGreater;
    }
  }

  private CppSwitchStmt ParseSwitch(CppToken start) {
    this.Expect(CppTokenKind.LParen, "'('");
    var val = this.ParseExpression();
    this.Expect(CppTokenKind.RParen, "')'");
    this.Expect(CppTokenKind.LBrace, "'{'");
    var cases = new List<CppSwitchCase>();
    while (this.Current.Kind != CppTokenKind.RBrace && this.Current.Kind != CppTokenKind.EndOfFile) {
      var caseStart = this.Current;
      var labels = new List<CppExpr?>();
      while (this.IsKeyword("case") || this.IsKeyword("default")) {
        if (this.MatchKeyword("case")) {
          labels.Add(this.ParseExpression());
          this.Expect(CppTokenKind.Colon, "':'");
        } else {
          this.Advance();
          this.Expect(CppTokenKind.Colon, "':'");
          labels.Add(null);
        }
      }

      if (labels.Count == 0)
        throw this.SyntaxError(this.Current, "expected 'case' or 'default' in switch");

      var stmts = new List<CppStmt>();
      while (this.Current.Kind != CppTokenKind.RBrace
          && this.Current.Kind != CppTokenKind.EndOfFile
          && !this.IsKeyword("case")
          && !this.IsKeyword("default")) {
        if (this.Match(CppTokenKind.Semicolon))
          continue;

        stmts.Add(this.ParseStatement());
      }

      cases.Add(new(labels, stmts, caseStart.Span(this._file)));
    }

    this.Expect(CppTokenKind.RBrace, "'}'");
    return new(val, cases, start.Span(this._file));
  }

  private CppTryStmt ParseTry(CppToken start) {
    var body = this.ParseBlock();
    var catches = new List<CppCatchClause>();
    while (this.MatchKeyword("catch")) {
      var cStart = this.Current;
      this.Expect(CppTokenKind.LParen, "'('");
      CppTypeRef? exType = null;
      string? varName = null;
      if (!this.Match(CppTokenKind.Ellipsis)) {
        exType = this.ParseTypeRef(false);
        if (this.Current.Kind == CppTokenKind.Identifier)
          varName = this.Advance().Text;
      }

      this.Expect(CppTokenKind.RParen, "')'");
      var cBody = this.ParseBlock();
      catches.Add(new(exType, varName, cBody, cStart.Span(this._file)));
    }

    if (catches.Count == 0)
      throw this.SyntaxError(this.Current, "expected 'catch' after 'try' block");

    return new(body, catches, start.Span(this._file));
  }

  private bool LooksLikeVarDecl() {
    if (this.IsKeyword("const") || this.IsKeyword("constexpr") || this.IsKeyword("auto"))
      return true;

    if (!this.IsTypeStart())
      return false;

    var saved = this._pos;
    var savedGreater = this._pendingGreater;
    try {
      this.ParseTypeRef(false);
      return this.Current.Kind == CppTokenKind.Identifier;
    } catch (CppDiagnosticException) {
      return false;
    } finally {
      this._pos = saved;
      this._pendingGreater = savedGreater;
    }
  }

  private bool IsTypeStart() {
    if (this.Current.Kind != CppTokenKind.Identifier)
      return false;

    var text = this.Current.Text;
    if (text is "const" or "constexpr" or "auto")
      return true;

    if (_primitiveStarters.Contains(text) || this._knownTypes.Contains(text))
      return true;

    return text == "std" && this.Peek(1).Kind == CppTokenKind.ColonColon;
  }

  private CppVarDeclStmt ParseVarDecl(bool expectSemi) {
    var start = this.Current;
    var isConst = false;
    while (this.IsKeyword("const") || this.IsKeyword("constexpr")) {
      isConst = true;
      this.Advance();
    }

    CppTypeRef? type = null;
    if (this.MatchKeyword("auto")) {
      this.Match(CppTokenKind.Amp);
    } else {
      type = this.ParseTypeRef(isConst);
    }

    var name = this.ExpectIdentifier("variable name").Text;
    if (type is not null && this.Match(CppTokenKind.LBracket)) {
      int? size = this.Current.Kind != CppTokenKind.RBracket ? (int)this.ParseConstantSize() : -1;
      this.Expect(CppTokenKind.RBracket, "']'");
      type = type with { ArraySize = size };
    }

    CppExpr? init = null;
    if (this.Match(CppTokenKind.Assign)) {
      init = this.ParseExpression();
    } else if (this.Current.Kind == CppTokenKind.LBrace) {
      init = this.ParseInitList(type);
    } else if (this.Current.Kind == CppTokenKind.LParen) {
      this.Advance();
      var args = new List<CppExpr>();
      if (this.Current.Kind != CppTokenKind.RParen) {
        do
          args.Add(this.ParseExpression());
        while (this.Match(CppTokenKind.Comma));
      }

      this.Expect(CppTokenKind.RParen, "')'");
      init = new CppInitListExpr(type, args, start.Span(this._file));
    }

    if (expectSemi)
      this.Expect(CppTokenKind.Semicolon, "';'");

    return new(type, name, init, start.Span(this._file));
  }

  // ---------------------------------------------------------------- expressions

  private CppExpr ParseExpression() => this.ParseAssignment();

  private CppExpr ParseAssignment() {
    var left = this.ParseTernary();
    var op = this.Current.Kind switch {
      CppTokenKind.Assign => "=",
      CppTokenKind.PlusAssign => "+=",
      CppTokenKind.MinusAssign => "-=",
      CppTokenKind.StarAssign => "*=",
      CppTokenKind.SlashAssign => "/=",
      CppTokenKind.PercentAssign => "%=",
      CppTokenKind.AmpAssign => "&=",
      CppTokenKind.PipeAssign => "|=",
      CppTokenKind.CaretAssign => "^=",
      CppTokenKind.ShiftLeftAssign => "<<=",
      CppTokenKind.ShiftRightAssign => ">>=",
      _ => null
    };

    if (op is null)
      return left;

    var tok = this.Advance();
    var right = this.ParseAssignment();
    return new CppAssignExpr(op, left, right, tok.Span(this._file));
  }

  private CppExpr ParseTernary() {
    var cond = this.ParseBinary(1);
    if (!this.Match(CppTokenKind.Question))
      return cond;

    var thenExpr = this.ParseExpression();
    this.Expect(CppTokenKind.Colon, "':'");
    var elseExpr = this.ParseTernary();
    return new CppTernaryExpr(cond, thenExpr, elseExpr, cond.Span);
  }

  private CppExpr ParseBinary(int minPrec) {
    var left = this.ParseUnary();
    while (true) {
      var (prec, op) = this.BinaryPrecedence(this.Current.Kind);
      if (prec < minPrec)
        break;

      var tok = this.Advance();
      var right = this.ParseBinary(prec + 1);
      left = new CppBinaryExpr(op!, left, right, tok.Span(this._file));
    }

    return left;
  }

  private (int Prec, string? Op) BinaryPrecedence(CppTokenKind kind) => kind switch {
    CppTokenKind.PipePipe => (1, "||"),
    CppTokenKind.AmpAmp => (2, "&&"),
    CppTokenKind.Pipe => (3, "|"),
    CppTokenKind.Caret => (4, "^"),
    CppTokenKind.Amp => (5, "&"),
    CppTokenKind.EqualEqual => (6, "=="),
    CppTokenKind.BangEqual => (6, "!="),
    CppTokenKind.Less => (7, "<"),
    CppTokenKind.LessEqual => (7, "<="),
    CppTokenKind.Greater => (7, ">"),
    CppTokenKind.GreaterEqual => (7, ">="),
    CppTokenKind.ShiftLeft => (8, "<<"),
    CppTokenKind.ShiftRight => (8, ">>"),
    CppTokenKind.Plus => (9, "+"),
    CppTokenKind.Minus => (9, "-"),
    CppTokenKind.Star => (10, "*"),
    CppTokenKind.Slash => (10, "/"),
    CppTokenKind.Percent => (10, "%"),
    _ => (0, null)
  };

  private CppExpr ParseUnary() {
    var tok = this.Current;
    if (this.IsKeyword("new") || this.IsKeyword("delete"))
      throw this.Unsupported(tok, tok.Text);

    if (tok.Kind is CppTokenKind.Plus or CppTokenKind.Minus or CppTokenKind.Bang or CppTokenKind.Tilde
        or CppTokenKind.PlusPlus or CppTokenKind.MinusMinus or CppTokenKind.Star) {
      this.Advance();
      return new CppUnaryExpr(tok.Text, this.ParseUnary(), false, tok.Span(this._file));
    }

    if (tok.Kind == CppTokenKind.LParen && this.TryParseCStyleCast(out var castType)) {
      var operand = this.ParseUnary();
      return new CppCastExpr("c_cast", castType!, operand, tok.Span(this._file));
    }

    return this.ParsePostfix();
  }

  private bool TryParseCStyleCast(out CppTypeRef? castType) {
    var saved = this._pos;
    var savedGreater = this._pendingGreater;
    castType = null;
    this.Advance();
    if (!this.IsTypeStart()) {
      this._pos = saved;
      return false;
    }

    try {
      var parsed = this.ParseTypeRef(false);
      if (this.Current.Kind == CppTokenKind.RParen) {
        this.Advance();
        if (this.CanStartUnaryExpression()) {
          castType = parsed;
          return true;
        }
      }
    } catch (CppDiagnosticException) {
    }

    this._pos = saved;
    this._pendingGreater = savedGreater;
    return false;
  }

  private bool CanStartUnaryExpression() => this.Current.Kind is CppTokenKind.Identifier or CppTokenKind.IntLiteral
    or CppTokenKind.FloatLiteral or CppTokenKind.CharLiteral or CppTokenKind.StringLiteral
    or CppTokenKind.LParen or CppTokenKind.Plus or CppTokenKind.Minus or CppTokenKind.Bang
    or CppTokenKind.Tilde or CppTokenKind.PlusPlus or CppTokenKind.MinusMinus or CppTokenKind.Star;

  private CppExpr ParsePostfix() {
    var expr = this.ParsePrimary();
    while (true) {
      if (this.Current.Kind == CppTokenKind.LParen) {
        var args = this.ParseArgList();
        expr = new CppCallExpr(expr, [], args, expr.Span);
        continue;
      }

      if (this.Current.Kind == CppTokenKind.Less && expr is CppIdentExpr or CppMemberExpr && this.TryParseTemplateCallArgs(out var tArgs)) {
        var args = this.ParseArgList();
        expr = new CppCallExpr(expr, tArgs!, args, expr.Span);
        continue;
      }

      if (this.Match(CppTokenKind.LBracket)) {
        var idx = this.ParseExpression();
        this.Expect(CppTokenKind.RBracket, "']'");
        expr = new CppIndexExpr(expr, idx, expr.Span);
        continue;
      }

      if (this.Match(CppTokenKind.Dot) || this.Match(CppTokenKind.Arrow)) {
        var member = this.ExpectIdentifier("member name").Text;
        expr = new CppMemberExpr(expr, member, expr.Span);
        continue;
      }

      if (this.Current.Kind is CppTokenKind.PlusPlus or CppTokenKind.MinusMinus) {
        var op = this.Advance();
        expr = new CppUnaryExpr(op.Text, expr, true, op.Span(this._file));
        continue;
      }

      break;
    }

    return expr;
  }

  private bool TryParseTemplateCallArgs(out List<CppTypeArg>? typeArgs) {
    var saved = this._pos;
    var savedGreater = this._pendingGreater;
    typeArgs = null;
    try {
      this.Advance();
      var parsed = new List<CppTypeArg>();
      do {
        if (this.Current.Kind == CppTokenKind.IntLiteral)
          parsed.Add(new(null, this.ParseConstantSize()));
        else if (this.IsTypeStart())
          parsed.Add(new(this.ParseTypeRef(false), null));
        else {
          this._pos = saved;
          this._pendingGreater = savedGreater;
          return false;
        }
      } while (this.Match(CppTokenKind.Comma));

      this.ExpectClosingAngle();
      if (this.Current.Kind == CppTokenKind.LParen) {
        typeArgs = parsed;
        return true;
      }
    } catch (CppDiagnosticException) {
    }

    this._pos = saved;
    this._pendingGreater = savedGreater;
    return false;
  }

  private List<CppExpr> ParseArgList() {
    this.Expect(CppTokenKind.LParen, "'('");
    var args = new List<CppExpr>();
    if (this.Current.Kind != CppTokenKind.RParen) {
      do
        args.Add(this.ParseExpression());
      while (this.Match(CppTokenKind.Comma));
    }

    this.Expect(CppTokenKind.RParen, "')'");
    return args;
  }

  private CppExpr ParsePrimary() {
    var tok = this.Current;
    switch (tok.Kind) {
      case CppTokenKind.IntLiteral: {
        this.Advance();
        var (val, isUnsigned, width) = this.ParseIntToken(tok);
        return new CppIntLiteral(val, isUnsigned, width, tok.Span(this._file));
      }
      case CppTokenKind.FloatLiteral: {
        this.Advance();
        var (val, isF32) = this.ParseFloatToken(tok);
        return new CppFloatLiteral(val, isF32, tok.Span(this._file));
      }
      case CppTokenKind.CharLiteral:
        this.Advance();
        return new CppCharLiteral((sbyte)tok.Text[0], tok.Span(this._file));
      case CppTokenKind.StringLiteral: {
        var text = this.Advance().Text;
        while (this.Current.Kind == CppTokenKind.StringLiteral)
          text += this.Advance().Text;

        return new CppStringLiteral(text, tok.Span(this._file));
      }
      case CppTokenKind.LParen: {
        this.Advance();
        var inner = this.ParseExpression();
        this.Expect(CppTokenKind.RParen, "')'");
        return inner;
      }
      case CppTokenKind.LBrace:
        return this.ParseInitList(null);
      case CppTokenKind.Identifier:
      case CppTokenKind.ColonColon:
        return this.ParseIdentifierPrimary(tok);
      default:
        throw this.SyntaxError(tok, $"unexpected token '{tok.Text}' in expression");
    }
  }

  private CppExpr ParseIdentifierPrimary(CppToken tok) {
    if (this.MatchKeyword("true"))
      return new CppBoolLiteral(true, tok.Span(this._file));

    if (this.MatchKeyword("false"))
      return new CppBoolLiteral(false, tok.Span(this._file));

    if (this.MatchKeyword("nullptr"))
      return new CppNullLiteral(false, tok.Span(this._file));

    if (this.MatchKeyword("sizeof")) {
      this.Expect(CppTokenKind.LParen, "'('");
      var target = this.ParseTypeRef(false);
      this.Expect(CppTokenKind.RParen, "')'");
      return new CppSizeofExpr(target, tok.Span(this._file));
    }

    if (this.IsKeyword("static_cast") || this.IsKeyword("reinterpret_cast") || this.IsKeyword("const_cast") || this.IsKeyword("dynamic_cast")) {
      var kind = this.Advance().Text;
      if (kind is "const_cast" or "dynamic_cast")
        throw this.Unsupported(tok, kind);

      this.Expect(CppTokenKind.Less, "'<'");
      var target = this.ParseTypeRef(false);
      this.ExpectClosingAngle();
      this.Expect(CppTokenKind.LParen, "'('");
      var operand = this.ParseExpression();
      this.Expect(CppTokenKind.RParen, "')'");
      return new CppCastExpr(kind, target, operand, tok.Span(this._file));
    }

    var name = this.ParseQualifiedName();
    if (name is "std::nullopt" or "nullopt")
      return new CppNullLiteral(true, tok.Span(this._file));

    if (name is "std::bit_cast" or "bit_cast") {
      this.Expect(CppTokenKind.Less, "'<'");
      var target = this.ParseTypeRef(false);
      this.ExpectClosingAngle();
      this.Expect(CppTokenKind.LParen, "'('");
      var operand = this.ParseExpression();
      this.Expect(CppTokenKind.RParen, "')'");
      return new CppCastExpr("bit_cast", target, operand, tok.Span(this._file));
    }

    if (this.Current.Kind == CppTokenKind.LBrace && (this._knownTypes.Contains(name) || _primitiveStarters.Contains(name)))
      return this.ParseInitList(new CppTypeRef(name, [], false, false, false, null, tok.Span(this._file)));

    if (this.Current.Kind == CppTokenKind.Less && this.TryParseTypeBraceInit(name, tok, out var initList))
      return initList!;

    return new CppIdentExpr(name, tok.Span(this._file));
  }

  private bool TryParseTypeBraceInit(string name, CppToken start, out CppExpr? result) {
    var saved = this._pos;
    var savedGreater = this._pendingGreater;
    result = null;
    if (name is not ("std::array" or "std::vector" or "std::optional" or "array" or "vector" or "optional"))
      return false;

    try {
      this.Advance();
      var typeArgs = new List<CppTypeArg>();
      do {
        if (this.Current.Kind == CppTokenKind.IntLiteral)
          typeArgs.Add(new(null, this.ParseConstantSize()));
        else
          typeArgs.Add(new(this.ParseTypeRef(false), null));
      } while (this.Match(CppTokenKind.Comma));

      this.ExpectClosingAngle();
      if (this.Current.Kind == CppTokenKind.LBrace) {
        result = this.ParseInitList(new CppTypeRef(name, typeArgs, false, false, false, null, start.Span(this._file)));
        return true;
      }
    } catch (CppDiagnosticException) {
    }

    this._pos = saved;
    this._pendingGreater = savedGreater;
    return false;
  }

  private CppInitListExpr ParseInitList(CppTypeRef? type) {
    var start = this.Expect(CppTokenKind.LBrace, "'{'");
    var elements = new List<CppExpr>();
    if (this.Current.Kind != CppTokenKind.RBrace) {
      do {
        if (this.Current.Kind == CppTokenKind.RBrace)
          break;

        elements.Add(this.ParseExpression());
      } while (this.Match(CppTokenKind.Comma));
    }

    this.Expect(CppTokenKind.RBrace, "'}'");
    return new(type, elements, start.Span(this._file));
  }

  private (ulong Value, bool IsUnsigned, int Width) ParseIntToken(CppToken tok) {
    var text = tok.Text.Replace("'", "", StringComparison.Ordinal);
    var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    var minIdx = isHex ? 2 : 0;
    var suffixIdx = text.Length;
    while (suffixIdx > minIdx && text[suffixIdx - 1] is 'u' or 'U' or 'l' or 'L' or 'z' or 'Z')
      --suffixIdx;

    var digits = text[..suffixIdx];
    var suffix = text[suffixIdx..].ToLowerInvariant();
    var isUnsigned = suffix.Contains('u');
    var width = suffix.Contains('l') || suffix.Contains('z') ? 64 : 32;

    ulong value;
    var isNonDecimal = isHex || digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase) || (digits.Length > 1 && digits[0] == '0');
    try {
      value = isHex ? Convert.ToUInt64(digits[2..], 16)
        : digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(digits[2..], 2)
        : digits.Length > 1 && digits[0] == '0' ? Convert.ToUInt64(digits[1..], 8)
        : ulong.Parse(digits, CultureInfo.InvariantCulture);
    } catch (Exception) {
      throw this.SyntaxError(tok, $"invalid integer literal '{tok.Text}'");
    }

    if (width == 32 && !isUnsigned && value > int.MaxValue) {
      if (isNonDecimal && value <= uint.MaxValue)
        isUnsigned = true;
      else
        width = 64;
    }

    if (width == 64 && !isUnsigned && value > long.MaxValue)
      isUnsigned = true;

    return (value, isUnsigned, width);
  }

  private (double Value, bool IsFloat32) ParseFloatToken(CppToken tok) {
    var text = tok.Text.Replace("'", "", StringComparison.Ordinal);
    var isF32 = text.EndsWith('f') || text.EndsWith('F');
    var clean = isF32 || text.EndsWith('l') || text.EndsWith('L') ? text[..^1] : text;
    if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
      throw this.SyntaxError(tok, $"invalid floating-point literal '{tok.Text}'");

    return (value, isF32);
  }

}
