using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Syntax;

/// <summary>A source nested deeper than the frontend parses, at a byte offset.</summary>
internal sealed class GoNestingException(int offset, int limit) : Exception($"nesting deeper than {limit} levels") {

  /// <summary>The byte offset the limit was reached at.</summary>
  public int Offset { get; } = offset;

}

/// <summary>
/// The parser of Go: a recursive descent over the grammar of The Go Programming Language
/// Specification, converted from <c>go/parser</c> of the Go distribution (BSD-3-Clause, see
/// <c>THIRD-PARTY-NOTICE.Go.txt</c>), with its look-ahead decisions - composite literals in control
/// clauses, type parameters against array lengths, type switch guards - and its error messages. It
/// builds <see cref="GoNode"/>s with the node types and field names of <c>go/ast</c>. The first error
/// ends the parse with a <see cref="GoSyntaxException"/>: <c>go/parser</c> recovers and goes on, but its
/// verdict is decided by the first error all the same. Declarations are not resolved here; that is the
/// type checker's.
/// </summary>
internal sealed class GoParser {

  /// <summary>The deepest nesting of expressions, types and statements the parser follows.</summary>
  public const int MaxNesting = 1000;

  private const int Basic = 0;
  private const int LabelOk = 1;
  private const int RangeOk = 2;

  private static readonly HashSet<GoTokenKind> _stmtStart = [
    GoTokenKind.Break, GoTokenKind.Const, GoTokenKind.Continue, GoTokenKind.Defer, GoTokenKind.Fallthrough, GoTokenKind.For, GoTokenKind.Go,
    GoTokenKind.Goto, GoTokenKind.If, GoTokenKind.Return, GoTokenKind.Select, GoTokenKind.Switch, GoTokenKind.Type, GoTokenKind.Var
  ];

  private readonly GoSource _source;
  private readonly GoScanner _scanner;
  private GoToken _token;
  private int _exprLev;
  private bool _inRhs;
  private int _nesting;

  private GoParser(GoSource source) {
    this._source = source;
    this._scanner = new GoScanner(source);
    this.Next();
  }

  /// <summary>Parses a source file into its <c>File</c> node.</summary>
  /// <param name="source">The source.</param>
  /// <returns>The tree.</returns>
  /// <exception cref="GoSyntaxException">The source is no Go file.</exception>
  /// <exception cref="GoNestingException">The source nests deeper than <see cref="MaxNesting"/>.</exception>
  public static GoNode ParseFile(GoSource source) => new GoParser(source).File();

  private GoTokenKind Tok => this._token.Kind;

  private int Pos => this._token.Offset;

  private string Lit => this._token.Literal;

  private void Next() => this._token = this._scanner.Scan();

  private GoNode Make(string kind, int offset) {
    var (line, column) = this._source.Position(offset);
    return new GoNode(kind, offset, line, column);
  }

  private void Enter() {
    if (++this._nesting > MaxNesting)
      throw new GoNestingException(this.Pos, MaxNesting);
  }

  private void Leave() => --this._nesting;

  private static GoSyntaxException Error(int offset, string message) => new(offset, message);

  private GoSyntaxException ErrorExpected(int offset, string what) {
    var message = "expected " + what;
    if (offset == this.Pos) {
      if (this.Tok == GoTokenKind.Semicolon && this.Lit == "\n")
        message += ", found newline";
      else if (this.Tok.IsLiteral())
        message += ", found " + this.Lit;
      else
        message += $", found '{this.Tok.Spelling()}'";
    }

    return Error(offset, message);
  }

  private int Expect(GoTokenKind kind) {
    var offset = this.Pos;
    if (this.Tok != kind)
      throw this.ErrorExpected(offset, $"'{kind.Spelling()}'");

    this.Next();
    return offset;
  }

  private int ExpectClosing(GoTokenKind kind, string context) {
    if (this.Tok != kind && this.Tok == GoTokenKind.Semicolon && this.Lit == "\n")
      throw Error(this.Pos, "missing ',' before newline in " + context);

    return this.Expect(kind);
  }

  private void ExpectSemi() {
    switch (this.Tok) {
      case GoTokenKind.RParen or GoTokenKind.RBrace:
        return;
      case GoTokenKind.Semicolon:
        this.Next();
        return;
      default:
        throw this.ErrorExpected(this.Pos, "';'");
    }
  }

  private bool AtComma(string context, GoTokenKind follow) {
    if (this.Tok == GoTokenKind.Comma)
      return true;

    if (this.Tok == follow)
      return false;

    var message = "missing ','";
    if (this.Tok == GoTokenKind.Semicolon && this.Lit == "\n")
      message += " before newline";

    throw Error(this.Pos, message + " in " + context);
  }

  // ---------------------------------------------------------------- identifiers and lists

  private GoNode Ident() {
    var offset = this.Pos;
    if (this.Tok != GoTokenKind.Ident)
      this.Expect(GoTokenKind.Ident);

    var name = this.Lit;
    this.Next();
    return this.Make("Ident", offset).With("Name", name);
  }

  private List<GoNode> IdentList() {
    var list = new List<GoNode> { this.Ident() };
    while (this.Tok == GoTokenKind.Comma) {
      this.Next();
      list.Add(this.Ident());
    }

    return list;
  }

  private List<GoNode> ExprList() {
    var list = new List<GoNode> { this.Expr() };
    while (this.Tok == GoTokenKind.Comma) {
      this.Next();
      list.Add(this.Expr());
    }

    return list;
  }

  private List<GoNode> List(bool inRhs) {
    var old = this._inRhs;
    this._inRhs = inRhs;
    var list = this.ExprList();
    this._inRhs = old;
    return list;
  }

  // ---------------------------------------------------------------- types

  private GoNode Type() => this.TryIdentOrType() ?? throw this.ErrorExpected(this.Pos, "type");

  private GoNode QualifiedIdent(GoNode? ident) {
    var type = this.TypeName(ident);
    return this.Tok == GoTokenKind.LBrack ? this.TypeInstance(type) : type;
  }

  private GoNode TypeName(GoNode? ident) {
    ident ??= this.Ident();
    if (this.Tok != GoTokenKind.Period)
      return ident;

    this.Next();
    var selector = this.Ident();
    return this.Make("SelectorExpr", ident.Offset).With("X", ident).With("Sel", selector);
  }

  private GoNode ArrayType(int lbrack, GoNode? length) {
    if (length is null) {
      ++this._exprLev;
      if (this.Tok == GoTokenKind.Ellipsis) {
        length = this.Make("Ellipsis", this.Pos);
        this.Next();
      } else if (this.Tok != GoTokenKind.RBrack) {
        length = this.Rhs();
      }

      --this._exprLev;
    }

    if (this.Tok == GoTokenKind.Comma)
      throw Error(this.Pos, "unexpected comma; expecting ]");

    this.Expect(GoTokenKind.RBrack);
    var element = this.Type();
    return this.Make("ArrayType", lbrack).With("Len", length).With("Elt", element);
  }

  /// <summary>After a name and <c>[</c>: an array type of a field or parameter of that name, or an instance of a generic type of that name.</summary>
  private (GoNode? Name, GoNode Type) ArrayFieldOrTypeInstance(GoNode name) {
    var lbrack = this.Expect(GoTokenKind.LBrack);
    var trailingComma = -1;
    var arguments = new List<GoNode>();
    if (this.Tok != GoTokenKind.RBrack) {
      ++this._exprLev;
      arguments.Add(this.Rhs());
      while (this.Tok == GoTokenKind.Comma) {
        var comma = this.Pos;
        this.Next();
        if (this.Tok == GoTokenKind.RBrack) {
          trailingComma = comma;
          break;
        }

        arguments.Add(this.Rhs());
      }

      --this._exprLev;
    }

    this.Expect(GoTokenKind.RBrack);
    if (arguments.Count == 0)
      return (name, this.Make("ArrayType", lbrack).With("Elt", this.Type()));

    if (arguments.Count == 1 && this.TryIdentOrType() is { } element) {
      if (trailingComma >= 0)
        throw Error(trailingComma, "unexpected comma; expecting ]");

      return (name, this.Make("ArrayType", lbrack).With("Len", arguments[0]).With("Elt", element));
    }

    return (null, this.PackIndex(name, arguments));
  }

  private GoNode PackIndex(GoNode x, List<GoNode> expressions)
    => expressions.Count == 1
      ? this.Make("IndexExpr", x.Offset).With("X", x).With("Index", expressions[0])
      : this.Make("IndexListExpr", x.Offset).With("X", x).With("Indices", expressions);

  private GoNode FieldDecl() {
    var names = new List<GoNode>();
    GoNode? type;
    switch (this.Tok) {
      case GoTokenKind.Ident: {
        var name = this.Ident();
        if (this.Tok is GoTokenKind.Period or GoTokenKind.String or GoTokenKind.Semicolon or GoTokenKind.RBrace) {
          type = this.Tok == GoTokenKind.Period ? this.QualifiedIdent(name) : name;
        } else {
          names.Add(name);
          while (this.Tok == GoTokenKind.Comma) {
            this.Next();
            names.Add(this.Ident());
          }

          if (names.Count == 1 && this.Tok == GoTokenKind.LBrack) {
            var (field, fieldType) = this.ArrayFieldOrTypeInstance(name);
            type = fieldType;
            if (field is null)
              names.Clear();
          } else {
            type = this.Type();
          }
        }

        break;
      }
      case GoTokenKind.Mul: {
        var star = this.Pos;
        this.Next();
        if (this.Tok == GoTokenKind.LParen)
          throw Error(this.Pos, "cannot parenthesize embedded type");

        type = this.Make("StarExpr", star).With("X", this.QualifiedIdent(null));
        break;
      }
      default:
        // StructType calls this on an identifier, a star or a parenthesis alone.
        throw Error(this.Pos, "cannot parenthesize embedded type");
    }

    GoNode? tag = null;
    if (this.Tok == GoTokenKind.String) {
      tag = this.Make("BasicLit", this.Pos).With("Kind", "STRING").With("Value", this.Lit);
      this.Next();
    }

    this.ExpectSemi();
    var offset = names.Count > 0 ? names[0].Offset : type.Offset;
    return this.Make("Field", offset).With("Names", names).With("Type", type).With("Tag", tag);
  }

  private GoNode StructType() {
    var offset = this.Expect(GoTokenKind.Struct);
    var lbrace = this.Expect(GoTokenKind.LBrace);
    var list = new List<GoNode>();
    while (this.Tok is GoTokenKind.Ident or GoTokenKind.Mul or GoTokenKind.LParen)
      list.Add(this.FieldDecl());

    this.Expect(GoTokenKind.RBrace);
    return this.Make("StructType", offset).With("Fields", this.Make("FieldList", lbrace).With("List", list));
  }

  private GoNode PointerType() {
    var star = this.Expect(GoTokenKind.Mul);
    return this.Make("StarExpr", star).With("X", this.Type());
  }

  private GoNode DotsType() {
    var offset = this.Expect(GoTokenKind.Ellipsis);
    return this.Make("Ellipsis", offset).With("Elt", this.Type());
  }

  private sealed record class Param(GoNode? Name, GoNode? Type);

  private Param ParamDecl(GoNode? name, bool typeSetsOk) {
    var token = this.Tok;
    if (name is not null)
      token = GoTokenKind.Ident;
    else if (typeSetsOk && this.Tok == GoTokenKind.Tilde)
      return new(null, this.EmbeddedElem(null));

    GoNode? parameterName = null;
    GoNode? type = null;
    switch (token) {
      case GoTokenKind.Ident:
        parameterName = name ?? this.Ident();
        switch (this.Tok) {
          case GoTokenKind.Ident or GoTokenKind.Mul or GoTokenKind.Arrow or GoTokenKind.Func or GoTokenKind.Chan or GoTokenKind.Map or GoTokenKind.Struct
            or GoTokenKind.Interface or GoTokenKind.LParen:
            type = this.Type();
            break;
          case GoTokenKind.LBrack:
            (parameterName, type) = this.ArrayFieldOrTypeInstance(parameterName!);
            break;
          case GoTokenKind.Ellipsis:
            return new(parameterName, this.DotsType());
          case GoTokenKind.Period:
            type = this.QualifiedIdent(parameterName);
            parameterName = null;
            break;
          case GoTokenKind.Tilde when typeSetsOk:
            return new(parameterName, this.EmbeddedElem(null));
          case GoTokenKind.Or when typeSetsOk:
            return new(null, this.EmbeddedElem(parameterName));
        }

        break;
      case GoTokenKind.Mul or GoTokenKind.Arrow or GoTokenKind.Func or GoTokenKind.LBrack or GoTokenKind.Chan or GoTokenKind.Map or GoTokenKind.Struct
        or GoTokenKind.Interface or GoTokenKind.LParen:
        type = this.Type();
        break;
      case GoTokenKind.Ellipsis:
        return new(null, this.DotsType());
      default:
        throw this.ErrorExpected(this.Pos, "')'");
    }

    if (typeSetsOk && this.Tok == GoTokenKind.Or && type is not null)
      type = this.EmbeddedElem(type);

    return new(parameterName, type);
  }

  private List<GoNode> ParameterList(GoNode? name0, GoNode? type0, GoTokenKind closing, bool dotsOk) {
    var typeParameters = closing == GoTokenKind.RBrack;
    var offset0 = name0?.Offset ?? type0?.Offset ?? this.Pos;
    var list = new List<Param>();
    var named = 0;
    var typed = 0;
    while (name0 is not null || this.Tok != closing && this.Tok != GoTokenKind.Eof) {
      Param parameter;
      if (type0 is not null) {
        if (typeParameters)
          type0 = this.EmbeddedElem(type0);

        parameter = new(name0, type0);
      } else {
        parameter = this.ParamDecl(name0, typeParameters);
      }

      name0 = null;
      type0 = null;
      if (parameter.Name is not null || parameter.Type is not null) {
        list.Add(parameter);
        if (parameter.Name is not null && parameter.Type is not null)
          ++named;

        if (parameter.Type is not null)
          ++typed;
      }

      if (!this.AtComma("parameter list", closing))
        break;

      this.Next();
    }

    // The loop parsed one parameter at least: it runs while the closing token is not there.
    var parameters = new List<GoNode>();

    if (named == 0) {
      list = [.. list.Select(parameter => parameter.Name is { } type ? new Param(null, type) : parameter)];
      if (typeParameters) {
        if (named == typed)
          throw Error(this.Pos, "missing type constraint");

        throw Error(offset0, list.Count == 1 ? "missing type parameter name or invalid array length" : "missing type parameter name");
      }
    } else if (named != list.Count) {
      var errorOffset = -1;
      GoNode? current = null;
      for (var index = list.Count - 1; index >= 0; --index) {
        var parameter = list[index];
        if (parameter.Type is not null) {
          current = parameter.Type;
          if (parameter.Name is null)
            errorOffset = current.Offset;
        } else if (current is not null) {
          list[index] = parameter with { Type = current };
        } else {
          errorOffset = parameter.Name!.Offset;
        }
      }

      if (errorOffset >= 0) {
        if (named == typed)
          throw Error(this.Pos, typeParameters ? "missing type constraint" : "missing parameter type");

        throw Error(errorOffset, typeParameters ? list.Count == 1 ? "missing type parameter name or invalid array length" : "missing type parameter name" : "missing parameter name");
      }
    }

    for (var index = 0; index < list.Count; ++index)
      if (list[index].Type is { Kind: "Ellipsis" } dots && (!dotsOk || index + 1 < list.Count))
        throw Error(dots.Offset, dotsOk ? "can only use ... with final parameter" : "invalid use of ...");

    if (named == 0) {
      foreach (var parameter in list)
        parameters.Add(this.Make("Field", parameter.Type!.Offset).With("Names", new List<GoNode>()).With("Type", parameter.Type));

      return parameters;
    }

    var names = new List<GoNode>();
    GoNode? type = null;
    void Add() {
      parameters.Add(this.Make("Field", names[0].Offset).With("Names", names).With("Type", type));
      names = [];
    }

    foreach (var parameter in list) {
      if (!ReferenceEquals(parameter.Type, type)) {
        if (names.Count > 0)
          Add();

        type = parameter.Type;
      }

      names.Add(parameter.Name!);
    }

    if (names.Count > 0)
      Add();

    return parameters;
  }

  private GoNode? TypeParameters() {
    var lbrack = this.Expect(GoTokenKind.LBrack);
    var list = this.Tok != GoTokenKind.RBrack ? this.ParameterList(null, null, GoTokenKind.RBrack, false) : [];
    var rbrack = this.Expect(GoTokenKind.RBrack);
    if (list.Count == 0)
      throw Error(rbrack, "empty type parameter list");

    return this.Make("FieldList", lbrack).With("List", list);
  }

  private GoNode? Parameters(bool result) {
    if (!result || this.Tok == GoTokenKind.LParen) {
      var lparen = this.Expect(GoTokenKind.LParen);
      var list = this.Tok != GoTokenKind.RParen ? this.ParameterList(null, null, GoTokenKind.RParen, !result) : [];
      this.Expect(GoTokenKind.RParen);
      return this.Make("FieldList", lparen).With("List", list);
    }

    if (this.TryIdentOrType() is not { } type)
      return null;

    return this.Make("FieldList", type.Offset).With("List", new List<GoNode> { this.Make("Field", type.Offset).With("Names", new List<GoNode>()).With("Type", type) });
  }

  private GoNode FuncType() {
    var offset = this.Expect(GoTokenKind.Func);
    if (this.Tok == GoTokenKind.LBrack && this.TypeParameters() is not null)
      throw Error(offset, "function type must have no type parameters");

    var parameters = this.Parameters(false);
    var results = this.Parameters(true);
    return this.Make("FuncType", offset).With("Params", parameters).With("Results", results);
  }

  private GoNode MethodSpec() {
    var names = new List<GoNode>();
    GoNode type;
    var x = this.TypeName(null);
    if (x.Is("Ident")) {
      switch (this.Tok) {
        case GoTokenKind.LBrack: {
          var lbrack = this.Pos;
          this.Next();
          ++this._exprLev;
          var first = this.Expr();
          --this._exprLev;
          if (first.Is("Ident") && this.Tok != GoTokenKind.Comma && this.Tok != GoTokenKind.RBrack) {
            this.ParameterList(first, null, GoTokenKind.RBrack, false);
            this.Expect(GoTokenKind.RBrack);
            throw Error(lbrack, "interface method must have no type parameters");
          }

          var list = new List<GoNode> { first };
          if (this.AtComma("type argument list", GoTokenKind.RBrack)) {
            ++this._exprLev;
            this.Next();
            while (this.Tok != GoTokenKind.RBrack && this.Tok != GoTokenKind.Eof) {
              list.Add(this.Type());
              if (!this.AtComma("type argument list", GoTokenKind.RBrack))
                break;

              this.Next();
            }

            --this._exprLev;
          }

          this.ExpectClosing(GoTokenKind.RBrack, "type argument list");
          type = this.PackIndex(x, list);
          break;
        }
        case GoTokenKind.LParen: {
          var parameters = this.Parameters(false);
          var results = this.Parameters(true);
          names.Add(x);
          type = this.Make("FuncType", parameters!.Offset).With("Params", parameters).With("Results", results);
          break;
        }
        default:
          type = x;
          break;
      }
    } else {
      type = this.Tok == GoTokenKind.LBrack ? this.TypeInstance(x) : x;
    }

    return this.Make("Field", names.Count > 0 ? names[0].Offset : type.Offset).With("Names", names).With("Type", type);
  }

  private GoNode EmbeddedElem(GoNode? x) {
    x ??= this.EmbeddedTerm();
    while (this.Tok == GoTokenKind.Or) {
      this.Next();
      var y = this.EmbeddedTerm();
      x = this.Make("BinaryExpr", x.Offset).With("X", x).With("Op", "|").With("Y", y);
    }

    return x;
  }

  private GoNode EmbeddedTerm() {
    if (this.Tok == GoTokenKind.Tilde) {
      var offset = this.Pos;
      this.Next();
      return this.Make("UnaryExpr", offset).With("Op", "~").With("X", this.Type());
    }

    return this.TryIdentOrType() ?? throw this.ErrorExpected(this.Pos, "~ term or type");
  }

  private GoNode InterfaceType() {
    var offset = this.Expect(GoTokenKind.Interface);
    var lbrace = this.Expect(GoTokenKind.LBrace);
    var list = new List<GoNode>();
    while (true) {
      if (this.Tok == GoTokenKind.Ident) {
        var field = this.MethodSpec();
        if (field.Nodes("Names").Count == 0)
          field.With("Type", this.EmbeddedElem(field.Node("Type")));

        this.ExpectSemi();
        list.Add(field);
      } else if (this.Tok == GoTokenKind.Tilde) {
        var type = this.EmbeddedElem(null);
        this.ExpectSemi();
        list.Add(this.Make("Field", type.Offset).With("Names", new List<GoNode>()).With("Type", type));
      } else if (this.TryIdentOrType() is { } term) {
        var type = this.EmbeddedElem(term);
        this.ExpectSemi();
        list.Add(this.Make("Field", type.Offset).With("Names", new List<GoNode>()).With("Type", type));
      } else {
        break;
      }
    }

    this.Expect(GoTokenKind.RBrace);
    return this.Make("InterfaceType", offset).With("Methods", this.Make("FieldList", lbrace).With("List", list));
  }

  private GoNode MapType() {
    var offset = this.Expect(GoTokenKind.Map);
    this.Expect(GoTokenKind.LBrack);
    var key = this.Type();
    this.Expect(GoTokenKind.RBrack);
    var value = this.Type();
    return this.Make("MapType", offset).With("Key", key).With("Value", value);
  }

  private GoNode ChanType() {
    var offset = this.Pos;
    var direction = "both";
    if (this.Tok == GoTokenKind.Chan) {
      this.Next();
      if (this.Tok == GoTokenKind.Arrow) {
        this.Next();
        direction = "send";
      }
    } else {
      this.Expect(GoTokenKind.Arrow);
      this.Expect(GoTokenKind.Chan);
      direction = "recv";
    }

    return this.Make("ChanType", offset).With("Dir", direction).With("Value", this.Type());
  }

  private GoNode TypeInstance(GoNode type) {
    var opening = this.Expect(GoTokenKind.LBrack);
    ++this._exprLev;
    var list = new List<GoNode>();
    while (this.Tok != GoTokenKind.RBrack && this.Tok != GoTokenKind.Eof) {
      list.Add(this.Type());
      if (!this.AtComma("type argument list", GoTokenKind.RBrack))
        break;

      this.Next();
    }

    --this._exprLev;
    var closing = this.ExpectClosing(GoTokenKind.RBrack, "type argument list");
    if (list.Count == 0)
      throw this.ErrorExpected(closing, "type argument list");

    _ = opening;
    return this.PackIndex(type, list);
  }

  private GoNode? TryIdentOrType() {
    this.Enter();
    try {
      switch (this.Tok) {
        case GoTokenKind.Ident: {
          var type = this.TypeName(null);
          return this.Tok == GoTokenKind.LBrack ? this.TypeInstance(type) : type;
        }
        case GoTokenKind.LBrack:
          return this.ArrayType(this.Expect(GoTokenKind.LBrack), null);
        case GoTokenKind.Struct:
          return this.StructType();
        case GoTokenKind.Mul:
          return this.PointerType();
        case GoTokenKind.Func:
          return this.FuncType();
        case GoTokenKind.Interface:
          return this.InterfaceType();
        case GoTokenKind.Map:
          return this.MapType();
        case GoTokenKind.Chan or GoTokenKind.Arrow:
          return this.ChanType();
        case GoTokenKind.LParen: {
          var lparen = this.Pos;
          this.Next();
          var type = this.Type();
          this.Expect(GoTokenKind.RParen);
          return this.Make("ParenExpr", lparen).With("X", type);
        }
        default:
          return null;
      }
    } finally {
      this.Leave();
    }
  }

  // ---------------------------------------------------------------- blocks

  private List<GoNode> StmtList() {
    var list = new List<GoNode>();
    while (this.Tok is not GoTokenKind.Case and not GoTokenKind.Default and not GoTokenKind.RBrace and not GoTokenKind.Eof)
      list.Add(this.Stmt());

    return list;
  }

  private GoNode BlockStmt() {
    var lbrace = this.Expect(GoTokenKind.LBrace);
    var list = this.StmtList();
    this.Expect(GoTokenKind.RBrace);
    return this.Make("BlockStmt", lbrace).With("List", list);
  }

  // ---------------------------------------------------------------- expressions

  private GoNode FuncTypeOrLit() {
    var type = this.FuncType();
    if (this.Tok != GoTokenKind.LBrace)
      return type;

    ++this._exprLev;
    var body = this.BlockStmt();
    --this._exprLev;
    return this.Make("FuncLit", type.Offset).With("Type", type).With("Body", body);
  }

  private GoNode Operand() {
    switch (this.Tok) {
      case GoTokenKind.Ident:
        return this.Ident();
      case GoTokenKind.Int or GoTokenKind.Float or GoTokenKind.Imag or GoTokenKind.Char or GoTokenKind.String: {
        var literal = this.Make("BasicLit", this.Pos).With("Kind", this.Tok.Spelling()).With("Value", this.Lit);
        this.Next();
        return literal;
      }
      case GoTokenKind.LParen: {
        var lparen = this.Pos;
        this.Next();
        ++this._exprLev;
        var x = this.Rhs();
        --this._exprLev;
        this.Expect(GoTokenKind.RParen);
        return this.Make("ParenExpr", lparen).With("X", x);
      }
      case GoTokenKind.Func:
        return this.FuncTypeOrLit();
    }

    return this.TryIdentOrType() ?? throw this.ErrorExpected(this.Pos, "operand");
  }

  private GoNode TypeAssertion(GoNode x) {
    this.Expect(GoTokenKind.LParen);
    GoNode? type = null;
    if (this.Tok == GoTokenKind.Type)
      this.Next();
    else
      type = this.Type();

    this.Expect(GoTokenKind.RParen);
    return this.Make("TypeAssertExpr", x.Offset).With("X", x).With("Type", type);
  }

  private GoNode IndexOrSliceOrInstance(GoNode x) {
    this.Expect(GoTokenKind.LBrack);
    if (this.Tok == GoTokenKind.RBrack)
      throw this.ErrorExpected(this.Pos, "operand");

    ++this._exprLev;
    var arguments = new List<GoNode>();
    var index = new GoNode?[3];
    var colons = new int[2];
    if (this.Tok != GoTokenKind.Colon)
      index[0] = this.Rhs();

    var colonCount = 0;
    switch (this.Tok) {
      case GoTokenKind.Colon:
        while (this.Tok == GoTokenKind.Colon && colonCount < 2) {
          colons[colonCount++] = this.Pos;
          this.Next();
          if (this.Tok is not GoTokenKind.Colon and not GoTokenKind.RBrack and not GoTokenKind.Eof)
            index[colonCount] = this.Rhs();
        }

        break;
      case GoTokenKind.Comma:
        arguments.Add(index[0]!);
        while (this.Tok == GoTokenKind.Comma) {
          this.Next();
          if (this.Tok is not GoTokenKind.RBrack and not GoTokenKind.Eof)
            arguments.Add(this.Type());
        }

        break;
    }

    --this._exprLev;
    this.Expect(GoTokenKind.RBrack);
    if (colonCount > 0) {
      if (colonCount == 2) {
        if (index[1] is null)
          throw Error(colons[0], "middle index required in 3-index slice");

        if (index[2] is null)
          throw Error(colons[1], "final index required in 3-index slice");
      }

      return this.Make("SliceExpr", x.Offset).With("X", x).With("Low", index[0]).With("High", index[1]).With("Max", index[2]).With("Slice3", colonCount == 2);
    }

    return arguments.Count == 0 ? this.Make("IndexExpr", x.Offset).With("X", x).With("Index", index[0]) : this.PackIndex(x, arguments);
  }

  private GoNode CallOrConversion(GoNode function) {
    this.Expect(GoTokenKind.LParen);
    ++this._exprLev;
    var list = new List<GoNode>();
    var spread = false;
    while (this.Tok != GoTokenKind.RParen && this.Tok != GoTokenKind.Eof && !spread) {
      list.Add(this.Rhs());
      if (this.Tok == GoTokenKind.Ellipsis) {
        spread = true;
        this.Next();
      }

      if (!this.AtComma("argument list", GoTokenKind.RParen))
        break;

      this.Next();
    }

    --this._exprLev;
    this.ExpectClosing(GoTokenKind.RParen, "argument list");
    return this.Make("CallExpr", function.Offset).With("Fun", function).With("Args", list).With("spread", spread);
  }

  private GoNode Value() => this.Tok == GoTokenKind.LBrace ? this.LiteralValue(null) : this.Expr();

  private GoNode Element() {
    var x = this.Value();
    if (this.Tok != GoTokenKind.Colon)
      return x;

    this.Next();
    return this.Make("KeyValueExpr", x.Offset).With("Key", x).With("Value", this.Value());
  }

  private GoNode LiteralValue(GoNode? type) {
    this.Enter();
    try {
      var lbrace = this.Expect(GoTokenKind.LBrace);
      var elements = new List<GoNode>();
      ++this._exprLev;
      while (this.Tok != GoTokenKind.RBrace && this.Tok != GoTokenKind.Eof) {
        elements.Add(this.Element());
        if (!this.AtComma("composite literal", GoTokenKind.RBrace))
          break;

        this.Next();
      }

      --this._exprLev;
      this.ExpectClosing(GoTokenKind.RBrace, "composite literal");
      return this.Make("CompositeLit", type?.Offset ?? lbrace).With("Type", type).With("Elts", elements);
    } finally {
      this.Leave();
    }
  }

  private static GoNode Unparen(GoNode x) => x.Is("ParenExpr") ? Unparen(x.Node("X")!) : x;

  private GoNode PrimaryExpr(GoNode? x) {
    x ??= this.Operand();
    var depth = 0;
    try {
      while (true) {
        ++depth;
        this.Enter();
        switch (this.Tok) {
          case GoTokenKind.Period:
            this.Next();
            switch (this.Tok) {
              case GoTokenKind.Ident:
                var selector = this.Ident();
                x = this.Make("SelectorExpr", x.Offset).With("X", x).With("Sel", selector);
                break;
              case GoTokenKind.LParen:
                x = this.TypeAssertion(x);
                break;
              default:
                throw this.ErrorExpected(this.Pos, "selector or type assertion");
            }

            break;
          case GoTokenKind.LBrack:
            x = this.IndexOrSliceOrInstance(x);
            break;
          case GoTokenKind.LParen:
            x = this.CallOrConversion(x);
            break;
          case GoTokenKind.LBrace: {
            var t = Unparen(x);
            switch (t.Kind) {
              case "Ident" or "SelectorExpr" or "IndexExpr" or "IndexListExpr":
                if (this._exprLev < 0)
                  return x;

                break;
              case "ArrayType" or "StructType" or "MapType":
                break;
              default:
                return x;
            }

            if (!ReferenceEquals(t, x))
              throw Error(t.Offset, "cannot parenthesize type in composite literal");

            x = this.LiteralValue(x);
            break;
          }
          default:
            return x;
        }
      }
    } finally {
      this._nesting -= depth;
    }
  }

  private GoNode UnaryExpr() {
    this.Enter();
    try {
      switch (this.Tok) {
        case GoTokenKind.Add or GoTokenKind.Sub or GoTokenKind.Not or GoTokenKind.Xor or GoTokenKind.And or GoTokenKind.Tilde: {
          var (offset, op) = (this.Pos, this.Tok);
          this.Next();
          var x = this.UnaryExpr();
          return this.Make("UnaryExpr", offset).With("Op", op.Spelling()).With("X", x);
        }
        case GoTokenKind.Arrow: {
          var arrow = this.Pos;
          this.Next();
          var x = this.UnaryExpr();
          if (x.Is("ChanType")) {
            // <-chan T: the arrow belongs to the channel type; a <- chan of a send channel is no type.
            var direction = "send";
            var type = x;
            while (type is { Kind: "ChanType" } && direction == "send") {
              if (type.Text("Dir") == "recv")
                throw this.ErrorExpected(type.Offset, "'chan'");

              direction = type.Text("Dir")!;
              type.With("Dir", "recv");
              type = type.Node("Value");
            }

            if (direction == "send")
              throw this.ErrorExpected(arrow, "channel type");

            return this.Make("ChanType", arrow).With("Dir", "recv").With("Value", x.Node("Value"));
          }

          return this.Make("UnaryExpr", arrow).With("Op", "<-").With("X", x);
        }
        case GoTokenKind.Mul: {
          var offset = this.Pos;
          this.Next();
          return this.Make("StarExpr", offset).With("X", this.UnaryExpr());
        }
        default:
          return this.PrimaryExpr(null);
      }
    } finally {
      this.Leave();
    }
  }

  private (GoTokenKind, int) TokPrec() {
    var token = this.Tok;
    if (this._inRhs && token == GoTokenKind.Assign)
      token = GoTokenKind.Eql;

    return (token, token.Precedence());
  }

  private GoNode BinaryExpr(GoNode? x, int precedence) {
    x ??= this.UnaryExpr();
    var depth = 0;
    try {
      while (true) {
        ++depth;
        this.Enter();
        var (op, opPrecedence) = this.TokPrec();
        if (opPrecedence < precedence)
          return x;

        this.Expect(op);
        var y = this.BinaryExpr(null, opPrecedence + 1);
        x = this.Make("BinaryExpr", x.Offset).With("X", x).With("Op", op.Spelling()).With("Y", y);
      }
    } finally {
      this._nesting -= depth;
    }
  }

  private GoNode Expr() => this.BinaryExpr(null, 1);

  private GoNode Rhs() {
    var old = this._inRhs;
    this._inRhs = true;
    var x = this.Expr();
    this._inRhs = old;
    return x;
  }

  // ---------------------------------------------------------------- statements

  private static readonly HashSet<GoTokenKind> _assignments = [
    GoTokenKind.Define, GoTokenKind.Assign, GoTokenKind.AddAssign, GoTokenKind.SubAssign, GoTokenKind.MulAssign, GoTokenKind.QuoAssign,
    GoTokenKind.RemAssign, GoTokenKind.AndAssign, GoTokenKind.OrAssign, GoTokenKind.XorAssign, GoTokenKind.ShlAssign, GoTokenKind.ShrAssign,
    GoTokenKind.AndNotAssign
  ];

  private (GoNode Statement, bool IsRange) SimpleStmt(int mode) {
    var x = this.List(false);
    if (_assignments.Contains(this.Tok)) {
      var token = this.Tok;
      this.Next();
      List<GoNode> y;
      var isRange = false;
      if (mode == RangeOk && this.Tok == GoTokenKind.Range && token is GoTokenKind.Define or GoTokenKind.Assign) {
        var offset = this.Pos;
        this.Next();
        y = [this.Make("UnaryExpr", offset).With("Op", "range").With("X", this.Rhs())];
        isRange = true;
      } else {
        y = this.List(true);
      }

      return (this.Make("AssignStmt", x[0].Offset).With("Lhs", x).With("Tok", token.Spelling()).With("Rhs", y), isRange);
    }

    if (x.Count > 1)
      throw this.ErrorExpected(x[0].Offset, "1 expression");

    switch (this.Tok) {
      case GoTokenKind.Colon: {
        var colon = this.Pos;
        this.Next();
        if (mode == LabelOk && x[0].Is("Ident"))
          return (this.Make("LabeledStmt", x[0].Offset).With("Label", x[0]).With("Stmt", this.Stmt()), false);

        throw Error(colon, "illegal label declaration");
      }
      case GoTokenKind.Arrow:
        this.Next();
        return (this.Make("SendStmt", x[0].Offset).With("Chan", x[0]).With("Value", this.Rhs()), false);
      case GoTokenKind.Inc or GoTokenKind.Dec: {
        var statement = this.Make("IncDecStmt", x[0].Offset).With("X", x[0]).With("Tok", this.Tok.Spelling());
        this.Next();
        return (statement, false);
      }
      default:
        return (this.Make("ExprStmt", x[0].Offset).With("X", x[0]), false);
    }
  }

  private GoNode CallExpr(string callType) {
    var x = this.Rhs();
    if (!ReferenceEquals(Unparen(x), x))
      throw Error(x.Offset, $"expression in {callType} must not be parenthesized");

    if (!x.Is("CallExpr"))
      throw Error(this._scanner.End, $"expression in {callType} must be function call");

    return x;
  }

  private GoNode GoOrDefer(GoTokenKind keyword, string kind) {
    var offset = this.Expect(keyword);
    var call = this.CallExpr(keyword.Spelling());
    this.ExpectSemi();
    return this.Make(kind, offset).With("Call", call);
  }

  private GoNode ReturnStmt() {
    var offset = this.Expect(GoTokenKind.Return);
    var results = this.Tok is not GoTokenKind.Semicolon and not GoTokenKind.RBrace ? this.List(true) : [];
    this.ExpectSemi();
    return this.Make("ReturnStmt", offset).With("Results", results);
  }

  private GoNode BranchStmt(GoTokenKind token) {
    var offset = this.Expect(token);
    GoNode? label = null;
    if (token == GoTokenKind.Goto || token is GoTokenKind.Continue or GoTokenKind.Break && this.Tok == GoTokenKind.Ident)
      label = this.Ident();

    this.ExpectSemi();
    return this.Make("BranchStmt", offset).With("Tok", token.Spelling()).With("Label", label);
  }

  private static GoNode? MakeExpr(GoNode? statement, string want) {
    if (statement is null)
      return null;

    if (statement.Is("ExprStmt"))
      return statement.Node("X");

    var found = statement.Is("AssignStmt") ? "assignment" : "simple statement";
    throw Error(statement.Offset, $"expected {want}, found {found} (missing parentheses around composite literal?)");
  }

  private (GoNode? Init, GoNode Cond) IfHeader() {
    if (this.Tok == GoTokenKind.LBrace)
      throw Error(this.Pos, "missing condition in if statement");

    var previous = this._exprLev;
    this._exprLev = -1;
    GoNode? init = null;
    if (this.Tok != GoTokenKind.Semicolon) {
      if (this.Tok == GoTokenKind.Var) {
        this.Next();
        throw Error(this.Pos, "var declaration not allowed in if initializer");
      }

      init = this.SimpleStmt(Basic).Statement;
    }

    GoNode? conditionStatement = null;
    var semicolon = (Offset: -1, Literal: "");
    if (this.Tok != GoTokenKind.LBrace) {
      if (this.Tok == GoTokenKind.Semicolon) {
        semicolon = (this.Pos, this.Lit);
        this.Next();
      } else {
        this.Expect(GoTokenKind.Semicolon);
      }

      if (this.Tok != GoTokenKind.LBrace)
        conditionStatement = this.SimpleStmt(Basic).Statement;
    } else {
      conditionStatement = init;
      init = null;
    }

    GoNode? condition = null;
    if (conditionStatement is not null)
      condition = MakeExpr(conditionStatement, "boolean expression");
    else if (semicolon.Offset >= 0)
      throw Error(semicolon.Offset, semicolon.Literal == "\n" ? "unexpected newline, expecting { after if clause" : "missing condition in if statement");

    this._exprLev = previous;
    return (init, condition ?? throw Error(this.Pos, "missing condition in if statement"));
  }

  private GoNode IfStmt() {
    this.Enter();
    try {
      var offset = this.Expect(GoTokenKind.If);
      var (init, condition) = this.IfHeader();
      var body = this.BlockStmt();
      GoNode? otherwise = null;
      if (this.Tok == GoTokenKind.Else) {
        this.Next();
        switch (this.Tok) {
          case GoTokenKind.If:
            otherwise = this.IfStmt();
            break;
          case GoTokenKind.LBrace:
            otherwise = this.BlockStmt();
            this.ExpectSemi();
            break;
          default:
            throw this.ErrorExpected(this.Pos, "if statement or block");
        }
      } else {
        this.ExpectSemi();
      }

      return this.Make("IfStmt", offset).With("Init", init).With("Cond", condition).With("Body", body).With("Else", otherwise);
    } finally {
      this.Leave();
    }
  }

  private GoNode CaseClause() {
    var offset = this.Pos;
    var list = new List<GoNode>();
    if (this.Tok == GoTokenKind.Case) {
      this.Next();
      list = this.List(true);
    } else {
      this.Expect(GoTokenKind.Default);
    }

    this.Expect(GoTokenKind.Colon);
    return this.Make("CaseClause", offset).With("List", list).With("Body", this.StmtList());
  }

  private static bool IsTypeSwitchAssert(GoNode x) => x.Is("TypeAssertExpr") && x.Node("Type") is null;

  private static bool IsTypeSwitchGuard(GoNode? statement) {
    switch (statement?.Kind) {
      case "ExprStmt":
        return IsTypeSwitchAssert(statement.Node("X")!);
      case "AssignStmt":
        if (statement.Nodes("Lhs").Count == 1 && statement.Nodes("Rhs").Count == 1 && IsTypeSwitchAssert(statement.Nodes("Rhs")[0])) {
          switch (statement.Text("Tok")) {
            case "=":
              throw Error(statement.Nodes("Lhs")[0].Offset, "expected ':=', found '='");
            case ":=":
              return true;
          }
        }

        return false;
      default:
        return false;
    }
  }

  private GoNode SwitchStmt() {
    var offset = this.Expect(GoTokenKind.Switch);
    GoNode? s1 = null, s2 = null;
    if (this.Tok != GoTokenKind.LBrace) {
      var previous = this._exprLev;
      this._exprLev = -1;
      if (this.Tok != GoTokenKind.Semicolon)
        s2 = this.SimpleStmt(Basic).Statement;

      if (this.Tok == GoTokenKind.Semicolon) {
        this.Next();
        s1 = s2;
        s2 = null;
        if (this.Tok != GoTokenKind.LBrace)
          s2 = this.SimpleStmt(Basic).Statement;
      }

      this._exprLev = previous;
    }

    var typeSwitch = IsTypeSwitchGuard(s2);
    var lbrace = this.Expect(GoTokenKind.LBrace);
    var list = new List<GoNode>();
    while (this.Tok is GoTokenKind.Case or GoTokenKind.Default)
      list.Add(this.CaseClause());

    this.Expect(GoTokenKind.RBrace);
    this.ExpectSemi();
    var body = this.Make("BlockStmt", lbrace).With("List", list);
    if (typeSwitch)
      return this.Make("TypeSwitchStmt", offset).With("Init", s1).With("Assign", s2).With("Body", body);

    return this.Make("SwitchStmt", offset).With("Init", s1).With("Tag", MakeExpr(s2, "switch expression")).With("Body", body);
  }

  private GoNode CommClause() {
    var offset = this.Pos;
    GoNode? communication = null;
    if (this.Tok == GoTokenKind.Case) {
      this.Next();
      var lhs = this.List(false);
      if (this.Tok == GoTokenKind.Arrow) {
        if (lhs.Count > 1)
          throw this.ErrorExpected(lhs[0].Offset, "1 expression");

        this.Next();
        communication = this.Make("SendStmt", lhs[0].Offset).With("Chan", lhs[0]).With("Value", this.Rhs());
      } else if (this.Tok is GoTokenKind.Assign or GoTokenKind.Define) {
        if (lhs.Count > 2)
          throw this.ErrorExpected(lhs[0].Offset, "1 or 2 expressions");

        var token = this.Tok;
        this.Next();
        var rhs = this.Rhs();
        communication = this.Make("AssignStmt", lhs[0].Offset).With("Lhs", lhs).With("Tok", token.Spelling()).With("Rhs", new List<GoNode> { rhs });
      } else {
        if (lhs.Count > 1)
          throw this.ErrorExpected(lhs[0].Offset, "1 expression");

        communication = this.Make("ExprStmt", lhs[0].Offset).With("X", lhs[0]);
      }
    } else {
      this.Expect(GoTokenKind.Default);
    }

    this.Expect(GoTokenKind.Colon);
    return this.Make("CommClause", offset).With("Comm", communication).With("Body", this.StmtList());
  }

  private GoNode SelectStmt() {
    var offset = this.Expect(GoTokenKind.Select);
    var lbrace = this.Expect(GoTokenKind.LBrace);
    var list = new List<GoNode>();
    while (this.Tok is GoTokenKind.Case or GoTokenKind.Default)
      list.Add(this.CommClause());

    this.Expect(GoTokenKind.RBrace);
    this.ExpectSemi();
    return this.Make("SelectStmt", offset).With("Body", this.Make("BlockStmt", lbrace).With("List", list));
  }

  private GoNode ForStmt() {
    var offset = this.Expect(GoTokenKind.For);
    GoNode? s1 = null, s2 = null, s3 = null;
    var isRange = false;
    if (this.Tok != GoTokenKind.LBrace) {
      var previous = this._exprLev;
      this._exprLev = -1;
      if (this.Tok != GoTokenKind.Semicolon) {
        if (this.Tok == GoTokenKind.Range) {
          var range = this.Pos;
          this.Next();
          var y = new List<GoNode> { this.Make("UnaryExpr", range).With("Op", "range").With("X", this.Rhs()) };
          s2 = this.Make("AssignStmt", range).With("Lhs", new List<GoNode>()).With("Rhs", y);
          isRange = true;
        } else {
          (s2, isRange) = this.SimpleStmt(RangeOk);
        }
      }

      if (!isRange && this.Tok == GoTokenKind.Semicolon) {
        this.Next();
        s1 = s2;
        s2 = null;
        if (this.Tok != GoTokenKind.Semicolon)
          s2 = this.SimpleStmt(Basic).Statement;

        this.ExpectSemi();
        if (this.Tok != GoTokenKind.LBrace)
          s3 = this.SimpleStmt(Basic).Statement;
      }

      this._exprLev = previous;
    }

    var body = this.BlockStmt();
    this.ExpectSemi();
    if (isRange) {
      var lhs = s2!.Nodes("Lhs");
      if (lhs.Count > 2)
        throw this.ErrorExpected(lhs[^1].Offset, "at most 2 expressions");

      return this.Make("RangeStmt", offset).With("Key", lhs.Count > 0 ? lhs[0] : null).With("Value", lhs.Count > 1 ? lhs[1] : null)
        .With("Tok", s2.Text("Tok")).With("X", s2.Nodes("Rhs")[0].Node("X")).With("Body", body);
    }

    return this.Make("ForStmt", offset).With("Init", s1).With("Cond", MakeExpr(s2, "boolean or range expression")).With("Post", s3).With("Body", body);
  }

  private GoNode Stmt() {
    this.Enter();
    try {
      switch (this.Tok) {
        case GoTokenKind.Const or GoTokenKind.Type or GoTokenKind.Var: {
          var offset = this.Pos;
          return this.Make("DeclStmt", offset).With("Decl", this.Decl());
        }
        case GoTokenKind.Ident or GoTokenKind.Int or GoTokenKind.Float or GoTokenKind.Imag or GoTokenKind.Char or GoTokenKind.String or GoTokenKind.Func
          or GoTokenKind.LParen or GoTokenKind.LBrack or GoTokenKind.Struct or GoTokenKind.Map or GoTokenKind.Chan or GoTokenKind.Interface
          or GoTokenKind.Add or GoTokenKind.Sub or GoTokenKind.Mul or GoTokenKind.And or GoTokenKind.Xor or GoTokenKind.Arrow or GoTokenKind.Not: {
          var (statement, _) = this.SimpleStmt(LabelOk);
          if (!statement.Is("LabeledStmt"))
            this.ExpectSemi();

          return statement;
        }
        case GoTokenKind.Go:
          return this.GoOrDefer(GoTokenKind.Go, "GoStmt");
        case GoTokenKind.Defer:
          return this.GoOrDefer(GoTokenKind.Defer, "DeferStmt");
        case GoTokenKind.Return:
          return this.ReturnStmt();
        case GoTokenKind.Break or GoTokenKind.Continue or GoTokenKind.Goto or GoTokenKind.Fallthrough:
          return this.BranchStmt(this.Tok);
        case GoTokenKind.LBrace: {
          var block = this.BlockStmt();
          this.ExpectSemi();
          return block;
        }
        case GoTokenKind.If:
          return this.IfStmt();
        case GoTokenKind.Switch:
          return this.SwitchStmt();
        case GoTokenKind.Select:
          return this.SelectStmt();
        case GoTokenKind.For:
          return this.ForStmt();
        case GoTokenKind.Semicolon: {
          var empty = this.Make("EmptyStmt", this.Pos);
          this.Next();
          return empty;
        }
        case GoTokenKind.RBrace:
          return this.Make("EmptyStmt", this.Pos);
        default:
          throw this.ErrorExpected(this.Pos, "statement");
      }
    } finally {
      this.Leave();
    }
  }

  // ---------------------------------------------------------------- declarations

  private GoNode ImportSpec() {
    GoNode? name = null;
    if (this.Tok == GoTokenKind.Ident) {
      name = this.Ident();
    } else if (this.Tok == GoTokenKind.Period) {
      name = this.Make("Ident", this.Pos).With("Name", ".");
      this.Next();
    }

    var offset = this.Pos;
    if (this.Tok != GoTokenKind.String)
      throw Error(offset, this.Tok.IsLiteral() ? "import path must be a string" : "missing import path");

    var path = this.Make("BasicLit", offset).With("Kind", "STRING").With("Value", this.Lit);
    this.Next();
    this.ExpectSemi();
    return this.Make("ImportSpec", name?.Offset ?? offset).With("Name", name).With("Path", path);
  }

  private GoNode ValueSpec(GoTokenKind keyword, int iota) {
    var names = this.IdentList();
    GoNode? type = null;
    var values = new List<GoNode>();
    if (keyword == GoTokenKind.Const) {
      if (this.Tok is not GoTokenKind.Eof and not GoTokenKind.Semicolon and not GoTokenKind.RParen) {
        type = this.TryIdentOrType();
        if (this.Tok == GoTokenKind.Assign) {
          this.Next();
          values = this.List(true);
        }
      }
    } else {
      if (this.Tok != GoTokenKind.Assign)
        type = this.Type();

      if (this.Tok == GoTokenKind.Assign) {
        this.Next();
        values = this.List(true);
      }
    }

    this.ExpectSemi();
    return this.Make("ValueSpec", names[0].Offset).With("Names", names).With("Type", type).With("Values", values).With("iota", iota.ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  private void GenericType(GoNode spec, int opening, GoNode name0, GoNode? type0) {
    var list = this.ParameterList(name0, type0, GoTokenKind.RBrack, false);
    this.Expect(GoTokenKind.RBrack);
    spec.With("TypeParams", this.Make("FieldList", opening).With("List", list));
    if (this.Tok == GoTokenKind.Assign) {
      spec.With("alias", true);
      this.Next();
    }

    spec.With("Type", this.Type());
  }

  private GoNode TypeSpec() {
    var name = this.Ident();
    var spec = this.Make("TypeSpec", name.Offset).With("Name", name);
    if (this.Tok == GoTokenKind.LBrack) {
      var lbrack = this.Pos;
      this.Next();
      if (this.Tok == GoTokenKind.Ident) {
        GoNode x = this.Ident();
        if (this.Tok != GoTokenKind.LBrack) {
          ++this._exprLev;
          var lhs = this.PrimaryExpr(x);
          x = this.BinaryExpr(lhs, 1);
          --this._exprLev;
        }

        var (parameterName, parameterType) = ExtractName(x, this.Tok == GoTokenKind.Comma);
        if (parameterName is not null && (parameterType is not null || this.Tok != GoTokenKind.RBrack))
          this.GenericType(spec, lbrack, parameterName, parameterType);
        else
          spec.With("Type", this.ArrayType(lbrack, x));
      } else {
        spec.With("Type", this.ArrayType(lbrack, null));
      }
    } else {
      if (this.Tok == GoTokenKind.Assign) {
        spec.With("alias", true);
        this.Next();
      }

      spec.With("Type", this.Type());
    }

    this.ExpectSemi();
    return spec;
  }

  /// <summary>Splits the expression after <c>type T[</c> into the name of a type parameter and its constraint where it is one.</summary>
  private static (GoNode? Name, GoNode? Type) ExtractName(GoNode x, bool force) {
    switch (x.Kind) {
      case "Ident":
        return (x, null);
      case "BinaryExpr" when x.Text("Op") == "*":
        if (x.Node("X") is { Kind: "Ident" } name && (force || IsTypeElem(x.Node("Y")!)))
          return (name, new GoNode("StarExpr", x.Offset, x.Line, x.Column).With("X", x.Node("Y")));

        break;
      case "BinaryExpr" when x.Text("Op") == "|":
        if (ExtractName(x.Node("X")!, force || IsTypeElem(x.Node("Y")!)) is (GoNode named, GoNode lhs))
          return (named, new GoNode("BinaryExpr", lhs.Offset, lhs.Line, lhs.Column).With("X", lhs).With("Op", "|").With("Y", x.Node("Y")));

        break;
      case "CallExpr":
        if (x.Node("Fun") is { Kind: "Ident" } function && x.Nodes("Args").Count == 1 && !x.Flag("spread") && (force || IsTypeElem(x.Nodes("Args")[0])))
          return (function, new GoNode("ParenExpr", x.Offset, x.Line, x.Column).With("X", x.Nodes("Args")[0]));

        break;
    }

    return (null, x);
  }

  private static bool IsTypeElem(GoNode x) => x.Kind switch {
    "ArrayType" or "StructType" or "FuncType" or "InterfaceType" or "MapType" or "ChanType" => true,
    "BinaryExpr" => IsTypeElem(x.Node("X")!) || IsTypeElem(x.Node("Y")!),
    "UnaryExpr" => x.Text("Op") == "~",
    "ParenExpr" => IsTypeElem(x.Node("X")!),
    _ => false
  };

  private GoNode GenDecl(GoTokenKind keyword, Func<int, GoNode> spec) {
    var offset = this.Expect(keyword);
    var list = new List<GoNode>();
    var grouped = false;
    if (this.Tok == GoTokenKind.LParen) {
      grouped = true;
      this.Next();
      for (var iota = 0; this.Tok != GoTokenKind.RParen && this.Tok != GoTokenKind.Eof; ++iota)
        list.Add(spec(iota));

      this.Expect(GoTokenKind.RParen);
      this.ExpectSemi();
    } else {
      list.Add(spec(0));
    }

    return this.Make("GenDecl", offset).With("Tok", keyword.Spelling()).With("Specs", list).With("grouped", grouped);
  }

  private GoNode FuncDecl() {
    var offset = this.Expect(GoTokenKind.Func);
    GoNode? receiver = null;
    if (this.Tok == GoTokenKind.LParen)
      receiver = this.Parameters(false);

    var name = this.Ident();
    GoNode? typeParameters = null;
    if (this.Tok == GoTokenKind.LBrack)
      typeParameters = this.TypeParameters();

    var parameters = this.Parameters(false);
    var results = this.Parameters(true);
    GoNode? body = null;
    switch (this.Tok) {
      case GoTokenKind.LBrace:
        body = this.BlockStmt();
        this.ExpectSemi();
        break;
      case GoTokenKind.Semicolon:
        this.Next();
        if (this.Tok == GoTokenKind.LBrace)
          throw Error(this.Pos, "unexpected semicolon or newline before {");

        break;
      default:
        this.ExpectSemi();
        break;
    }

    var type = this.Make("FuncType", offset).With("TypeParams", typeParameters).With("Params", parameters).With("Results", results);
    return this.Make("FuncDecl", offset).With("Recv", receiver).With("Name", name).With("Type", type).With("Body", body);
  }

  private GoNode Decl() => this.Tok switch {
    GoTokenKind.Const => this.GenDecl(GoTokenKind.Const, iota => this.ValueSpec(GoTokenKind.Const, iota)),
    GoTokenKind.Var => this.GenDecl(GoTokenKind.Var, iota => this.ValueSpec(GoTokenKind.Var, iota)),
    GoTokenKind.Type => this.GenDecl(GoTokenKind.Type, _ => this.TypeSpec()),
    GoTokenKind.Func => this.FuncDecl(),
    _ => throw this.ErrorExpected(this.Pos, "declaration")
  };

  private GoNode File() {
    var offset = this.Expect(GoTokenKind.Package);
    var name = this.Ident();
    this.ExpectSemi();
    var declarations = new List<GoNode>();
    while (this.Tok == GoTokenKind.Import)
      declarations.Add(this.GenDecl(GoTokenKind.Import, _ => this.ImportSpec()));

    var previous = GoTokenKind.Import;
    while (this.Tok != GoTokenKind.Eof) {
      if (this.Tok == GoTokenKind.Import && previous != GoTokenKind.Import)
        throw Error(this.Pos, "imports must appear before other declarations");

      previous = this.Tok;
      declarations.Add(this.Decl());
    }

    return this.Make("File", offset).With("Name", name).With("Decls", declarations).With("GoVersion", GoBuildConstraint.MinimumMinor(this._scanner.BuildConstraint));
  }

}
