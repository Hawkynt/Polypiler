using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Checking;

internal sealed partial class GoChecker {

  /// <summary>Whether the expression being checked holds a call or a receive, which keeps <c>len</c> of an array from being a constant.</summary>
  private bool _hasCallOrReceive;

  private GoOperand Call(GoNode node) {
    var callee = this.Raw(node.Node("Fun")!, null);
    switch (callee.Mode) {
      case GoMode.TypeExpr: {
        var converted = this.Conversion(node, callee.Type);
        if (converted.Mode != GoMode.Constant)
          this._hasCallOrReceive = true;

        return converted;
      }
      case GoMode.Builtin: {
        var result = this.Builtin(node, callee.Builtin!);
        if (result.Mode != GoMode.Constant)
          this._hasCallOrReceive = true;

        return result;
      }
      case GoMode.NoValue:
        throw Error(node.Node("Fun"), $"{GoNodeText.Of(node.Node("Fun"))} (no value) used as value");
    }

    SingleValue(callee);
    if (callee.Type.Underlying is not GoSignature signature)
      throw Error(node, $"invalid operation: cannot call non-function {callee}");

    this._hasCallOrReceive = true;
    this.Arguments(node, signature, GoNodeText.Of(node.Node("Fun")));
    return signature.Result switch {
      null => new GoOperand(GoMode.NoValue, GoTypes.Invalid, node),
      var type => new GoOperand(GoMode.Value, type, node)
    };
  }

  /// <summary>The operands of the arguments of a call: one call with several results as the sole argument gives them all.</summary>
  private List<GoOperand> ArgumentList(GoNode call) {
    var arguments = call.Nodes("Args");
    if (arguments.Count == 1 && !call.Flag("spread")) {
      var x = this.Raw(arguments[0], null);
      Exclude(x);
      if (x.Type is GoTuple tuple)
        return [.. tuple.Types.Select(type => new GoOperand(GoMode.Value, type, arguments[0]))];

      return [x];
    }

    return [.. arguments.Select(argument => this.Expression(argument))];
  }

  private void Arguments(GoNode call, GoSignature signature, string name) {
    var arguments = this.ArgumentList(call);
    var spread = call.Flag("spread");
    var parameters = signature.Parameters;
    if (spread) {
      if (!signature.Variadic)
        throw Error(call, $"have (...{arguments[^1].Type}) for non-variadic {name}: cannot use ... in call to non-variadic {name}");

      if (arguments.Count != parameters.Count)
        throw Error(call, $"{(arguments.Count < parameters.Count ? "not enough" : "too many")} arguments in call to {name}");

      for (var index = 0; index < arguments.Count; ++index)
        this.Assignment(arguments[index], parameters[index].Type, $"argument to {name}");

      return;
    }

    var fixedCount = signature.Variadic ? parameters.Count - 1 : parameters.Count;
    if (arguments.Count < fixedCount)
      throw Error(call, $"not enough arguments in call to {name}\n\thave ({string.Join(", ", arguments.Select(argument => argument.Type))})\n\twant {signature.Text.Split(' ')[0]}");

    if (!signature.Variadic && arguments.Count > fixedCount)
      throw Error(arguments[fixedCount].Expr, $"too many arguments in call to {name}");

    for (var index = 0; index < arguments.Count; ++index) {
      var type = index < fixedCount ? parameters[index].Type : ((GoSlice)parameters[^1].Type).Element;
      this.Assignment(arguments[index], type, $"argument to {name}");
    }
  }

  // ---------------------------------------------------------------- conversions

  private GoOperand Conversion(GoNode call, GoType target) {
    var arguments = call.Nodes("Args");
    if (arguments.Count != 1)
      throw Error(call, $"{(arguments.Count == 0 ? "missing argument" : "too many arguments")} in conversion to {target}");

    if (call.Flag("spread"))
      throw Error(call, $"invalid use of ... in conversion to {target}");

    var x = this.Expression(arguments[0], target);
    if (GoTypes.IsComplex(target) || GoTypes.IsComplex(x.Type))
      throw new Lowering.GoUnsupportedException(GoFrontend.UnsupportedTypeDescriptor, $"type '{(GoTypes.IsComplex(target) ? target : x.Type)}' is not supported by the Go frontend", call);

    var constant = x.Mode == GoMode.Constant;
    var ok = false;
    GoMode mode = GoMode.Value;
    if (constant && GoTypes.IsConstType(target)) {
      var basic = GoTypes.AsBasic(target)!;
      if (Representable(x.Value!, basic) is { } value) {
        x.Value = value;
        ok = true;
      } else if (GoTypes.IsInteger(x.Type) && basic.IsString) {
        x.Value = GoConstant.MakeString(GoConstant.Utf8Of(x.Value!.Numerator));
        ok = true;
      } else if (GoTypes.IsInteger(x.Type) && basic.IsInteger) {
        throw Error(arguments[0], $"cannot convert {x} to type {target}: constant {x.Value} overflows {target}");
      }

      mode = GoMode.Constant;
    } else if (this.ConvertibleTo(x, target)) {
      ok = true;
    }

    if (!ok)
      throw Error(arguments[0], $"cannot convert {x} to type {target}");

    if (GoTypes.IsUntyped(x.Type)) {
      var final = target;
      if (GoTypes.IsInterface(target) || constant && !GoTypes.IsConstType(target) || x.IsNil)
        final = GoTypes.Default(x.Type);
      else if (constant && GoTypes.IsInteger(x.Type) && GoTypes.IsString(target))
        final = x.Type;

      this.UpdateExprType(x.Expr, final, true);
    }

    return new GoOperand(mode, target, call, mode == GoMode.Constant ? x.Value : null);
  }

  /// <summary>Whether a value can be converted to a type (The Go Programming Language Specification, "Conversions").</summary>
  private bool ConvertibleTo(GoOperand x, GoType target) {
    if (this.AssignableTo(x, target))
      return true;

    var (vu, tu) = (x.Type.Underlying, target.Underlying);
    if (IdenticalIgnoringTags(vu, tu))
      return true;

    if (x.Type is GoPointer from && target is GoPointer to && IdenticalIgnoringTags(from.Element.Underlying, to.Element.Underlying))
      return true;

    if ((GoTypes.IsInteger(vu) || GoTypes.IsFloat(vu)) && (GoTypes.IsInteger(tu) || GoTypes.IsFloat(tu)))
      return true;

    if ((GoTypes.IsInteger(vu) || IsBytesOrRunes(vu)) && GoTypes.IsString(tu))
      return true;

    if (GoTypes.IsString(vu) && IsBytesOrRunes(tu))
      return true;

    return vu is GoSlice slice && (tu is GoArray array && GoTypes.Identical(slice.Element, array.Element) && this._minor >= 20
      || tu is GoPointer { Element.Underlying: GoArray pointed } && GoTypes.Identical(slice.Element, pointed.Element));
  }

  private static bool IsBytesOrRunes(GoType type) => type is GoSlice slice && GoTypes.AsBasic(slice.Element) is { Kind: GoBasicKind.Uint8 or GoBasicKind.Int32 };

  private static bool IdenticalIgnoringTags(GoType x, GoType y) {
    if (x is GoStruct a && y is GoStruct b)
      return a.Fields.Count == b.Fields.Count && a.Fields.Zip(b.Fields).All(pair => pair.First.Name == pair.Second.Name && pair.First.Embedded == pair.Second.Embedded && GoTypes.Identical(pair.First.Type, pair.Second.Type));

    return GoTypes.Identical(x, y);
  }

  // ---------------------------------------------------------------- built-ins

  private static GoCheckException ArgumentCount(GoNode call, string name, int expected, int found)
    => Error(call, $"{(found < expected ? "not enough" : "too many")} arguments for {GoNodeText.Of(call)} (expected {expected}, found {found})");

  private GoOperand Builtin(GoNode call, string name) {
    var arguments = call.Nodes("Args");
    if (call.Flag("spread") && name != "append")
      throw Error(call, $"invalid operation: invalid use of ... with built-in {name}");

    switch (name) {
      case "len" or "cap": {
        if (arguments.Count != 1)
          throw ArgumentCount(call, name, 1, arguments.Count);

        var saved = this._hasCallOrReceive;
        this._hasCallOrReceive = false;
        var x = this.Expression(arguments[0]);
        var hasCall = this._hasCallOrReceive;
        this._hasCallOrReceive = saved;
        var type = x.Type.Underlying is GoPointer { Element.Underlying: GoArray pointed } ? pointed : x.Type.Underlying;
        switch (type) {
          case GoBasic { IsString: true } when name == "len":
            return x.Mode == GoMode.Constant
              ? new GoOperand(GoMode.Constant, GoTypes.Int, call, GoConstant.MakeInt(x.Value!.Bytes.Length))
              : new GoOperand(GoMode.Value, GoTypes.Int, call);
          case GoArray array:
            return hasCall ? new GoOperand(GoMode.Value, GoTypes.Int, call) : new GoOperand(GoMode.Constant, GoTypes.Int, call, GoConstant.MakeInt(array.Length));
          case GoSlice or GoChan:
          case GoMap when name == "len":
            return new GoOperand(GoMode.Value, GoTypes.Int, call);
          default:
            throw Error(arguments[0], $"invalid argument: {x} for built-in {name}");
        }
      }
      case "append": {
        if (arguments.Count == 0)
          throw ArgumentCount(call, name, 1, 0);

        var slice = this.Expression(arguments[0]);
        if (slice.IsNil)
          throw Error(arguments[0], "invalid argument: first argument to append must be a typed slice; found nil");

        if (slice.Type.Underlying is not GoSlice sliceType)
          throw Error(arguments[0], $"invalid argument: {slice} (variable of type {slice.Type}) is not a slice");

        if (call.Flag("spread")) {
          if (arguments.Count != 2)
            throw ArgumentCount(call, name, 2, arguments.Count);

          var rest = this.Expression(arguments[1]);
          if (GoTypes.AsBasic(sliceType.Element) is { Kind: GoBasicKind.Uint8 } && GoTypes.IsString(rest.Type)) {
            this.Assignment(rest, GoTypes.String, "argument to append");
          } else {
            this.Assignment(rest, slice.Type is GoNamed ? sliceType : slice.Type, "argument to append");
          }
        } else {
          foreach (var argument in arguments.Skip(1)) {
            var x = this.Expression(argument, sliceType.Element);
            this.Assignment(x, sliceType.Element, "argument to append");
          }
        }

        return new GoOperand(GoMode.Value, slice.Type, call);
      }
      case "clear": {
        if (arguments.Count != 1)
          throw ArgumentCount(call, name, 1, arguments.Count);

        var x = this.Expression(arguments[0]);
        if (x.Type.Underlying is not GoMap and not GoSlice)
          throw Error(arguments[0], $"invalid argument: {x} cannot be cleared: argument must be (or constrained by) map or slice");

        return new GoOperand(GoMode.NoValue, GoTypes.Invalid, call);
      }
      case "close":
        throw Unsupported(call, "built-in close");
      case "complex" or "real" or "imag":
        throw new Lowering.GoUnsupportedException(GoFrontend.UnsupportedTypeDescriptor, "type 'complex128' is not supported by the Go frontend", call);
      case "copy": {
        if (arguments.Count != 2)
          throw ArgumentCount(call, name, 2, arguments.Count);

        var destination = this.Expression(arguments[0]);
        var source = this.Expression(arguments[1]);
        if (destination.Type.Underlying is not GoSlice target)
          throw Error(arguments[0], $"invalid argument: copy expects slice arguments; found {destination} and {source}");

        var element = source.Type.Underlying switch {
          GoSlice from => from.Element,
          GoBasic { IsString: true } => GoTypes.Byte,
          _ => throw Error(arguments[1], $"invalid argument: copy expects slice arguments; found {destination} and {source}")
        };
        if (!GoTypes.Identical(target.Element, element))
          throw Error(call, $"invalid argument: arguments to copy {destination} and {source} have different element types {target.Element} and {element}");

        if (GoTypes.IsUntyped(source.Type))
          this.ConvertUntyped(source, GoTypes.String);

        return new GoOperand(GoMode.Value, GoTypes.Int, call);
      }
      case "delete": {
        if (arguments.Count != 2)
          throw ArgumentCount(call, name, 2, arguments.Count);

        var map = this.Expression(arguments[0]);
        if (map.Type.Underlying is not GoMap mapType)
          throw Error(arguments[0], $"invalid argument: {map} is not a map");

        var key = this.Expression(arguments[1]);
        this.Assignment(key, mapType.Key, "argument to delete");
        return new GoOperand(GoMode.NoValue, GoTypes.Invalid, call);
      }
      case "make":
        return this.Make(call, arguments);
      case "max" or "min":
        return this.MinMax(call, name, arguments);
      case "new": {
        if (arguments.Count != 1)
          throw ArgumentCount(call, name, 1, arguments.Count);

        var x = this.ExpressionOrType(arguments[0]);
        if (x.Mode == GoMode.TypeExpr)
          return new GoOperand(GoMode.Value, new GoPointer(x.Type), call);

        if (this._minor < 26)
          throw Error(arguments[0], $"{GoNodeText.Of(arguments[0])} is not a type");

        this.Assignment(x, null, "argument to new");
        return new GoOperand(GoMode.Value, new GoPointer(x.Type), call);
      }
      case "panic": {
        if (arguments.Count != 1)
          throw ArgumentCount(call, name, 1, arguments.Count);

        var x = this.Expression(arguments[0]);
        this.Assignment(x, GoTypes.EmptyInterface, "argument to panic");
        return new GoOperand(GoMode.NoValue, GoTypes.Invalid, call);
      }
      case "print" or "println": {
        foreach (var argument in arguments) {
          var x = this.Expression(argument);
          this.Assignment(x, null, $"argument to built-in {name}");
        }

        return new GoOperand(GoMode.NoValue, GoTypes.Invalid, call);
      }
      default: {
        if (arguments.Count != 0)
          throw ArgumentCount(call, name, 0, arguments.Count);

        return new GoOperand(GoMode.Value, GoTypes.EmptyInterface, call);
      }
    }
  }

  private GoOperand Make(GoNode call, IReadOnlyList<GoNode> arguments) {
    if (arguments.Count == 0)
      throw ArgumentCount(call, "make", 1, 0);

    var type = this.TypeOf(arguments[0]);
    var minimum = type.Underlying switch {
      GoSlice => 2,
      GoMap => 1,
      _ => throw Error(arguments[0], $"invalid argument: cannot make {GoNodeText.Of(arguments[0])}; type must be slice, map, or channel")
    };
    if (arguments.Count < minimum || arguments.Count > minimum + 1)
      throw Error(call, $"invalid operation: {GoNodeText.Of(call)} expects {minimum} or {minimum + 1} arguments; found {arguments.Count}");

    var sizes = arguments.Skip(1).Select(argument => this.IndexValue(argument, -1)).ToList();
    if (sizes.Count == 2 && sizes[0] >= 0 && sizes[1] >= 0 && sizes[0] > sizes[1])
      throw Error(arguments[1], "invalid argument: length and capacity swapped");

    return new GoOperand(GoMode.Value, type, call);
  }

  private GoOperand MinMax(GoNode call, string name, IReadOnlyList<GoNode> arguments) {
    if (arguments.Count == 0)
      throw Error(call, $"not enough arguments for {GoNodeText.Of(call)} (expected 1, found 0)");

    var operands = arguments.Select(argument => this.Expression(argument)).ToList();
    var op = name == "min" ? "<" : ">";
    var x = new GoOperand(operands[0].Mode, operands[0].Type, operands[0].Expr, operands[0].Value);
    for (var index = 0; index < operands.Count; ++index) {
      var a = operands[index];
      if (!GoTypes.IsOrdered(a.Type))
        throw Error(a.Expr, $"invalid argument: {a} cannot be ordered");

      if (index == 0)
        continue;

      this.MatchTypes(x, a);
      if (!GoTypes.Identical(x.Type, a.Type))
        throw Error(a.Expr, $"invalid argument: mismatched types {x.Type} (previous argument) and {a.Type} (type of {GoNodeText.Of(a.Expr)})");

      if (x.Mode == GoMode.Constant && a.Mode == GoMode.Constant) {
        if (GoConstant.Compare(a.Value!, op, x.Value!))
          x = new GoOperand(GoMode.Constant, a.Type, a.Expr, a.Value);
      } else {
        x.Mode = GoMode.Value;
        x.Value = null;
      }
    }

    foreach (var a in operands)
      this.UpdateExprType(a.Expr, x.Type, true);

    return new GoOperand(x.Mode, x.Type, call, x.Mode == GoMode.Constant ? x.Value : null);
  }
}
