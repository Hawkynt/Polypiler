using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// The operations of Python's <c>dict</c> and <c>set</c> that no single IR operation computes, each a
/// private function of the module made once per type of its collection: displays, the lookups that
/// raise <c>KeyError</c>, <c>pop</c>, <c>get</c>, copies, the iterators of a dict that raise
/// <c>RuntimeError</c> once the dict changed its size, and <c>==</c>. A dict is the IR's map in the
/// order of insertion (CPython keeps that order since 3.7, the language guarantees it); a key and an
/// element of a set are held as a field holds them, so an <c>int</c> one is an <c>i64</c>. Written from
/// the Python Library Reference ("Mapping Types - dict", "Set Types - set, frozenset"), with CPython as
/// the oracle the tests compare against.
/// </summary>
internal sealed partial class PythonHelpers {

  private const string ChangedSize = "dictionary changed size during iteration";

  private static IrExpr MapCount(IrExpr map) => new IrCollection(IrCollectionOp.MapCount, IrType.I32, [map]);

  private static IrExpr Has(IrExpr map, IrExpr key) => new IrCollection(IrCollectionOp.MapContainsKey, IrType.Bool, [map, key]);

  private static IrExpr Lookup(IrExpr map, IrExpr key) => new IrCollection(IrCollectionOp.MapGet, map.Type.Element!, [map, key]);

  private static IrStmt Put(IrExpr map, IrExpr key, IrExpr value) => new IrExprStmt(new IrCollection(IrCollectionOp.MapPut, IrType.Void, [map, key, value]));

  private static IrStmt Insert(IrExpr set, IrExpr element) => new IrExprStmt(new IrCollection(IrCollectionOp.SetAdd, IrType.Bool, [set, element]));

  private static IrStmt RaiseKeyError() => Raise(IrExceptionClass.KeyNotFoundException, "key not found");

  /// <summary><c>{k0: v0, k1: v1, …}</c>: a new dict, the pairs inserted in order; a key present again keeps its place and takes the later value.</summary>
  public IrCall DictOf(IReadOnlyList<IrExpr> pairs, IrType mapType)
    => this.Call($"PyDictOf{pairs.Count / 2}_{Name(mapType)}", mapType, pairs, name => {
      var parameters = pairs.Select((_, index) => Local((index % 2 == 0 ? "k" : "v") + index / 2, index % 2 == 0 ? mapType.Key! : mapType.Element!)).ToList();
      var map = Local("map", mapType);
      var puts = Enumerable.Range(0, pairs.Count / 2).Select(index => Put(Ref(map), Ref(parameters[2 * index]), Ref(parameters[2 * index + 1])));
      return Function(name, mapType, parameters, [map], [new IrDeclare(map, new IrCollection(IrCollectionOp.MapNew, mapType, [])), .. puts, Return(Ref(map))]);
    });

  /// <summary><c>{e0, e1, …}</c>: a new set, the elements inserted in order; an element present again is not inserted.</summary>
  public IrCall SetOf(IReadOnlyList<IrExpr> elements, IrType setType)
    => this.Call($"PySetOf{elements.Count}_{Name(setType)}", setType, elements, name => {
      var parameters = elements.Select((_, index) => Local("e" + index, setType.Element!)).ToList();
      var set = Local("set", setType);
      return Function(name, setType, parameters, [set], [new IrDeclare(set, new IrCollection(IrCollectionOp.SetNew, setType, [])), .. parameters.Select(parameter => Insert(Ref(set), Ref(parameter))), Return(Ref(set))]);
    });

  /// <summary><c>set(it)</c>: a new set of the elements of a sequence, in the order they come, each as the set holds it.</summary>
  public IrCall SetFrom(IrExpr sequence, IrType setType)
    => this.Call($"PySetFrom_{Name(sequence.Type)}_{Name(setType)}", setType, [sequence], name => {
      var (source, set, item) = (Local("source", sequence.Type), Local("set", setType), Local("item", sequence.Type.Element!));
      return Function(name, setType, [source], [set, item],
        new IrDeclare(set, new IrCollection(IrCollectionOp.SetNew, setType, [])),
        new IrForEachSequence(item, Ref(source), Block(Insert(Ref(set), Fit(Ref(item), setType.Element!)))),
        Return(Ref(set)));
    });

  /// <summary><c>d.copy()</c> and <c>dict(d)</c>: a new dict of the same pairs in the same order.</summary>
  public IrCall DictCopy(IrExpr map, IrType mapType)
    => this.Call($"PyDictCopy_{Name(mapType)}", mapType, [map], name => {
      var (source, copy, key, value) = (Local("source", mapType), Local("copy", mapType), Local("key", mapType.Key!), Local("value", mapType.Element!));
      return Function(name, mapType, [source], [copy, key, value],
        new IrDeclare(copy, new IrCollection(IrCollectionOp.MapNew, mapType, [])),
        new IrForEachCollection(Ref(source), EnumerationMode.Live, key, value, Block(Put(Ref(copy), Ref(key), Ref(value)))),
        Return(Ref(copy)));
    });

  /// <summary><c>s.copy()</c> and <c>set(s)</c>: a new set of the same elements.</summary>
  public IrCall SetCopy(IrExpr set, IrType setType)
    => this.Call($"PySetCopy_{Name(setType)}", setType, [set], name => {
      var (source, copy, element) = (Local("source", setType), Local("copy", setType), Local("element", setType.Element!));
      return Function(name, setType, [source], [copy, element],
        new IrDeclare(copy, new IrCollection(IrCollectionOp.SetNew, setType, [])),
        new IrForEachCollection(Ref(source), EnumerationMode.Live, element, null, Block(Insert(Ref(copy), Ref(element)))),
        Return(Ref(copy)));
    });

  /// <summary><c>d.get(k)</c>: the value of a key as an optional, <c>None</c> where the key is absent.</summary>
  public IrCall DictGet(IrExpr map, IrExpr key, IrType mapType, IrType optional)
    => this.Call($"PyDictGet_{Name(mapType)}", optional, [map, key], name => {
      var (m, k) = (Local("map", mapType), Local("key", mapType.Key!));
      var found = Lookup(Ref(m), Ref(k));
      return Function(name, optional, [m, k], [],
        If(Not(Has(Ref(m), Ref(k))), Return(optional.IsOptional ? new IrNone(optional) : new IrNull(optional))),
        Return(optional.IsOptional ? new IrSome(Fit(found, optional.Underlying!)) : found));
    });

  /// <summary><c>d.get(k, default)</c>: the value of a key, or the default, evaluated before the call, where it is absent.</summary>
  public IrCall DictGetOr(IrExpr map, IrExpr key, IrExpr fallback, IrType mapType)
    => this.Call($"PyDictGetOr_{Name(mapType)}", mapType.Element!, [map, key, fallback], name => {
      var (m, k, d) = (Local("map", mapType), Local("key", mapType.Key!), Local("fallback", mapType.Element!));
      return Function(name, mapType.Element!, [m, k, d], [],
        If(Has(Ref(m), Ref(k)), Return(Lookup(Ref(m), Ref(k)))),
        Return(Ref(d)));
    });

  /// <summary><c>d.pop(k)</c>: the value of a key, which is removed; <c>KeyError</c> where it is absent.</summary>
  public IrCall DictPop(IrExpr map, IrExpr key, IrType mapType)
    => this.Call($"PyDictPop_{Name(mapType)}", mapType.Element!, [map, key], name => {
      var (m, k, v) = (Local("map", mapType), Local("key", mapType.Key!), Local("value", mapType.Element!));
      return Function(name, mapType.Element!, [m, k], [v],
        If(Not(Has(Ref(m), Ref(k))), RaiseKeyError()),
        new IrDeclare(v, Lookup(Ref(m), Ref(k))),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [Ref(m), Ref(k)])),
        Return(Ref(v)));
    });

  /// <summary><c>d.pop(k, default)</c>: the value of a key, which is removed, or the default where it is absent.</summary>
  public IrCall DictPopOr(IrExpr map, IrExpr key, IrExpr fallback, IrType mapType)
    => this.Call($"PyDictPopOr_{Name(mapType)}", mapType.Element!, [map, key, fallback], name => {
      var (m, k, d, v) = (Local("map", mapType), Local("key", mapType.Key!), Local("fallback", mapType.Element!), Local("value", mapType.Element!));
      return Function(name, mapType.Element!, [m, k, d], [v],
        If(Not(Has(Ref(m), Ref(k))), Return(Ref(d))),
        new IrDeclare(v, Lookup(Ref(m), Ref(k))),
        new IrExprStmt(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [Ref(m), Ref(k)])),
        Return(Ref(v)));
    });

  /// <summary><c>del d[k]</c>: the key removed; <c>KeyError</c> where it is absent.</summary>
  public IrCall DictDelete(IrExpr map, IrExpr key, IrType mapType)
    => this.Call($"PyDictDelete_{Name(mapType)}", IrType.Void, [map, key], name => {
      var (m, k) = (Local("map", mapType), Local("key", mapType.Key!));
      return Function(name, IrType.Void, [m, k], [],
        If(Not(new IrCollection(IrCollectionOp.MapRemove, IrType.Bool, [Ref(m), Ref(k)])), RaiseKeyError()));
    });

  /// <summary><c>s.remove(x)</c>: the element removed; <c>KeyError</c> where it is absent.</summary>
  public IrCall SetRemove(IrExpr set, IrExpr element, IrType setType)
    => this.Call($"PySetRemove_{Name(setType)}", IrType.Void, [set, element], name => {
      var (s, e) = (Local("set", setType), Local("element", setType.Element!));
      return Function(name, IrType.Void, [s, e], [],
        If(Not(new IrCollection(IrCollectionOp.SetRemove, IrType.Bool, [Ref(s), Ref(e)])), RaiseKeyError()));
    });

  /// <summary>
  /// What a <c>for</c> over a dict, its <c>keys()</c>, <c>values()</c> or <c>items()</c> takes, as a
  /// sequence, as CPython's iterator walks it: before every step after the first and after the last
  /// one, a size other than the one the iterator was made with raises <c>RuntimeError</c>; so does an
  /// entry found after as many as that size were given - a key removed and inserted again, which the
  /// walk meets a second time at the end. <paramref name="size"/> is the size when the iterator is made,
  /// or -1 for the size at its first step. <paramref name="part"/> is <c>keys</c>, <c>values</c> or
  /// <c>items</c>; <paramref name="element"/> the type the sequence gives, which <paramref name="make"/>
  /// makes of the key and the value read from the dict.
  /// </summary>
  public IrCall DictIterator(IrExpr map, IrExpr size, IrType mapType, string part, IrType element, Func<IrExpr, IrExpr, IrExpr> make)
    => this.Call($"PyDict_{part}_{Name(mapType)}", IrType.Sequence(element, false), [map, size], name => {
      var (m, n, given, key, value) = (Local("map", mapType), Local("size", IrType.I32), Local("given", IrType.I32), Local("key", mapType.Key!), Local("value", mapType.Element!));
      var changed = Binary(IrBinaryOp.NotEqual, MapCount(Ref(m)), Ref(n));
      return Function(name, IrType.Sequence(element, false), [m, n], [given, key, value],
        If(Less(Ref(n), Int32(0)), Assign(n, MapCount(Ref(m)))),
        new IrDeclare(given, Int32(0)),
        new IrForEachCollection(Ref(m), EnumerationMode.Live, key, value, Block(
          If(changed, Raise(IrExceptionClass.InvalidOperationException, ChangedSize)),
          If(Equal(Ref(given), Ref(n)), Raise(IrExceptionClass.InvalidOperationException, "dictionary keys changed during iteration")),
          Assign(given, Plus(Ref(given), Int32(1))),
          new IrYield(make(Ref(key), Ref(value))))),
        If(changed, Raise(IrExceptionClass.InvalidOperationException, ChangedSize)));
    });

  /// <summary>
  /// The elements of a set as a sequence, in the set's order: only for an operation whose result no
  /// order can change - <c>sorted</c>, <c>sum</c> of integers, <c>min</c>, <c>max</c>, <c>any</c>, <c>all</c> -
  /// since Python leaves the order of a set to the hash.
  /// </summary>
  public IrCall SetElements(IrExpr set, IrType setType, IrType element)
    => this.Call($"PySetElements_{Name(setType)}", IrType.Sequence(element, false), [set], name => {
      var (s, e) = (Local("set", setType), Local("element", setType.Element!));
      return Function(name, IrType.Sequence(element, false), [s], [e],
        new IrForEachCollection(Ref(s), EnumerationMode.Live, e, null, Block(new IrYield(Fit(Ref(e), element)))));
    });

  /// <summary><c>a == b</c> of two dicts: the same keys, each with values <c>==</c> to each other; the order does not count.</summary>
  public IrCall DictEqual(IrExpr left, IrExpr right, IrType mapType, Func<IrExpr, IrExpr, IrExpr> equal)
    => this.Call($"PyDictEqual_{Name(mapType)}", IrType.Bool, [left, right], name => {
      var (a, b, key, value) = (Local("a", mapType), Local("b", mapType), Local("key", mapType.Key!), Local("value", mapType.Element!));
      return Function(name, IrType.Bool, [a, b], [key, value],
        If(Binary(IrBinaryOp.NotEqual, MapCount(Ref(a)), MapCount(Ref(b))), Return(Bool(false))),
        new IrForEachCollection(Ref(a), EnumerationMode.Live, key, value, Block(
          If(Not(Has(Ref(b), Ref(key))), Return(Bool(false))),
          If(Not(equal(Ref(value), Lookup(Ref(b), Ref(key)))), Return(Bool(false))))),
        Return(Bool(true)));
    });

  /// <summary><c>a == b</c> of two sets: the same elements.</summary>
  public IrCall SetEqual(IrExpr left, IrExpr right, IrType setType)
    => this.Call($"PySetEqual_{Name(setType)}", IrType.Bool, [left, right], name => {
      var (a, b, element) = (Local("a", setType), Local("b", setType), Local("element", setType.Element!));
      return Function(name, IrType.Bool, [a, b], [element],
        If(Binary(IrBinaryOp.NotEqual, new IrCollection(IrCollectionOp.SetCount, IrType.I32, [Ref(a)]), new IrCollection(IrCollectionOp.SetCount, IrType.I32, [Ref(b)])), Return(Bool(false))),
        new IrForEachCollection(Ref(a), EnumerationMode.Live, element, null, Block(
          If(Not(new IrCollection(IrCollectionOp.SetContains, IrType.Bool, [Ref(b), Ref(element)])), Return(Bool(false))))),
        Return(Bool(true)));
    });

  /// <summary>A comprehension's sequence, whose every element was put into a collection as it was made, drained; the collection is the value.</summary>
  public IrCall Drain(IrExpr sequence, IrExpr collection)
    => this.Call($"PyDrain_{Name(sequence.Type)}_{Name(collection.Type)}", collection.Type, [sequence, collection], name => {
      var (source, target, item) = (Local("source", sequence.Type), Local("target", collection.Type), Local("item", sequence.Type.Element!));
      return Function(name, collection.Type, [source, target], [item],
        new IrForEachSequence(item, Ref(source), Block()),
        Return(Ref(target)));
    });

}
