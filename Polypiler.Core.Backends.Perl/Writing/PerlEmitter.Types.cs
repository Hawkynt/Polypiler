using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Perl;

public static partial class PerlEmitter {

  private sealed partial class Writer {

    private string DefaultValue(IrType type) {
      if (type.IsOptional || type.IsNullable)
        return "undef";
      if (type == IrType.Bool)
        return "0";
      if (type.IsFloat)
        return "0.0";
      if (type.IsInteger)
        return "0";
      if (type.IsStruct) {
        var st = type.StructType!;
        var fields = st.Fields.Select(f => $"{PerlNaming.HashKey(f.Name)} => {this.DefaultValue(f.Type)}");
        return "{ " + string.Join(", ", fields) + " }";
      }
      if (type.IsList)
        return "[]";
      if (type.Kind is IrTypeKind.Map or IrTypeKind.Set)
        return "{}";
      return "undef";
    }

    private string CopyIfStruct(IrType type, string expr)
      => type.IsStruct ? $"{{ %{{{expr}}} }}" : expr;

    private string EmitStructNew(IrStructNew structNew) {
      var st = structNew.Type.StructType!;
      var pairs = st.Fields.Zip(structNew.Arguments, (field, arg) =>
        $"{PerlNaming.HashKey(field.Name)} => {this.CopyIfStruct(field.Type, this.EmitExpr(arg))}");
      return "{ " + string.Join(", ", pairs) + " }";
    }

    private string EmitFieldLoad(IrFieldLoad load) {
      var key = PerlNaming.HashKey(load.Field.Name);
      var target = this.EmitExpr(load.Target);
      if (load.Target.Type.IsObject) {
        if (load.Field == IrExceptionClass.Message) {
          this.UseHelper(PerlHelpers.TrapMessage);
          return $"{PerlHelpers.TrapMessage}({target})";
        }
        return $"do {{ my $_o = {target}; die \"NullReference: null object\\n\" unless defined $_o; {this.CopyIfStruct(load.Type, $"$_o->{{{key}}}")} }}";
      }
      return this.CopyIfStruct(load.Type, $"({target})->{{{key}}}");
    }

    private void EmitFieldStoreStatement(IrFieldStore store, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var key = PerlNaming.HashKey(store.Field.Name);
      var target = this.EmitExpr(store.Target);
      var value = this.CopyIfStruct(store.Field.Type, this.EmitExpr(store.Value));
      if (store.Target.Type.IsObject) {
        text.Append(pad).Append("do { my ($_o, $_v) = (").Append(target).Append(", ").Append(value)
          .Append("); die \"NullReference: null object\\n\" unless defined $_o; $_o->{").Append(key).Append("} = $_v; };\n");
        return;
      }
      text.Append(pad).Append('(').Append(target).Append(")->{").Append(key).Append("} = ").Append(value).Append(";\n");
    }

    private string EmitStructEquals(IrStructEquals equals) {
      var st = equals.Left.Type.StructType!;
      this._equatableStructs.Add(st);
      return $"_eq_{PerlNaming.Snake(st.Name)}({this.EmitExpr(equals.Left)}, {this.EmitExpr(equals.Right)})";
    }

    private string EmitStructEqualsHelper(IrStructType st) {
      var text = new StringBuilder();
      text.Append("sub _eq_").Append(PerlNaming.Snake(st.Name)).Append(" {\n");
      text.Append("    my ($_p, $_q) = @_;\n");
      foreach (var field in st.Fields) {
        var k = PerlNaming.HashKey(field.Name);
        if (field.Type.IsStruct) {
          this._equatableStructs.Add(field.Type.StructType!);
          text.Append("    return 0 unless _eq_").Append(PerlNaming.Snake(field.Type.StructType!.Name))
            .Append("($_p->{").Append(k).Append("}, $_q->{").Append(k).Append("});\n");
        } else if (field.Type.IsFloat) {
          text.Append("    if ($_p->{").Append(k).Append("} == $_p->{").Append(k).Append("} || $_q->{").Append(k).Append("} == $_q->{").Append(k).Append("}) {\n");
          text.Append("        return 0 if $_p->{").Append(k).Append("} != $_q->{").Append(k).Append("};\n");
          text.Append("    }\n");
        } else {
          text.Append("    return 0 if $_p->{").Append(k).Append("} != $_q->{").Append(k).Append("};\n");
        }
      }
      text.Append("    return 1;\n}");
      return text.ToString();
    }

    private string EmitObjectNew(IrNew objNew) {
      var cls = objNew.Type.ClassType!;
      if (IrExceptionClass.IsException(cls))
        return this.EmitExceptionConstruction(cls, objNew.Arguments);

      var fields = cls.Fields.Select(f => $"{PerlNaming.HashKey(f.Name)} => {this.DefaultValue(f.Type)}");
      var initHash = "{ " + string.Join(", ", fields) + " }";
      var ctorFn = this.FunctionPerlName(objNew.Constructor.Function);
      var args = string.Join(", ", objNew.Arguments.Select(this.EmitExpr).Prepend("$_obj"));
      return $"do {{ my $_obj = {initHash}; {ctorFn}({args}); $_obj }}";
    }

    private string EmitExceptionConstruction(IrClassType cls, IReadOnlyList<IrExpr> arguments) {
      var kind = ExceptionKindName(cls);
      if (arguments.Count == 0) {
        var def = PerlNaming.StringLiteral(IrExceptionClass.DefaultMessage(cls));
        return $"\"{kind}: \" . {def} . \"\\n\"";
      }
      var msg = this.EmitExpr(arguments[0]);
      if (IrExceptionClass.IsLibrary(cls))
        return $"\"{kind}: \" . ({msg} // '') . \"\\n\"";
      return $"{{ kind => '{kind}', message => ({msg} // '') }}";
    }

    private string EmitExceptionValue(IrExpr exception) {
      if (exception is IrNew { Type.ClassType: { } cls } n && IrExceptionClass.IsException(cls))
        return this.EmitExceptionConstruction(cls, n.Arguments);
      return this.EmitExpr(exception);
    }

    private static readonly Dictionary<string, string> _exceptionKinds = new(StringComparer.Ordinal) {
      ["DivideByZeroException"] = "DivideByZero",
      ["OverflowException"] = "Overflow",
      ["IndexOutOfRangeException"] = "IndexOutOfRange",
      ["NullReferenceException"] = "NullReference",
      ["InvalidCastException"] = "InvalidCast",
      ["InvalidOperationException"] = "InvalidOperation",
      ["ArgumentException"] = "ContractViolation",
      ["ArgumentOutOfRangeException"] = "ContractViolation",
      ["KeyNotFoundException"] = "KeyNotFound",
      ["FormatException"] = "InvalidFormat",
      ["AssertionException"] = "Assertion",
      ["ArithmeticException"] = "ArithmeticException",
      ["Exception"] = "User"
    };

    private static string ExceptionKindName(IrClassType cls)
      => _exceptionKinds.GetValueOrDefault(cls.Name, cls.Name);

    private string EmitMethodCall(IrMethodCall call) {
      if (call.Kind is IrMethodCallKind.Virtual or IrMethodCallKind.Interface)
        return this.UnsupportedTypeConstruct(call.Method.Name, call.Kind == IrMethodCallKind.Interface ? "OBJ-INTERFACES" : "OBJ-VIRTUAL");

      var targetFn = this.FunctionPerlName(call.Method.Function);
      var evalArgs = call.Arguments.Select(this.EmitExpr).ToList();
      if (call.Target is null)
        return $"{targetFn}({string.Join(", ", evalArgs)})";

      var receiver = this.EmitExpr(call.Target);
      var allArgs = string.Join(", ", evalArgs.Prepend("$_r"));
      return $"do {{ my $_r = {receiver}; die \"NullReference: null receiver\\n\" unless defined $_r; {targetFn}({allArgs}) }}";
    }

    private string EmitReferenceEquals(IrReferenceEquals refEq) {
      var l = this.EmitExpr(refEq.Left);
      var r = this.EmitExpr(refEq.Right);
      return $"do {{ my ($_l, $_r) = ({l}, {r}); (!defined($_l) && !defined($_r)) || (defined($_l) && defined($_r) && $_l == $_r) ? 1 : 0 }}";
    }

    private string EmitUnwrap(IrUnwrap unwrap) {
      var val = this.EmitExpr(unwrap.Value);
      if (unwrap.IsTested)
        return val;
      return $"do {{ my $_o = {val}; die \"InvalidOperation: missing value\\n\" unless defined $_o; $_o }}";
    }

    private string EmitEnumIntrinsic(IrIntrinsic intrinsic, string[] args) {
      var enumType = (intrinsic.Arguments[0].Type.EnumType ?? intrinsic.Type.EnumType)!;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.EnumHasFlag:
          return $"((({args[0]}) & ({args[1]})) == ({args[1]}) ? 1 : 0)";
        case IrIntrinsicOp.EnumIsDefined: {
          var checks = enumType.Members.Select(m => $"$_v == {FormatIntConstant(enumType.Underlying, m.Value)}");
          return $"do {{ my $_v = {args[0]}; ({string.Join(" || ", checks)}) ? 1 : 0 }}";
        }
        case IrIntrinsicOp.EnumNames:
          return "[" + string.Join(", ", enumType.Ordered.Select(m => PerlNaming.StringLiteral(m.Name))) + "]";
        case IrIntrinsicOp.EnumValues:
          return "[" + string.Join(", ", enumType.Ordered.Select(m => FormatIntConstant(enumType.Underlying, m.Value))) + "]";
        case IrIntrinsicOp.EnumToString: {
          var pairs = enumType.Members.Select(m => $"{FormatIntConstant(enumType.Underlying, m.Value)} => {PerlNaming.StringLiteral(m.Name)}");
          return $"do {{ my $_v = {args[0]}; my %_m = ({string.Join(", ", pairs)}); exists($_m{{$_v}}) ? $_m{{$_v}} : \"$_v\" }}";
        }
        case IrIntrinsicOp.EnumParse: {
          var pairs = enumType.Members.Select(m => $"{PerlNaming.StringLiteral(m.Name)} => {FormatIntConstant(enumType.Underlying, m.Value)}");
          return $"do {{ my $_s = {args[0]}; my %_m = ({string.Join(", ", pairs)}); exists($_m{{$_s}}) ? $_m{{$_s}} : die(\"InvalidFormat: unknown enum member $_s\\n\") }}";
        }
        default:
          return this.UnsupportedOp(intrinsic.Op.ToString());
      }
    }

    private string EmitCollection(IrCollection collection) {
      var args = collection.Arguments.Select(this.EmitExpr).ToArray();
      return collection.Op switch {
        IrCollectionOp.ListNew => "[]",
        IrCollectionOp.ListOf => $"[{string.Join(", ", args)}]",
        IrCollectionOp.ListFromArray => $"[@{{{args[0]}}}]",
        IrCollectionOp.ListCopyToArray => $"[@{{{args[0]}}}]",
        IrCollectionOp.ListCount => $"scalar(@{{{args[0]}}})",
        IrCollectionOp.ListGet => $"do {{ my ($_l, $_i) = ({args[0]}, {args[1]}); die \"ContractViolation: index $_i\\n\" if $_i < 0 || $_i > $#$_l; $_l->[$_i] }}",
        IrCollectionOp.ListSet => $"do {{ my ($_l, $_i, $_v) = ({args[0]}, {args[1]}, {args[2]}); die \"ContractViolation: index $_i\\n\" if $_i < 0 || $_i > $#$_l; $_l->[$_i] = $_v; }}",
        IrCollectionOp.ListAdd => $"push(@{{{args[0]}}}, {args[1]})",
        IrCollectionOp.ListInsert => $"do {{ my ($_l, $_i, $_v) = ({args[0]}, {args[1]}, {args[2]}); die \"ContractViolation: index $_i\\n\" if $_i < 0 || $_i > scalar(@$_l); splice(@$_l, $_i, 0, $_v); }}",
        IrCollectionOp.ListRemoveAt => $"do {{ my ($_l, $_i) = ({args[0]}, {args[1]}); die \"ContractViolation: index $_i\\n\" if $_i < 0 || $_i > $#$_l; splice(@$_l, $_i, 1); }}",
        IrCollectionOp.ListRemoveLast => $"do {{ my $_l = {args[0]}; @$_l ? pop(@$_l) : undef }}",
        IrCollectionOp.ListClear => $"@{{{args[0]}}} = ()",
        IrCollectionOp.ListContains or IrCollectionOp.ListIncludesSameValueZero => this.EmitListSearch(collection.Arguments[0].Type.Element!, args[0], args[1], returnIndex: false),
        IrCollectionOp.ListIndexOfEquals or IrCollectionOp.ListIndexOfStrict => this.EmitListSearch(collection.Arguments[0].Type.Element!, args[0], args[1], returnIndex: true),
        IrCollectionOp.MapNew => "{}",
        IrCollectionOp.MapCount => $"scalar(keys %{{{args[0]}}})",
        IrCollectionOp.MapGet => $"do {{ my ($_m, $_k) = ({args[0]}, {args[1]}); die \"KeyNotFound: missing key\\n\" unless exists $_m->{{$_k}}; $_m->{{$_k}} }}",
        IrCollectionOp.MapTryGet => $"do {{ my ($_m, $_k) = ({args[0]}, {args[1]}); exists $_m->{{$_k}} ? $_m->{{$_k}} : undef }}",
        IrCollectionOp.MapContainsKey => $"(exists(({args[0]})->{{{args[1]}}}) ? 1 : 0)",
        IrCollectionOp.MapPut => $"(({args[0]})->{{{args[1]}}} = {args[2]})",
        IrCollectionOp.MapAdd => $"do {{ my ($_m, $_k, $_v) = ({args[0]}, {args[1]}, {args[2]}); die \"ContractViolation: duplicate key\\n\" if exists $_m->{{$_k}}; $_m->{{$_k}} = $_v; }}",
        IrCollectionOp.MapTryAdd => $"do {{ my ($_m, $_k, $_v) = ({args[0]}, {args[1]}, {args[2]}); exists $_m->{{$_k}} ? 0 : do {{ $_m->{{$_k}} = $_v; 1 }} }}",
        IrCollectionOp.MapRemove => $"do {{ my ($_m, $_k) = ({args[0]}, {args[1]}); exists $_m->{{$_k}} ? do {{ delete $_m->{{$_k}}; 1 }} : 0 }}",
        IrCollectionOp.MapClear => $"%{{{args[0]}}} = ()",
        IrCollectionOp.SetNew => "{}",
        IrCollectionOp.SetCount => $"scalar(keys %{{{args[0]}}})",
        IrCollectionOp.SetAdd => $"do {{ my ($_s, $_k) = ({args[0]}, {args[1]}); exists $_s->{{$_k}} ? 0 : do {{ $_s->{{$_k}} = 1; 1 }} }}",
        IrCollectionOp.SetRemove => $"do {{ my ($_s, $_k) = ({args[0]}, {args[1]}); exists $_s->{{$_k}} ? do {{ delete $_s->{{$_k}}; 1 }} : 0 }}",
        IrCollectionOp.SetContains => $"(exists(({args[0]})->{{{args[1]}}}) ? 1 : 0)",
        IrCollectionOp.SetClear => $"%{{{args[0]}}} = ()",
        _ => this.UnsupportedOp(collection.Op.ToString())
      };
    }

    private string EmitListSearch(IrType element, string list, string value, bool returnIndex) {
      var cmp = element == IrType.String ? "$_l->[$_i] eq $_v" : "$_l->[$_i] == $_v";
      var found = returnIndex ? "$_i" : "1";
      var missing = returnIndex ? "-1" : "0";
      return $"do {{ my ($_l, $_v) = ({list}, {value}); my $_res = {missing}; for my $_i (0 .. $#$_l) {{ if ({cmp}) {{ $_res = {found}; last; }} }} $_res }}";
    }

    private void EmitForEachCollection(IrForEachCollection each, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var col = this.EmitExpr(each.Collection);
      var label = this.NextLabel();
      if (each.Collection.Type.IsList) {
        var elem = this.LocalName(each.Element!);
        text.Append(pad).Append(label).Append(": for my ").Append(elem).Append(" (@{").Append(col).Append("}) {\n");
        this._loops.Push(new(label, null, this._tryFrames.Count));
        this.EmitBlockStatements(each.Body, text, indent + 1);
        this._loops.Pop();
        text.Append(pad).Append("}\n");
        return;
      }

      var mapTmp = this.Temp("_col");
      var keyVar = each.Element is not null ? this.LocalName(each.Element) : this.Temp("_k");
      text.Append(pad).Append("my ").Append(mapTmp).Append(" = ").Append(col).Append(";\n");
      text.Append(pad).Append(label).Append(": for my ").Append(keyVar).Append(" (sort keys %{").Append(mapTmp).Append("}) {\n");
      if (each.Value is not null)
        text.Append(Indent(indent + 1)).Append("my ").Append(this.LocalName(each.Value)).Append(" = ")
          .Append(mapTmp).Append("->{").Append(keyVar).Append("};\n");
      this._loops.Push(new(label, null, this._tryFrames.Count));
      this.EmitBlockStatements(each.Body, text, indent + 1);
      this._loops.Pop();
      text.Append(pad).Append("}\n");
    }

    private string EmitLambda(IrLambda lambda) {
      var savedTry = new Stack<TryFrame>(this._tryFrames.Reverse());
      var savedLoops = new Stack<LoopFrame>(this._loops.Reverse());
      this._tryFrames.Clear();
      this._loops.Clear();

      var text = new StringBuilder();
      text.Append("sub {\n");
      if (lambda.Parameters.Count > 0)
        text.Append("        my (").AppendJoin(", ", lambda.Parameters.Select(this.LocalName)).Append(") = @_;\n");
      this.EmitBlockStatements(lambda.Body, text, 2);
      if (lambda.ReturnType == IrType.Void && !EndsWithReturn(lambda.Body))
        text.Append("        return;\n");
      text.Append("    }");

      foreach (var frame in savedTry) this._tryFrames.Push(frame);
      foreach (var loop in savedLoops) this._loops.Push(loop);
      return text.ToString();
    }

    private string EmitInvoke(IrInvoke invoke) {
      var callee = this.EmitExpr(invoke.Callee);
      var args = string.Join(", ", invoke.Arguments.Select(this.EmitExpr));
      return $"do {{ my $_fn = {callee}; die \"NullReference: null function\\n\" unless defined $_fn; $_fn->({args}) }}";
    }

    private void EmitTry(IrTry @try, StringBuilder text, int indent) {
      var pad = Indent(indent);
      var innerPad = Indent(indent + 1);
      var okVar = this.Temp("_ok");
      var errVar = this.Temp("_err");
      var retFlag = this.Temp("_ret");
      var retVal = this.Temp("_rval");
      var brkFlag = this._loops.Count > 0 ? this.Temp("_brk") : null;
      var cntFlag = this._loops.Count > 0 ? this.Temp("_cnt") : null;

      var usesRet = ContainsReturn(@try.Body) || @try.Catches.Any(c => ContainsReturn(c.Body));
      var usesBrk = brkFlag is not null && (ContainsDirectLoopJump(@try.Body, true) || @try.Catches.Any(c => ContainsDirectLoopJump(c.Body, true)));
      var usesCnt = cntFlag is not null && (ContainsDirectLoopJump(@try.Body, false) || @try.Catches.Any(c => ContainsDirectLoopJump(c.Body, false)));

      if (usesRet)
        text.Append(pad).Append("my (").Append(retFlag).Append(", ").Append(retVal).Append(") = (0, undef);\n");
      if (usesBrk)
        text.Append(pad).Append("my ").Append(brkFlag).Append(" = 0;\n");
      if (usesCnt)
        text.Append(pad).Append("my ").Append(cntFlag).Append(" = 0;\n");

      var frame = new TryFrame(usesRet ? retFlag : null, usesRet ? retVal : null, usesBrk ? brkFlag : null, usesCnt ? cntFlag : null);
      text.Append(pad).Append("my ").Append(okVar).Append(" = eval {\n");
      this._tryFrames.Push(frame);
      this.EmitBlockStatements(@try.Body, text, indent + 1);
      this._tryFrames.Pop();
      text.Append(innerPad).Append("1;\n").Append(pad).Append("};\n");
      text.Append(pad).Append("my ").Append(errVar).Append(" = ").Append(okVar).Append(" ? undef : $@;\n");

      var rethrowVar = this.Temp("_rethrow");
      if (@try.Catches.Count > 0) {
        this.UseHelper(PerlHelpers.TrapMatches);
        text.Append(pad).Append("my ").Append(rethrowVar).Append(" = 0;\n");
        text.Append(pad).Append("if (!").Append(okVar).Append(") {\n");
        text.Append(innerPad).Append("my $_e = ").Append(errVar).Append(";\n");

        var handlerOk = this.Temp("_hok");
        text.Append(innerPad).Append("my ").Append(handlerOk).Append(" = eval {\n");
        this._tryFrames.Push(frame);
        var first = true;
        foreach (var @catch in @try.Catches) {
          var targetKind = @catch.Class == IrExceptionClass.Exception ? "Exception" : ExceptionKindName(@catch.Class);
          var matchExpr = $"{PerlHelpers.TrapMatches}($_e, '{targetKind}')";
          var clausePad = Indent(indent + 2);
          text.Append(clausePad).Append(first ? "if (" : "} elsif (").Append(matchExpr).Append(") {\n");
          if (@catch.Local is not null)
            text.Append(Indent(indent + 3)).Append("my ").Append(this.LocalName(@catch.Local)).Append(" = $_e;\n");
          this.EmitBlockStatements(@catch.Body, text, indent + 3);
          first = false;
        }
        text.Append(Indent(indent + 2)).Append("} else {\n");
        text.Append(Indent(indent + 3)).Append(rethrowVar).Append(" = 1;\n");
        text.Append(Indent(indent + 2)).Append("}\n");
        this._tryFrames.Pop();
        text.Append(Indent(indent + 2)).Append("1;\n");
        text.Append(innerPad).Append("};\n");
        text.Append(innerPad).Append("if (!").Append(handlerOk).Append(") { ").Append(errVar).Append(" = $@; ").Append(rethrowVar).Append(" = 1; }\n");
        text.Append(pad).Append("}\n");
      }

      if (@try.Finally is not null)
        this.EmitBlockStatements(@try.Finally, text, indent);

      if (@try.Catches.Count > 0)
        text.Append(pad).Append("die ").Append(errVar).Append(" if ").Append(rethrowVar).Append(";\n");
      else
        text.Append(pad).Append("die ").Append(errVar).Append(" unless ").Append(okVar).Append(";\n");

      if (usesRet) {
        if (this._tryFrames.Count > 0) {
          var outer = this._tryFrames.Peek();
          text.Append(pad).Append("if (").Append(retFlag).Append(") { ")
            .Append(outer.ReturnValue).Append(" = ").Append(retVal).Append("; ")
            .Append(outer.ReturnFlag).Append(" = 1; return 1; }\n");
        } else {
          var retStmt = this._currentFunction?.ReturnType == IrType.Void ? "return;" : $"return {retVal};";
          text.Append(pad).Append("if (").Append(retFlag).Append(") { ").Append(retStmt).Append(" }\n");
        }
      }
      if (usesBrk) {
        var loop = this._loops.Peek();
        text.Append(pad).Append("last ").Append(loop.Label).Append(" if ").Append(brkFlag).Append(";\n");
      }
      if (usesCnt) {
        var loop = this._loops.Peek();
        var jump = loop.ContinueLabel is not null ? $"last {loop.ContinueLabel}" : $"next {loop.Label}";
        text.Append(pad).Append(jump).Append(" if ").Append(cntFlag).Append(";\n");
      }
    }

    private static bool ContainsReturn(IrBlock block)
      => AllStatements(block).Any(s => s is IrReturn);

    private string UnsupportedTypeConstruct(string construct, string cause) {
      this.Problems.Add(Diagnostic.Create(TypesUnsupported, null, construct) with { Cause = cause });
      return "0";
    }

  }

}
