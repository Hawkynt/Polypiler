using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>
/// Structs, tuples, field-less enums, <c>Option</c> and <c>Result</c>: their values, the derived
/// <c>==</c>, <c>clone</c> and <c>default</c>, the methods of the subset, and <c>?</c> (The Rust
/// Reference, "Struct expressions", "Enumerations", "The question mark operator"; the
/// <c>core::option</c> and <c>core::result</c> documentation). An <c>Err</c> or a <c>None</c> that
/// <c>?</c> passes on is a value the function returns, never a trap.
/// </summary>
internal sealed partial class FunctionLowering {

  // ---------------------------------------------------------------- values

  private IrExpr? Tuple(TupleExpr tuple) {
    if (tuple.Elements.Count == 0)
      return null;

    var type = this._typed.TypeOf(tuple);
    var values = this.Operands([.. tuple.Elements], [.. type.Elements]);
    return new IrStructNew(this.Ir(type), values);
  }

  /// <summary>A struct expression: the fields in the order written, the base last, then the struct in declaration order.</summary>
  private IrExpr StructExpression(StructExpr structure) {
    var type = this._typed.TypeOf(structure);
    var info = type.Struct!;
    var ir = this.Ir(type);
    var declaration = ir.StructType!;
    var written = structure.Fields.Select(field => info.Fields.FindIndex(candidate => candidate.Name == field.Name)).ToList();
    var inOrder = structure.Base is null && written.SequenceEqual(Enumerable.Range(0, info.Fields.Count));
    var values = this.Operands([.. structure.Fields.Select(field => field.Value)], [.. written.Select(index => info.Fields[index].Type)]);
    if (inOrder)
      return new IrStructNew(ir, values);

    var stable = values.Select(value => this.Stable(value, "field")).ToList();
    var @base = structure.Base is null ? null : this.Stable(this.Value(structure.Base), "base");
    var fields = declaration.Fields.Select((field, index) => {
      var position = written.IndexOf(index);
      return position >= 0 ? stable[position] : new IrFieldLoad(@base!, field);
    }).ToList();
    return new IrStructNew(ir, fields);
  }

  /// <summary>A call that makes a value: <c>Some</c>, <c>Ok</c>, <c>Err</c>, a tuple struct, or a function of <c>core</c>.</summary>
  private IrExpr? Construct(CallExpr call, object resolution) {
    var type = this._typed.TypeOf(call);
    switch (resolution) {
      case "Some":
        return new IrSome(this.Value(call.Arguments[0]));
      case "Ok" or "Err":
        return this.Result(type, (string)resolution == "Ok", this.Converted(this.Value(call.Arguments[0]), (string)resolution == "Ok" ? type.Element! : type.Error, null));
      case StructInfo structure: {
        var values = this.Operands([.. call.Arguments], [.. structure.Fields.Select(field => field.Type)]);
        return new IrStructNew(this.Ir(type), values);
      }
      case "cmp::min" or "cmp::max": {
        var values = this.Operands([.. call.Arguments]);
        var first = this.Stable(values[0]);
        var second = this.Stable(values[1]);
        // min returns the first of equal values and max the second; for numbers nobody can tell them apart but by NaN, which Ord excludes.
        return (string)resolution == "cmp::min"
          ? new IrConditional(Compare(IrBinaryOp.LessOrEqual, first, second), first, second)
          : new IrConditional(Compare(IrBinaryOp.Greater, first, second), first, second);
      }
      case "mem::swap": {
        var first = this.PlaceOf(((ReferenceExpr)call.Arguments[0]).Operand);
        var second = first is null ? null : this.PlaceOf(((ReferenceExpr)call.Arguments[1]).Operand);
        if (first is null || second is null)
          return null;

        var saved = this.Temporary(this.Read(first), "swapped");
        this.Write(first, this.Read(second));
        this.Write(second, saved);
        return null;
      }
      case "Vec::new" or "Vec::with_capacity":
        return this.NewVec(type, call.Arguments);
      case "collection::new" or "collection::with_capacity":
        if (call.Arguments.Count > 0)
          this.Effect(this.Value(call.Arguments[0]));

        return this.Zero(this.Ir(type));
      case string text when text.StartsWith("String::", StringComparison.Ordinal) || text.StartsWith("char::", StringComparison.Ordinal) || text.StartsWith("__", StringComparison.Ordinal):
        return this.TextCall(call, text);
      case "from_bits": {
        var bits = this.Value(call.Arguments[0]);
        return new IrIntrinsic(IrIntrinsicOp.BitsFloat, type.Ir, [Cast(bits, type.Ir == IrType.F32 ? IrType.I32 : IrType.I64)]);
      }
      case "from_le_bytes" or "from_be_bytes":
        return this.FromBytes(this.Value(call.Arguments[0]), type.Ir, (string)resolution == "from_le_bytes");
      default:
        // default.
        return this.Default(type);
    }
  }

  /// <summary>The value <c>Default::default()</c> gives: zero, false, none, empty, the <c>#[default]</c> variant, every field its own.</summary>
  private IrExpr Default(RType type) {
    var resolved = type.Resolved;
    switch (resolved.Kind) {
      case RKind.Struct: {
        var ir = this.Ir(resolved);
        if (ir.StructType!.HasDefault)
          return new IrStructDefault(ir);

        return new IrStructNew(ir, [.. resolved.Struct!.Fields.Select(field => this.Default(field.Type))]);
      }
      case RKind.Enum: {
        var info = resolved.Enum!;
        return this.Variant(info, info.Default ?? info.Variants[0].Name);
      }
      case RKind.Array:
        return this.Filled(this.Ir(resolved), new IrConst(IrType.I32, resolved.Length), this.Default(resolved.Element!));
      case RKind.Tuple:
        return new IrStructNew(this.Ir(resolved), [.. resolved.Elements.Select(this.Default)]);
      default:
        return this.Zero(this.Ir(resolved));
    }
  }

  /// <summary>A value of a <c>Result</c>: the tag, and the payload in its field, the other field its zero.</summary>
  private IrExpr Result(RType type, bool ok, IrExpr? payload) {
    var ir = this.Ir(type);
    var fields = new List<IrExpr> { new IrConst(IrType.Bool, ok ? 1 : 0) };
    foreach (var field in ir.StructType!.Fields.Skip(1))
      fields.Add((field.Name == "ok") == ok && payload is not null ? payload : this.Zero(field.Type));

    return new IrStructNew(ir, fields);
  }

  private static IrField Tag(IrExpr result) => result.Type.StructType!.Fields[0];

  private static IrExpr IsOk(IrExpr result) => new IrFieldLoad(result, Tag(result));

  private static IrExpr? Payload(IrExpr result, string name) => result.Type.StructType!.Field(name) is { } field ? new IrFieldLoad(result, field) : null;

  // ---------------------------------------------------------------- equality

  /// <summary>
  /// <c>==</c> of the derived <c>PartialEq</c>: field by field in declaration order, stopping at the first
  /// difference, a float as IEEE 754 compares it (a NaN is not equal to itself); a variant by its
  /// discriminant; an <c>Option</c> and a <c>Result</c> by variant and payload.
  /// </summary>
  private IrExpr Equality(bool equal, IrExpr left, IrExpr right, RType type) {
    var first = this.Stable(left, "left");
    var second = this.Stable(right, "right");
    // `p == p` compares a value with itself, which a float field may fail; a copy keeps a target from calling it a typo.
    if (second == first && first is IrLocalRef)
      second = this.Temporary(second, "right");

    var test = this.Equal(first, second, type);
    return equal ? test : Not(test);
  }

  private IrExpr Equal(IrExpr left, IrExpr right, RType type) {
    var resolved = type.Dereferenced;
    switch (resolved.Kind) {
      case RKind.Integer or RKind.Float or RKind.Bool or RKind.Char:
        return Compare(IrBinaryOp.Equal, left, right);
      case RKind.String or RKind.Str:
        return new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]);
      case RKind.Enum:
        return Compare(IrBinaryOp.Equal, new IrConvert(left.Type.Integral, left, IrConversion.Wrap), new IrConvert(right.Type.Integral, right, IrConversion.Wrap));
      case RKind.Unit:
        return Truth(true);
      case RKind.Struct or RKind.Tuple: {
        var parts = resolved.Kind == RKind.Struct ? resolved.Struct!.Fields.Select(field => field.Type).ToList() : resolved.Elements.ToList();
        var fields = left.Type.StructType!.Fields;
        IrExpr? all = null;
        for (var index = 0; index < fields.Count; ++index) {
          var test = this.Equal(new IrFieldLoad(left, fields[index]), new IrFieldLoad(right, fields[index]), parts[index]);
          all = all is null ? test : And(all, test);
        }

        return all ?? Truth(true);
      }
      case RKind.Option: {
        var none = And(new IrIsNone(left), new IrIsNone(right));
        var some = And(And(Not(new IrIsNone(left)), Not(new IrIsNone(right))),
          this.Equal(new IrUnwrap(left) { IsTested = true }, new IrUnwrap(right) { IsTested = true }, resolved.Element!));
        return Or(none, some);
      }
      case RKind.Result: {
        var ok = Payload(left, "ok") is { } leftOk ? this.Equal(leftOk, Payload(right, "ok")!, resolved.Element!) : Truth(true);
        var err = Payload(left, "err") is { } leftErr ? this.Equal(leftErr, Payload(right, "err")!, resolved.Error) : Truth(true);
        return And(Compare(IrBinaryOp.Equal, IsOk(left), IsOk(right)), new IrConditional(IsOk(left), ok, err));
      }
      default: {
        // Arrays: element by element.
        var same = this.Temporary(Truth(true), "same");
        var index = this.Counter("i");
        var body = this.Capture(() => this.Equal(new IrArrayLoad(left, new IrLocalRef(index)), new IrArrayLoad(right, new IrLocalRef(index)), resolved.Element!));
        body.Statements.Add(new IrIf(Not(body.Value!), new IrBlock([new IrAssign(same.Local, Truth(false)), new IrBreak()]), null));
        this.Emit(new IrFor(Binary(IrBinaryOp.Less, new IrLocalRef(index), new IrArrayLength(left)), new IrBlock(body.Statements), new IrBlock([Next(index)])));
        return same;
      }
    }
  }

  // ---------------------------------------------------------------- Option and Result methods

  private IrExpr? OptionMethod(MethodCallExpr call, string name) {
    var values = this.Operands([call.Receiver, .. call.Arguments.Where(argument => argument is not StringLiteral)]);
    var option = this.Stable(values[0], "option");
    var value = new IrUnwrap(option) { IsTested = true };
    switch (name) {
      case "is_some":
        return Not(new IrIsNone(option));
      case "is_none":
        return new IrIsNone(option);
      case "unwrap":
        return new IrConditional(new IrIsNone(option), RustTraps.Panic(value.Type, "called `Option::unwrap()` on a `None` value"), value);
      case "expect":
        return new IrConditional(new IrIsNone(option), RustTraps.Panic(value.Type, ((StringLiteral)call.Arguments[0]).Value), value);
      case "unwrap_or":
        return new IrCoalesce(option, this.Stable(values[1], "fallback"));
      case "ok_or": {
        var error = this.Stable(values[1], "error");
        var result = this._typed.TypeOf(call);
        return new IrConditional(new IrIsNone(option), this.Result(result, false, error), this.Result(result, true, value));
      }
      default:
        // copied, cloned, clone: a reference to a value is the value.
        return option;
    }
  }

  private IrExpr? ResultMethod(MethodCallExpr call, string name) {
    var values = this.Operands([call.Receiver, .. call.Arguments.Where(argument => argument is not StringLiteral)]);
    var result = this.Stable(values[0], "result");
    var ok = Payload(result, "ok");
    switch (name) {
      case "is_ok":
        return IsOk(result);
      case "is_err":
        return Not(IsOk(result));
      case "unwrap" or "expect": {
        var message = name == "unwrap" ? "called `Result::unwrap()` on an `Err` value" : ((StringLiteral)call.Arguments[0]).Value;
        if (ok is null) {
          this.Emit(new IrIf(Not(IsOk(result)), new IrBlock([RustTraps.PanicStatement(message)]), null));
          return null;
        }

        return new IrConditional(IsOk(result), ok, RustTraps.Panic(ok.Type, message));
      }
      case "unwrap_or": {
        var fallback = this.Stable(values[1], "fallback");
        return ok is null ? null : new IrConditional(IsOk(result), ok, fallback);
      }
      case "ok": {
        var type = this.Ir(this._typed.TypeOf(call));
        return new IrConditional(IsOk(result), ok is null ? new IrNone(type) : new IrSome(ok), new IrNone(type));
      }
      default:
        return result;
    }
  }

  // ---------------------------------------------------------------- ?

  /// <summary>The result type of the function or closure being lowered, which <c>?</c> returns a <c>None</c> or an <c>Err</c> of.</summary>
  private RType CurrentResult => this._closureFrames.Count > 0 ? this._closureResults[^1] : this._signature.Result;

  private IrExpr? Try(TryExpr attempt) {
    var value = this.Stable(this.Value(attempt.Operand), "tried");
    var type = this._typed.TypeOf(attempt.Operand);
    var result = this.CurrentResult;
    var (statements, _, _) = this.Capture(() => {
      if (type.Kind == RKind.Option)
        this.EmitReturn(new IrNone(this.Ir(result)));
      else
        this.EmitReturn(this.Result(result, false, Payload(value, "err")));

      return null;
    });
    if (type.Kind == RKind.Option) {
      this.Emit(new IrIf(new IrIsNone(value), new IrBlock(statements), null));
      return new IrUnwrap(value) { IsTested = true };
    }

    this.Emit(new IrIf(Not(IsOk(value)), new IrBlock(statements), null));
    return Payload(value, "ok");
  }

}
