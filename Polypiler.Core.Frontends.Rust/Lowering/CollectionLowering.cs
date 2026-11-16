using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>The value of a key of a map as a place: <c>*map.entry(k).or_insert(v)</c>, read and written by the key.</summary>
internal sealed record MapEntryPlace(IrExpr Map, IrExpr Key) : Place(Map.Type.Element!);

/// <summary>
/// The maps and sets of the standard library onto the IR's: a Rust map is moved, never shared, so
/// the IR's map, which is a reference, holds it exactly; a key or an element passed by reference is
/// the value, as every shared reference is.
/// </summary>
internal sealed partial class FunctionLowering {

  private static IrExpr Collection(IrCollectionOp op, IrType type, params IrExpr[] operands) => new IrCollection(op, type, operands);

  private IrExpr? CollectionMethod(MethodCallExpr call, string name) {
    // The receiver of or_insert is the entry, which names the map and the key.
    if (name is "or_insert" or "or_default")
      return this.Read(this.Entry(call));

    var collection = this.Stable(this.Value(call.Receiver), "collection");
    var map = collection.Type.Kind == IrTypeKind.Map;
    switch (name) {
      case "len":
        return Cast(Collection(map ? IrCollectionOp.MapCount : IrCollectionOp.SetCount, IrType.I32, collection), IrType.U64);
      case "is_empty":
        return Equal(Collection(map ? IrCollectionOp.MapCount : IrCollectionOp.SetCount, IrType.I32, collection), new IrConst(IrType.I32, 0));
      case "clear":
        this.Emit(new IrExprStmt(Collection(map ? IrCollectionOp.MapClear : IrCollectionOp.SetClear, IrType.Void, collection)));
        return null;
    }

    var key = this.Stable(this.Value(call.Arguments[0]), "key");
    if (!map) {
      var op = name switch { "insert" => IrCollectionOp.SetAdd, "remove" => IrCollectionOp.SetRemove, _ => IrCollectionOp.SetContains };
      return Collection(op, IrType.Bool, collection, key);
    }

    var optional = IrType.Optional(collection.Type.Element!);
    switch (name) {
      case "insert": {
        // insert returns the value the key had: read it before the key is overwritten.
        var value = this.Stable(this.Value(call.Arguments[1]), "value");
        var old = this.Temporary(Collection(IrCollectionOp.MapTryGet, optional, collection, key), "old");
        this.Emit(new IrExprStmt(Collection(IrCollectionOp.MapPut, IrType.Void, collection, key, value)));
        return old;
      }
      case "remove": {
        var old = this.Temporary(Collection(IrCollectionOp.MapTryGet, optional, collection, key), "old");
        this.Emit(new IrExprStmt(Collection(IrCollectionOp.MapRemove, IrType.Bool, collection, key)));
        return old;
      }
      case "get":
        return Collection(IrCollectionOp.MapTryGet, optional, collection, key);
      default:
        // contains_key.
        return Collection(IrCollectionOp.MapContainsKey, IrType.Bool, collection, key);
    }
  }

  /// <summary>
  /// <c>map.entry(k).or_insert(v)</c>: the key and the value evaluated, as the arguments are, then the
  /// value inserted where the key is absent; the place is the key's value.
  /// </summary>
  private Place Entry(MethodCallExpr call) {
    var entry = (MethodCallExpr)call.Receiver;
    var map = this.Stable(this.Value(entry.Receiver), "map");
    var key = this.Stable(this.Value(entry.Arguments[0]), "key");
    var value = call.Name == "or_insert" ? this.Stable(this.Value(call.Arguments[0]), "value") : this.Default(this._typed.TypeOf(call).Dereferenced);
    var absent = Not(Collection(IrCollectionOp.MapContainsKey, IrType.Bool, map, key));
    this.Emit(new IrIf(absent, new IrBlock([new IrExprStmt(Collection(IrCollectionOp.MapPut, IrType.Void, map, key, value))]), null));
    return new MapEntryPlace(map, key);
  }

  /// <summary><c>map[&amp;k]</c>: the key's value; an absent key panics as Rust's indexing does.</summary>
  private IrExpr MapIndex(IndexExpr index) {
    var map = this.Stable(this.Value(index.Target), "map");
    return Collection(IrCollectionOp.MapGet, map.Type.Element!, map, this.Value(index.Index));
  }

}
