using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.VBScript.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VBScript.Lowering;

internal sealed partial class VBScriptLowerer {

  private static readonly Dictionary<string, (int Min, int Max)> _builtinArity = new(StringComparer.OrdinalIgnoreCase) {
    ["abs"] = (1, 1), ["sqr"] = (1, 1), ["int"] = (1, 1), ["fix"] = (1, 1), ["round"] = (1, 2), ["sgn"] = (1, 1),
    ["len"] = (1, 1), ["left"] = (2, 2), ["right"] = (2, 2), ["mid"] = (2, 3), ["instr"] = (2, 4),
    ["replace"] = (3, 3), ["split"] = (1, 3), ["join"] = (1, 2),
    ["trim"] = (1, 1), ["ltrim"] = (1, 1), ["rtrim"] = (1, 1), ["ucase"] = (1, 1), ["lcase"] = (1, 1),
    ["chr"] = (1, 1), ["asc"] = (1, 1),
    ["cstr"] = (1, 1), ["cint"] = (1, 1), ["clng"] = (1, 1), ["cdbl"] = (1, 1), ["cbool"] = (1, 1),
    ["ubound"] = (1, 1), ["lbound"] = (1, 1),
    ["isempty"] = (1, 1), ["isnull"] = (1, 1), ["isnumeric"] = (1, 1)
  };

  private bool TryLowerBuiltin(
    string name,
    IReadOnlyList<VbsExpr> args,
    int line,
    int column,
    ProcContext ctx,
    out IrExpr result) {
    result = null!;
    if (_builtinArity.TryGetValue(name, out var arity) && (args.Count < arity.Min || args.Count > arity.Max)) {
      this.AddDiagnostic(VBScriptFrontend.UnsupportedCall, line, column, $"{name} with {args.Count} arguments");
      result = new IrConst(IrType.I32, 0);
      return true;
    }

    switch (name.ToLowerInvariant()) {
      // ---- Math ----
      case "abs": {
        var x = this.LowerExpr(args[0], ctx);
        var target = x.Type.IsNumeric ? x.Type : IrType.I32;
        result = new IrIntrinsic(IrIntrinsicOp.Abs, target, [this.Coerce(x, target)]);
        return true;
      }
      case "sqr":
        result = new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [this.Coerce(this.LowerExpr(args[0], ctx), IrType.F64)]);
        return true;
      case "int":
        result = new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [this.Coerce(this.LowerExpr(args[0], ctx), IrType.F64)]);
        return true;
      case "fix":
        result = new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [this.Coerce(this.LowerExpr(args[0], ctx), IrType.F64)]);
        return true;
      case "round": {
        var x = this.Coerce(this.LowerExpr(args[0], ctx), IrType.F64);
        result = args.Count == 1
          ? new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [x])
          : new IrIntrinsic(IrIntrinsicOp.RoundDigits, IrType.F64, [x, this.Coerce(this.LowerExpr(args[1], ctx), IrType.I32), new IrConst(IrType.I32, 0)]);
        return true;
      }
      case "sgn": {
        var x = this.LowerExpr(args[0], ctx);
        var target = x.Type.IsNumeric ? x.Type : IrType.I32;
        result = new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [this.Coerce(x, target)]);
        return true;
      }

      // ---- String ----
      case "len":
        result = new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [this.ToStringExpr(this.LowerExpr(args[0], ctx))]);
        return true;
      case "left":
        result = new IrCall(this.EnsureLeftHelper(), IrType.String, [
          this.ToStringExpr(this.LowerExpr(args[0], ctx)),
          this.Coerce(this.LowerExpr(args[1], ctx), IrType.I32)
        ]);
        return true;
      case "right":
        result = new IrCall(this.EnsureRightHelper(), IrType.String, [
          this.ToStringExpr(this.LowerExpr(args[0], ctx)),
          this.Coerce(this.LowerExpr(args[1], ctx), IrType.I32)
        ]);
        return true;
      case "mid": {
        var s = this.ToStringExpr(this.LowerExpr(args[0], ctx));
        var start = this.Coerce(this.LowerExpr(args[1], ctx), IrType.I32);
        result = args.Count == 2
          ? new IrCall(this.EnsureMid2Helper(), IrType.String, [s, start])
          : new IrCall(this.EnsureMid3Helper(), IrType.String, [s, start, this.Coerce(this.LowerExpr(args[2], ctx), IrType.I32)]);
        return true;
      }
      case "instr": {
        IrExpr startExpr;
        IrExpr hayExpr;
        IrExpr needleExpr;
        if (args.Count == 2) {
          startExpr = new IrConst(IrType.I32, 1);
          hayExpr = this.ToStringExpr(this.LowerExpr(args[0], ctx));
          needleExpr = this.ToStringExpr(this.LowerExpr(args[1], ctx));
        } else {
          startExpr = this.Coerce(this.LowerExpr(args[0], ctx), IrType.I32);
          hayExpr = this.ToStringExpr(this.LowerExpr(args[1], ctx));
          needleExpr = this.ToStringExpr(this.LowerExpr(args[2], ctx));
          if (args.Count == 4 && args[3] is VbsIntLit { Value: 1 }) {
            var lc = this.EnsureCaseHelper(toUpper: false);
            hayExpr = new IrCall(lc, IrType.String, [hayExpr]);
            needleExpr = new IrCall(lc, IrType.String, [needleExpr]);
          }
        }
        result = new IrCall(this.EnsureInStrHelper(), IrType.I32, [startExpr, hayExpr, needleExpr]);
        return true;
      }
      case "replace":
        result = new IrCall(this.EnsureReplaceHelper(), IrType.String, [
          this.ToStringExpr(this.LowerExpr(args[0], ctx)),
          this.ToStringExpr(this.LowerExpr(args[1], ctx)),
          this.ToStringExpr(this.LowerExpr(args[2], ctx))
        ]);
        return true;
      case "split": {
        var s = this.ToStringExpr(this.LowerExpr(args[0], ctx));
        IrExpr delim = args.Count >= 2 ? this.ToStringExpr(this.LowerExpr(args[1], ctx)) : new IrStrConst(" ");
        IrExpr limit = args.Count >= 3 ? this.Coerce(this.LowerExpr(args[2], ctx), IrType.I32) : new IrConst(IrType.I32, -1);
        result = new IrCall(this.EnsureSplitHelper(), IrType.Array(IrType.String), [s, delim, limit]);
        return true;
      }
      case "join": {
        var arr = this.LowerExpr(args[0], ctx);
        IrExpr delim = args.Count == 2 ? this.ToStringExpr(this.LowerExpr(args[1], ctx)) : new IrStrConst(" ");
        result = new IrIntrinsic(IrIntrinsicOp.StrJoin, IrType.String, [delim, arr]);
        return true;
      }
      case "trim":
        result = new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [this.ToStringExpr(this.LowerExpr(args[0], ctx)), StringOperations.Set(WhitespaceSet.DotNet)]);
        return true;
      case "ltrim":
        result = new IrIntrinsic(IrIntrinsicOp.StrTrimStart, IrType.String, [this.ToStringExpr(this.LowerExpr(args[0], ctx)), StringOperations.Set(WhitespaceSet.DotNet)]);
        return true;
      case "rtrim":
        result = new IrIntrinsic(IrIntrinsicOp.StrTrimEnd, IrType.String, [this.ToStringExpr(this.LowerExpr(args[0], ctx)), StringOperations.Set(WhitespaceSet.DotNet)]);
        return true;
      case "ucase":
        result = new IrCall(this.EnsureCaseHelper(toUpper: true), IrType.String, [this.ToStringExpr(this.LowerExpr(args[0], ctx))]);
        return true;
      case "lcase":
        result = new IrCall(this.EnsureCaseHelper(toUpper: false), IrType.String, [this.ToStringExpr(this.LowerExpr(args[0], ctx))]);
        return true;
      case "chr":
        result = new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [this.Coerce(this.LowerExpr(args[0], ctx), IrType.U16)]);
        return true;
      case "asc": {
        var u16 = new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [this.ToStringExpr(this.LowerExpr(args[0], ctx)), new IrConst(IrType.I32, 0)]);
        result = new IrConvert(IrType.I32, u16, IrConversion.Wrap);
        return true;
      }

      // ---- Conversions ----
      case "cstr":
        result = this.ToStringExpr(this.LowerExpr(args[0], ctx));
        return true;
      case "cint" or "clng": {
        var x = this.LowerExpr(args[0], ctx);
        if (x.Type == IrType.F64)
          result = new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [x]), IrConversion.Saturate);
        else if (x.Type == IrType.String)
          result = new IrConvert(IrType.I32, new IrIntrinsic(IrIntrinsicOp.RoundEven, IrType.F64, [new IrIntrinsic(IrIntrinsicOp.ParseDotNet, IrType.F64, [x])]), IrConversion.Saturate);
        else
          result = this.Coerce(x, IrType.I32);
        return true;
      }
      case "cdbl":
        result = this.Coerce(this.LowerExpr(args[0], ctx), IrType.F64);
        return true;
      case "cbool": {
        var x = this.LowerExpr(args[0], ctx);
        result = x.Type == IrType.String
          ? new IrCall(this.EnsureCBoolStrHelper(), IrType.Bool, [x])
          : this.ToBoolExpr(x);
        return true;
      }

      // ---- Arrays ----
      case "array": {
        var elems = new List<IrExpr>(args.Count);
        IrType? common = null;
        foreach (var a in args) {
          var lowered = this.LowerExpr(a, ctx);
          elems.Add(lowered);
          common = WidenType(common, lowered.Type);
        }
        var elemType = common ?? IrType.I32;
        for (var i = 0; i < elems.Count; ++i)
          elems[i] = this.Coerce(elems[i], elemType);
        result = new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(elemType), elems);
        return true;
      }
      case "ubound":
        result = new IrBinary(IrBinaryOp.Subtract, new IrArrayLength(this.LowerExpr(args[0], ctx)), new IrConst(IrType.I32, 1));
        return true;
      case "lbound":
        result = new IrConst(IrType.I32, 0);
        return true;

      // ---- Predicates ----
      case "isempty": {
        if (args[0] is VbsEmptyLit) {
          result = new IrConst(IrType.Bool, 1);
          return true;
        }
        if (args[0] is VbsIdentExpr id && ctx.UnassignedLocals.Contains(id.Name)) {
          result = new IrConst(IrType.Bool, 1);
          return true;
        }
        var x = this.LowerExpr(args[0], ctx);
        result = x.Type.IsOptional ? new IrIsNone(x) : new IrConst(IrType.Bool, 0);
        return true;
      }
      case "isnull": {
        if (args[0] is VbsNullLit) {
          result = new IrConst(IrType.Bool, 1);
          return true;
        }
        var x = this.LowerExpr(args[0], ctx);
        result = x.Type.IsOptional ? new IrIsNone(x)
          : x.Type.IsNullable ? new IrIsNull(x)
          : new IrConst(IrType.Bool, 0);
        return true;
      }
      case "isnumeric": {
        if (args[0] is VbsEmptyLit) {
          result = new IrConst(IrType.Bool, 1);
          return true;
        }
        if (args[0] is VbsNullLit) {
          result = new IrConst(IrType.Bool, 0);
          return true;
        }
        var x = this.LowerExpr(args[0], ctx);
        result = x.Type.IsNumeric || x.Type == IrType.Bool
          ? new IrConst(IrType.Bool, 1)
          : x.Type == IrType.String
            ? new IrCall(this.EnsureIsNumericHelper(), IrType.Bool, [x])
            : new IrConst(IrType.Bool, 0);
        return true;
      }

      // ---- Explicitly unsupported host / dynamic COM builtins ----
      case "createobject" or "getobject" or "msgbox" or "inputbox":
        this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, line, column, name);
        result = new IrConst(IrType.I32, 0);
        return true;

      default:
        return false;
    }
  }

  private IrExpr AllocateArray(IrType arrayType, IrExpr length)
    => arrayType.Element == IrType.String
      ? new IrCall(this.EnsureStrArrayHelper(), arrayType, [length])
      : new IrNewArray(arrayType, length);

  // ---- On-demand pure PolyIR helper functions ----

  private string RegisterHelper(string name, Func<IrFunction> factory) {
    if (!this._helpers.ContainsKey(name)) {
      var fn = factory();
      this._helpers[name] = fn;
      this._helperList.Add(fn);
    }
    return name;
  }

  private string EnsureLeftHelper() => this.RegisterHelper("__vbs_left", () => {
    var s = new IrLocal("s", IrType.String);
    var n = new IrLocal("n", IrType.I32);
    var len = new IrLocal("len", IrType.I32);
    return new IrFunction("__vbs_left", false, IrType.String, [s, n], [len], new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(n), new IrConst(IrType.I32, 0)),
        new IrBlock([new IrReturn(new IrStrConst(""))]), null),
      new IrDeclare(len, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(s)])),
      new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(n), new IrLocalRef(len)),
        new IrBlock([new IrReturn(new IrLocalRef(s))]), null),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [new IrLocalRef(s), new IrConst(IrType.I32, 0), new IrLocalRef(n)]))
    ]));
  });

  private string EnsureRightHelper() => this.RegisterHelper("__vbs_right", () => {
    var s = new IrLocal("s", IrType.String);
    var n = new IrLocal("n", IrType.I32);
    var len = new IrLocal("len", IrType.I32);
    return new IrFunction("__vbs_right", false, IrType.String, [s, n], [len], new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(n), new IrConst(IrType.I32, 0)),
        new IrBlock([new IrReturn(new IrStrConst(""))]), null),
      new IrDeclare(len, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(s)])),
      new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(n), new IrLocalRef(len)),
        new IrBlock([new IrReturn(new IrLocalRef(s))]), null),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [
        new IrLocalRef(s),
        new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(len), new IrLocalRef(n)),
        new IrLocalRef(n)
      ]))
    ]));
  });

  private string EnsureMid2Helper() => this.RegisterHelper("__vbs_mid2", () => {
    var s = new IrLocal("s", IrType.String);
    var start = new IrLocal("start", IrType.I32);
    var idx = new IrLocal("idx", IrType.I32);
    var len = new IrLocal("len", IrType.I32);
    return new IrFunction("__vbs_mid2", false, IrType.String, [s, start], [idx, len], new IrBlock([
      new IrDeclare(idx, new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(start), new IrConst(IrType.I32, 1))),
      new IrDeclare(len, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(s)])),
      new IrIf(new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(idx), new IrLocalRef(len)),
        new IrBlock([new IrReturn(new IrStrConst(""))]), null),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [new IrLocalRef(s), new IrLocalRef(idx)]))
    ]));
  });

  private string EnsureMid3Helper() => this.RegisterHelper("__vbs_mid3", () => {
    var s = new IrLocal("s", IrType.String);
    var start = new IrLocal("start", IrType.I32);
    var count = new IrLocal("count", IrType.I32);
    var idx = new IrLocal("idx", IrType.I32);
    var avail = new IrLocal("avail", IrType.I32);
    var take = new IrLocal("take", IrType.I32);
    return new IrFunction("__vbs_mid3", false, IrType.String, [s, start, count], [idx, avail, take], new IrBlock([
      new IrDeclare(idx, new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(start), new IrConst(IrType.I32, 1))),
      new IrDeclare(avail, new IrBinary(IrBinaryOp.Subtract,
        new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(s)]), new IrLocalRef(idx))),
      new IrIf(new IrBinary(IrBinaryOp.LogicalOr,
        new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(avail), new IrConst(IrType.I32, 0)),
        new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(count), new IrConst(IrType.I32, 0))),
        new IrBlock([new IrReturn(new IrStrConst(""))]), null),
      new IrDeclare(take, new IrConditional(
        new IrBinary(IrBinaryOp.Less, new IrLocalRef(count), new IrLocalRef(avail)),
        new IrLocalRef(count), new IrLocalRef(avail))),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSubstring, IrType.String, [new IrLocalRef(s), new IrLocalRef(idx), new IrLocalRef(take)]))
    ]));
  });

  private string EnsureInStrHelper() => this.RegisterHelper("__vbs_instr", () => {
    var start = new IrLocal("start", IrType.I32);
    var hay = new IrLocal("hay", IrType.String);
    var needle = new IrLocal("needle", IrType.String);
    var idx = new IrLocal("idx", IrType.I32);
    var len = new IrLocal("len", IrType.I32);
    return new IrFunction("__vbs_instr", false, IrType.I32, [start, hay, needle], [idx, len], new IrBlock([
      new IrDeclare(idx, new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(start), new IrConst(IrType.I32, 1))),
      new IrDeclare(len, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(hay)])),
      new IrIf(new IrBinary(IrBinaryOp.LogicalOr,
        new IrBinary(IrBinaryOp.Less, new IrLocalRef(idx), new IrConst(IrType.I32, 0)),
        new IrBinary(IrBinaryOp.Greater, new IrLocalRef(idx), new IrLocalRef(len))),
        new IrBlock([new IrReturn(new IrConst(IrType.I32, 0))]), null),
      new IrReturn(new IrBinary(IrBinaryOp.Add,
        new IrIntrinsic(IrIntrinsicOp.StrIndexOf, IrType.I32, [new IrLocalRef(hay), new IrLocalRef(needle), new IrLocalRef(idx)]),
        new IrConst(IrType.I32, 1)))
    ]));
  });

  private string EnsureReplaceHelper() => this.RegisterHelper("__vbs_replace", () => {
    var s = new IrLocal("s", IrType.String);
    var find = new IrLocal("find", IrType.String);
    var repl = new IrLocal("repl", IrType.String);
    return new IrFunction("__vbs_replace", false, IrType.String, [s, find, repl], [], new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.Equal,
        new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(find)]), new IrConst(IrType.I32, 0)),
        new IrBlock([new IrReturn(new IrLocalRef(s))]), null),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrReplaceAll, IrType.String, [new IrLocalRef(s), new IrLocalRef(find), new IrLocalRef(repl)]))
    ]));
  });

  private string EnsureSplitHelper() => this.RegisterHelper("__vbs_split", () => {
    var s = new IrLocal("s", IrType.String);
    var delim = new IrLocal("delim", IrType.String);
    var limit = new IrLocal("limit", IrType.I32);
    var strArr = IrType.Array(IrType.String);
    return new IrFunction("__vbs_split", false, strArr, [s, delim, limit], [], new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.Equal,
        new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(s)]), new IrConst(IrType.I32, 0)),
        new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.ArrayOf, strArr, []))]), null),
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrSplit, strArr, [new IrLocalRef(s), new IrLocalRef(delim), new IrLocalRef(limit)]))
    ]));
  });

  private string EnsureCaseHelper(bool toUpper) {
    var name = toUpper ? "__vbs_ucase" : "__vbs_lcase";
    var lo = toUpper ? 97 : 65;
    var hi = toUpper ? 122 : 90;
    var delta = toUpper ? -32 : 32;
    return this.RegisterHelper(name, () => {
      var s = new IrLocal("s", IrType.String);
      var units = new IrLocal("units", IrType.Array(IrType.U16));
      var i = new IrLocal("i", IrType.I32);
      var c = new IrLocal("c", IrType.I32);
      return new IrFunction(name, false, IrType.String, [s], [units, i, c], new IrBlock([
        new IrDeclare(units, new IrIntrinsic(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), [new IrLocalRef(s)])),
        new IrDeclare(i, new IrConst(IrType.I32, 0)),
        new IrFor(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrArrayLength(new IrLocalRef(units))),
          new IrBlock([
            new IrDeclare(c, new IrConvert(IrType.I32, new IrArrayLoad(new IrLocalRef(units), new IrLocalRef(i)), IrConversion.Wrap)),
            new IrIf(
              new IrBinary(IrBinaryOp.LogicalAnd,
                new IrBinary(IrBinaryOp.GreaterOrEqual, new IrLocalRef(c), new IrConst(IrType.I32, lo)),
                new IrBinary(IrBinaryOp.LessOrEqual, new IrLocalRef(c), new IrConst(IrType.I32, hi))),
              new IrBlock([
                new IrStore(new IrLocalRef(units), new IrLocalRef(i),
                  new IrConvert(IrType.U16, new IrBinary(IrBinaryOp.Add, new IrLocalRef(c), new IrConst(IrType.I32, delta)), IrConversion.Wrap))
              ]),
              null)
          ]),
          new IrBlock([new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1)))])),
        new IrReturn(new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnits, IrType.String, [new IrLocalRef(units)]))
      ]));
    });
  }

  private string EnsureIsNumericHelper() => this.RegisterHelper("__vbs_is_numeric", () => {
    var s = new IrLocal("s", IrType.String);
    var parsed = new IrLocal("parsed", IrType.F64);
    var ex = new IrLocal("ex", IrExceptionClass.Reference);
    return new IrFunction("__vbs_is_numeric", false, IrType.Bool, [s], [parsed, ex], new IrBlock([
      new IrTry(
        new IrBlock([
          new IrDeclare(parsed, new IrIntrinsic(IrIntrinsicOp.ParseDotNet, IrType.F64, [new IrLocalRef(s)])),
          new IrReturn(new IrConst(IrType.Bool, 1))
        ]),
        [new IrCatch(IrExceptionClass.Exception, ex, null, new IrBlock([new IrReturn(new IrConst(IrType.Bool, 0))]))],
        null),
      new IrReturn(new IrConst(IrType.Bool, 0))
    ]));
  });

  private string EnsureCBoolStrHelper() => this.RegisterHelper("__vbs_cbool_str", () => {
    var s = new IrLocal("s", IrType.String);
    var lc = this.EnsureCaseHelper(toUpper: false);
    var lowered = new IrLocal("lowered", IrType.String);
    return new IrFunction("__vbs_cbool_str", false, IrType.Bool, [s], [lowered], new IrBlock([
      new IrDeclare(lowered, new IrCall(lc, IrType.String, [new IrLocalRef(s)])),
      new IrIf(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrLocalRef(lowered), new IrStrConst("true")]),
        new IrBlock([new IrReturn(new IrConst(IrType.Bool, 1))]), null),
      new IrIf(new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [new IrLocalRef(lowered), new IrStrConst("false")]),
        new IrBlock([new IrReturn(new IrConst(IrType.Bool, 0))]), null),
      new IrReturn(new IrBinary(IrBinaryOp.NotEqual,
        new IrIntrinsic(IrIntrinsicOp.ParseDotNet, IrType.F64, [new IrLocalRef(lowered)]),
        new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 0.0))))
    ]));
  });

  private string EnsureStrArrayHelper() => this.RegisterHelper("__vbs_str_array", () => {
    var len = new IrLocal("len", IrType.I32);
    var listType = IrType.List(IrType.String);
    var list = new IrLocal("list", listType);
    var i = new IrLocal("i", IrType.I32);
    return new IrFunction("__vbs_str_array", false, IrType.Array(IrType.String), [len], [list, i], new IrBlock([
      new IrDeclare(list, new IrCollection(IrCollectionOp.ListNew, listType, [])),
      new IrDeclare(i, new IrConst(IrType.I32, 0)),
      new IrFor(
        new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(len)),
        new IrBlock([new IrExprStmt(new IrCollection(IrCollectionOp.ListAdd, IrType.Void, [new IrLocalRef(list), new IrStrConst("")]))]),
        new IrBlock([new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1)))])),
      new IrReturn(new IrCollection(IrCollectionOp.ListCopyToArray, IrType.Array(IrType.String), [new IrLocalRef(list)]))
    ]));
  });

  private string EnsureReDimPreserveHelper(IrType arrayType) {
    var elem = arrayType.Element!;
    var name = $"__vbs_redim_{elem.Kind.ToString().ToLowerInvariant()}";
    return this.RegisterHelper(name, () => {
      var src = new IrLocal("src", arrayType);
      var newLen = new IrLocal("newLen", IrType.I32);
      var dst = new IrLocal("dst", arrayType);
      var oldLen = new IrLocal("oldLen", IrType.I32);
      var copyLen = new IrLocal("copyLen", IrType.I32);
      var i = new IrLocal("i", IrType.I32);
      var alloc = this.AllocateArray(arrayType, new IrLocalRef(newLen));
      return new IrFunction(name, false, arrayType, [src, newLen], [dst, oldLen, copyLen, i], new IrBlock([
        new IrDeclare(dst, alloc),
        new IrDeclare(oldLen, new IrArrayLength(new IrLocalRef(src))),
        new IrDeclare(copyLen, new IrConditional(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(oldLen), new IrLocalRef(newLen)),
          new IrLocalRef(oldLen), new IrLocalRef(newLen))),
        new IrDeclare(i, new IrConst(IrType.I32, 0)),
        new IrFor(
          new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(copyLen)),
          new IrBlock([new IrStore(new IrLocalRef(dst), new IrLocalRef(i), new IrArrayLoad(new IrLocalRef(src), new IrLocalRef(i)))]),
          new IrBlock([new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1)))])),
        new IrReturn(new IrLocalRef(dst))
      ]));
    });
  }

}
