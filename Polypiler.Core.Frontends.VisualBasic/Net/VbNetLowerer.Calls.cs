using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Net;

internal sealed partial class VbNetLowerer {

  /// <summary>The functions of <c>System.Math</c> of one <c>Double</c> that are operations of the IR, by name.</summary>
  private static readonly Dictionary<string, IrIntrinsicOp> _math = new(StringComparer.Ordinal) {
    ["Sqrt"] = IrIntrinsicOp.Sqrt, ["Floor"] = IrIntrinsicOp.Floor, ["Ceiling"] = IrIntrinsicOp.Ceiling, ["Truncate"] = IrIntrinsicOp.Truncate,
    ["Sin"] = IrIntrinsicOp.Sin, ["Cos"] = IrIntrinsicOp.Cos, ["Tan"] = IrIntrinsicOp.Tan, ["Asin"] = IrIntrinsicOp.Asin, ["Acos"] = IrIntrinsicOp.Acos,
    ["Atan"] = IrIntrinsicOp.Atan, ["Exp"] = IrIntrinsicOp.Exp, ["Log10"] = IrIntrinsicOp.Log10
  };

  /// <summary>The arguments of a call in the order of the parameters, a missing optional one its default.</summary>
  private List<IrExpr> Arguments(IInvocationOperation call)
    => [.. call.Arguments.OrderBy(argument => argument.Parameter?.Ordinal ?? 0).Select(argument => this.Value(argument.Value))];

  private IrExpr Invocation(IInvocationOperation call) {
    var method = call.TargetMethod;
    var arguments = this.Arguments(call);
    var type = method.ReturnsVoid ? IrType.Void : this.Type(method.ReturnType, method);
    if (this._functions.TryGetValue(method.OriginalDefinition, out var function))
      return new IrCall(function.Name, function.Returns, arguments);

    var container = method.ContainingType.ToDisplayString();
    var first = arguments.Count > 0 ? arguments[0].Type : IrType.Void;
    IrIntrinsic Op(IrIntrinsicOp op, IrType result, params IrExpr[] operands) => new(op, result, operands);
    switch (container, method.Name, arguments.Count) {
      case ("System.Math", "Abs" or "Sign", 1):
        return Op(method.Name == "Abs" ? IrIntrinsicOp.Abs : IrIntrinsicOp.Sign, type, arguments[0]);
      case ("System.Math", "Min" or "Max", 2):
        return Op(method.Name == "Min" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max, type, arguments[0], arguments[1]);
      case ("System.Math", "Round", 1) when first.IsFloat:
        return Op(IrIntrinsicOp.RoundEven, type, arguments[0]);
      case ("System.Math", "Round", 2) when first.IsFloat && call.Arguments.Last().Value.ConstantValue is { HasValue: true, Value: int mode } && mode is 0 or 1:
        return Op(mode == 0 ? IrIntrinsicOp.RoundEven : IrIntrinsicOp.RoundAway, type, arguments[0]);
      case ("System.Math", "Pow" or "Atan2", 2):
        return Op(method.Name == "Pow" ? IrIntrinsicOp.Pow : IrIntrinsicOp.Atan2, type, arguments[0], arguments[1]);
      case ("System.Math", "Log", 1):
        return Op(IrIntrinsicOp.Log, type, arguments[0]);
      case ("System.Math", var name, 1) when first.IsFloat && _math.TryGetValue(name, out var op):
        return Op(op, type, arguments[0]);
      case ("Microsoft.VisualBasic.Strings", "Len", 1) when first.IsString:
        return Op(IrIntrinsicOp.StrLength, IrType.I32, arguments[0]);
      case ("Microsoft.VisualBasic.Strings", "AscW", 1) when call.Arguments[0].Value.Type?.SpecialType == SpecialType.System_Char:
        return new IrConvert(IrType.I32, arguments[0]);
      case ("Microsoft.VisualBasic.Strings", "AscW", 1) when first.IsString:
        return new IrConvert(IrType.I32, Op(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, arguments[0], new IrConst(IrType.I32, 0)));
      case ("Microsoft.VisualBasic.Strings", "ChrW", 1) when first == IrType.I32:
        return new IrConvert(IrType.U16, arguments[0], IrConversion.Trap);
      case ("Microsoft.VisualBasic.Conversion", "Fix" or "Int", 1) when first.IsFloat:
        return Op(method.Name == "Fix" ? IrIntrinsicOp.Truncate : IrIntrinsicOp.Floor, type, arguments[0]);
      case ("Microsoft.VisualBasic.Conversion", "Fix" or "Int", 1) when first.IsInteger:
        return arguments[0];
      case ("String", "Concat", >= 2) when arguments.All(argument => argument.Type.IsString):
        return Op(IrIntrinsicOp.StrConcat, IrType.String, [.. arguments]);
      case ("String", "Equals", 2) when method.IsStatic:
        return Op(IrIntrinsicOp.StrEquals, IrType.Bool, arguments[0], arguments[1]);
      case ("String", "Join", 2) when arguments[1].Type.IsArray:
        return Op(IrIntrinsicOp.StrJoin, IrType.String, arguments[0], arguments[1]);
      case ("String", _, _) when !method.IsStatic && call.Instance is { } instance && this.StringMethod(method, this.Value(instance), arguments, call) is { } text:
        return text;
      case (_, "ToString", 0) when call.Instance is { } value && this.Value(value) is var shown && (shown.Type.IsNumeric || shown.Type == IrType.Bool):
        return this.Convert(shown, value.Type!, compilation.GetSpecialType(SpecialType.System_String), call.Syntax);
      default:
        this.Refuse(call.Syntax, $"the call of {container}.{method.Name}");
        return Default(type == IrType.Void ? IrType.I32 : type);
    }
  }

  /// <summary>A method of <c>String</c> whose comparison is by code unit: <c>Substring</c>, <c>IndexOf</c> of a <c>Char</c> or with <c>StringComparison.Ordinal</c>, <c>Contains</c>, <c>Replace</c>, …</summary>
  private IrExpr? StringMethod(IMethodSymbol method, IrExpr text, List<IrExpr> arguments, IInvocationOperation call) {
    var ordinal = call.Arguments.LastOrDefault()?.Value.ConstantValue is { HasValue: true, Value: int comparison } && comparison == (int)StringComparison.Ordinal
      && method.Parameters.Last().Type.Name == "StringComparison";
    var plain = ordinal ? arguments[..^1] : arguments;
    var unit = plain.Count > 0 && method.Parameters[0].Type.SpecialType == SpecialType.System_Char;
    return (method.Name, plain.Count) switch {
      ("Substring", 1 or 2) => new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [text, .. plain]),
      ("IndexOf", 1 or 2) when unit || ordinal => new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [text, .. plain]),
      ("StartsWith" or "EndsWith", 1) when ordinal => new IrIntrinsic(method.Name == "StartsWith" ? IrIntrinsicOp.StrStartsWith : IrIntrinsicOp.StrEndsWith, IrType.Bool, [text, .. plain]),
      ("Contains", 1) when !ordinal => new IrIntrinsic(IrIntrinsicOp.StrContains, IrType.Bool, [text, .. plain]),
      ("Replace", 2) when !ordinal => new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [text, .. plain]),
      ("ToString", 0) => text,
      _ => null
    };
  }

  /// <summary><c>Console.WriteLine</c> of a string, of nothing, or of a number or a Boolean as its text.</summary>
  private IrStmt? WriteLine(IInvocationOperation call) {
    if (call.TargetMethod is not { Name: "WriteLine", ContainingType.Name: "Console" } || call.TargetMethod.ContainingType.ContainingNamespace?.Name != "System")
      return null;
    if (call.Arguments.Length == 0)
      return new IrWriteLine(new IrStrConst(""));
    if (call.Arguments.Length != 1)
      return null;

    var value = this.Value(call.Arguments[0].Value);
    return new IrWriteLine(value.Type.IsString ? value : this.Convert(value, call.Arguments[0].Value.Type!, compilation.GetSpecialType(SpecialType.System_String), call.Syntax));
  }

  /// <summary>
  /// <c>New S With {.F = v}</c> of a structure, every field not given its default, in the order of the
  /// fields - which the initializer has to keep where a value could have an effect - and
  /// <c>New OverflowException(message)</c> and the other exceptions of the library.
  /// </summary>
  private IrExpr Creation(IObjectCreationOperation creation) {
    var type = this.Type(creation.Type!);
    if (type.IsStruct && type.Declaration is IrStructType declaration) {
      if (creation.Arguments.Length > 0) {
        this.Refuse(creation.Syntax, "a structure made by a constructor of its own");
        return Default(type);
      }

      if (creation.Initializer is null)
        return new IrStructDefault(type);

      var given = new Dictionary<IrField, IrExpr>(ReferenceEqualityComparer.Instance);
      var order = new List<IrField>();
      foreach (var initializer in creation.Initializer.Initializers) {
        if (initializer is ISimpleAssignmentOperation { Target: IFieldReferenceOperation field } assignment && this._fields.TryGetValue(field.Field, out var member)) {
          given[member] = this.Value(assignment.Value);
          order.Add(member);
        } else {
          this.Refuse(initializer.Syntax, $"the initializer '{initializer.Syntax}'");
        }
      }

      var declared = declaration.Fields.Where(given.ContainsKey).ToList();
      if (!declared.SequenceEqual(order) && !given.Values.All(Pure))
        this.Refuse(creation.Syntax, "an initializer that gives fields with effects out of their order");
      return new IrStructNew(type, [.. declaration.Fields.Select(field => given.TryGetValue(field, out var value) ? value : Default(field.Type))]);
    }

    if (type is { IsObject: true, Declaration: IrClassType { IsLibrary: true } library }) {
      var arguments = creation.Arguments.OrderBy(argument => argument.Parameter?.Ordinal ?? 0).Select(argument => this.Value(argument.Value)).ToList();
      if (arguments.Count <= 2) {
        var constructor = IrExceptionClass.Constructor(library, arguments.Count);
        if (IrExceptionClass.Parameters(constructor) is { } parameters && parameters.Zip(arguments).All(pair => pair.Second.Type.IsAssignableTo(pair.First)))
          return new IrNew(type, constructor, arguments);
      }
    }

    this.Refuse(creation.Syntax, $"the creation '{creation.Syntax}'");
    return Default(type);
  }

  /// <summary><c>New T(n) {}</c> of n + 1 elements, or <c>{a, b}</c> of its elements; an array of strings with elements, the empty string where none are given.</summary>
  private IrExpr ArrayCreation(IArrayCreationOperation creation) {
    var type = this.Type(creation.Type!);
    if (creation.DimensionSizes.Length != 1) {
      this.Refuse(creation.Syntax, "an array of several dimensions");
      return Default(type);
    }

    if (creation.Initializer is { } initializer) {
      if (initializer.ElementValues.Any(element => element is IArrayInitializerOperation)) {
        this.Refuse(creation.Syntax, "a nested array initializer");
        return Default(type);
      }

      return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, [.. initializer.ElementValues.Select(this.Value)]);
    }

    var size = this.Value(creation.DimensionSizes[0]);
    if (type.Element!.IsString) {
      if (size is IrConst { Value: >= 0 and <= 4096 } count)
        return new IrIntrinsic(IrIntrinsicOp.ArrayOf, type, [.. Enumerable.Repeat<IrExpr>(new IrStrConst(""), (int)count.Value)]);

      this.Refuse(creation.Syntax, "an array of strings whose length is no constant");
      return Default(type);
    }

    return new IrNewArray(type, size);
  }

}
