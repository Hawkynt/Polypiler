using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

internal sealed partial class MsilMethodCompiler {

  private void EmitCollection(IrCollection collection) {
    var args = collection.Arguments;
    var collType = IrCollections.Constructs(collection.Op) ? collection.Type : args[0].Type;

    switch (collection.Op) {
      case IrCollectionOp.ListNew: {
        var spec = this._ctx.GetListSpec(collType.Element!);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{collType.Element}>..ctor()", spec, ".ctor", sig => sig.Parameters(0, r => r.Void(), _ => { })));
        break;
      }

      case IrCollectionOp.ListFromArray: {
        var elem = collType.Element!;
        var arrSlot = this.AllocTemp(args[0].Type);
        var listSlot = this.AllocTemp(collType);
        var idxSlot = this.AllocTemp(IrType.I32);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(arrSlot);

        var notNullLabel = this._il.DefineLabel();
        this._il.LoadLocal(arrSlot);
        this._il.Branch(ILOpCode.Brtrue, notNullLabel);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetExceptionCtor(IrExceptionClass.NullReferenceException, 0));
        this._il.OpCode(ILOpCode.Throw);
        this._il.MarkLabel(notNullLabel);

        var spec = this._ctx.GetListSpec(elem);
        this._il.LoadLocal(arrSlot);
        this._il.OpCode(ILOpCode.Ldlen);
        this._il.OpCode(ILOpCode.Conv_i4);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{elem}>..ctor(int)", spec, ".ctor", sig => sig.Parameters(1, r => r.Void(), p => p.AddParameter().Type().Int32())));
        this._il.StoreLocal(listSlot);

        this._il.LoadConstantI4(0);
        this._il.StoreLocal(idxSlot);
        var condLabel = this._il.DefineLabel();
        var doneLabel = this._il.DefineLabel();
        this._il.MarkLabel(condLabel);
        this._il.LoadLocal(idxSlot);
        this._il.LoadLocal(arrSlot);
        this._il.OpCode(ILOpCode.Ldlen);
        this._il.OpCode(ILOpCode.Conv_i4);
        this._il.Branch(ILOpCode.Bge, doneLabel);

        this._il.LoadLocal(listSlot);
        this._il.LoadLocal(arrSlot);
        this._il.LoadLocal(idxSlot);
        this.EmitLoadElement(elem);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this.GetListAddRef(elem));

        this._il.LoadLocal(idxSlot);
        this._il.LoadConstantI4(1);
        this._il.OpCode(ILOpCode.Add);
        this._il.StoreLocal(idxSlot);
        this._il.Branch(ILOpCode.Br, condLabel);
        this._il.MarkLabel(doneLabel);
        this._il.LoadLocal(listSlot);
        break;
      }

      case IrCollectionOp.ListOf: {
        var elem = collType.Element!;
        var spec = this._ctx.GetListSpec(elem);
        this._il.LoadConstantI4(args.Count);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{elem}>..ctor(int)", spec, ".ctor", sig => sig.Parameters(1, r => r.Void(), p => p.AddParameter().Type().Int32())));
        var addRef = this.GetListAddRef(elem);
        foreach (var item in args) {
          this._il.OpCode(ILOpCode.Dup);
          this.EmitExpr(item);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(addRef);
        }
        break;
      }

      case IrCollectionOp.ListCount:
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this.GetListCountRef(collType.Element!));
        break;

      case IrCollectionOp.ListGet:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this.GetListGetItemRef(collType.Element!));
        break;

      case IrCollectionOp.ListSet:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this.EmitExpr(args[2]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{collType.Element}>::set_Item(int,!0)", this._ctx.GetListSpec(collType.Element!), "set_Item", sig => sig.Parameters(
          2,
          r => r.Void(),
          p => {
            p.AddParameter().Type().Int32();
            p.AddParameter().Type().GenericTypeParameter(0);
          })));
        break;

      case IrCollectionOp.ListAdd:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this.GetListAddRef(collType.Element!));
        break;

      case IrCollectionOp.ListInsert:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this.EmitExpr(args[2]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{collType.Element}>::Insert(int,!0)", this._ctx.GetListSpec(collType.Element!), "Insert", sig => sig.Parameters(
          2,
          r => r.Void(),
          p => {
            p.AddParameter().Type().Int32();
            p.AddParameter().Type().GenericTypeParameter(0);
          })));
        break;

      case IrCollectionOp.ListRemoveAt:
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{collType.Element}>::RemoveAt(int)", this._ctx.GetListSpec(collType.Element!), "RemoveAt", sig => sig.Parameters(
          1,
          r => r.Void(),
          p => p.AddParameter().Type().Int32())));
        break;

      case IrCollectionOp.ListRemoveLast: {
        var elem = collType.Element!;
        var listSlot = this.AllocTemp(collType);
        var countSlot = this.AllocTemp(IrType.I32);
        var itemSlot = this.AllocTemp(elem);
        this.EmitExpr(args[0]);
        this._il.StoreLocal(listSlot);

        this._il.LoadLocal(listSlot);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this.GetListCountRef(elem));
        this._il.StoreLocal(countSlot);

        var emptyLabel = this._il.DefineLabel();
        var endLabel = this._il.DefineLabel();
        this._il.LoadLocal(countSlot);
        this._il.Branch(ILOpCode.Brfalse, emptyLabel);

        this._il.LoadLocal(countSlot);
        this._il.LoadConstantI4(1);
        this._il.OpCode(ILOpCode.Sub);
        this._il.StoreLocal(countSlot);

        this._il.LoadLocal(listSlot);
        this._il.LoadLocal(countSlot);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this.GetListGetItemRef(elem));
        this._il.StoreLocal(itemSlot);

        this._il.LoadLocal(listSlot);
        this._il.LoadLocal(countSlot);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{elem}>::RemoveAt(int)", this._ctx.GetListSpec(elem), "RemoveAt", sig => sig.Parameters(
          1,
          r => r.Void(),
          p => p.AddParameter().Type().Int32())));

        this._il.LoadLocal(itemSlot);
        if (collection.Type.IsOptional) {
          this._il.OpCode(ILOpCode.Newobj);
          this._il.Token(this._ctx.GetNullableCtor(elem));
        }
        this._il.Branch(ILOpCode.Br, endLabel);

        this._il.MarkLabel(emptyLabel);
        this.EmitDefaultValue(collection.Type);
        this._il.MarkLabel(endLabel);
        break;
      }

      case IrCollectionOp.ListClear:
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{collType.Element}>::Clear()", this._ctx.GetListSpec(collType.Element!), "Clear", sig => sig.Parameters(0, r => r.Void(), _ => { })));
        break;

      case IrCollectionOp.ListContains or IrCollectionOp.ListIncludesSameValueZero: {
        var elem = collType.Element!;
        if (collection.Op == IrCollectionOp.ListContains && elem.ClassType is { IsRecord: true }) {
          this.EmitListSearchLoop(collType, args[0], args[1], strict: false, returnBool: true);
        } else {
          this.EmitExpr(args[0]);
          this.EmitExpr(args[1]);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetMemberRef($"inst:List<{elem}>::Contains(!0)", this._ctx.GetListSpec(elem), "Contains", sig => sig.Parameters(
            1,
            r => r.Type().Boolean(),
            p => p.AddParameter().Type().GenericTypeParameter(0))));
        }
        break;
      }

      case IrCollectionOp.ListIndexOfEquals: {
        var elem = collType.Element!;
        if (elem.ClassType is { IsRecord: true }) {
          this.EmitListSearchLoop(collType, args[0], args[1], strict: false, returnBool: false);
        } else {
          this.EmitExpr(args[0]);
          this.EmitExpr(args[1]);
          this._il.OpCode(ILOpCode.Callvirt);
          this._il.Token(this._ctx.GetMemberRef($"inst:List<{elem}>::IndexOf(!0)", this._ctx.GetListSpec(elem), "IndexOf", sig => sig.Parameters(
            1,
            r => r.Type().Int32(),
            p => p.AddParameter().Type().GenericTypeParameter(0))));
        }
        break;
      }

      case IrCollectionOp.ListIndexOfStrict:
        this.EmitListSearchLoop(collType, args[0], args[1], strict: true, returnBool: false);
        break;

      case IrCollectionOp.ListCopyToArray:
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:List<{collType.Element}>::ToArray()", this._ctx.GetListSpec(collType.Element!), "ToArray", sig => sig.Parameters(
          0,
          r => r.Type().SZArray().GenericTypeParameter(0),
          _ => { })));
        break;

      case IrCollectionOp.MapNew: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>..ctor()", spec, ".ctor", sig => sig.Parameters(0, r => r.Void(), _ => { })));
        break;
      }

      case IrCollectionOp.MapCount: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::get_Count()", spec, "get_Count", sig => sig.Parameters(0, r => r.Type().Int32(), _ => { })));
        break;
      }

      case IrCollectionOp.MapGet: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::get_Item(!0)", spec, "get_Item", sig => sig.Parameters(
          1,
          r => r.Type().GenericTypeParameter(1),
          p => p.AddParameter().Type().GenericTypeParameter(0))));
        break;
      }

      case IrCollectionOp.MapTryGet: {
        var key = collType.Key!;
        var val = collType.Element!;
        var spec = this._ctx.GetDictionarySpec(key, val);
        var valSlot = this.AllocTemp(val);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.LoadLocalAddress(valSlot);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{key},{val}>::TryGetValue(!0,!1&)", spec, "TryGetValue", sig => sig.Parameters(
          2,
          r => r.Type().Boolean(),
          p => {
            p.AddParameter().Type().GenericTypeParameter(0);
            p.AddParameter().Type(isByRef: true).GenericTypeParameter(1);
          })));

        var missLabel = this._il.DefineLabel();
        var endLabel = this._il.DefineLabel();
        this._il.Branch(ILOpCode.Brfalse, missLabel);
        this._il.LoadLocal(valSlot);
        if (collection.Type.IsOptional) {
          this._il.OpCode(ILOpCode.Newobj);
          this._il.Token(this._ctx.GetNullableCtor(val));
        }
        this._il.Branch(ILOpCode.Br, endLabel);
        this._il.MarkLabel(missLabel);
        this.EmitDefaultValue(collection.Type);
        this._il.MarkLabel(endLabel);
        break;
      }

      case IrCollectionOp.MapContainsKey: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::ContainsKey(!0)", spec, "ContainsKey", sig => sig.Parameters(
          1,
          r => r.Type().Boolean(),
          p => p.AddParameter().Type().GenericTypeParameter(0))));
        break;
      }

      case IrCollectionOp.MapPut: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this.EmitExpr(args[2]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::set_Item(!0,!1)", spec, "set_Item", sig => sig.Parameters(
          2,
          r => r.Void(),
          p => {
            p.AddParameter().Type().GenericTypeParameter(0);
            p.AddParameter().Type().GenericTypeParameter(1);
          })));
        break;
      }

      case IrCollectionOp.MapAdd: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this.EmitExpr(args[2]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::Add(!0,!1)", spec, "Add", sig => sig.Parameters(
          2,
          r => r.Void(),
          p => {
            p.AddParameter().Type().GenericTypeParameter(0);
            p.AddParameter().Type().GenericTypeParameter(1);
          })));
        break;
      }

      case IrCollectionOp.MapTryAdd: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this.EmitExpr(args[2]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::TryAdd(!0,!1)", spec, "TryAdd", sig => sig.Parameters(
          2,
          r => r.Type().Boolean(),
          p => {
            p.AddParameter().Type().GenericTypeParameter(0);
            p.AddParameter().Type().GenericTypeParameter(1);
          })));
        break;
      }

      case IrCollectionOp.MapRemove: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::Remove(!0)", spec, "Remove", sig => sig.Parameters(
          1,
          r => r.Type().Boolean(),
          p => p.AddParameter().Type().GenericTypeParameter(0))));
        break;
      }

      case IrCollectionOp.MapClear: {
        var spec = this._ctx.GetDictionarySpec(collType.Key!, collType.Element!);
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:Dict<{collType.Key},{collType.Element}>::Clear()", spec, "Clear", sig => sig.Parameters(0, r => r.Void(), _ => { })));
        break;
      }

      case IrCollectionOp.SetNew: {
        var spec = this._ctx.GetHashSetSpec(collType.Element!);
        this._il.OpCode(ILOpCode.Newobj);
        this._il.Token(this._ctx.GetMemberRef($"inst:HashSet<{collType.Element}>..ctor()", spec, ".ctor", sig => sig.Parameters(0, r => r.Void(), _ => { })));
        break;
      }

      case IrCollectionOp.SetCount: {
        var spec = this._ctx.GetHashSetSpec(collType.Element!);
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:HashSet<{collType.Element}>::get_Count()", spec, "get_Count", sig => sig.Parameters(0, r => r.Type().Int32(), _ => { })));
        break;
      }

      case IrCollectionOp.SetAdd: {
        var spec = this._ctx.GetHashSetSpec(collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:HashSet<{collType.Element}>::Add(!0)", spec, "Add", sig => sig.Parameters(
          1,
          r => r.Type().Boolean(),
          p => p.AddParameter().Type().GenericTypeParameter(0))));
        break;
      }

      case IrCollectionOp.SetRemove: {
        var spec = this._ctx.GetHashSetSpec(collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:HashSet<{collType.Element}>::Remove(!0)", spec, "Remove", sig => sig.Parameters(
          1,
          r => r.Type().Boolean(),
          p => p.AddParameter().Type().GenericTypeParameter(0))));
        break;
      }

      case IrCollectionOp.SetContains: {
        var spec = this._ctx.GetHashSetSpec(collType.Element!);
        this.EmitExpr(args[0]);
        this.EmitExpr(args[1]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:HashSet<{collType.Element}>::Contains(!0)", spec, "Contains", sig => sig.Parameters(
          1,
          r => r.Type().Boolean(),
          p => p.AddParameter().Type().GenericTypeParameter(0))));
        break;
      }

      case IrCollectionOp.SetClear: {
        var spec = this._ctx.GetHashSetSpec(collType.Element!);
        this.EmitExpr(args[0]);
        this._il.OpCode(ILOpCode.Callvirt);
        this._il.Token(this._ctx.GetMemberRef($"inst:HashSet<{collType.Element}>::Clear()", spec, "Clear", sig => sig.Parameters(0, r => r.Void(), _ => { })));
        break;
      }
    }
  }

  private void EmitListSearchLoop(IrType listType, IrExpr listExpr, IrExpr targetExpr, bool strict, bool returnBool) {
    var elem = listType.Element!;
    var listSlot = this.AllocTemp(listType);
    var targetSlot = this.AllocTemp(elem);
    var idxSlot = this.AllocTemp(IrType.I32);
    var itemSlot = this.AllocTemp(elem);
    this.EmitExpr(listExpr);
    this._il.StoreLocal(listSlot);
    this.EmitExpr(targetExpr);
    this._il.StoreLocal(targetSlot);
    this._il.LoadConstantI4(0);
    this._il.StoreLocal(idxSlot);

    var condLabel = this._il.DefineLabel();
    var foundLabel = this._il.DefineLabel();
    var notFoundLabel = this._il.DefineLabel();
    var endLabel = this._il.DefineLabel();

    this._il.MarkLabel(condLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocal(listSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListCountRef(elem));
    this._il.Branch(ILOpCode.Bge, notFoundLabel);

    this._il.LoadLocal(listSlot);
    this._il.LoadLocal(idxSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListGetItemRef(elem));
    this._il.StoreLocal(itemSlot);

    if (strict) {
      if (elem.IsString) {
        this._il.LoadLocal(itemSlot);
        this._il.LoadLocal(targetSlot);
        this._il.LoadConstantI4(4); // StringComparison.Ordinal
        this._il.Call(this.GetStringComparisonStaticRef("Equals", IrType.Bool));
        this._il.Branch(ILOpCode.Brtrue, foundLabel);
      } else if (elem.IsStruct) {
        var nextLabel = this._il.DefineLabel();
        this.EmitStructFieldsEqual(elem.StructType!, () => this._il.LoadLocalAddress(itemSlot), () => this._il.LoadLocalAddress(targetSlot), nextLabel);
        this._il.Branch(ILOpCode.Br, foundLabel);
        this._il.MarkLabel(nextLabel);
      } else {
        this._il.LoadLocal(itemSlot);
        this._il.LoadLocal(targetSlot);
        this._il.OpCode(ILOpCode.Ceq);
        this._il.Branch(ILOpCode.Brtrue, foundLabel);
      }
    } else {
      this._il.LoadLocal(itemSlot);
      this._il.LoadLocal(targetSlot);
      this._il.Call(this._ctx.RecordEqualsHandles[elem.ClassType!]);
      this._il.Branch(ILOpCode.Brtrue, foundLabel);
    }

    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(1);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(idxSlot);
    this._il.Branch(ILOpCode.Br, condLabel);

    this._il.MarkLabel(foundLabel);
    if (returnBool)
      this._il.LoadConstantI4(1);
    else
      this._il.LoadLocal(idxSlot);
    this._il.Branch(ILOpCode.Br, endLabel);

    this._il.MarkLabel(notFoundLabel);
    this._il.LoadConstantI4(returnBool ? 0 : -1);
    this._il.MarkLabel(endLabel);
  }

  private void EmitForEachCollection(IrForEachCollection loop) {
    var collType = loop.Collection.Type;
    if (collType.Kind == IrTypeKind.List && loop.Mode == EnumerationMode.Live) {
      this.EmitForEachListLive(loop);
      return;
    }

    // Use IEnumerable<E>.GetEnumerator() / IEnumerator<E> / try-finally Dispose().
    string elemKey;
    Action<SignatureTypeEncoder> encodeElem;
    if (collType.Kind == IrTypeKind.Map) {
      var k = collType.Key!;
      var v = collType.Element!;
      elemKey = $"KVP<{k},{v}>";
      encodeElem = enc => {
        var args = enc.GenericInstantiation(this._ctx.GetBclType("System.Collections.Generic", "KeyValuePair`2"), 2, isValueType: true);
        this._ctx.EncodeType(args.AddArgument(), k);
        this._ctx.EncodeType(args.AddArgument(), v);
      };
    } else {
      var e = collType.Element!;
      elemKey = e.ToString();
      encodeElem = enc => this._ctx.EncodeType(enc, e);
    }

    var enumerableSpec = this._ctx.GetIEnumerableSpec(elemKey, encodeElem);
    var enumeratorSpec = this._ctx.GetIEnumeratorSpec(elemKey, encodeElem);
    var enumSlot = this.AllocCustomTemp(enc => {
      var args = enc.GenericInstantiation(this._ctx.GetBclType("System.Collections.Generic", "IEnumerator`1"), 1, isValueType: false);
      encodeElem(args.AddArgument());
    });

    this.EmitExpr(loop.Collection);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this._ctx.GetMemberRef(
      $"inst:IEnumerable<{elemKey}>::GetEnumerator()",
      enumerableSpec,
      "GetEnumerator",
      sig => sig.Parameters(
        0,
        r => r.Type().GenericInstantiation(this._ctx.GetBclType("System.Collections.Generic", "IEnumerator`1"), 1, isValueType: false).AddArgument().GenericTypeParameter(0),
        _ => { })));
    this._il.StoreLocal(enumSlot);

    var tryStart = this._il.DefineLabel();
    var condLabel = this._il.DefineLabel();
    var leaveTryLabel = this._il.DefineLabel();
    var breakLabel = this._il.DefineLabel();
    var finallyStart = this._il.DefineLabel();
    var finallyEnd = this._il.DefineLabel();
    var skipDispose = this._il.DefineLabel();

    this._il.MarkLabel(tryStart);
    this._tryDepth++;
    this._loops.Push(new LoopTarget(leaveTryLabel, condLabel, this._tryDepth));

    this._il.MarkLabel(condLabel);
    this._il.LoadLocal(enumSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this._ctx.GetMemberRef(
      "inst:System.Collections.IEnumerator::MoveNext()",
      this._ctx.GetBclType("System.Collections", "IEnumerator"),
      "MoveNext",
      sig => sig.Parameters(0, r => r.Type().Boolean(), _ => { })));
    this._il.Branch(ILOpCode.Brfalse, leaveTryLabel);

    this._il.LoadLocal(enumSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this._ctx.GetMemberRef(
      $"inst:IEnumerator<{elemKey}>::get_Current()",
      enumeratorSpec,
      "get_Current",
      sig => sig.Parameters(0, r => r.Type().GenericTypeParameter(0), _ => { })));

    if (collType.Kind == IrTypeKind.Map) {
      var kvpSpec = this._ctx.GetKeyValuePairSpec(collType.Key!, collType.Element!);
      var kvpSlot = this.AllocCustomTemp(encodeElem);
      this._il.StoreLocal(kvpSlot);
      if (loop.Element is not null) {
        this.EnsureCapturedCell(loop.Element);
        this._il.LoadLocalAddress(kvpSlot);
        this._il.Call(this._ctx.GetMemberRef($"inst:KVP<{collType.Key},{collType.Element}>::get_Key()", kvpSpec, "get_Key", sig => sig.Parameters(0, r => r.Type().GenericTypeParameter(0), _ => { })));
        this.EmitStoreLocalOrParam(loop.Element);
      }
      if (loop.Value is not null) {
        this.EnsureCapturedCell(loop.Value);
        this._il.LoadLocalAddress(kvpSlot);
        this._il.Call(this._ctx.GetMemberRef($"inst:KVP<{collType.Key},{collType.Element}>::get_Value()", kvpSpec, "get_Value", sig => sig.Parameters(0, r => r.Type().GenericTypeParameter(1), _ => { })));
        this.EmitStoreLocalOrParam(loop.Value);
      }
    } else {
      var valSlot = this.AllocTemp(collType.Element!);
      this._il.StoreLocal(valSlot);
      if (loop.Element is not null) {
        this.EnsureCapturedCell(loop.Element);
        this._il.LoadLocal(valSlot);
        this.EmitStoreLocalOrParam(loop.Element);
      }
    }

    this.EmitStmt(loop.Body);
    this._il.Branch(ILOpCode.Br, condLabel);

    this._loops.Pop();
    this._il.MarkLabel(leaveTryLabel);
    this._il.Branch(ILOpCode.Leave, breakLabel);
    this._tryDepth--;

    this._il.MarkLabel(finallyStart);
    this._il.LoadLocal(enumSlot);
    this._il.Branch(ILOpCode.Brfalse, skipDispose);
    this._il.LoadLocal(enumSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this._ctx.GetInstanceMethodRef("System", "IDisposable", "Dispose", IrType.Void));
    this._il.MarkLabel(skipDispose);
    this._il.OpCode(ILOpCode.Endfinally);
    this._il.MarkLabel(finallyEnd);

    this._flow.AddFinallyRegion(tryStart, finallyStart, finallyStart, finallyEnd);
    this._il.MarkLabel(breakLabel);
  }

  private void EmitForEachListLive(IrForEachCollection loop) {
    var listType = loop.Collection.Type;
    var elem = listType.Element!;
    var listSlot = this.AllocTemp(listType);
    var idxSlot = this.AllocTemp(IrType.I32);

    this.EmitExpr(loop.Collection);
    this._il.StoreLocal(listSlot);
    this._il.LoadConstantI4(0);
    this._il.StoreLocal(idxSlot);

    var condLabel = this._il.DefineLabel();
    var stepLabel = this._il.DefineLabel();
    var breakLabel = this._il.DefineLabel();

    this._il.MarkLabel(condLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadLocal(listSlot);
    this._il.OpCode(ILOpCode.Callvirt);
    this._il.Token(this.GetListCountRef(elem));
    this._il.Branch(ILOpCode.Bge, breakLabel);

    if (loop.Element is not null) {
      this.EnsureCapturedCell(loop.Element);
      this._il.LoadLocal(listSlot);
      this._il.LoadLocal(idxSlot);
      this._il.OpCode(ILOpCode.Callvirt);
      this._il.Token(this.GetListGetItemRef(elem));
      this.EmitStoreLocalOrParam(loop.Element);
    }

    this._loops.Push(new LoopTarget(breakLabel, stepLabel, this._tryDepth));
    this.EmitStmt(loop.Body);
    this._loops.Pop();

    this._il.MarkLabel(stepLabel);
    this._il.LoadLocal(idxSlot);
    this._il.LoadConstantI4(1);
    this._il.OpCode(ILOpCode.Add);
    this._il.StoreLocal(idxSlot);
    this._il.Branch(ILOpCode.Br, condLabel);
    this._il.MarkLabel(breakLabel);
  }

  private MemberReferenceHandle GetListAddRef(IrType element)
    => this._ctx.GetMemberRef($"inst:List<{element}>::Add(!0)", this._ctx.GetListSpec(element), "Add", sig => sig.Parameters(
      1,
      r => r.Void(),
      p => p.AddParameter().Type().GenericTypeParameter(0)));

  private MemberReferenceHandle GetListCountRef(IrType element)
    => this._ctx.GetMemberRef($"inst:List<{element}>::get_Count()", this._ctx.GetListSpec(element), "get_Count", sig => sig.Parameters(
      0,
      r => r.Type().Int32(),
      _ => { }));

  private MemberReferenceHandle GetListGetItemRef(IrType element)
    => this._ctx.GetMemberRef($"inst:List<{element}>::get_Item(int)", this._ctx.GetListSpec(element), "get_Item", sig => sig.Parameters(
      1,
      r => r.Type().GenericTypeParameter(0),
      p => p.AddParameter().Type().Int32()));

}
