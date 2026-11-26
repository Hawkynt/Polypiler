using System;
using System.Collections.Generic;

namespace Polypiler.Core.Frontends.VBScript.Parsing;

internal sealed class VBScriptParser {

  private readonly string _file;
  private readonly IReadOnlyList<VBScriptToken> _tokens;
  private readonly List<Diagnostic> _diagnostics;
  private int _pos;

  private VBScriptParser(string file, IReadOnlyList<VBScriptToken> tokens, List<Diagnostic> diagnostics) {
    this._file = file;
    this._tokens = tokens;
    this._diagnostics = diagnostics;
  }

  public static VbsProgram Parse(string file, IReadOnlyList<VBScriptToken> tokens, List<Diagnostic> diagnostics)
    => new VBScriptParser(file, tokens, diagnostics).ParseProgram();

  private VBScriptToken Current => this._tokens[this._pos];

  private VBScriptToken Peek(int offset) {
    var index = this._pos + offset;
    return index < this._tokens.Count ? this._tokens[index] : this._tokens[^1];
  }

  private VBScriptToken Advance() {
    var tok = this._tokens[this._pos];
    if (this._pos + 1 < this._tokens.Count)
      ++this._pos;
    return tok;
  }

  private bool Match(VBScriptTokenKind kind) {
    if (this.Current.Kind != kind)
      return false;
    this.Advance();
    return true;
  }

  private VBScriptToken Expect(VBScriptTokenKind kind, string description) {
    if (this.Current.Kind == kind)
      return this.Advance();

    this.Error(this.Current, $"expected {description}, found '{this.Current.Text}'");
    return this.Current;
  }

  private void Error(VBScriptToken token, string message)
    => this._diagnostics.Add(Diagnostic.Create(VBScriptFrontend.SyntaxError, new(this._file, token.Line, token.Column, token.Line, token.Column), message));

  private void Unsupported(VBScriptToken token, string construct)
    => this._diagnostics.Add(Diagnostic.Create(VBScriptFrontend.UnsupportedConstruct, new(this._file, token.Line, token.Column, token.Line, token.Column), construct));

  private void SkipTerminators() {
    while (this.Current.Kind is VBScriptTokenKind.NewLine or VBScriptTokenKind.Colon)
      this.Advance();
  }

  private List<string> CollectDocComments() {
    var docs = new List<string>();
    while (this.Current.Kind is VBScriptTokenKind.NewLine or VBScriptTokenKind.Colon or VBScriptTokenKind.DocComment) {
      if (this.Current.Kind == VBScriptTokenKind.DocComment)
        docs.Add(this.Current.StringValue!);
      this.Advance();
    }
    return docs;
  }

  private void ConsumeEndOfStatement() {
    if (this.Current.Kind is VBScriptTokenKind.NewLine or VBScriptTokenKind.Colon or VBScriptTokenKind.EndOfFile or VBScriptTokenKind.KwElse)
      return;

    this.Error(this.Current, $"unexpected token '{this.Current.Text}' at end of statement");
    while (!this.Current.IsStatementTerminator)
      this.Advance();
  }

  private VbsProgram ParseProgram() {
    var optionExplicit = false;
    var statements = new List<VbsStmt>();

    while (this.Current.Kind != VBScriptTokenKind.EndOfFile) {
      var docs = this.CollectDocComments();
      if (this.Current.Kind == VBScriptTokenKind.EndOfFile)
        break;

      if (this.Current.Kind == VBScriptTokenKind.KwOption) {
        this.Advance();
        this.Expect(VBScriptTokenKind.KwExplicit, "'Explicit'");
        optionExplicit = true;
        this.ConsumeEndOfStatement();
        continue;
      }

      var stmt = this.ParseTopLevelOrStatement(docs, inClass: false);
      if (stmt is not null)
        statements.Add(stmt);
      this.ConsumeEndOfStatement();
    }

    return new(optionExplicit, statements);
  }

  private VbsStmt? ParseTopLevelOrStatement(List<string> docs, bool inClass) {
    var tok = this.Current;
    switch (tok.Kind) {
      case VBScriptTokenKind.KwPublic:
      case VBScriptTokenKind.KwPrivate: {
        var isPublic = tok.Kind == VBScriptTokenKind.KwPublic;
        this.Advance();
        if (isPublic && this.Current.Kind == VBScriptTokenKind.KwDefault)
          this.Advance();

        return this.Current.Kind switch {
          VBScriptTokenKind.KwConst => this.ParseConstDecl(isPublic),
          VBScriptTokenKind.KwFunction => this.ParseProcedure(VbsProcedureKind.Function, isPublic, docs),
          VBScriptTokenKind.KwSub => this.ParseProcedure(VbsProcedureKind.Sub, isPublic, docs),
          VBScriptTokenKind.KwProperty => this.ParseProperty(isPublic, docs),
          _ => this.ParseDimDecl(isPublic, consumeDimKeyword: false)
        };
      }
      case VBScriptTokenKind.KwFunction:
        return this.ParseProcedure(VbsProcedureKind.Function, isPublic: true, docs);
      case VBScriptTokenKind.KwSub:
        return this.ParseProcedure(VbsProcedureKind.Sub, isPublic: true, docs);
      case VBScriptTokenKind.KwProperty:
        return this.ParseProperty(isPublic: true, docs);
      case VBScriptTokenKind.KwClass when !inClass:
        return this.ParseClassDecl();
      case VBScriptTokenKind.KwConst:
        return this.ParseConstDecl(isPublic: true);
      case VBScriptTokenKind.KwDim:
        return this.ParseDimDecl(isPublic: !inClass, consumeDimKeyword: true);
      default:
        return this.ParseStatement();
    }
  }

  private VbsConstDecl ParseConstDecl(bool isPublic) {
    var kw = this.Expect(VBScriptTokenKind.KwConst, "'Const'");
    var name = this.ExpectIdentifier("constant name");
    this.Expect(VBScriptTokenKind.Equal, "'='");
    var value = this.ParseExpression();
    return new(name.Text, value, isPublic, kw.Line, kw.Column);
  }

  private VbsDimDecl ParseDimDecl(bool isPublic, bool consumeDimKeyword) {
    var startTok = consumeDimKeyword ? this.Expect(VBScriptTokenKind.KwDim, "'Dim'") : this.Current;
    var vars = this.ParseDimVarList();
    return new(vars, isPublic, startTok.Line, startTok.Column);
  }

  private List<VbsDimVar> ParseDimVarList() {
    var vars = new List<VbsDimVar>();
    do {
      var name = this.ExpectIdentifier("variable name");
      VbsExpr? bound = null;
      var isArray = false;
      if (this.Match(VBScriptTokenKind.LParen)) {
        isArray = true;
        if (this.Current.Kind != VBScriptTokenKind.RParen) {
          bound = this.ParseExpression();
          if (this.Match(VBScriptTokenKind.Comma)) {
            this.Unsupported(name, "multi-dimensional arrays");
            while (this.Current.Kind is not (VBScriptTokenKind.RParen or VBScriptTokenKind.EndOfFile))
              this.Advance();
          }
        }
        this.Expect(VBScriptTokenKind.RParen, "')'");
      }
      vars.Add(new(name.Text, bound, isArray, name.Line, name.Column));
    } while (this.Match(VBScriptTokenKind.Comma));
    return vars;
  }

  private VbsReDimStmt ParseReDimStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwReDim, "'ReDim'");
    var preserve = this.Match(VBScriptTokenKind.KwPreserve);
    var vars = this.ParseDimVarList();
    return new(preserve, vars, kw.Line, kw.Column);
  }

  private VbsProcedureDecl ParseProperty(bool isPublic, List<string> docs) {
    var kw = this.Expect(VBScriptTokenKind.KwProperty, "'Property'");
    VbsProcedureKind kind;
    if (this.Match(VBScriptTokenKind.KwGet)) {
      kind = VbsProcedureKind.PropertyGet;
    } else if (this.Match(VBScriptTokenKind.KwLet) || this.Match(VBScriptTokenKind.KwSet)) {
      kind = VbsProcedureKind.PropertyLet;
    } else {
      this.Error(this.Current, "expected 'Get', 'Let', or 'Set' after 'Property'");
      kind = VbsProcedureKind.PropertyGet;
    }

    return this.ParseProcedureTail(kind, isPublic, docs, kw);
  }

  private VbsProcedureDecl ParseProcedure(VbsProcedureKind kind, bool isPublic, List<string> docs) {
    var kw = this.Advance();
    return this.ParseProcedureTail(kind, isPublic, docs, kw);
  }

  private VbsProcedureDecl ParseProcedureTail(VbsProcedureKind kind, bool isPublic, List<string> docs, VBScriptToken kw) {
    var name = this.ExpectIdentifier("procedure name");
    var parameters = new List<VbsParam>();
    if (this.Match(VBScriptTokenKind.LParen)) {
      if (this.Current.Kind != VBScriptTokenKind.RParen) {
        do {
          var byRef = true;
          if (this.Match(VBScriptTokenKind.KwByVal))
            byRef = false;
          else
            this.Match(VBScriptTokenKind.KwByRef);

          var pName = this.ExpectIdentifier("parameter name");
          if (this.Match(VBScriptTokenKind.LParen))
            this.Expect(VBScriptTokenKind.RParen, "')'");
          parameters.Add(new(pName.Text, byRef, pName.Line, pName.Column));
        } while (this.Match(VBScriptTokenKind.Comma));
      }
      this.Expect(VBScriptTokenKind.RParen, "')'");
    }

    var endKeyword = kind switch {
      VbsProcedureKind.Function => VBScriptTokenKind.KwFunction,
      VbsProcedureKind.Sub => VBScriptTokenKind.KwSub,
      _ => VBScriptTokenKind.KwProperty
    };
    // Also accept '@param / '@return doc comments placed at the top of the procedure body.
    docs.AddRange(this.CollectDocComments());
    var body = this.ParseBlockUntil(t => t.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == endKeyword);
    this.Expect(VBScriptTokenKind.KwEnd, "'End'");
    this.Expect(endKeyword, endKeyword.ToString()[2..]);
    return new(kind, name.Text, parameters, body, isPublic, docs, kw.Line, kw.Column);
  }

  private VbsClassDecl ParseClassDecl() {
    var kw = this.Expect(VBScriptTokenKind.KwClass, "'Class'");
    var name = this.ExpectIdentifier("class name");
    var fields = new List<VbsDimDecl>();
    var members = new List<VbsProcedureDecl>();

    while (this.Current.Kind != VBScriptTokenKind.EndOfFile) {
      var docs = this.CollectDocComments();
      if (this.Current.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == VBScriptTokenKind.KwClass)
        break;

      var item = this.ParseTopLevelOrStatement(docs, inClass: true);
      if (item is VbsDimDecl field)
        fields.Add(field);
      else if (item is VbsProcedureDecl proc)
        members.Add(proc);
      else
        this.Error(this.Current, "unexpected statement inside Class declaration");

      this.ConsumeEndOfStatement();
    }

    this.Expect(VBScriptTokenKind.KwEnd, "'End'");
    this.Expect(VBScriptTokenKind.KwClass, "'Class'");
    return new(name.Text, fields, members, kw.Line, kw.Column);
  }

  private List<VbsStmt> ParseBlockUntil(Func<VBScriptToken, bool> stop) {
    var stmts = new List<VbsStmt>();
    while (this.Current.Kind != VBScriptTokenKind.EndOfFile) {
      this.SkipTerminators();
      while (this.Current.Kind == VBScriptTokenKind.DocComment) {
        this.Advance();
        this.SkipTerminators();
      }

      if (this.Current.Kind == VBScriptTokenKind.EndOfFile || stop(this.Current))
        break;

      var stmt = this.ParseStatement();
      if (stmt is not null)
        stmts.Add(stmt);
      this.ConsumeEndOfStatement();
    }
    return stmts;
  }

  private VbsStmt? ParseStatement() {
    var tok = this.Current;
    switch (tok.Kind) {
      case VBScriptTokenKind.KwDim:
        return this.ParseDimDecl(isPublic: false, consumeDimKeyword: true);
      case VBScriptTokenKind.KwConst:
        return this.ParseConstDecl(isPublic: false);
      case VBScriptTokenKind.KwReDim:
        return this.ParseReDimStmt();
      case VBScriptTokenKind.KwIf:
        return this.ParseIfStmt();
      case VBScriptTokenKind.KwSelect:
        return this.ParseSelectStmt();
      case VBScriptTokenKind.KwFor:
        return this.ParseForStmt();
      case VBScriptTokenKind.KwDo:
        return this.ParseDoLoopStmt();
      case VBScriptTokenKind.KwWhile:
        return this.ParseWhileWendStmt();
      case VBScriptTokenKind.KwExit:
        return this.ParseExitStmt();
      case VBScriptTokenKind.KwCall:
        return this.ParseExplicitCallStmt();
      case VBScriptTokenKind.KwSet:
        return this.ParseSetStmt();
      case VBScriptTokenKind.KwOn:
        return this.ParseOnErrorStmt();
      case VBScriptTokenKind.KwWith: {
        this.Advance();
        while (this.Current.Kind != VBScriptTokenKind.EndOfFile
            && !(this.Current.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == VBScriptTokenKind.KwWith))
          this.Advance();
        if (this.Current.Kind == VBScriptTokenKind.KwEnd) {
          this.Advance();
          this.Advance();
        }
        return new VbsUnsupportedStmt(tok.Text, tok.Line, tok.Column);
      }
      case VBScriptTokenKind.KwErase:
      case VBScriptTokenKind.KwExecute:
      case VBScriptTokenKind.KwExecuteGlobal:
      case VBScriptTokenKind.KwEval: {
        this.Advance();
        while (!this.Current.IsStatementTerminator)
          this.Advance();
        return new VbsUnsupportedStmt(tok.Text, tok.Line, tok.Column);
      }
      default:
        return this.ParseAssignOrCallStmt();
    }
  }

  private VbsIfStmt ParseIfStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwIf, "'If'");
    var cond = this.ParseExpression();
    this.Expect(VBScriptTokenKind.KwThen, "'Then'");

    // Single-line If: If cond Then stmt [: stmt] [Else stmt [: stmt]]
    if (this.Current.Kind is not (VBScriptTokenKind.NewLine or VBScriptTokenKind.EndOfFile)) {
      var thenSingle = this.ParseInlineStatements(stopAtElse: true);
      List<VbsStmt>? elseSingle = null;
      if (this.Match(VBScriptTokenKind.KwElse))
        elseSingle = this.ParseInlineStatements(stopAtElse: false);
      return new(cond, thenSingle, [], elseSingle, kw.Line, kw.Column);
    }

    // Multi-line If ... ElseIf ... Else ... End If
    var thenBody = this.ParseBlockUntil(t =>
      t.Kind is VBScriptTokenKind.KwElseIf or VBScriptTokenKind.KwElse
      || (t.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == VBScriptTokenKind.KwIf));

    var elseIfs = new List<VbsElseIf>();
    while (this.Match(VBScriptTokenKind.KwElseIf)) {
      var eiCond = this.ParseExpression();
      this.Expect(VBScriptTokenKind.KwThen, "'Then'");
      var eiBody = this.ParseBlockUntil(t =>
        t.Kind is VBScriptTokenKind.KwElseIf or VBScriptTokenKind.KwElse
        || (t.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == VBScriptTokenKind.KwIf));
      elseIfs.Add(new(eiCond, eiBody));
    }

    List<VbsStmt>? elseBody = null;
    if (this.Match(VBScriptTokenKind.KwElse))
      elseBody = this.ParseBlockUntil(t => t.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == VBScriptTokenKind.KwIf);

    this.Expect(VBScriptTokenKind.KwEnd, "'End'");
    this.Expect(VBScriptTokenKind.KwIf, "'If'");
    return new(cond, thenBody, elseIfs, elseBody, kw.Line, kw.Column);
  }

  private List<VbsStmt> ParseInlineStatements(bool stopAtElse) {
    var stmts = new List<VbsStmt>();
    while (this.Current.Kind is not (VBScriptTokenKind.NewLine or VBScriptTokenKind.EndOfFile)) {
      if (stopAtElse && this.Current.Kind == VBScriptTokenKind.KwElse)
        break;
      if (this.Match(VBScriptTokenKind.Colon))
        continue;
      var stmt = this.ParseStatement();
      if (stmt is not null)
        stmts.Add(stmt);
      if (this.Current.Kind == VBScriptTokenKind.Colon)
        this.Advance();
      else
        break;
    }
    return stmts;
  }

  private VbsSelectStmt ParseSelectStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwSelect, "'Select'");
    this.Expect(VBScriptTokenKind.KwCase, "'Case'");
    var expr = this.ParseExpression();
    this.SkipTerminators();

    var cases = new List<VbsCaseClause>();
    List<VbsStmt>? elseBody = null;

    while (this.Current.Kind == VBScriptTokenKind.KwCase) {
      this.Advance();
      if (this.Match(VBScriptTokenKind.KwElse)) {
        elseBody = this.ParseBlockUntil(t => t.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == VBScriptTokenKind.KwSelect);
        break;
      }

      var exprs = new List<VbsExpr>();
      do {
        exprs.Add(this.ParseExpression());
      } while (this.Match(VBScriptTokenKind.Comma));

      var body = this.ParseBlockUntil(t =>
        t.Kind == VBScriptTokenKind.KwCase
        || (t.Kind == VBScriptTokenKind.KwEnd && this.Peek(1).Kind == VBScriptTokenKind.KwSelect));
      cases.Add(new(exprs, body));
    }

    this.Expect(VBScriptTokenKind.KwEnd, "'End'");
    this.Expect(VBScriptTokenKind.KwSelect, "'Select'");
    return new(expr, cases, elseBody, kw.Line, kw.Column);
  }

  private VbsStmt ParseForStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwFor, "'For'");
    if (this.Match(VBScriptTokenKind.KwEach)) {
      var item = this.ExpectIdentifier("loop variable");
      this.Expect(VBScriptTokenKind.KwIn, "'In'");
      var collection = this.ParseExpression();
      var feBody = this.ParseBlockUntil(t => t.Kind == VBScriptTokenKind.KwNext);
      this.Expect(VBScriptTokenKind.KwNext, "'Next'");
      if (this.Current.Kind == VBScriptTokenKind.Identifier)
        this.Advance();
      return new VbsForEachStmt(item.Text, collection, feBody, kw.Line, kw.Column);
    }

    var varName = this.ExpectIdentifier("loop variable");
    this.Expect(VBScriptTokenKind.Equal, "'='");
    var start = this.ParseExpression();
    this.Expect(VBScriptTokenKind.KwTo, "'To'");
    var end = this.ParseExpression();
    VbsExpr? step = null;
    if (this.Match(VBScriptTokenKind.KwStep))
      step = this.ParseExpression();

    var body = this.ParseBlockUntil(t => t.Kind == VBScriptTokenKind.KwNext);
    this.Expect(VBScriptTokenKind.KwNext, "'Next'");
    if (this.Current.Kind == VBScriptTokenKind.Identifier)
      this.Advance();
    return new VbsForStmt(varName.Text, start, end, step, body, kw.Line, kw.Column);
  }

  private VbsDoLoopStmt ParseDoLoopStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwDo, "'Do'");
    VbsExpr? preCond = null;
    var isUntil = false;
    if (this.Match(VBScriptTokenKind.KwWhile)) {
      preCond = this.ParseExpression();
    } else if (this.Match(VBScriptTokenKind.KwUntil)) {
      isUntil = true;
      preCond = this.ParseExpression();
    }

    var body = this.ParseBlockUntil(t => t.Kind == VBScriptTokenKind.KwLoop);
    this.Expect(VBScriptTokenKind.KwLoop, "'Loop'");

    VbsExpr? postCond = null;
    var postUntil = false;
    if (this.Match(VBScriptTokenKind.KwWhile)) {
      postCond = this.ParseExpression();
    } else if (this.Match(VBScriptTokenKind.KwUntil)) {
      postUntil = true;
      postCond = this.ParseExpression();
    }

    if (preCond is not null && postCond is not null)
      this.Error(kw, "Do...Loop cannot specify both a pre-condition and a post-condition");

    return postCond is not null
      ? new(IsPostCondition: true, IsUntil: postUntil, Condition: postCond, Body: body, kw.Line, kw.Column)
      : new(IsPostCondition: false, IsUntil: isUntil, Condition: preCond, Body: body, kw.Line, kw.Column);
  }

  private VbsDoLoopStmt ParseWhileWendStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwWhile, "'While'");
    var cond = this.ParseExpression();
    var body = this.ParseBlockUntil(t => t.Kind == VBScriptTokenKind.KwWend);
    this.Expect(VBScriptTokenKind.KwWend, "'Wend'");
    return new(IsPostCondition: false, IsUntil: false, Condition: cond, Body: body, kw.Line, kw.Column);
  }

  private VbsExitStmt ParseExitStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwExit, "'Exit'");
    var kind = this.Current.Kind switch {
      VBScriptTokenKind.KwFor => VbsExitKind.For,
      VBScriptTokenKind.KwDo => VbsExitKind.Do,
      VBScriptTokenKind.KwFunction => VbsExitKind.Function,
      VBScriptTokenKind.KwSub => VbsExitKind.Sub,
      VBScriptTokenKind.KwProperty => VbsExitKind.Property,
      _ => (VbsExitKind)(-1)
    };
    if ((int)kind < 0) {
      this.Error(this.Current, $"expected For, Do, Function, Sub, or Property after Exit, found '{this.Current.Text}'");
      return new(VbsExitKind.Sub, kw.Line, kw.Column);
    }

    this.Advance();
    return new(kind, kw.Line, kw.Column);
  }

  private VbsCallStmt ParseExplicitCallStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwCall, "'Call'");
    var target = this.ParsePostfix();
    if (target is VbsCallOrIndexExpr call)
      return new(call.Target, call.Arguments, kw.Line, kw.Column);
    return new(target, [], kw.Line, kw.Column);
  }

  private VbsAssignStmt ParseSetStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwSet, "'Set'");
    var target = this.ParsePostfix();
    this.Expect(VBScriptTokenKind.Equal, "'='");
    var value = this.ParseExpression();
    return new(target, value, IsSet: true, kw.Line, kw.Column);
  }

  private VbsStmt ParseOnErrorStmt() {
    var kw = this.Expect(VBScriptTokenKind.KwOn, "'On'");
    this.Expect(VBScriptTokenKind.KwError, "'Error'");
    if (this.Match(VBScriptTokenKind.KwResume)) {
      this.Expect(VBScriptTokenKind.KwNext, "'Next'");
      return new VbsOnErrorStmt(ResumeNext: true, kw.Line, kw.Column);
    }

    if (this.Match(VBScriptTokenKind.KwGoTo)) {
      if (this.Current is { Kind: VBScriptTokenKind.IntLiteral, LongValue: 0 }) {
        this.Advance();
        return new VbsOnErrorStmt(ResumeNext: false, kw.Line, kw.Column);
      }

      while (!this.Current.IsStatementTerminator)
        this.Advance();
      return new VbsUnsupportedStmt("On Error GoTo label", kw.Line, kw.Column);
    }

    this.Error(this.Current, "expected 'Resume Next' or 'GoTo 0' after 'On Error'");
    return new VbsOnErrorStmt(ResumeNext: false, kw.Line, kw.Column);
  }

  private VbsStmt ParseAssignOrCallStmt() {
    var start = this.Current;
    var lhs = this.ParsePostfix();
    if (this.Match(VBScriptTokenKind.Equal)) {
      var value = this.ParseExpression();
      if (start.Kind == VBScriptTokenKind.LParen)
        return new VbsUnsupportedStmt("parenthesized assignment target", start.Line, start.Column);
      return new VbsAssignStmt(lhs, value, IsSet: false, start.Line, start.Column);
    }

    // Parenthesis-less Sub/Method call: Foo a, b  or  obj.Method a, b
    if (this.Current.Kind is not (VBScriptTokenKind.NewLine or VBScriptTokenKind.Colon or VBScriptTokenKind.EndOfFile or VBScriptTokenKind.KwElse)) {
      var args = new List<VbsExpr>();
      do {
        args.Add(this.ParseExpression());
      } while (this.Match(VBScriptTokenKind.Comma));
      return new VbsCallStmt(lhs, args, start.Line, start.Column);
    }

    if (lhs is VbsCallOrIndexExpr call)
      return new VbsCallStmt(call.Target, call.Arguments, start.Line, start.Column);

    return new VbsCallStmt(lhs, [], start.Line, start.Column);
  }

  // ---- Expressions (VBScript 5.8 precedence, lowest to highest) ----

  private VbsExpr ParseExpression() => this.ParseXor();

  private VbsExpr ParseXor() {
    var left = this.ParseOr();
    while (this.Current.Kind == VBScriptTokenKind.KwXor) {
      var op = this.Advance();
      left = new VbsBinaryExpr(VbsBinaryOp.Xor, left, this.ParseOr(), op.Line, op.Column);
    }
    return left;
  }

  private VbsExpr ParseOr() {
    var left = this.ParseAnd();
    while (this.Current.Kind == VBScriptTokenKind.KwOr) {
      var op = this.Advance();
      left = new VbsBinaryExpr(VbsBinaryOp.Or, left, this.ParseAnd(), op.Line, op.Column);
    }
    return left;
  }

  private VbsExpr ParseAnd() {
    var left = this.ParseNot();
    while (this.Current.Kind == VBScriptTokenKind.KwAnd) {
      var op = this.Advance();
      left = new VbsBinaryExpr(VbsBinaryOp.And, left, this.ParseNot(), op.Line, op.Column);
    }
    return left;
  }

  private VbsExpr ParseNot() {
    if (this.Current.Kind == VBScriptTokenKind.KwNot) {
      var op = this.Advance();
      return new VbsUnaryExpr(VbsUnaryOp.Not, this.ParseNot(), op.Line, op.Column);
    }
    return this.ParseComparison();
  }

  private VbsExpr ParseComparison() {
    var left = this.ParseConcat();
    while (true) {
      VbsBinaryOp? op = this.Current.Kind switch {
        VBScriptTokenKind.Equal => VbsBinaryOp.Equal,
        VBScriptTokenKind.NotEqual => VbsBinaryOp.NotEqual,
        VBScriptTokenKind.Less => VbsBinaryOp.Less,
        VBScriptTokenKind.LessEqual => VbsBinaryOp.LessEqual,
        VBScriptTokenKind.Greater => VbsBinaryOp.Greater,
        VBScriptTokenKind.GreaterEqual => VbsBinaryOp.GreaterEqual,
        VBScriptTokenKind.KwIs => VbsBinaryOp.Is,
        _ => null
      };
      if (op is null)
        return left;
      var tok = this.Advance();
      left = new VbsBinaryExpr(op.Value, left, this.ParseConcat(), tok.Line, tok.Column);
    }
  }

  private VbsExpr ParseConcat() {
    var left = this.ParseAdditive();
    while (this.Current.Kind == VBScriptTokenKind.Ampersand) {
      var tok = this.Advance();
      left = new VbsBinaryExpr(VbsBinaryOp.Concat, left, this.ParseAdditive(), tok.Line, tok.Column);
    }
    return left;
  }

  private VbsExpr ParseAdditive() {
    var left = this.ParseMod();
    while (this.Current.Kind is VBScriptTokenKind.Plus or VBScriptTokenKind.Minus) {
      var tok = this.Advance();
      var op = tok.Kind == VBScriptTokenKind.Plus ? VbsBinaryOp.Add : VbsBinaryOp.Subtract;
      left = new VbsBinaryExpr(op, left, this.ParseMod(), tok.Line, tok.Column);
    }
    return left;
  }

  private VbsExpr ParseMod() {
    var left = this.ParseIntDivide();
    while (this.Current.Kind == VBScriptTokenKind.KwMod) {
      var tok = this.Advance();
      left = new VbsBinaryExpr(VbsBinaryOp.Mod, left, this.ParseIntDivide(), tok.Line, tok.Column);
    }
    return left;
  }

  private VbsExpr ParseIntDivide() {
    var left = this.ParseMultiplicative();
    while (this.Current.Kind == VBScriptTokenKind.Backslash) {
      var tok = this.Advance();
      left = new VbsBinaryExpr(VbsBinaryOp.IntDivide, left, this.ParseMultiplicative(), tok.Line, tok.Column);
    }
    return left;
  }

  private VbsExpr ParseMultiplicative() {
    var left = this.ParsePower();
    while (this.Current.Kind is VBScriptTokenKind.Star or VBScriptTokenKind.Slash) {
      var tok = this.Advance();
      var op = tok.Kind == VBScriptTokenKind.Star ? VbsBinaryOp.Multiply : VbsBinaryOp.Divide;
      left = new VbsBinaryExpr(op, left, this.ParsePower(), tok.Line, tok.Column);
    }
    return left;
  }

  // VBScript 5.8: ^ is left-associative and unary - binds tighter than ^ (-2 ^ 2 = 4, 2 ^ -2 = 0.25, 2 ^ 3 ^ 2 = 64).
  private VbsExpr ParsePower() {
    var left = this.ParseUnary();
    while (this.Current.Kind == VBScriptTokenKind.Caret) {
      var tok = this.Advance();
      left = new VbsBinaryExpr(VbsBinaryOp.Power, left, this.ParseUnary(), tok.Line, tok.Column);
    }
    return left;
  }

  private VbsExpr ParseUnary() {
    if (this.Current.Kind == VBScriptTokenKind.Minus) {
      var tok = this.Advance();
      return new VbsUnaryExpr(VbsUnaryOp.Negate, this.ParseUnary(), tok.Line, tok.Column);
    }
    if (this.Current.Kind == VBScriptTokenKind.Plus) {
      var tok = this.Advance();
      return new VbsUnaryExpr(VbsUnaryOp.Plus, this.ParseUnary(), tok.Line, tok.Column);
    }
    return this.ParsePostfix();
  }

  private VbsExpr ParsePostfix() {
    var expr = this.ParsePrimary();
    while (true) {
      if (this.Match(VBScriptTokenKind.Dot)) {
        var member = this.ExpectMemberName();
        expr = new VbsMemberExpr(expr, member.Text, member.Line, member.Column);
      } else if (this.Match(VBScriptTokenKind.LParen)) {
        var args = new List<VbsExpr>();
        if (this.Current.Kind != VBScriptTokenKind.RParen) {
          do {
            args.Add(this.ParseExpression());
          } while (this.Match(VBScriptTokenKind.Comma));
        }
        this.Expect(VBScriptTokenKind.RParen, "')'");
        expr = new VbsCallOrIndexExpr(expr, args, expr.Line, expr.Column);
      } else {
        return expr;
      }
    }
  }

  private VbsExpr ParsePrimary() {
    var tok = this.Current;
    switch (tok.Kind) {
      case VBScriptTokenKind.IntLiteral:
        this.Advance();
        return new VbsIntLit(tok.LongValue, tok.Line, tok.Column);
      case VBScriptTokenKind.FloatLiteral:
        this.Advance();
        return new VbsFloatLit(tok.DoubleValue, tok.Line, tok.Column);
      case VBScriptTokenKind.StringLiteral:
        this.Advance();
        return new VbsStringLit(tok.StringValue!, tok.Line, tok.Column);
      case VBScriptTokenKind.KwTrue:
        this.Advance();
        return new VbsBoolLit(true, tok.Line, tok.Column);
      case VBScriptTokenKind.KwFalse:
        this.Advance();
        return new VbsBoolLit(false, tok.Line, tok.Column);
      case VBScriptTokenKind.KwEmpty:
        this.Advance();
        return new VbsEmptyLit(tok.Line, tok.Column);
      case VBScriptTokenKind.KwNull:
        this.Advance();
        return new VbsNullLit(tok.Line, tok.Column);
      case VBScriptTokenKind.KwNothing:
        this.Advance();
        return new VbsNothingLit(tok.Line, tok.Column);
      case VBScriptTokenKind.KwMe:
        this.Advance();
        return new VbsMeExpr(tok.Line, tok.Column);
      case VBScriptTokenKind.KwNew: {
        this.Advance();
        var cls = this.ExpectIdentifier("class name");
        return new VbsNewExpr(cls.Text, tok.Line, tok.Column);
      }
      case VBScriptTokenKind.Identifier:
        this.Advance();
        return new VbsIdentExpr(tok.Text, tok.Line, tok.Column);
      case VBScriptTokenKind.LParen: {
        this.Advance();
        var inner = this.ParseExpression();
        this.Expect(VBScriptTokenKind.RParen, "')'");
        return inner;
      }
      default:
        this.Error(tok, $"unexpected token '{tok.Text}' in expression");
        if (!tok.IsStatementTerminator)
          this.Advance();
        return new VbsIntLit(0, tok.Line, tok.Column);
    }
  }

  private VBScriptToken ExpectIdentifier(string what) {
    if (this.Current.Kind == VBScriptTokenKind.Identifier)
      return this.Advance();

    this.Error(this.Current, $"expected {what}, found '{this.Current.Text}'");
    return this.Current;
  }

  private VBScriptToken ExpectMemberName() {
    if (this.Current.Kind == VBScriptTokenKind.Identifier || this.Current.IsKeyword)
      return this.Advance();

    this.Error(this.Current, $"expected member name after '.', found '{this.Current.Text}'");
    return this.Current;
  }

}
