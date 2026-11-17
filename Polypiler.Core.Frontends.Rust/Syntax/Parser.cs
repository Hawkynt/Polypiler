using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core.Frontends.Rust.Syntax;

/// <summary>
/// A recursive-descent parser for the subset of Rust the frontend imports, following the grammar of
/// The Rust Reference ("Items", "Statements", "Expressions", "Patterns", "Types"): functions,
/// constants and statics, structs, field-less enums, inherent <c>impl</c> blocks, modules and
/// <c>use</c> declarations; statements, the operators with their precedence, blocks, the
/// control-flow expressions, closures and the imported macros; patterns and types. A construct of
/// the language outside the subset is recognised as far as is needed to skip it and becomes a
/// <see cref="Refusal"/> naming it; text that is no Rust at all is a <see cref="RustSyntaxException"/>.
/// </summary>
internal sealed class Parser {

  /// <summary>The attributes any item may carry: they change nothing the import observes.</summary>
  private static readonly HashSet<string> _accepted = ["inline", "must_use", "allow", "warn", "deny", "forbid", "expect", "doc", "cold", "track_caller"];

  /// <summary>The macros the frontend imports.</summary>
  private static readonly HashSet<string> _macros = [
    "assert", "assert_eq", "assert_ne", "debug_assert", "debug_assert_eq", "debug_assert_ne",
    "panic", "unreachable", "unimplemented", "todo", "vec", "format"
  ];

  private readonly List<Token> _tokens;
  private int _position;

  /// <summary>Whether a struct expression is not allowed here: in the head of <c>if</c>, <c>while</c>, <c>match</c> and <c>for</c>.</summary>
  private bool _noStruct;

  private Parser(List<Token> tokens) => this._tokens = tokens;

  /// <summary>Parses a source file.</summary>
  /// <exception cref="RustSyntaxException">The text is not valid Rust.</exception>
  public static SourceFile Parse(string source) => new Parser(Lexer.Tokens(source)).File();

  private Token Current => this._tokens[this._position];

  private Token PeekAt(int offset) => this._tokens[System.Math.Min(this._position + offset, this._tokens.Count - 1)];

  private Token Next() => this._tokens[this._position++ < this._tokens.Count - 1 ? this._position - 1 : this._tokens.Count - 1];

  private bool At(string text) => this.Current.Is(text);

  private bool Accept(string text) {
    if (!this.At(text))
      return false;

    ++this._position;
    return true;
  }

  private Token Expect(string text) => this.At(text) ? this.Next() : throw this.Error($"expected '{text}', found {this.Current}");

  private string Identifier() {
    if (this.Current.Kind != TokenKind.Identifier)
      throw this.Error($"expected identifier, found {this.Current}");

    return this.Next().Text;
  }

  private RustSyntaxException Error(string message) => new(message, this.Current.At);

  /// <summary>Parses with struct expressions allowed or not, restoring the state afterwards.</summary>
  private T Restricted<T>(bool noStruct, System.Func<T> parse) {
    var saved = this._noStruct;
    this._noStruct = noStruct;
    try {
      return parse();
    } finally {
      this._noStruct = saved;
    }
  }

  // ---------------------------------------------------------------- items

  private SourceFile File() {
    var items = new List<Item>();
    // Inner attributes of the crate: the lint levels are accepted, anything else refused.
    while (this.At("#") && this.PeekAt(1).Is("!") && this.PeekAt(2).Is("[")) {
      var at = this.Current.At;
      this.Next();
      this.Next();
      var attribute = this.AttributeBody(at);
      if (!_accepted.Contains(attribute.Name))
        items.Add(new RefusedItem(new Refusal(RustFrontend.UnsupportedAttribute, attribute.Name, at)));
    }

    while (this.Current.Kind != TokenKind.End)
      items.Add(this.Item());

    return new SourceFile(items);
  }

  /// <summary>An attribute whose <c>[</c> is next: its name and the text of its arguments.</summary>
  private Attribute AttributeBody(Span at) {
    this.Expect("[");
    var name = this.Current.Kind == TokenKind.Identifier ? this.Current.Text : "?";
    var arguments = new List<string>();
    var current = new StringBuilder();
    var depth = 1;
    var inner = 0;
    while (depth > 0) {
      var token = this.Next();
      if (token.Kind == TokenKind.End)
        throw new RustSyntaxException("unterminated attribute", token.At);

      depth += token.Is("[") ? 1 : token.Is("]") ? -1 : 0;
      if (depth == 0)
        break;

      if (token.Is("(")) {
        if (++inner == 1)
          continue;
      } else if (token.Is(")")) {
        if (--inner == 0)
          continue;
      }

      if (inner == 1 && token.Is(",")) {
        arguments.Add(current.ToString());
        current.Clear();
        continue;
      }

      if (inner >= 1)
        current.Append(token.Kind == TokenKind.String ? "\"" + token.Text + "\"" : token.Text);
    }

    if (current.Length > 0)
      arguments.Add(current.ToString());

    return new Attribute(name, arguments, at);
  }

  /// <summary>The outer attributes before an item or a statement.</summary>
  private List<Attribute> OuterAttributes() {
    var attributes = new List<Attribute>();
    while (this.At("#") && !this.PeekAt(1).Is("!")) {
      var at = this.Current.At;
      this.Next();
      attributes.Add(this.AttributeBody(at));
    }

    return attributes;
  }

  private Item Item() {
    var at = this.Current.At;
    var attributes = this.OuterAttributes();
    var (isPublic, isVisible) = this.Visibility();
    return this.ItemAfterVisibility(at, isPublic, isVisible, attributes);
  }

  /// <summary><c>pub</c>, <c>pub(crate)</c>, <c>pub(super)</c>, <c>pub(in path)</c> or nothing.</summary>
  private (bool IsPublic, bool IsVisible) Visibility() {
    if (!this.Accept("pub"))
      return (false, false);

    if (!this.At("("))
      return (true, true);

    this.SkipTree();
    return (false, true);
  }

  /// <summary>Whether an attribute is one an item of every kind may carry.</summary>
  private static bool IsNeutral(Attribute attribute) => _accepted.Contains(attribute.Name) && !_functionOnly.Contains(attribute.Name);

  /// <summary>The attributes only a function may carry; on any other item rustc rejects them (E0518).</summary>
  private static readonly HashSet<string> _functionOnly = ["inline", "cold", "track_caller"];

  /// <summary>The refusal of the first attribute an item does not accept, or <see langword="null"/>.</summary>
  private static Refusal? Refused(IEnumerable<Attribute> attributes, System.Func<Attribute, bool> accepts) {
    var refused = attributes.FirstOrDefault(attribute => !IsNeutral(attribute) && !accepts(attribute));
    if (refused is null)
      return null;

    var code = refused.Name is "derive" or "repr" ? RustFrontend.UnsupportedDerive : RustFrontend.UnsupportedAttribute;
    var construct = refused.Name is "derive" or "repr" ? $"{refused.Name}({string.Join(", ", refused.Arguments)})"
      : refused.Name == "cfg" ? $"cfg({string.Join(", ", refused.Arguments)})"
      : refused.Name;
    return new Refusal(code, construct, refused.At);
  }

  private static bool IsTestConfiguration(Attribute attribute) => attribute is { Name: "cfg", Arguments: ["test"] };

  private Item ItemAfterVisibility(Span at, bool isPublic, bool isVisible, List<Attribute> attributes) {
    var qualifiers = new List<string>();
    while (this.Current.Kind == TokenKind.Identifier && this.Current.Text is "const" or "unsafe" or "async" or "extern" && this.IsFunctionAhead()) {
      qualifiers.Add(this.Next().Text);
      if (qualifiers[^1] == "extern" && this.Current.Kind == TokenKind.String)
        this.Next();
    }

    if (this.At("fn")) {
      var refused = Refused(attributes, attribute => attribute.Name == "test" || IsTestConfiguration(attribute) || _functionOnly.Contains(attribute.Name));
      var function = this.Function(at, qualifiers);
      if (function is FunctionItem item) {
        if (refused is not null)
          return new RefusedItem(refused);

        return new FunctionItem(item.At, item.Name, item.Parameters, item.ReturnType, item.Body) {
          IsPublic = isPublic, IsVisible = isVisible, Receiver = item.Receiver, MutableReceiver = item.MutableReceiver,
          ReceiverAt = item.ReceiverAt, IsTest = attributes.Any(attribute => attribute.Name == "test"), Generics = item.Generics
        };
      }

      return function;
    }

    var keyword = this.Current is { Kind: TokenKind.Identifier, Raw: false } ? this.Current.Text : "";
    switch (keyword) {
      case "const" when this.PeekAt(1).Kind == TokenKind.Identifier || this.PeekAt(1).Is("_"): {
        this.Next();
        var constant = this.Constant(at, false);
        return Refused(attributes, _ => false) is { } refused ? new RefusedItem(refused) : WithVisibility(constant, isPublic, isVisible);
      }
      case "static": {
        this.Next();
        if (this.At("mut")) {
          this.SkipItem();
          return new RefusedItem(new Refusal(RustFrontend.UnsafeConstruct, "static mut", at));
        }

        var constant = this.Constant(at, true);
        return Refused(attributes, _ => false) is { } refused ? new RefusedItem(refused) : WithVisibility(constant, isPublic, isVisible);
      }
      case "struct":
        return this.Struct(at, isPublic, isVisible, attributes);
      case "enum":
        return this.Enum(at, isPublic, isVisible, attributes);
      case "impl":
        return this.Impl(at, attributes);
      case "mod":
        return this.Module(at, isPublic, isVisible, attributes);
      case "use":
        return this.Use(at, isPublic, isVisible, attributes);
      case "trait":
        return this.Trait(at, isPublic, isVisible, attributes);
      case "unsafe": {
        // unsafe trait, unsafe impl: an unsafe fn was taken as a qualifier above.
        var form = "unsafe " + this.PeekAt(1).Text;
        this.SkipItem();
        return new RefusedItem(new Refusal(RustFrontend.UnsafeConstruct, form, at));
      }
      case "union":
        this.SkipItem();
        return new RefusedItem(new Refusal(RustFrontend.UnsafeConstruct, "union", at));
      case "macro_rules":
        this.SkipItem();
        return new RefusedItem(new Refusal(RustFrontend.UnsupportedMacro, "macro_rules!", at));
      case "extern":
        this.SkipItem();
        return new RefusedItem(new Refusal(RustFrontend.UnsupportedFunction, "extern block", at));
      case "type":
        this.SkipItem();
        return new RefusedItem(new Refusal(RustFrontend.Unsupported, "type alias", at));
    }

    throw this.Error($"expected item, found {this.Current}");
  }

  private static Item WithVisibility(ConstItem constant, bool isPublic, bool isVisible)
    => new ConstItem(constant.At, constant.Name, constant.Type, constant.Value) { IsStatic = constant.IsStatic, IsPublic = isPublic, IsVisible = isVisible };

  /// <summary>The rest of a <c>const</c> or <c>static</c>: its name, type and value.</summary>
  private ConstItem Constant(Span at, bool isStatic) {
    var name = this.At("_") ? this.Next().Text : this.Identifier();
    this.Expect(":");
    var type = this.Type();
    this.Expect("=");
    var value = this.Expression();
    this.Expect(";");
    return new ConstItem(at, name, type, value) { IsStatic = isStatic };
  }

  /// <summary>Whether a qualifier at the current token belongs to a function: <c>fn</c> follows the qualifiers.</summary>
  private bool IsFunctionAhead() {
    for (var offset = 0; offset < 4; ++offset) {
      var token = this.PeekAt(offset);
      if (token.Is("fn"))
        return true;

      if (!(token.Kind == TokenKind.String || token.Text is "const" or "unsafe" or "async" or "extern"))
        return false;
    }

    return false;
  }

  /// <summary>Skips an item: up to its <c>;</c> or through its braced body.</summary>
  private void SkipItem() {
    while (this.Current.Kind != TokenKind.End) {
      if (this.Accept(";"))
        return;

      if (this.At("{")) {
        this.SkipTree();
        return;
      }

      this.SkipTree();
    }
  }

  /// <summary>Skips one token, or a delimited group of them with everything inside.</summary>
  private void SkipTree() {
    var open = this.Next();
    if (open.Kind == TokenKind.End)
      throw new RustSyntaxException("unexpected end of file", open.At);

    var close = open.Text switch { "(" => ")", "[" => "]", "{" => "}", _ => null };
    if (open.Kind != TokenKind.Punctuation || close is null)
      return;

    while (!this.At(close)) {
      if (this.Current.Kind == TokenKind.End)
        throw new RustSyntaxException($"unclosed delimiter '{open.Text}'", open.At);

      this.SkipTree();
    }

    this.Next();
  }

  /// <summary>Generic parameters at a <c>&lt;</c>, skipped: whether they are lifetimes only.</summary>
  private bool LifetimesOnly() {
    var start = this._position;
    this.SkipAngles();
    var inside = this._tokens.Skip(start + 1).Take(this._position - start - 2).ToList();
    return inside.Count > 0 && inside.All(token => token.Kind == TokenKind.Lifetime || token.Is(",") || token.Is(":") || token.Is("+"));
  }

  private Item Function(Span at, List<string> qualifiers, bool inTrait = false) {
    this.Expect("fn");
    var name = this.Identifier();
    Refusal? refused = null;
    if (qualifiers.Contains("async"))
      refused = new Refusal(RustFrontend.Coroutine, "async fn", at);
    else if (qualifiers.FirstOrDefault(qualifier => qualifier is "unsafe" or "extern") is { } qualifier)
      refused = new Refusal(RustFrontend.UnsupportedFunction, $"{qualifier} fn", at);

    var generics = this.Generics($"fn {name}", ref refused);
    this.Expect("(");
    var parameters = new List<Parameter>();
    var receiver = ReceiverKind.None;
    var mutableReceiver = false;
    var receiverAt = this.Current.At;
    // The receiver of a method: self, mut self, &self, &mut self, &'a self.
    if (this.SelfAhead()) {
      receiverAt = this.Current.At;
      if (this.Accept("&")) {
        if (this.Current.Kind == TokenKind.Lifetime) {
          refused ??= new Refusal(RustFrontend.UnsupportedLifetime, "lifetime of self", this.Current.At);
          this.Next();
        }

        receiver = this.Accept("mut") ? ReceiverKind.Exclusive : ReceiverKind.Shared;
      } else {
        receiver = ReceiverKind.Value;
        mutableReceiver = this.Accept("mut");
      }

      this.Expect("self");
      if (this.Accept(":")) {
        refused ??= new Refusal(RustFrontend.UnsupportedFunction, "typed self", receiverAt);
        this.Type();
      }

      if (!this.Accept(","))
        this.Expect(")");
      else if (this.At(")"))
        this.Next();
    }

    if (receiver == ReceiverKind.None || this._tokens[this._position - 1].Is(",")) {
      while (!this.At(")")) {
        var parameterAt = this.Current.At;
        if (this.At("...")) {
          refused ??= new Refusal(RustFrontend.UnsupportedFunction, "variadic fn", parameterAt);
          this.Next();
        } else {
          var pattern = this.PatternNoTopAlternatives();
          this.Expect(":");
          if (this.At("...")) {
            refused ??= new Refusal(RustFrontend.UnsupportedFunction, "variadic fn", parameterAt);
            this.Next();
          } else {
            parameters.Add(new Parameter(pattern, this.Type(), parameterAt));
          }
        }

        if (!this.Accept(","))
          break;
      }

      this.Expect(")");
    }

    TypeRef? returnType = null;
    if (this.Accept("->"))
      returnType = this.Type();

    generics = this.Where(generics, ref refused);
    if (this.At(";")) {
      this.Next();
      if (!inTrait)
        return new RefusedItem(refused ?? new Refusal(RustFrontend.UnsupportedFunction, "fn without a body", at));

      return refused is null
        ? new FunctionItem(at, name, parameters, returnType, new Block(at, [], null)) { Receiver = receiver, MutableReceiver = mutableReceiver, ReceiverAt = receiverAt, Generics = generics, IsRequired = true }
        : new RefusedItem(refused);
    }

    var body = this.Block();
    return refused is null
      ? new FunctionItem(at, name, parameters, returnType, body) { Receiver = receiver, MutableReceiver = mutableReceiver, ReceiverAt = receiverAt, Generics = generics }
      : new RefusedItem(refused);
  }

  /// <summary>
  /// The generic type parameters at a <c>&lt;</c>, each with its bounds; none without one. A lifetime,
  /// a const generic and a default type refuse the item the parameters belong to.
  /// </summary>
  private List<GenericParameter> Generics(string owner, ref Refusal? refused) {
    var generics = new List<GenericParameter>();
    if (!this.At("<"))
      return generics;

    var at = this.Current.At;
    if (this.LifetimesAhead()) {
      refused ??= new Refusal(RustFrontend.UnsupportedLifetime, owner == "impl" ? "impl<'a>" : $"lifetime parameters of {owner}", at);
      this.SkipAngles();
      return generics;
    }

    this.Next();
    while (!this.At(">")) {
      var parameterAt = this.Current.At;
      if (this.Current.Kind == TokenKind.Lifetime) {
        refused ??= new Refusal(RustFrontend.UnsupportedLifetime, $"lifetime parameters of {owner}", parameterAt);
        this.Next();
        if (this.Accept(":"))
          this.Bounds(ref refused);
      } else if (this.Accept("const")) {
        refused ??= new Refusal(RustFrontend.UnsupportedGeneric, $"const generic parameter of {owner}", parameterAt);
        this.Identifier();
        this.Expect(":");
        this.Type();
      } else {
        var name = this.Identifier();
        var bounds = this.Accept(":") ? this.Bounds(ref refused) : [];
        generics.Add(new GenericParameter(name, parameterAt) { Bounds = bounds });
      }

      if (this.Accept("=")) {
        refused ??= new Refusal(RustFrontend.UnsupportedGeneric, $"default type parameter of {owner}", this.Current.At);
        this.Type();
      }

      if (!this.Accept(","))
        break;
    }

    this.CloseAngle();
    return generics;
  }

  /// <summary>Whether the generic parameters at a <c>&lt;</c> are lifetimes only, which the subset refuses as before generics.</summary>
  private bool LifetimesAhead() {
    var start = this._position;
    var only = this.LifetimesOnly();
    this._position = start;
    return only;
  }

  /// <summary>The bounds after a <c>:</c>: traits joined by <c>+</c>; <c>?Sized</c> says nothing a value of the subset needs, a lifetime refuses.</summary>
  private List<TypeRef> Bounds(ref Refusal? refused) {
    var bounds = new List<TypeRef>();
    do {
      if (this.Current.Kind == TokenKind.Lifetime) {
        refused ??= new Refusal(RustFrontend.UnsupportedLifetime, $"'{this.Current.Text}", this.Current.At);
        this.Next();
      } else if (this.Accept("?")) {
        this.Type();
      } else if (this.At("for")) {
        refused ??= new Refusal(RustFrontend.UnsupportedTrait, "higher-ranked bound", this.Current.At);
        this.Next();
        this.SkipAngles();
        this.Type();
      } else {
        bounds.Add(this.Type());
      }
    } while (this.Accept("+"));

    return bounds;
  }

  /// <summary>A <c>where</c> clause: its bounds join those of the parameters they name; a bound of any other type refuses.</summary>
  private List<GenericParameter> Where(List<GenericParameter> generics, ref Refusal? refused) {
    if (!this.Accept("where"))
      return generics;

    var joined = generics.ToList();
    while (!this.At("{") && !this.At(";") && this.Current.Kind != TokenKind.End) {
      var at = this.Current.At;
      if (this.Current.Kind == TokenKind.Identifier && this.PeekAt(1).Is(":") && joined.FindIndex(parameter => parameter.Name == this.Current.Text) is var index and >= 0) {
        this.Next();
        this.Next();
        var bounds = this.Bounds(ref refused);
        joined[index] = joined[index] with { Bounds = [.. joined[index].Bounds, .. bounds] };
      } else {
        refused ??= new Refusal(RustFrontend.UnsupportedGeneric, "where clause on a type that is no type parameter", at);
        while (!this.At(",") && !this.At("{") && !this.At(";") && this.Current.Kind != TokenKind.End)
          this.SkipTree();
      }

      if (!this.Accept(","))
        break;
    }

    return joined;
  }

  /// <summary><c>type Name = Type;</c> of an impl, <c>type Name;</c> of a trait; bounds and generics of the type are refused.</summary>
  private Item AssociatedType(Span at) {
    this.Expect("type");
    var name = this.Identifier();
    if (this.At("<") || this.At(":")) {
      this.SkipItem();
      return new RefusedItem(new Refusal(RustFrontend.UnsupportedTrait, $"associated type {name} with generics or bounds", at));
    }

    var type = this.Accept("=") ? this.Type() : null;
    this.Expect(";");
    return new AssociatedTypeItem(at, name, type);
  }

  /// <summary>A trait: its generic parameters are refused, its supertraits kept, its methods with and without a body and its associated types.</summary>
  private Item Trait(Span at, bool isPublic, bool isVisible, List<Attribute> attributes) {
    this.Expect("trait");
    var name = this.Identifier();
    Refusal? refused = Refused(attributes, _ => false);
    var generics = this.Generics($"trait {name}", ref refused);
    var supertraits = this.Accept(":") ? this.Bounds(ref refused) : [];
    if (this.At("where")) {
      refused ??= new Refusal(RustFrontend.UnsupportedGeneric, "where clause of a trait", this.Current.At);
      while (!this.At("{") && this.Current.Kind != TokenKind.End)
        this.SkipTree();
    }

    if (refused is not null) {
      this.SkipTree();
      return new RefusedItem(refused);
    }

    this.Expect("{");
    var items = new List<Item>();
    while (!this.At("}")) {
      if (this.Current.Kind == TokenKind.End)
        throw this.Error("this file contains an unclosed delimiter");

      var itemAt = this.Current.At;
      var itemAttributes = this.OuterAttributes();
      if (this.At("type")) {
        items.Add(this.AssociatedType(itemAt));
      } else if (this.At("fn")) {
        var function = this.Function(itemAt, [], true);
        items.Add(Refused(itemAttributes, attribute => _functionOnly.Contains(attribute.Name)) is { } attribute ? new RefusedItem(attribute) : function);
      } else {
        this.SkipItem();
        items.Add(new RefusedItem(new Refusal(RustFrontend.UnsupportedTrait, "trait item other than a method or an associated type", itemAt)));
      }
    }

    this.Expect("}");
    return new TraitItem(at, name, items) { Supertraits = supertraits, Generics = generics, IsPublic = isPublic, IsVisible = isVisible };
  }

  /// <summary>Whether the parameter list starts with a receiver.</summary>
  private bool SelfAhead() {
    if (this.At("self"))
      return true;

    if (this.At("mut"))
      return this.PeekAt(1).Is("self");

    if (!this.At("&"))
      return false;

    var offset = 1;
    if (this.PeekAt(offset).Kind == TokenKind.Lifetime)
      ++offset;

    if (this.PeekAt(offset).Is("mut"))
      ++offset;

    return this.PeekAt(offset).Is("self");
  }

  private Item Struct(Span at, bool isPublic, bool isVisible, List<Attribute> attributes) {
    this.Expect("struct");
    var name = this.Identifier();
    Refusal? refused = Refused(attributes, attribute => attribute.Name is "derive" or "repr");
    var generics = this.Where(this.Generics($"struct {name}", ref refused), ref refused);

    var fields = new List<FieldDeclaration>();
    StructForm form;
    if (this.Accept(";")) {
      form = StructForm.Unit;
    } else if (this.Accept("(")) {
      form = StructForm.Tuple;
      while (!this.At(")")) {
        var fieldAt = this.Current.At;
        this.OuterAttributes();
        var (_, visible) = this.Visibility();
        fields.Add(new FieldDeclaration(fields.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), this.Type(), visible, fieldAt));
        if (!this.Accept(","))
          break;
      }

      this.Expect(")");
      this.Expect(";");
    } else {
      form = StructForm.Named;
      this.Expect("{");
      while (!this.At("}")) {
        var fieldAt = this.Current.At;
        this.OuterAttributes();
        var (_, visible) = this.Visibility();
        var fieldName = this.Identifier();
        this.Expect(":");
        fields.Add(new FieldDeclaration(fieldName, this.Type(), visible, fieldAt));
        if (!this.Accept(","))
          break;
      }

      this.Expect("}");
    }

    if (form == StructForm.Tuple)
      generics = this.Where(generics, ref refused);

    return refused is not null ? new RefusedItem(refused) : new StructItem(at, name, form, fields, attributes) { IsPublic = isPublic, IsVisible = isVisible, Generics = generics };
  }

  private Item Enum(Span at, bool isPublic, bool isVisible, List<Attribute> attributes) {
    this.Expect("enum");
    var name = this.Identifier();
    Refusal? refused = Refused(attributes, attribute => attribute.Name is "derive" or "repr");
    if (this.At("<")) {
      refused ??= new Refusal(RustFrontend.UnsupportedGeneric, $"enum {name}<...>", this.Current.At);
      this.SkipAngles();
    }

    this.Expect("{");
    var variants = new List<VariantDeclaration>();
    while (!this.At("}")) {
      var variantAt = this.Current.At;
      var variantAttributes = this.OuterAttributes();
      var isDefault = variantAttributes.Any(attribute => attribute.Name == "default");
      refused ??= Refused(variantAttributes, attribute => attribute.Name == "default");
      var variant = this.Identifier();
      if (this.At("(") || this.At("{")) {
        refused ??= new Refusal(RustFrontend.DataEnum, name, at);
        this.SkipTree();
      }

      Expr? discriminant = null;
      if (this.Accept("="))
        discriminant = this.Expression();

      variants.Add(new VariantDeclaration(variant, discriminant, isDefault, variantAt));
      if (!this.Accept(","))
        break;
    }

    this.Expect("}");
    return refused is not null ? new RefusedItem(refused) : new EnumItem(at, name, variants, attributes) { IsPublic = isPublic, IsVisible = isVisible };
  }

  private Item Impl(Span at, List<Attribute> attributes) {
    this.Expect("impl");
    Refusal? refused = Refused(attributes, _ => false);
    var generics = this.Generics("impl", ref refused);
    if (this.Accept("!"))
      refused ??= new Refusal(RustFrontend.UnsupportedTrait, "negative impl", at);

    var type = this.Type();
    TypeRef? implemented = null;
    if (this.Accept("for")) {
      var target = this.Type();
      if (type.Name.Split("::")[^1] == "Drop")
        refused ??= new Refusal(RustFrontend.Destructor, target.Text, at);

      implemented = type;
      type = target;
    }

    generics = this.Where(generics, ref refused);

    if (refused is not null) {
      this.SkipTree();
      return new RefusedItem(refused);
    }

    this.Expect("{");
    var items = new List<Item>();
    while (!this.At("}")) {
      if (this.Current.Kind == TokenKind.End)
        throw this.Error("this file contains an unclosed delimiter");

      var itemAt = this.Current.At;
      var itemAttributes = this.OuterAttributes();
      var (isPublic, isVisible) = this.Visibility();
      if (this.At("type")) {
        items.Add(this.AssociatedType(itemAt));
        continue;
      }

      var item = this.ItemAfterVisibility(itemAt, isPublic, isVisible, itemAttributes);
      items.Add(item is FunctionItem or ConstItem or RefusedItem ? item : new RefusedItem(new Refusal(RustFrontend.Unsupported, "item in an impl block", itemAt)));
    }

    this.Expect("}");
    return new ImplItem(at, type, items) { Trait = implemented, Generics = generics };
  }

  private Item Module(Span at, bool isPublic, bool isVisible, List<Attribute> attributes) {
    this.Expect("mod");
    var name = this.Identifier();
    var refused = Refused(attributes, IsTestConfiguration);
    var testOnly = attributes.Any(IsTestConfiguration);
    if (this.Accept(";"))
      return refused is not null ? new RefusedItem(refused) : new ModuleItem(at, name, null) { IsPublic = isPublic, IsVisible = isVisible, TestOnly = testOnly };

    this.Expect("{");
    var items = new List<Item>();
    while (this.At("#") && this.PeekAt(1).Is("!") && this.PeekAt(2).Is("[")) {
      var attributeAt = this.Current.At;
      this.Next();
      this.Next();
      var attribute = this.AttributeBody(attributeAt);
      if (!_accepted.Contains(attribute.Name))
        items.Add(new RefusedItem(new Refusal(RustFrontend.UnsupportedAttribute, attribute.Name, attributeAt)));
    }

    while (!this.At("}")) {
      if (this.Current.Kind == TokenKind.End)
        throw this.Error("this file contains an unclosed delimiter");

      items.Add(this.Item());
    }

    this.Expect("}");
    return refused is not null ? new RefusedItem(refused) : new ModuleItem(at, name, items) { IsPublic = isPublic, IsVisible = isVisible, TestOnly = testOnly };
  }

  private Item Use(Span at, bool isPublic, bool isVisible, List<Attribute> attributes) {
    this.Expect("use");
    var paths = new List<UsePath>();
    this.Accept("::");
    this.UseTree([], paths);
    this.Expect(";");
    var refused = Refused(attributes, _ => false);
    return refused is not null ? new RefusedItem(refused) : new UseItem(at, paths) { IsPublic = isPublic, IsVisible = isVisible };
  }

  private void UseTree(List<string> prefix, List<UsePath> paths) {
    var at = this.Current.At;
    if (this.Accept("*")) {
      paths.Add(new UsePath(prefix, null, true, at));
      return;
    }

    if (this.Accept("{")) {
      while (!this.At("}")) {
        this.UseTree([.. prefix], paths);
        if (!this.Accept(","))
          break;
      }

      this.Expect("}");
      return;
    }

    var segments = new List<string>(prefix) { this.Identifier() };
    while (this.Accept("::")) {
      if (this.At("*") || this.At("{")) {
        this.UseTree(segments, paths);
        return;
      }

      segments.Add(this.Identifier());
    }

    string? alias = null;
    if (this.Accept("as"))
      alias = this.At("_") ? this.Next().Text : this.Identifier();

    paths.Add(new UsePath(segments, alias, false, at));
  }

  private void SkipAngles() {
    var depth = 0;
    do {
      var token = this.Next();
      if (token.Kind == TokenKind.End)
        throw new RustSyntaxException("unclosed '<'", token.At);

      depth += token.Kind != TokenKind.Punctuation ? 0 : token.Text switch { "<" => 1, ">" => -1, ">>" => -2, "->" => 0, _ => 0 };
    } while (depth > 0);
  }

  // ---------------------------------------------------------------- types

  /// <summary>A type of the source.</summary>
  private TypeRef Type() {
    var at = this.Current.At;
    var start = this._position;
    var type = this.TypeUnrecorded(at);
    var text = string.Join("", this._tokens.Skip(start).Take(this._position - start).Select(token => token.Kind switch {
      TokenKind.Lifetime => "'" + token.Text + " ",
      TokenKind.Identifier when token.Text is "mut" or "const" or "dyn" or "impl" or "unsafe" or "extern" => token.Text + " ",
      TokenKind.Punctuation when token.Text is "," or ";" => token.Text + " ",
      TokenKind.Punctuation when token.Text == "->" => " -> ",
      TokenKind.Punctuation when token.Text == "=" => " = ",
      TokenKind.String => "\"" + token.Text + "\" ",
      _ => token.Text
    })).TrimEnd();
    // A '>' that closed this type as half of a '>>' stays with the token the type around it closes with.
    var open = text.Count(character => character == '<') - text.Count(character => character == '>') + (text.Length - text.Replace("->", "", System.StringComparison.Ordinal).Length) / 2;
    return type with { Text = open > 0 ? text + new string('>', open) : text };
  }

  private TypeRef TypeUnrecorded(Span at) {
    var token = this.Current;
    if (this.Accept("(")) {
      var elements = new List<TypeRef>();
      while (!this.At(")")) {
        elements.Add(this.Type());
        if (!this.Accept(","))
          break;
      }

      this.Expect(")");
      return elements.Count == 1 && !this._tokens[this._position - 2].Is(",") ? elements[0]
        : new TypeRef("(" + string.Join(", ", elements.Select(element => element.Text)) + ")", at) { Shape = TypeShape.Tuple, Arguments = elements };
    }

    if (this.Accept("[")) {
      var element = this.Type();
      if (this.Accept(";")) {
        var length = this.Restricted(false, this.Expression);
        this.Expect("]");
        return new TypeRef($"[{element.Text}; _]", at) { Shape = TypeShape.Array, Element = element, Length = length };
      }

      this.Expect("]");
      return new TypeRef($"[{element.Text}]", at) { Shape = TypeShape.Slice, Element = element };
    }

    if (this.Accept("!"))
      return new TypeRef("!", at) { Shape = TypeShape.Never };

    // <T as Trait>::Name: the associated type of the type, which the subset finds by its name.
    if (token.Is("<")) {
      this.Next();
      var owner = this.Type();
      this.Expect("as");
      this.Type();
      this.CloseAngle();
      this.Expect("::");
      return new TypeRef(owner.Name + "::" + this.Identifier(), at);
    }

    if (token.Is("&") || token.Is("&&")) {
      this.Next();
      string? lifetime = null;
      if (this.Current.Kind == TokenKind.Lifetime)
        lifetime = this.Next().Text;

      var mutable = this.Accept("mut");
      var target = this.Type();
      var reference = new TypeRef("&" + (mutable ? "mut " : "") + target.Text, at) { Shape = TypeShape.Reference, Element = target, Mutable = mutable, Lifetime = lifetime };
      return token.Is("&&") ? new TypeRef("&" + reference.Name, at) { Shape = TypeShape.Reference, Element = reference with { Text = reference.Name } } : reference;
    }

    if (token.Is("*")) {
      this.Next();
      var mutable = this.Accept("mut");
      if (!mutable)
        this.Accept("const");

      var target = this.Type();
      return new TypeRef((mutable ? "*mut " : "*const ") + target.Text, at) { Shape = TypeShape.Pointer, Element = target, Mutable = mutable };
    }

    if (token.Is("fn") || token.Is("unsafe") || token.Is("extern")) {
      var plain = token.Is("fn");
      while (!this.At("fn")) {
        if (this.Current.Kind == TokenKind.End)
          throw this.Error("expected 'fn'");

        this.Next();
      }

      this.Next();
      this.Expect("(");
      var parameters = new List<TypeRef>();
      while (!this.At(")")) {
        if (this.Current.Kind == TokenKind.Identifier && this.PeekAt(1).Is(":")) {
          this.Next();
          this.Next();
        }

        parameters.Add(this.Type());
        if (!this.Accept(","))
          break;
      }

      this.Expect(")");
      var result = this.Accept("->") ? this.Type() : null;
      return new TypeRef(plain ? "fn(...)" : "unsafe fn(...)", at) { Shape = plain ? TypeShape.Function : TypeShape.Pointer, Arguments = parameters, Element = result };
    }

    if (token.Is("dyn") || token.Is("impl")) {
      this.Next();
      var traits = new List<TypeRef> { this.Type() };
      var bounds = new List<string> { traits[0].Text };
      while (this.Accept("+")) {
        if (this.Current.Kind == TokenKind.Lifetime) {
          bounds.Add("'" + this.Next().Text);
          continue;
        }

        traits.Add(this.Type());
        bounds.Add(traits[^1].Text);
      }

      return new TypeRef(token.Text + " " + string.Join(" + ", bounds), at) { Shape = TypeShape.Trait, Arguments = traits };
    }

    if (token.Is("_"))
      return new TypeRef("_", this.Next().At);

    var segments = new List<string>();
    if (this.Accept("::") && this.Current.Text is not ("std" or "core" or "alloc"))
      segments.Add("");

    segments.Add(this.Identifier());
    var arguments = new List<TypeRef>();
    while (true) {
      if (this.At("<")) {
        this.Next();
        while (!this.At(">") && !this.At(">>")) {
          if (this.Current.Kind == TokenKind.Lifetime) {
            arguments.Add(new TypeRef("'" + this.Current.Text, this.Current.At) { Shape = TypeShape.Reference, Lifetime = this.Next().Text });
          } else if (this.Current.Kind == TokenKind.Identifier && this.PeekAt(1).Is("=")) {
            var binding = this.Next();
            this.Next();
            var bound = this.Type();
            arguments.Add(new TypeRef(binding.Text, binding.At) { Binding = binding.Text, Element = bound, Text = $"{binding.Text} = {bound.Text}" });
          } else {
            arguments.Add(this.Type());
          }

          if (!this.Accept(","))
            break;
        }

        this.CloseAngle();
        if (!this.At("::"))
          break;
      }

      // The parenthesised form of the closure traits: Fn(u32) -> u32.
      if (this.At("(")) {
        this.SkipTree();
        if (this.Accept("->"))
          this.Type();

        return new TypeRef(string.Join("::", segments) + "(...)", at) { Shape = TypeShape.Trait };
      }

      if (!this.Accept("::"))
        break;

      segments.Add(this.Identifier());
    }

    return new TypeRef(string.Join("::", segments), at) { Arguments = arguments };
  }

  /// <summary>Closes generic arguments: a <c>&gt;</c>, or half of a <c>&gt;&gt;</c>, which then becomes the <c>&gt;</c> left.</summary>
  private void CloseAngle() {
    if (this.Accept(">"))
      return;

    // A token that starts with the '>' closing the list - '>>', '>=', '>>=' - gives it up and keeps the rest.
    var token = this.Current;
    if (token.Kind != TokenKind.Punctuation || token.Text is not (">>" or ">=" or ">>="))
      throw this.Error($"expected '>', found {token}");

    this._tokens[this._position] = token with { Text = token.Text[1..], At = new Span(token.At.Line, token.At.Column + 1) };
  }

  // ---------------------------------------------------------------- statements and blocks

  private Block Block() {
    var at = this.Expect("{").At;
    return this.Restricted(false, () => this.BlockBody(at));
  }

  private Block BlockBody(Span at) {
    var statements = new List<Statement>();
    Expr? tail = null;
    while (!this.At("}")) {
      if (this.Current.Kind == TokenKind.End)
        throw this.Error("this file contains an unclosed delimiter");

      if (this.Accept(";"))
        continue;

      var statementAt = this.Current.At;
      // Attributes of a statement: the accepted ones change nothing, any other refuses the statement.
      if (this.At("#") && !this.PeekAt(1).Is("!")) {
        var attribute = this.OuterAttributes()[0];
        if (!_accepted.Contains(attribute.Name))
          statements.Add(new RefusedStatement(new Refusal(RustFrontend.UnsupportedAttribute, attribute.Name == "cfg" ? $"cfg({string.Join(", ", attribute.Arguments)})" : attribute.Name, attribute.At)));

        continue;
      }

      if (this.At("let")) {
        statements.Add(this.Let());
        continue;
      }

      if (this.At("const") && (this.PeekAt(1).Kind == TokenKind.Identifier || this.PeekAt(1).Is("_")) && this.PeekAt(2).Is(":")) {
        this.Next();
        statements.Add(new ConstStatement(this.Constant(statementAt, false)));
        continue;
      }

      if (this.IsItemAhead()) {
        var item = this.Item();
        statements.Add(item switch {
          FunctionItem function => new FunctionStatement(function),
          ConstItem constant => new ConstStatement(constant),
          RefusedItem refused => new RefusedStatement(refused.Refusal),
          _ => new RefusedStatement(new Refusal(RustFrontend.UnsupportedFunction, "nested item", statementAt))
        });
        continue;
      }

      // A block-like expression ends the statement: `if c { a } else { b } - 1` is two of them.
      var blockLike = this.IsBlockLike();
      var expression = blockLike ? this.Primary() : this.Expression();
      if (blockLike && (this.At(".") || this.At("?"))) {
        expression = this.Postfix(expression);
        blockLike = false;
      }

      if (this.At("}")) {
        tail = expression;
        break;
      }

      if (!this.Accept(";") && !blockLike)
        throw this.Error($"expected ';', found {this.Current}");

      statements.Add(new ExpressionStatement(statementAt, expression));
    }

    this.Expect("}");
    return new Block(at, statements, tail);
  }

  /// <summary>Whether an item starts at the current token inside a block.</summary>
  private bool IsItemAhead() {
    var token = this.Current;
    if (token.Is("fn") || token.Is("pub"))
      return true;

    if (token is not { Kind: TokenKind.Identifier, Raw: false } || this.PeekAt(1).Is("!") || this.PeekAt(1).Is("::"))
      return false;

    return token.Text is "use" or "mod" or "struct" or "enum" or "impl" or "trait" or "union" or "extern" or "macro_rules" or "type"
      || (token.Text == "static" && this.PeekAt(1).Kind == TokenKind.Identifier)
      || (token.Text is "const" or "unsafe" or "async" && this.IsFunctionAhead());
  }

  /// <summary>Whether the expression at the current token is block-like, which ends a statement without a semicolon.</summary>
  private bool IsBlockLike() {
    var token = this.Current;
    if (token.Is("{") || (token.Kind == TokenKind.Lifetime && this.PeekAt(1).Is(":")))
      return true;

    return token is { Kind: TokenKind.Identifier, Raw: false } && token.Text is "if" or "match" or "loop" or "while" or "for" or "unsafe";
  }

  private Statement Let() {
    var at = this.Expect("let").At;
    var pattern = this.PatternNoTopAlternatives();
    TypeRef? type = null;
    if (this.Accept(":"))
      type = this.Type();

    Expr? initializer = null;
    if (this.Accept("="))
      initializer = this.Expression();

    Block? otherwise = null;
    if (this.Accept("else"))
      otherwise = this.Block();

    this.Expect(";");
    return new LetStatement(at, pattern, type, initializer, otherwise);
  }

  // ---------------------------------------------------------------- expressions

  /// <summary>An expression, assignment included.</summary>
  private Expr Expression() => this.Assignment();

  private static readonly string[] _assignments = ["=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>="];

  private Expr Assignment() {
    var left = this.Range();
    var op = this.Current;
    if (op.Kind == TokenKind.Punctuation && _assignments.Contains(op.Text)) {
      this.Next();
      var right = this.Assignment();
      return new AssignExpr(op.At, op.Text == "=" ? "=" : op.Text[..^1], left, right);
    }

    return left;
  }

  private Expr Range() {
    var at = this.Current.At;
    if (this.At("..") || this.At("..=")) {
      var inclusive = this.Next().Text == "..=";
      return new RangeExpr(at, null, this.StartsExpression() ? this.Binary(0) : null, inclusive);
    }

    var left = this.Binary(0);
    if (this.At("..") || this.At("..=")) {
      var inclusive = this.Next().Text == "..=";
      return new RangeExpr(left.At, left, this.StartsExpression() ? this.Binary(0) : null, inclusive);
    }

    return left;
  }

  private bool StartsExpression() {
    var token = this.Current;
    return token.Kind switch {
      TokenKind.Punctuation => token.Text is "(" or "-" or "!" or "*" or "&" or "&&" or "[" or "|" or "||" || (token.Text == "{" && !this._noStruct),
      TokenKind.Identifier => token.Text is not ("as" or "else"),
      TokenKind.End => false,
      _ => true
    };
  }

  /// <summary>The binary operators by precedence, loosest first.</summary>
  private static readonly string[][] _levels = [
    ["||"], ["&&"], ["==", "!=", "<", ">", "<=", ">="], ["|"], ["^"], ["&"], ["<<", ">>"], ["+", "-"], ["*", "/", "%"]
  ];

  private Expr Binary(int level) {
    if (level == _levels.Length)
      return this.Cast();

    var left = this.Binary(level + 1);
    while (this.Current.Kind == TokenKind.Punctuation && _levels[level].Contains(this.Current.Text)) {
      var op = this.Next();
      var right = this.Binary(level + 1);
      left = new BinaryExpr(op.At, op.Text, left, right);
      // Comparisons do not associate: `a < b < c` needs parentheses.
      if (level == 2 && this.Current.Kind == TokenKind.Punctuation && _levels[2].Contains(this.Current.Text))
        throw this.Error("comparison operators cannot be chained");
    }

    return left;
  }

  private Expr Cast() {
    var operand = this.Unary();
    while (this.At("as")) {
      var at = this.Next().At;
      operand = new CastExpr(at, operand, this.Type());
    }

    return operand;
  }

  private Expr Unary() {
    var token = this.Current;
    if (token.Is("-") || token.Is("!")) {
      this.Next();
      return new UnaryExpr(token.At, token.Text, this.Unary());
    }

    if (token.Is("*")) {
      this.Next();
      return new DerefExpr(token.At, this.Unary());
    }

    if (token.Is("&") || token.Is("&&")) {
      this.Next();
      if (this.At("raw") && (this.PeekAt(1).Is("const") || this.PeekAt(1).Is("mut"))) {
        this.Next();
        this.Next();
        this.Unary();
        return new RefusedExpr(new Refusal(RustFrontend.UnsafeConstruct, "raw borrow", token.At));
      }

      var mutable = this.Accept("mut");
      var operand = this.Unary();
      var reference = new ReferenceExpr(token.At, mutable, operand);
      return token.Is("&&") ? new ReferenceExpr(token.At, false, reference) : reference;
    }

    return this.Postfix(this.Primary());
  }

  private Expr Postfix(Expr expression) {
    while (true) {
      var token = this.Current;
      if (token.Is(".")) {
        this.Next();
        var name = this.Current;
        if (name.Kind == TokenKind.Integer) {
          this.Next();
          expression = new FieldExpr(name.At, expression, name.Text);
          continue;
        }

        // `t.0.1` lexes the indices as one float.
        if (name.Kind == TokenKind.Float && name.Suffix is null && name.Text.Split('.') is [var first, var second] && first.Length > 0 && second.Length > 0 && first.All(char.IsAsciiDigit) && second.All(char.IsAsciiDigit)) {
          this.Next();
          expression = new FieldExpr(name.At, new FieldExpr(name.At, expression, first), second);
          continue;
        }

        var member = this.Identifier();
        if (member == "await") {
          expression = new RefusedExpr(new Refusal(RustFrontend.Coroutine, ".await", name.At));
          continue;
        }

        IReadOnlyList<TypeRef> generics = [];
        if (this.At("::")) {
          this.Next();
          generics = this.GenericArguments();
        }

        if (this.At("(")) {
          expression = new MethodCallExpr(name.At, expression, member, this.Arguments()) { Generics = generics };
          continue;
        }

        expression = new FieldExpr(name.At, expression, member);
        continue;
      }

      if (token.Is("(")) {
        expression = new CallExpr(token.At, expression, this.Arguments());
        continue;
      }

      if (token.Is("[")) {
        this.Next();
        var index = this.Restricted(false, this.Expression);
        this.Expect("]");
        expression = new IndexExpr(token.At, expression, index);
        continue;
      }

      if (token.Is("?")) {
        this.Next();
        expression = new TryExpr(token.At, expression);
        continue;
      }

      return expression;
    }
  }

  /// <summary>The generic arguments of a turbofish at its <c>&lt;</c>.</summary>
  private List<TypeRef> GenericArguments() {
    this.Expect("<");
    var arguments = new List<TypeRef>();
    while (!this.At(">") && !this.At(">>")) {
      arguments.Add(this.Type());
      if (!this.Accept(","))
        break;
    }

    this.CloseAngle();
    return arguments;
  }

  private List<Expr> Arguments() {
    this.Expect("(");
    var arguments = new List<Expr>();
    this.Restricted(false, () => {
      while (!this.At(")")) {
        arguments.Add(this.Expression());
        if (!this.Accept(","))
          break;
      }

      return 0;
    });
    this.Expect(")");
    return arguments;
  }

  private Expr Primary() {
    var token = this.Current;
    switch (token.Kind) {
      case TokenKind.Integer:
        this.Next();
        return new IntegerLiteral(token.At, token.Value, token.Suffix);
      case TokenKind.Float:
        this.Next();
        return new FloatLiteral(token.At, token.Text, token.Suffix);
      case TokenKind.String:
        this.Next();
        return token.Byte ? new ByteStringLiteral(token.At, [.. token.Text.Select(unit => (byte)unit)]) : new StringLiteral(token.At, token.Text);
      case TokenKind.Character:
        this.Next();
        return new CharLiteral(token.At, char.ConvertToUtf32(token.Text, 0));
      case TokenKind.Lifetime:
        return this.Labelled();
      case TokenKind.Punctuation:
        return this.Punctuated(token);
      case TokenKind.End:
        throw this.Error("expected expression, found end of file");
    }

    return token.Raw ? this.Path() : token.Text switch {
      "true" or "false" => this.Literal(token),
      "if" => this.If(),
      "match" => this.Match(),
      "loop" => this.Loop(null, token.At),
      "while" => this.While(null, token.At),
      "for" => this.For(null, token.At),
      "return" => this.Return(),
      "break" => this.Break(),
      "continue" => this.Continue(),
      "unsafe" => this.Skipped(RustFrontend.UnsafeConstruct, "unsafe block"),
      "async" or "gen" when this.PeekAt(1).Is("{") || this.PeekAt(1).Is("move") => this.Skipped(RustFrontend.Coroutine, token.Text + " block"),
      "try" when this.PeekAt(1).Is("{") => this.Skipped(RustFrontend.Coroutine, "try block"),
      "yield" => this.Yield(),
      "move" => this.Closure(),
      "let" => throw this.Error("expected expression, found `let` statement"),
      _ => this.Path()
    };
  }

  private Expr Literal(Token token) {
    this.Next();
    return new BoolLiteral(token.At, token.Text == "true");
  }

  /// <summary>A keyword followed by a block, which is skipped and refused.</summary>
  private RefusedExpr Skipped(string code, string construct) {
    var at = this.Next().At;
    this.Accept("move");
    this.SkipTree();
    return new RefusedExpr(new Refusal(code, construct, at));
  }

  private RefusedExpr Yield() {
    var at = this.Next().At;
    if (this.StartsExpression() && !this.At("}") && !this.At(";"))
      this.Expression();

    return new RefusedExpr(new Refusal(RustFrontend.Coroutine, "yield", at));
  }

  private Expr Punctuated(Token token) {
    switch (token.Text) {
      case "(": {
        this.Next();
        return this.Restricted(false, () => {
          if (this.Accept(")"))
            return (Expr)new TupleExpr(token.At, []);

          var first = this.Expression();
          if (this.Accept(")"))
            return first;

          var elements = new List<Expr> { first };
          while (this.Accept(",")) {
            if (this.At(")"))
              break;

            elements.Add(this.Expression());
          }

          this.Expect(")");
          return new TupleExpr(token.At, elements);
        });
      }
      case "{":
        return new BlockExpr(token.At, this.Block(), null);
      case "[": {
        this.Next();
        return this.Restricted(false, () => {
          var elements = new List<Expr>();
          if (this.Accept("]"))
            return (Expr)new ArrayExpr(token.At, elements);

          var first = this.Expression();
          if (this.Accept(";")) {
            var count = this.Expression();
            this.Expect("]");
            return new ArrayRepeatExpr(token.At, first, count);
          }

          elements.Add(first);
          while (this.Accept(",")) {
            if (this.At("]"))
              break;

            elements.Add(this.Expression());
          }

          this.Expect("]");
          return new ArrayExpr(token.At, elements);
        });
      }
      case "|" or "||":
        return this.Closure();
      case "::":
        return this.Path();
      case "<":
        return this.QualifiedPath();
      default:
        throw this.Error($"expected expression, found {token}");
    }
  }

  /// <summary>
  /// A qualified path: <c>&lt;T&gt;::f</c> of a path type <c>T</c> is the path <c>T::f</c>, its generic
  /// arguments those of <c>T</c>'s last segment, and is checked as that path. <c>&lt;T as Tr&gt;::f</c>
  /// names an item of a trait implementation and is refused (<c>PLY1540</c>); so is the path of a type
  /// that is no path, <c>&lt;[u8]&gt;::len</c> (<c>PLY1502</c>).
  /// </summary>
  private Expr QualifiedPath() {
    var at = this.Next().At;
    var type = this.Type();
    TypeRef? trait = null;
    if (this.Accept("as"))
      trait = this.Type();

    this.CloseAngle();
    var segments = type.Shape == TypeShape.Path ? type.Name.Split("::").ToList() : [];
    var generics = new Dictionary<int, IReadOnlyList<TypeRef>>();
    if (type.Arguments.Count > 0)
      generics[segments.Count - 1] = type.Arguments;

    var rest = new List<string>();
    do {
      this.Expect("::");
      if (this.At("<")) {
        generics[segments.Count + rest.Count - 1] = this.GenericArguments();
        continue;
      }

      rest.Add(this.Identifier());
    } while (this.At("::"));

    var text = $"<{type.Text}{(trait is null ? "" : " as " + trait.Text)}>::{string.Join("::", rest)}";
    if (trait is not null)
      return new RefusedExpr(new Refusal(RustFrontend.UnsupportedTrait, $"qualified path {text}", at));

    if (type.Shape != TypeShape.Path)
      return new RefusedExpr(new Refusal(RustFrontend.Unsupported, $"qualified path {text}", at));

    return new PathExpr(at, [.. segments, .. rest]) { Generics = generics };
  }

  private Expr Closure() {
    var at = this.Current.At;
    var move = this.Accept("move");
    var parameters = new List<Parameter>();
    if (this.Accept("|")) {
      while (!this.At("|")) {
        if (this.Current.Kind == TokenKind.End)
          throw this.Error("unterminated closure parameters");

        var parameterAt = this.Current.At;
        var pattern = this.PatternNoTopAlternatives();
        TypeRef? type = null;
        if (this.Accept(":"))
          type = this.Type();

        parameters.Add(new Parameter(pattern, type, parameterAt));
        if (!this.Accept(","))
          break;
      }

      this.Expect("|");
    } else {
      this.Expect("||");
    }

    TypeRef? returnType = null;
    Expr body;
    if (this.Accept("->")) {
      returnType = this.Type();
      var blockAt = this.Current.At;
      body = new BlockExpr(blockAt, this.Block(), null);
    } else {
      body = this.Expression();
    }

    return new ClosureExpr(at, move, parameters, returnType, body);
  }

  private Expr Path() {
    var at = this.Current.At;
    var segments = new List<string>();
    // `::name` names an external crate (2018 edition and later); `::std` is `std`.
    if (this.Accept("::") && this.Current.Text is not ("std" or "core" or "alloc"))
      segments.Add("");

    segments.Add(this.Identifier());
    var generics = new Dictionary<int, IReadOnlyList<TypeRef>>();
    while (this.At("::")) {
      this.Next();
      if (this.At("<")) {
        generics[segments.Count - 1] = this.GenericArguments();
        continue;
      }

      segments.Add(this.Identifier());
    }

    var path = new PathExpr(at, segments) { Generics = generics };
    if (this.At("!") && !this.PeekAt(1).Is("=")) {
      this.Next();
      return this.Macro(path);
    }

    if (this.At("{") && !this._noStruct)
      return this.StructLiteral(path);

    return path;
  }

  /// <summary>A struct expression, its path read and its <c>{</c> next.</summary>
  private Expr StructLiteral(PathExpr path) {
    this.Expect("{");
    var fields = new List<(string, Expr, Span)>();
    Expr? @base = null;
    this.Restricted(false, () => {
      while (!this.At("}")) {
        if (this.Accept("..")) {
          @base = this.Expression();
          break;
        }

        var fieldAt = this.Current.At;
        var name = this.Current.Kind == TokenKind.Integer ? this.Next().Text : this.Identifier();
        var value = this.Accept(":") ? this.Expression() : new PathExpr(fieldAt, [name]);
        fields.Add((name, value, fieldAt));
        if (!this.Accept(","))
          break;
      }

      return 0;
    });
    this.Expect("}");
    return new StructExpr(path.At, path, fields, @base);
  }

  /// <summary>A macro call, its <c>!</c> read: the imported macros with their arguments, any other refused.</summary>
  private Expr Macro(PathExpr path) {
    var name = path.Segments[^1];
    var known = _macros.Contains(name) && (path.Segments.Count == 1 || path.Segments is ["std" or "core", _]);
    if (!known || !(this.At("(") || this.At("["))) {
      if (this.At("(") || this.At("[") || this.At("{"))
        this.SkipTree();

      return new RefusedExpr(new Refusal(RustFrontend.UnsupportedMacro, path.Text + "!", path.At));
    }

    var close = this.Next().Is("(") ? ")" : "]";
    var arguments = new List<Expr>();
    var repeat = false;
    this.Restricted(false, () => {
      while (!this.At(close)) {
        arguments.Add(this.Expression());
        if (name == "vec" && arguments.Count == 1 && this.Accept(";")) {
          repeat = true;
          continue;
        }

        if (!this.Accept(","))
          break;
      }

      return 0;
    });
    this.Expect(close);
    return new MacroExpr(path.At, name, arguments, repeat);
  }

  private Expr Labelled() {
    var label = this.Next();
    this.Expect(":");
    var token = this.Current;
    return token.Text switch {
      "loop" => this.Loop(label.Text, label.At),
      "while" => this.While(label.Text, label.At),
      "for" => this.For(label.Text, label.At),
      "{" => new BlockExpr(label.At, this.Block(), label.Text),
      _ => throw this.Error($"expected a loop or a block after the label '{label.Text}")
    };
  }

  /// <summary>The condition of an <c>if</c> or a <c>while</c>: an expression, or <c>let pattern = scrutinee</c>.</summary>
  private (Pattern? Pattern, Expr Condition, Refusal? Refused) Condition() {
    if (!this.At("let"))
      return (null, this.Restricted(true, this.Expression), null);

    var letAt = this.Next().At;
    var pattern = this.Pattern();
    this.Expect("=");
    // The scrutinee binds tighter than `&&` and `||`, which make a let chain.
    var scrutinee = this.Restricted(true, () => this.Binary(2));
    Refusal? refused = null;
    if (this.At("&&") || this.At("||")) {
      refused = new Refusal(RustFrontend.ConditionalBinding, "let chain", letAt);
      while (!this.At("{")) {
        if (this.Current.Kind == TokenKind.End)
          throw this.Error("expected '{'");

        this.SkipTree();
      }
    }

    return (pattern, scrutinee, refused);
  }

  private Expr If() {
    var at = this.Expect("if").At;
    var (pattern, condition, refused) = this.Condition();
    var then = this.Block();
    Expr? otherwise = null;
    if (this.Accept("else")) {
      var elseAt = this.Current.At;
      otherwise = this.At("if") ? this.If() : new BlockExpr(elseAt, this.Block(), null);
    }

    return refused is not null ? new RefusedExpr(refused) : new IfExpr(at, condition, then, otherwise) { Pattern = pattern };
  }

  private Expr Match() {
    var at = this.Expect("match").At;
    var scrutinee = this.Restricted(true, this.Expression);
    this.Expect("{");
    var arms = new List<MatchArm>();
    this.Restricted(false, () => {
      while (!this.At("}")) {
        var armAt = this.Current.At;
        this.Accept("|");
        var pattern = this.Pattern();
        Expr? guard = null;
        if (this.Accept("if"))
          guard = this.Expression();

        this.Expect("=>");
        var blockBody = this.At("{");
        var body = this.Expression();
        arms.Add(new MatchArm(armAt, pattern, guard, body));
        if (!this.Accept(",") && !blockBody && !this.At("}"))
          throw this.Error($"expected ',' after a match arm, found {this.Current}");
      }

      return 0;
    });

    this.Expect("}");
    return new MatchExpr(at, scrutinee, arms);
  }

  private Expr Loop(string? label, Span at) {
    this.Expect("loop");
    return new LoopExpr(at, label, this.Block());
  }

  private Expr While(string? label, Span at) {
    this.Expect("while");
    var (pattern, condition, refused) = this.Condition();
    var body = this.Block();
    return refused is not null ? new RefusedExpr(refused) : new WhileExpr(at, label, condition, body) { Pattern = pattern };
  }

  private Expr For(string? label, Span at) {
    this.Expect("for");
    var pattern = this.Pattern();
    this.Expect("in");
    var iterable = this.Restricted(true, this.Expression);
    return new ForExpr(at, label, pattern, iterable, this.Block());
  }

  private Expr Return() {
    var at = this.Next().At;
    return new ReturnExpr(at, this.EndsValue() ? null : this.Expression());
  }

  private Expr Break() {
    var at = this.Next().At;
    string? label = null;
    if (this.Current.Kind == TokenKind.Lifetime)
      label = this.Next().Text;

    return new BreakExpr(at, label, this.EndsValue() ? null : this.Expression());
  }

  private Expr Continue() {
    var at = this.Next().At;
    string? label = null;
    if (this.Current.Kind == TokenKind.Lifetime)
      label = this.Next().Text;

    return new ContinueExpr(at, label);
  }

  /// <summary>Whether no value follows a <c>return</c> or <c>break</c>.</summary>
  private bool EndsValue() => this.At(";") || this.At("}") || this.At(",") || this.At(")") || this.At("]") || !this.StartsExpression();

  // ---------------------------------------------------------------- patterns

  /// <summary>A pattern of a match arm or a <c>for</c>, alternatives allowed.</summary>
  private Pattern Pattern() {
    var at = this.Current.At;
    this.Accept("|");
    var first = this.PatternNoTopAlternatives();
    if (!this.At("|"))
      return first;

    var alternatives = new List<Pattern> { first };
    while (this.Accept("|"))
      alternatives.Add(this.PatternNoTopAlternatives());

    return new OrPattern(at, alternatives);
  }

  /// <summary>The patterns of a tuple, a tuple struct or a slice up to its closing delimiter.</summary>
  private List<Pattern> PatternList(string close) {
    var elements = new List<Pattern>();
    while (!this.At(close)) {
      elements.Add(this.Pattern());
      if (!this.Accept(","))
        break;
    }

    this.Expect(close);
    return elements;
  }

  private Pattern PatternNoTopAlternatives() {
    var token = this.Current;
    var at = token.At;
    if (token.Is("_")) {
      this.Next();
      return new WildcardPattern(at);
    }

    if (token.Is("(")) {
      this.Next();
      var elements = this.PatternList(")");
      return elements.Count == 1 && !this._tokens[this._position - 2].Is(",") ? elements[0] : new TuplePattern(at, elements);
    }

    if (token.Is("&") || token.Is("&&")) {
      this.Next();
      var mutable = this.Accept("mut");
      var inner = this.PatternNoTopAlternatives();
      var reference = new ReferencePattern(at, inner, mutable);
      return token.Is("&&") ? new ReferencePattern(at, reference, false) : reference;
    }

    if (token.Is("[")) {
      this.Next();
      this.PatternList("]");
      return new RefusedPattern(new Refusal(RustFrontend.UnsupportedElement, "slice pattern", at));
    }

    if (token.Is("..") && !this.PeekAt(1).Is("=")) {
      this.Next();
      return new RestPattern(at);
    }

    if (token.Is("..=")) {
      this.Next();
      return new RangePattern(at, null, this.RangeBound(), true);
    }

    if (token.Is("const")) {
      this.Next();
      this.SkipTree();
      return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, "const block", at));
    }

    if ((token.Is("ref") || token.Is("mut") || (token.Kind == TokenKind.Identifier && token.Text is not ("true" or "false"))) && !this.PeekAt(1).Is("::") && !this.PeekAt(1).Is("(") && !this.PeekAt(1).Is("{")) {
      var isRef = this.Accept("ref");
      var mutable = this.Accept("mut");
      var name = this.Identifier();
      if (this.At("@")) {
        this.Next();
        this.PatternNoTopAlternatives();
        return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, $"{name} @ ...", at));
      }

      if (isRef)
        return new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, "ref binding", at));

      var pattern = new IdentifierPattern(at, name, mutable);
      return mutable || !(this.At("..=") || this.At("..")) ? pattern : this.RangeFrom(new PathExpr(at, [name]), at);
    }

    if (token.Kind == TokenKind.Identifier && token.Text is not ("true" or "false") && (this.PeekAt(1).Is("(") || this.PeekAt(1).Is("{") || this.PeekAt(1).Is("::"))) {
      var path = this.PathText();
      if (this.Accept("("))
        return new TupleStructPattern(at, path, this.PatternList(")"));

      if (this.Accept("{")) {
        var fields = new List<(string, Pattern)>();
        var rest = false;
        while (!this.At("}")) {
          if (this.Accept("..")) {
            rest = true;
            break;
          }

          var fieldAt = this.Current.At;
          if (this.At("ref") || this.At("mut")) {
            var isRef = this.Accept("ref");
            var mutable = this.Accept("mut");
            var field = this.Identifier();
            fields.Add((field, isRef ? new RefusedPattern(new Refusal(RustFrontend.UnsupportedPattern, "ref binding", fieldAt)) : new IdentifierPattern(fieldAt, field, mutable)));
          } else {
            var field = this.Current.Kind == TokenKind.Integer ? this.Next().Text : this.Identifier();
            fields.Add((field, this.Accept(":") ? this.Pattern() : new IdentifierPattern(fieldAt, field, false)));
          }

          if (!this.Accept(","))
            break;
        }

        this.Expect("}");
        return new StructPattern(at, path, fields, rest);
      }

      return this.At("..=") || this.At("..") ? this.RangeFrom(path, at) : new LiteralPattern(at, path);
    }

    var literal = this.LiteralBound();
    return this.At("..=") || this.At("..") ? this.RangeFrom(literal, at) : new LiteralPattern(at, literal);
  }

  private PathExpr PathText() {
    var at = this.Current.At;
    var segments = new List<string> { this.Identifier() };
    while (this.Accept("::"))
      segments.Add(this.Identifier());

    return new PathExpr(at, segments);
  }

  private Pattern RangeFrom(Expr low, Span at) {
    var inclusive = this.Next().Text == "..=";
    var bounded = this.Current.Kind is TokenKind.Integer or TokenKind.Float or TokenKind.Character || this.At("-")
      || (this.Current.Kind == TokenKind.Identifier && this.Current.Text is not ("if" or "true" or "false"));
    if (!bounded && inclusive)
      throw this.Error("inclusive range with no end");

    return new RangePattern(at, low, bounded ? this.RangeBound() : null, inclusive);
  }

  private Expr RangeBound() => this.Current.Kind == TokenKind.Identifier && this.Current.Text is not ("true" or "false") ? this.PathText() : this.LiteralBound();

  /// <summary>A literal of a pattern: a number, possibly negative, a truth value, a character or a string.</summary>
  private Expr LiteralBound() {
    var token = this.Current;
    if (token.Is("-")) {
      this.Next();
      var operand = this.LiteralBound();
      return new UnaryExpr(token.At, "-", operand);
    }

    switch (token.Kind) {
      case TokenKind.Integer:
        this.Next();
        return new IntegerLiteral(token.At, token.Value, token.Suffix);
      case TokenKind.Float:
        this.Next();
        return new FloatLiteral(token.At, token.Text, token.Suffix);
      case TokenKind.Character:
        this.Next();
        return new CharLiteral(token.At, char.ConvertToUtf32(token.Text, 0));
      case TokenKind.String when !token.Byte:
        this.Next();
        return new StringLiteral(token.At, token.Text);
      case TokenKind.String:
        this.Next();
        return new RefusedExpr(new Refusal(RustFrontend.UnsupportedPattern, "byte string pattern", token.At));
      default:
        if (token.Is("true") || token.Is("false"))
          return this.Literal(token);

        throw this.Error($"expected pattern, found {token}");
    }
  }

}
