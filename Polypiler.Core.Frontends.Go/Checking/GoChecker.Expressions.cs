using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Checking;

internal sealed partial class GoChecker {

  /// <summary>The bits of an untyped integer constant beyond which it overflows.</summary>
  private const int ConstantPrecision = 512;

  /// <summary>The longest constant string the frontend computes.</summary>
  private const int MaxConstantString = 16 << 20;

  /// <summary>An expression that is one value.</summary>
  private GoOperand Expression(GoNode node, GoType? hint = null) {
    var x = this.Raw(node, hint);
    Exclude(x);
    SingleValue(x);
    return x;
  }

  /// <summary>An expression or a type.</summary>
  private GoOperand ExpressionOrType(GoNode node) {
    var x = this.Raw(node, null);
    if (x.Mode == GoMode.NoValue)
      throw Error(node, $"{GoNodeText.Of(node)} (no value) used as value");

    if (x.Mode == GoMode.Builtin)
      throw Error(node, $"{GoNodeText.Of(node)} (built-in) must be called");

    SingleValue(x);
    return x;
  }

  private static void Exclude(GoOperand x) {
    switch (x.Mode) {
      case GoMode.NoValue:
        throw Error(x.Expr, $"{GoNodeText.Of(x.Expr)} (no value) used as value");
      case GoMode.Builtin:
        throw Error(x.Expr, $"{GoNodeText.Of(x.Expr)} (built-in) must be called");
      case GoMode.TypeExpr:
        throw Error(x.Expr, $"{GoNodeText.Of(x.Expr)} (type) is not an expression");
    }
  }

  private static void SingleValue(GoOperand x) {
    if (x.Mode == GoMode.Value && x.Type is GoTuple tuple)
      throw Error(x.Expr, $"multiple-value {GoNodeText.Of(x.Expr)} (value of type {tuple}) in single-value context");
  }

  /// <summary>Checks an expression and records what it is on its node.</summary>
  private GoOperand Raw(GoNode node, GoType? hint) {
    var x = this.ExpressionInternal(node, hint);
    x.Expr = node;
    this.Record(x);
    return x;
  }

  private GoOperand ExpressionInternal(GoNode node, GoType? hint) {
    switch (node.Kind) {
      case "Ident":
        return this.Identifier(node);
      case "BasicLit":
        return this.Literal(node);
      case "FuncLit": {
        var scope = new GoScope(this._scope, "function");
        var signature = this.Signature(node.Node("Type")!, scope);
        node.Node("Type")!.TypeOfNode = signature;
        node.Node("Type")!.Mode = "type";
        this.Body(signature, scope, node.Node("Type")!, node.Node("Body")!);
        return new GoOperand(GoMode.Value, signature, node);
      }
      case "CompositeLit":
        return this.CompositeLiteral(node, hint);
      case "ParenExpr": {
        var inner = this.Raw(node.Node("X")!, hint);
        return new GoOperand(inner.Mode, inner.Type, node, inner.Value) { Builtin = inner.Builtin };
      }
      case "SelectorExpr":
        return this.Selector(node);
      case "IndexExpr":
        return this.Index(node);
      case "IndexListExpr":
        throw Unsupported(node, "generic instantiation");
      case "SliceExpr":
        return this.Slice(node);
      case "TypeAssertExpr":
        throw Unsupported(node, "type assertion");
      case "CallExpr":
        return this.Call(node);
      case "StarExpr": {
        var x = this.ExpressionOrType(node.Node("X")!);
        if (x.Mode == GoMode.TypeExpr)
          return new GoOperand(GoMode.TypeExpr, new GoPointer(x.Type), node);

        if (x.IsNil)
          throw Error(node, "invalid operation: cannot indirect nil");

        if (x.Type.Underlying is not GoPointer pointer)
          throw Error(node, $"invalid operation: cannot indirect {x}");

        return new GoOperand(GoMode.Variable, pointer.Element, node);
      }
      case "UnaryExpr":
        return this.Unary(node);
      case "BinaryExpr":
        return this.Binary(node);
      default:
        // A type literal; a key:value element appears in a composite literal alone, which takes it apart.
        return new GoOperand(GoMode.TypeExpr, this.TypeExpression(node), node);
    }
  }

  private GoOperand Identifier(GoNode node) {
    var name = node.Text("Name")!;
    if (name == "_")
      throw Error(node, "cannot use _ as value");

    var @object = this.Lookup(node) ?? throw Error(node, $"undefined: {name}");
    node.Target = @object;
    switch (@object.Kind) {
      case GoObjectKind.PackageName:
        throw Error(node, $"use of package {name} without selector");
      case GoObjectKind.Const:
        if (@object.Name == "iota" && this._scope.Lookup("iota")!.Value.Scope == this._universe) {
          if (this._iota is null)
            throw Error(node, "cannot use iota outside constant declaration");

          return new GoOperand(GoMode.Constant, @object.Type!, node, this._iota);
        }

        this.Resolve(@object);
        return new GoOperand(GoMode.Constant, @object.Type!, node, @object.Value);
      case GoObjectKind.TypeName:
        if (@object.Type is null)
          throw Unsupported(node, "the type constraint comparable");

        this.Resolve(@object);
        return new GoOperand(GoMode.TypeExpr, @object.Type!, node);
      case GoObjectKind.Var:
        this.Resolve(@object);
        @object.Used = true;
        this.Depend(@object);
        return new GoOperand(GoMode.Variable, @object.Type!, node);
      case GoObjectKind.Func:
        this.Resolve(@object);
        this.Depend(@object);
        return new GoOperand(GoMode.Value, @object.Type!, node);
      case GoObjectKind.Builtin:
        if (name is "min" or "max" or "clear" && this._minor < 21)
          throw Error(node, $"{name} requires go1.21 or later");

        return new GoOperand(GoMode.Builtin, GoTypes.Invalid, node) { Builtin = name };
      default:
        return new GoOperand(GoMode.Value, GoTypes.UntypedNil, node);
    }
  }

  private GoOperand Literal(GoNode node) {
    var text = node.Text("Value")!;
    var kind = node.Text("Kind");
    if (kind is "INT" or "FLOAT" or "IMAG" && text.Length > 10000)
      throw Error(node, $"excessively long constant: {text[..10]}... ({text.Length} chars)");

    switch (kind) {
      case "INT": {
        var value = GoConstant.FromIntLiteral(text);
        if (GoConstant.BitLength(value.Numerator) > ConstantPrecision)
          throw Error(node, "constant overflow");

        return new GoOperand(GoMode.Constant, GoTypes.Basic(GoBasicKind.UntypedInt), node, value);
      }
      case "FLOAT":
        try {
          return new GoOperand(GoMode.Constant, GoTypes.Basic(GoBasicKind.UntypedFloat), node, GoConstant.FromFloatLiteral(text));
        } catch (GoConstantRangeException exception) {
          throw Unsupported(node, exception.Message);
        }
      case "IMAG":
        throw new Lowering.GoUnsupportedException(GoFrontend.UnsupportedTypeDescriptor, "type 'untyped complex' is not supported by the Go frontend", node);
      case "CHAR":
        return new GoOperand(GoMode.Constant, GoTypes.Basic(GoBasicKind.UntypedRune), node, GoConstant.MakeInt(GoConstant.RuneOf(text)));
      default:
        return new GoOperand(GoMode.Constant, GoTypes.Basic(GoBasicKind.UntypedString), node, GoConstant.MakeString(GoConstant.Unquote(text)));
    }
  }

  // ---------------------------------------------------------------- representability and untyped conversion

  /// <summary>The value of a constant in a basic type, rounded where the type is floating-point (<c>representableConst</c>); <see langword="null"/> where it is not representable.</summary>
  private static GoConstant? Representable(GoConstant value, GoBasic type) {
    if (type.IsInteger) {
      var integer = value.ToInt();
      if (integer is null)
        return null;

      var number = integer.Numerator;
      if (type.Kind is GoBasicKind.UntypedInt or GoBasicKind.UntypedRune)
        return integer;

      var bits = type.Bits;
      return type.IsUnsigned
        ? number.Sign >= 0 && GoConstant.BitLength(number) <= bits ? integer : null
        : number >= -(BigInteger.One << (bits - 1)) && number < BigInteger.One << (bits - 1) ? integer : null;
    }

    if (type.IsFloat) {
      var floating = value.ToFloat();
      if (floating is null)
        return null;

      return type.Kind switch {
        GoBasicKind.Float32 => float.IsFinite(floating.ToFloat32()) ? GoConstant.MakeFloat64(floating.ToFloat32()) : null,
        GoBasicKind.Float64 => GoConstant.MakeFloat64(floating.ToFloat64()),
        _ => floating
      };
    }

    if (type.IsString)
      return value.Kind == GoConstantKind.String ? value : null;

    return type.IsBoolean && value.Kind == GoConstantKind.Bool ? value : null;
  }

  /// <summary>The value of a constant operand in a basic type, or the reason it has none there: <c>truncated</c>, <c>overflows</c> or <c>cannot</c>.</summary>
  private static (GoConstant? Value, string Reason) Representation(GoOperand x, GoBasic type) {
    if (Representable(x.Value!, type) is { } value)
      return (value, "");

    if (GoTypes.IsNumeric(x.Type) && type.IsNumeric)
      return (null, !GoTypes.IsInteger(x.Type) && type.IsInteger ? "truncated" : "overflows");

    return (null, "cannot");
  }

  private static GoCheckException InvalidConversion(GoOperand x, GoType target, string reason) => Error(x.Expr, reason switch {
    "truncated" => $"{x} truncated to {target}",
    "overflows" => $"{x} overflows {target}",
    _ => $"cannot convert {x} to type {target}"
  });

  /// <summary>Checks that a constant is representable in its typed basic type, rounding it there (<c>representable</c>).</summary>
  private static void CheckRepresentable(GoOperand x, GoBasic type) {
    var (value, reason) = Representation(x, type);
    if (value is null)
      throw InvalidConversion(x, type, reason);

    x.Value = value;
  }

  /// <summary>
  /// The type and value an untyped operand takes in the context of a target type
  /// (<c>implicitTypeAndValue</c>), or the reason it cannot: <c>truncated</c>, <c>overflows</c> or <c>cannot</c>.
  /// </summary>
  private static (GoType? Type, GoConstant? Value, string Reason) ImplicitTypeAndValue(GoOperand x, GoType target) {
    if (!GoTypes.IsUntyped(x.Type) || target is GoInvalid)
      return (x.Type, null, "");

    if (GoTypes.IsUntyped(target))
      return GoTypes.MaxType(x.Type, target) is { } larger ? (larger, null, "") : (null, null, "cannot");

    var kind = ((GoBasic)x.Type).Kind;
    switch (target.Underlying) {
      case GoBasic basic:
        if (x.Mode == GoMode.Constant) {
          var (value, reason) = Representation(x, basic);
          return value is null ? (null, null, reason) : (target, value, "");
        }

        var fits = kind switch {
          GoBasicKind.UntypedBool => basic.IsBoolean,
          GoBasicKind.UntypedInt or GoBasicKind.UntypedRune or GoBasicKind.UntypedFloat or GoBasicKind.UntypedComplex => basic.IsNumeric,
          // An untyped string is always a constant; untyped nil goes to no basic type.
          _ => false
        };
        return fits ? (target, null, "") : (null, null, "cannot");
      case GoInterface @interface:
        if (x.IsNil)
          return (GoTypes.UntypedNil, null, "");

        return @interface.Methods.Count > 0 ? (null, null, "cannot") : (GoTypes.Default(x.Type), null, "");
      case GoPointer or GoSignature or GoSlice or GoMap or GoChan:
        return x.IsNil ? (GoTypes.UntypedNil, null, "") : (null, null, "cannot");
      default:
        return (null, null, "cannot");
    }
  }

  /// <summary>Converts an untyped operand to the type its context gives it (<c>convertUntyped</c>).</summary>
  private void ConvertUntyped(GoOperand x, GoType target) {
    var (type, value, reason) = ImplicitTypeAndValue(x, target);
    if (type is null)
      throw InvalidConversion(x, target, reason);

    if (value is not null) {
      x.Value = value;
      this.UpdateExprValue(x.Expr, value);
    }

    if (!ReferenceEquals(type, x.Type)) {
      x.Type = type;
      this.UpdateExprType(x.Expr, type, false);
    }
  }

  /// <summary>Checks that a constant result stays within its type, or for an untyped integer within 512 bits (<c>overflow</c>).</summary>
  private static void Overflow(GoOperand x, string operation) {
    if (!GoTypes.IsUntyped(x.Type)) {
      CheckRepresentable(x, GoTypes.AsBasic(x.Type)!);
      return;
    }

    if (x.Value!.Kind == GoConstantKind.Int && GoConstant.BitLength(x.Value.Numerator) > ConstantPrecision)
      throw Error(x.Expr, $"constant {operation}overflow");
  }

  private static string OperationName(string op) => op switch {
    "+" => "addition ",
    "-" => "subtraction ",
    "*" => "multiplication ",
    _ => ""
  };

  // ---------------------------------------------------------------- assignability

  /// <summary>Whether an operand may be assigned to a variable of a type (The Go Programming Language Specification, "Assignability").</summary>
  private bool AssignableTo(GoOperand x, GoType target) {
    var source = x.Type;
    if (GoTypes.Identical(source, target))
      return true;

    var (vu, tu) = (source.Underlying, target.Underlying);
    if (GoTypes.IsUntyped(vu)) {
      if (tu is GoBasic basic) {
        if (x.Mode == GoMode.Constant)
          return Representable(x.Value!, basic) is not null;

        return vu is GoBasic { Kind: GoBasicKind.UntypedBool } && basic.IsBoolean;
      }

      return ImplicitTypeAndValue(x, target).Type is not null;
    }

    if (GoTypes.Identical(vu, tu) && (!GoTypes.HasName(source) || !GoTypes.HasName(target)))
      return true;

    if (tu is GoInterface @interface)
      return this.Implements(source, @interface);

    return false;
  }

  /// <summary>Whether a type implements an interface: it has every method of it with an identical signature.</summary>
  private bool Implements(GoType type, GoInterface @interface) {
    foreach (var method in @interface.Methods) {
      var found = this.MethodOf(type, method.Name);
      if (found is null || !GoTypes.Identical(found.Value.Signature, method.Signature))
        return false;
    }

    return true;
  }

  /// <summary>The method of a name in the method set of a type, or <see langword="null"/>.</summary>
  private (GoSignature Signature, GoObject? Object)? MethodOf(GoType type, string name) {
    if (type.Underlying is GoInterface @interface)
      return @interface.Methods.FirstOrDefault(method => method.Name == name) is { } declared ? (declared.Signature, null) : null;

    var pointer = type is GoPointer;
    var named = (pointer ? ((GoPointer)type).Element : type) as GoNamed;
    var method = named?.Methods.FirstOrDefault(candidate => candidate.Name == name);
    if (method is null || method.PointerReceiver && !pointer)
      return null;

    return ((GoSignature)method.Type!, method);
  }

  /// <summary>
  /// Checks that an operand may be assigned to a variable of a type and gives an untyped one its type
  /// (<c>assignment</c>): to the blank identifier, where <paramref name="target"/> is <see langword="null"/>,
  /// and to an interface an untyped constant goes as its default type.
  /// </summary>
  private void Assignment(GoOperand x, GoType? target, string context) {
    // Every operand that reaches here is a value: the callers excluded types, built-ins and calls without one.
    SingleValue(x);

    if (GoTypes.IsUntyped(x.Type)) {
      var to = target;
      if (target is null || GoTypes.IsInterface(target)) {
        if (target is null && x.IsNil)
          throw Error(x.Expr, $"use of untyped nil in {context}");

        to = GoTypes.Default(x.Type);
      }

      var (type, value, reason) = ImplicitTypeAndValue(x, to!);
      if (type is null)
        throw Error(x.Expr, $"cannot use {x} as {to} value in {context}{reason switch { "truncated" => " (truncated)", "overflows" => " (overflows)", _ => "" }}");

      if (value is not null) {
        x.Value = value;
        this.UpdateExprValue(x.Expr, value);
      }

      if (!ReferenceEquals(type, x.Type)) {
        x.Type = type;
        this.UpdateExprType(x.Expr, type, false);
      }
    }

    if (target is null)
      return;

    if (!this.AssignableTo(x, target))
      throw Error(x.Expr, $"cannot use {x} as {target} value in {context}");
  }

  // ---------------------------------------------------------------- operators

  private GoOperand Unary(GoNode node) {
    var op = node.Text("Op")!;
    var operand = node.Node("X")!;
    if (op == "<-")
      throw Unsupported(node, "channel receive");

    var x = this.Expression(operand);
    switch (op) {
      case "&":
        if (!Unparen(operand).Is("CompositeLit") && x.Mode != GoMode.Variable)
          throw Error(node, $"invalid operation: cannot take address of {x}");

        return new GoOperand(GoMode.Value, new GoPointer(x.Type), node);
      case "~":
        throw Error(node, "cannot use ~ outside of interface or type constraint");
    }

    var defined = op switch {
      "+" or "-" => GoTypes.IsNumeric(x.Type),
      "^" => GoTypes.IsInteger(x.Type),
      _ => GoTypes.IsBoolean(x.Type)
    };
    if (!defined)
      throw Error(node, $"invalid operation: operator {op} not defined on {x}");

    if (GoTypes.IsComplex(x.Type))
      throw new Lowering.GoUnsupportedException(GoFrontend.UnsupportedTypeDescriptor, $"type '{x.Type}' is not supported by the Go frontend", node);

    if (x.Mode == GoMode.Constant) {
      var precision = GoTypes.IsUnsigned(x.Type) ? GoTypes.AsBasic(x.Type)!.Bits : 0;
      var result = new GoOperand(GoMode.Constant, x.Type, node, GoConstant.Unary(op, x.Value!, precision));
      Overflow(result, op == "-" ? "negation " : "");
      return result;
    }

    return new GoOperand(GoMode.Value, x.Type, node);
  }

  private static bool IsComparison(string op) => op is "==" or "!=" or "<" or "<=" or ">" or ">=";

  private GoOperand Binary(GoNode node) => this.BinaryOperands(node, node.Node("X")!, node.Node("Y")!, node.Text("Op")!, null);

  /// <summary>A binary operation of two expressions; without a node of its own, as an assignment operation, at its left operand.</summary>
  private GoOperand BinaryOperands(GoNode? expression, GoNode lhs, GoNode rhs, string op, GoOperand? left) {
    var x = left ?? this.Expression(lhs);
    var y = this.Expression(rhs);
    var node = expression ?? lhs;
    if (op is "<<" or ">>")
      return this.Shift(x, y, node, op);

    this.MatchTypes(x, y);
    if (IsComparison(op))
      return this.Comparison(x, y, node, op);

    if (!GoTypes.Identical(x.Type, y.Type))
      throw Error(node, $"invalid operation: {GoNodeText.Of(node)} (mismatched types {x.Type} and {y.Type})");

    var defined = op switch {
      "+" => GoTypes.IsNumeric(x.Type) || GoTypes.IsString(x.Type),
      "-" or "*" or "/" => GoTypes.IsNumeric(x.Type),
      "%" or "&" or "|" or "^" or "&^" => GoTypes.IsInteger(x.Type),
      _ => GoTypes.IsBoolean(x.Type)
    };
    if (!defined)
      throw Error(node, $"invalid operation: operator {op} not defined on {x}");

    if (GoTypes.IsComplex(x.Type))
      throw new Lowering.GoUnsupportedException(GoFrontend.UnsupportedTypeDescriptor, $"type '{x.Type}' is not supported by the Go frontend", node);

    if (op is "/" or "%" && (x.Mode == GoMode.Constant || GoTypes.IsInteger(x.Type)) && y.Mode == GoMode.Constant && y.Value!.Sign == 0)
      throw Error(node.Node("Y"), "invalid operation: division by zero");

    if (x.Mode == GoMode.Constant && y.Mode == GoMode.Constant) {
      // Go allows constant strings of up to 2e9 bytes; the frontend computes them in memory, far below that.
      if (x.Value!.Kind == GoConstantKind.String && (long)x.Value.Bytes.Length + y.Value!.Bytes.Length > MaxConstantString)
        throw Unsupported(node, "a constant string longer than 16 MiB");

      var value = GoConstant.Binary(x.Value!, op, y.Value!, op == "/" && GoTypes.IsInteger(x.Type));
      var result = new GoOperand(GoMode.Constant, x.Type, node, value);
      Overflow(result, OperationName(op));
      return result;
    }

    return new GoOperand(GoMode.Value, x.Type, node);
  }

  /// <summary>Converts the untyped one of two operands to the type of the other where the two can meet (<c>matchTypes</c>).</summary>
  private void MatchTypes(GoOperand x, GoOperand y) {
    if (!GoTypes.IsUntyped(x.Type) && !GoTypes.IsUntyped(y.Type))
      return;

    if (GoTypes.IsNumeric(x.Type) != GoTypes.IsNumeric(y.Type) || GoTypes.IsBoolean(x.Type) != GoTypes.IsBoolean(y.Type) || GoTypes.IsString(x.Type) != GoTypes.IsString(y.Type))
      return;

    if (x.IsNil) {
      if (!GoTypes.HasNil(y.Type))
        return;
    } else if (y.IsNil && !GoTypes.HasNil(x.Type)) {
      // An untyped constant is a number, a boolean or a string, which never meets a pointer here.
      return;
    }

    this.ConvertUntyped(x, y.Type);
    this.ConvertUntyped(y, x.Type);
  }

  private GoOperand Comparison(GoOperand x, GoOperand y, GoNode node, string op) {
    if (!this.AssignableTo(x, y.Type) && !this.AssignableTo(y, x.Type))
      throw Error(node, $"invalid operation: {GoNodeText.Of(node)} (mismatched types {x.Type} and {y.Type})");

    if (op is "==" or "!=") {
      if (x.IsNil || y.IsNil) {
        var other = x.IsNil ? y.Type : x.Type;
        if (!GoTypes.HasNil(other) || x.IsNil && y.IsNil)
          throw Error(node, $"invalid operation: {GoNodeText.Of(node)} (operator {op} not defined on nil)");
      } else if (!GoTypes.Comparable(x.Type)) {
        throw Error(node, $"invalid operation: {GoNodeText.Of(node)} ({IncomparableCause(x.Type)})");
      } else if (!GoTypes.Comparable(y.Type)) {
        throw Error(node, $"invalid operation: {GoNodeText.Of(node)} ({IncomparableCause(y.Type)})");
      }
    } else if (!GoTypes.IsOrdered(x.Type) || !GoTypes.IsOrdered(y.Type)) {
      throw Error(node, $"invalid operation: {GoNodeText.Of(node)} (operator {op} not defined on {(GoTypes.IsOrdered(x.Type) ? y : x)})");
    }

    if (GoTypes.IsComplex(x.Type) || GoTypes.IsComplex(y.Type))
      throw new Lowering.GoUnsupportedException(GoFrontend.UnsupportedTypeDescriptor, $"type '{x.Type}' is not supported by the Go frontend", node);

    if (x.Mode == GoMode.Constant && y.Mode == GoMode.Constant)
      return new GoOperand(GoMode.Constant, GoTypes.UntypedBool, node, GoConstant.MakeBool(GoConstant.Compare(x.Value!, op, y.Value!)));

    this.UpdateExprType(x.Expr, GoTypes.Default(x.Type), true);
    this.UpdateExprType(y.Expr, GoTypes.Default(y.Type), true);
    return new GoOperand(GoMode.Value, GoTypes.UntypedBool, node);
  }

  private static string IncomparableCause(GoType type) => type.Underlying switch {
    GoSlice => "slice can only be compared to nil",
    GoMap => "map can only be compared to nil",
    GoSignature => "func can only be compared to nil",
    _ => $"{type} cannot be compared"
  };

  private GoOperand Shift(GoOperand x, GoOperand y, GoNode node, string op) {
    var integral = x.Mode == GoMode.Constant ? x.Value!.ToInt() : null;
    if (!(GoTypes.IsInteger(x.Type) || GoTypes.IsUntyped(x.Type) && integral is not null))
      throw Error(node, $"invalid operation: shifted operand {x} must be integer");

    GoConstant? count = null;
    if (y.Mode == GoMode.Constant) {
      count = y.Value!.ToInt();
      if (count is { Sign: < 0 })
        throw Error(y.Expr, $"invalid operation: negative shift count {y}");

      if (GoTypes.IsUntyped(y.Type) && Representable(y.Value!, GoTypes.Uint) is null)
        throw Error(y.Expr, $"cannot use {y} as uint value in shift count{(GoTypes.IsInteger(y.Type) ? " (overflows)" : " (truncated)")}");
    } else if (!GoTypes.IsInteger(y.Type)) {
      if (!GoTypes.IsUntyped(y.Type))
        throw Error(y.Expr, $"invalid operation: shift count {y} must be integer");

      this.ConvertUntyped(y, GoTypes.Uint);
    }

    if (x.Mode == GoMode.Constant) {
      if (y.Mode == GoMode.Constant) {
        const int shiftBound = 1023 - 1 + 52;
        if (count is null || count.Numerator > shiftBound)
          throw Error(y.Expr, $"invalid operation: invalid shift count {y}");

        var type = GoTypes.IsInteger(x.Type) ? x.Type : GoTypes.Basic(GoBasicKind.UntypedInt);
        var result = new GoOperand(GoMode.Constant, type, node, GoConstant.Shift(integral!, op, (int)count.Numerator));
        Overflow(result, "shift ");
        return result;
      }

      if (GoTypes.IsUntyped(x.Type)) {
        // The type of the constant is the one it would have alone, which only its context decides.
        if (this._untyped.TryGetValue(x.Expr, out var untyped))
          untyped.IsLhs = true;

        return new GoOperand(GoMode.Value, x.Type, node);
      }
    }

    // The first test left an integer: an untyped operand that is no constant is no integer.
    return new GoOperand(GoMode.Value, x.Type, node);
  }

  // ---------------------------------------------------------------- selectors, indexes, slices

  private GoOperand Selector(GoNode node) {
    var selectorNode = node.Node("Sel")!;
    var name = selectorNode.Text("Name")!;
    if (this.QualifiedMember(node) is { } member) {
      // math and math/bits declare constants and functions alone.
      return member.Kind == GoObjectKind.Const
        ? new GoOperand(GoMode.Constant, member.Type!, node, member.Value)
        : new GoOperand(GoMode.Value, member.Type!, node);
    }

    var x = this.ExpressionOrType(node.Node("X")!);
    if (x.Mode == GoMode.TypeExpr)
      throw Unsupported(node, "method expression");

    if (name == "_")
      throw Error(selectorNode, "cannot refer to blank field or method");

    var type = x.Type;
    var viaPointer = type.Underlying is GoPointer;
    var @base = type.Underlying is GoPointer referenced ? referenced.Element : type;
    if (!(viaPointer && type is GoNamed)) {
      GoSignature? signature = null;
      GoObject? @object = null;
      if (!viaPointer && @base.Underlying is GoInterface @interface) {
        signature = @interface.Methods.FirstOrDefault(method => method.Name == name)?.Signature;
      } else if (@base is GoNamed named && named.Methods.FirstOrDefault(method => method.Name == name) is { } method) {
        if (method.PointerReceiver && !viaPointer && x.Mode != GoMode.Variable)
          throw Error(node, $"cannot call pointer method {name} on {type}");

        (signature, @object) = ((GoSignature)method.Type!, method);
        this.Depend(method);
      }

      if (signature is not null) {
        node.With("selection", "method");
        selectorNode.Target = @object;
        selectorNode.TypeOfNode = signature;
        return new GoOperand(GoMode.Value, signature, node);
      }
    }

    if (@base.Underlying is GoStruct structure && structure.Field(name) is { } field) {
      node.With("selection", "field");
      selectorNode.Target = new GoObject(GoObjectKind.Var, name, field.Type) { IsField = true };
      selectorNode.TypeOfNode = field.Type;
      return new GoOperand(x.Mode == GoMode.Variable || viaPointer ? GoMode.Variable : GoMode.Value, field.Type, node);
    }

    throw Error(selectorNode, $"{GoNodeText.Of(node)} undefined (type {type} has no field or method {name})");
  }

  /// <summary>Checks an index (<c>check.index</c>): an integer, non-negative and within <paramref name="length"/> where it is a constant; the constant value, or -1.</summary>
  private long IndexValue(GoNode node, long length) {
    var x = this.Expression(node);
    this.ValidIndex(x, "index", false);
    if (x.Mode != GoMode.Constant)
      return -1;

    var value = (long)x.Value!.Numerator;
    if (length >= 0 && value >= length)
      throw Error(node, $"invalid argument: index {x.Value} out of bounds [0:{length}]");

    return value;
  }

  private void ValidIndex(GoOperand x, string what, bool allowNegative) {
    this.ConvertUntyped(x, GoTypes.Int);
    if (!GoTypes.IsInteger(x.Type))
      throw Error(x.Expr, $"invalid argument: {what} {x} must be integer");

    if (x.Mode == GoMode.Constant) {
      if (!allowNegative && x.Value!.Sign < 0)
        throw Error(x.Expr, $"invalid argument: {what} {x} must not be negative");

      x.Value = Representable(x.Value!, GoTypes.Int) ?? throw Error(x.Expr, $"invalid argument: {what} {x} overflows int");
    }
  }

  private GoOperand Index(GoNode node) {
    var x = this.ExpressionOrType(node.Node("X")!);
    if (x.Mode == GoMode.TypeExpr)
      throw Unsupported(node, "generic type instantiation");

    if (x.Type is GoSignature)
      throw Unsupported(node, "generic function instantiation");

    var index = node.Node("Index")!;
    long length = -1;
    GoOperand result;
    switch (x.Type.Underlying) {
      case GoBasic { IsString: true }:
        if (x.Mode == GoMode.Constant)
          length = x.Value!.Bytes.Length;

        result = new GoOperand(GoMode.Value, GoTypes.Byte, node);
        break;
      case GoArray array:
        length = array.Length;
        result = new GoOperand(x.Mode == GoMode.Variable ? GoMode.Variable : GoMode.Value, array.Element, node);
        break;
      case GoPointer { Element.Underlying: GoArray array }:
        length = array.Length;
        result = new GoOperand(GoMode.Variable, array.Element, node);
        break;
      case GoSlice slice:
        result = new GoOperand(GoMode.Variable, slice.Element, node);
        break;
      case GoMap map:
        var key = this.Expression(index, map.Key);
        this.Assignment(key, map.Key, "map index");
        return new GoOperand(GoMode.MapIndex, map.Element, node);
      default:
        throw Error(node, $"invalid operation: cannot index {x}");
    }

    this.IndexValue(index, length);
    return result;
  }

  private GoOperand Slice(GoNode node) {
    var x = this.Expression(node.Node("X")!);
    long length = -1;
    GoType type;
    switch (x.Type.Underlying) {
      case GoBasic { IsString: true }:
        if (node.Flag("Slice3"))
          throw Error(node, "invalid operation: 3-index slice of string");

        if (x.Mode == GoMode.Constant)
          length = x.Value!.Bytes.Length;

        type = GoTypes.IsUntyped(x.Type) ? GoTypes.String : x.Type;
        break;
      case GoArray array:
        if (x.Mode != GoMode.Variable)
          throw Error(node, $"invalid operation: cannot slice {x} (value not addressable)");

        length = array.Length;
        type = new GoSlice(array.Element);
        break;
      case GoPointer { Element.Underlying: GoArray array }:
        length = array.Length;
        type = new GoSlice(array.Element);
        break;
      case GoSlice:
        type = x.Type;
        break;
      default:
        throw Error(node, $"cannot slice {x}");
    }

    var indices = new long[3];
    var parts = new[] { node.Node("Low"), node.Node("High"), node.Node("Max") };
    for (var position = 0; position < 3; ++position) {
      indices[position] = parts[position] switch {
        { } part => this.IndexValue(part, length >= 0 ? length + 1 : -1),
        null when position == 0 => 0,
        _ => length
      };
    }

    for (var low = 0; low < 2; ++low)
      for (var high = low + 1; high < 3; ++high)
        if (indices[low] > 0 && indices[high] >= 0 && indices[high] < indices[low])
          throw Error(node, $"invalid slice indices: {indices[high]} < {indices[low]}");

    return new GoOperand(GoMode.Value, type, node);
  }

  // ---------------------------------------------------------------- composite literals

  private GoOperand CompositeLiteral(GoNode node, GoType? hint) {
    GoType type;
    GoType @base;
    var typeNode = node.Node("Type");
    var open = false;
    if (typeNode is { Kind: "ArrayType" } && typeNode.Node("Len") is { Kind: "Ellipsis" }) {
      type = new GoArray(-1, this.TypeOf(typeNode.Node("Elt")!));
      @base = type;
      open = true;
    } else if (typeNode is not null) {
      type = this.TypeOf(typeNode);
      @base = type;
    } else if (hint is not null) {
      type = hint;
      @base = hint.Underlying is GoPointer pointer ? pointer.Element : hint;
    } else {
      throw Error(node, "missing type in composite literal");
    }

    var elements = node.Nodes("Elts");
    switch (@base.Underlying) {
      case GoStruct structure:
        this.StructLiteral(node, structure, @base, elements);
        break;
      case GoArray array: {
        var count = this.IndexedElements(elements, array.Element, open ? -1 : array.Length);
        if (open) {
          type = new GoArray(count, array.Element);
          typeNode!.TypeOfNode = type;
          typeNode.Mode = "type";
        }

        break;
      }
      case GoSlice slice:
        this.IndexedElements(elements, slice.Element, -1);
        break;
      case GoMap map: {
        var seen = new HashSet<GoConstant>();
        foreach (var element in elements) {
          if (!element.Is("KeyValueExpr"))
            throw Error(element, "missing key in map literal");

          var key = this.Expression(element.Node("Key")!, map.Key);
          this.Assignment(key, map.Key, "map literal");
          if (key.Mode == GoMode.Constant && !GoTypes.IsInterface(map.Key) && !seen.Add(key.Value!))
            throw Error(element.Node("Key"), $"duplicate key {key.Value} in map literal");

          var value = this.Expression(element.Node("Value")!, map.Element);
          this.Assignment(value, map.Element, "map literal");
        }

        break;
      }
      default:
        throw Error(node, $"invalid composite literal{(typeNode is null ? " element" : "")} type {type}");
    }

    return new GoOperand(GoMode.Value, type, node);
  }

  private void StructLiteral(GoNode node, GoStruct structure, GoType type, IReadOnlyList<GoNode> elements) {
    if (elements.Count == 0)
      return;

    if (elements[0].Is("KeyValueExpr")) {
      var seen = new HashSet<string>(System.StringComparer.Ordinal);
      foreach (var element in elements) {
        if (!element.Is("KeyValueExpr"))
          throw Error(element, "mixture of field:value and value elements in struct literal");

        var x = this.Expression(element.Node("Value")!);
        if (element.Node("Key") is not { Kind: "Ident" } key)
          throw Error(element, $"invalid field name {GoNodeText.Of(element.Node("Key"))} in struct literal");

        var name = key.Text("Name")!;
        var field = structure.Field(name) ?? throw Error(key, $"unknown field {name} in struct literal of type {type}");
        key.Target = new GoObject(GoObjectKind.Var, name, field.Type) { IsField = true };
        key.TypeOfNode = field.Type;
        this.Assignment(x, field.Type, "struct literal");
        if (!seen.Add(name))
          throw Error(element, $"duplicate field name {name} in struct literal");
      }

      return;
    }

    for (var index = 0; index < elements.Count; ++index) {
      var element = elements[index];
      if (element.Is("KeyValueExpr"))
        throw Error(element, "mixture of field:value and value elements in struct literal");

      var x = this.Expression(element);
      if (index >= structure.Fields.Count)
        throw Error(element, $"too many values in struct literal of type {type}");

      this.Assignment(x, structure.Fields[index].Type, "struct literal");
    }

    if (elements.Count < structure.Fields.Count)
      throw Error(node, $"too few values in struct literal of type {type}");
  }

  private long IndexedElements(IReadOnlyList<GoNode> elements, GoType type, long length) {
    var seen = new HashSet<long>();
    long index = 0, max = 0;
    foreach (var element in elements) {
      var value = element;
      if (element.Is("KeyValueExpr")) {
        var key = this.IndexValue(element.Node("Key")!, length);
        if (key < 0)
          throw Error(element.Node("Key"), $"index {GoNodeText.Of(element.Node("Key"))} must be integer constant");

        index = key;
        value = element.Node("Value")!;
      } else if (length >= 0 && index >= length) {
        throw Error(element, $"index {index} is out of bounds (>= {length})");
      }

      if (!seen.Add(index))
        throw Error(element, $"duplicate index {index} in array or slice literal");

      ++index;
      max = System.Math.Max(max, index);
      var x = this.Expression(value, type);
      this.Assignment(x, type, "array or slice literal");
    }

    return max;
  }

}
