using System.Collections.Generic;

namespace Polypiler.Core.IR;

/// <summary>The order a map or a set enumerates its entries in.</summary>
public enum MapOrder {

  /// <summary>
  /// The order the keys were first inserted in; a removed key is gone from it, and inserted again it
  /// comes last: JavaScript's <c>Map</c> and <c>Set</c> (ECMA-262 §24.1, §24.2).
  /// </summary>
  Insertion,

  /// <summary>
  /// The order of the slots of .NET's <c>Dictionary</c> and <c>HashSet</c>: a new key takes the slot
  /// freed most recently by a removal, or else the next unused one; <c>Clear</c> frees every slot and
  /// forgets the freed ones, so the next key takes the first slot again. Adding 1, 2, 3, removing 1
  /// and 3 and adding 4, 5, 6 enumerates 5, 2, 4, 6.
  /// </summary>
  SlotReuse

}

/// <summary>What a collection does when the body of a loop over it changes it.</summary>
public enum EnumerationMode {

  /// <summary>
  /// .NET's enumerators: a list traps with <see cref="IrTrap.CollectionModified"/> at the next step
  /// after any change of it - an addition, an insertion, a removal, a clear or a write of an element -
  /// also after the last element; a map or a set at the next step after the insertion of a new key, and
  /// not after a removal, a clear or the replacement of a value (.NET Core 3.0 and later).
  /// </summary>
  Checked,

  /// <summary>
  /// JavaScript's iterators: a list is walked by index up to its length at each step; a map or a set
  /// visits the entries added during the walk and skips the removed ones it has not reached.
  /// </summary>
  Live

}

/// <summary>
/// The operations on lists, maps and sets. Arguments come in the order given; the first is the
/// collection, except for the constructions. A key is equal to another as one rule says for every
/// order: integers and truth values by value, strings by code unit, references to objects by
/// identity, floating-point numbers by <c>Equals</c> - every NaN equals every NaN and <c>+0</c> equals
/// <c>-0</c>. Inserting a key equal to one present keeps the key present and replaces its value.
/// </summary>
public enum IrCollectionOp {

  /// <summary>A new empty list.</summary>
  ListNew,

  /// <summary>The number of elements of a list, an <c>i32</c>.</summary>
  ListCount,

  /// <summary>The element at an index; traps with <see cref="IrTrap.ArgumentOutOfRange"/> outside 0 … count - 1.</summary>
  ListGet,

  /// <summary>Writes the element at an index; traps like <see cref="ListGet"/>.</summary>
  ListSet,

  /// <summary>Appends an element.</summary>
  ListAdd,

  /// <summary>Inserts an element at an index 0 … count; traps with <see cref="IrTrap.ArgumentOutOfRange"/> outside.</summary>
  ListInsert,

  /// <summary>Removes the element at an index; traps like <see cref="ListGet"/>.</summary>
  ListRemoveAt,

  /// <summary>Removes the last element and is it, or none when the list is empty: JavaScript's <c>pop</c>.</summary>
  ListRemoveLast,

  /// <summary>Removes every element.</summary>
  ListClear,

  /// <summary>Whether an element equals a value by <c>Equals</c>: a NaN is found.</summary>
  ListContains,

  /// <summary>The first index whose element equals a value by <c>Equals</c>, or -1: C#'s <c>IndexOf</c>.</summary>
  ListIndexOfEquals,

  /// <summary>The first index whose element is <c>==</c> to a value, or -1: JavaScript's <c>indexOf</c> - a NaN is not found, <c>-0</c> is found as <c>0</c>.</summary>
  ListIndexOfStrict,

  /// <summary>Whether an element is the same value as one by SameValueZero: JavaScript's <c>includes</c> - a NaN is found.</summary>
  ListIncludesSameValueZero,

  /// <summary>A new array of the elements in order.</summary>
  ListCopyToArray,

  /// <summary>A new list of the elements of an array in order.</summary>
  ListFromArray,

  /// <summary>A new empty map.</summary>
  MapNew,

  /// <summary>The number of entries of a map.</summary>
  MapCount,

  /// <summary>The value of a key; traps with <see cref="IrTrap.KeyNotFound"/> where the key is absent.</summary>
  MapGet,

  /// <summary>The value of a key as an optional value, none where the key is absent.</summary>
  MapTryGet,

  /// <summary>Whether a key is present.</summary>
  MapContainsKey,

  /// <summary>Inserts a key with its value, or replaces the value of the key present.</summary>
  MapPut,

  /// <summary>Inserts a key with its value; traps with <see cref="IrTrap.DuplicateKey"/> where the key is present.</summary>
  MapAdd,

  /// <summary>Inserts a key with its value where it is absent and says whether it did; a present key keeps its value.</summary>
  MapTryAdd,

  /// <summary>Removes a key and says whether it was present.</summary>
  MapRemove,

  /// <summary>Removes every entry.</summary>
  MapClear,

  /// <summary>A new empty set.</summary>
  SetNew,

  /// <summary>The number of elements of a set.</summary>
  SetCount,

  /// <summary>Inserts an element and says whether it was absent.</summary>
  SetAdd,

  /// <summary>Removes an element and says whether it was present.</summary>
  SetRemove,

  /// <summary>Whether an element is present.</summary>
  SetContains,

  /// <summary>Removes every element.</summary>
  SetClear,

  /// <summary>
  /// A new list of the operands, in order: a literal of elements no array of the IR holds, such as
  /// objects or arrays (#1365). Last of all the operations, so that the numbers of the others stay.
  /// </summary>
  ListOf

}

/// <summary>
/// An operation on a list, a map or a set (<see cref="IrCollectionOp"/>). Lists, maps and sets are
/// references with an identity, never <c>null</c>: every copy of one names the same collection.
/// </summary>
/// <param name="Op">The operation.</param>
/// <param name="Type">The type of its value; <see cref="IrType.Void"/> for one that only changes the collection.</param>
/// <param name="Arguments">The collection first, then the operands.</param>
public sealed record IrCollection(IrCollectionOp Op, IrType Type, IReadOnlyList<IrExpr> Arguments) : IrExpr(Type);

/// <summary>
/// A loop over the elements of a list or a set, or over the entries of a map, in the collection's
/// order: before every pass the next element is assigned to <paramref name="Element"/> - the key of
/// an entry of a map - and its value to <paramref name="Value"/>. The collection is evaluated once;
/// what a change of it during the loop does is <paramref name="Mode"/>'s. <c>break</c> and
/// <c>continue</c> act on this loop.
/// </summary>
/// <param name="Collection">The list, map or set.</param>
/// <param name="Mode">What a change of the collection during the loop does.</param>
/// <param name="Element">The local of the element or the key, or <see langword="null"/> for a loop over the values of a map only.</param>
/// <param name="Value">The local of the value of an entry of a map, or <see langword="null"/>.</param>
/// <param name="Body">The statements of a pass.</param>
public sealed record IrForEachCollection(IrExpr Collection, EnumerationMode Mode, IrLocal? Element, IrLocal? Value, IrBlock Body) : IrStmt;

/// <summary>The rules the types of collections follow.</summary>
public static class IrCollections {

  /// <summary>
  /// Whether a collection type is well formed: an element - and a value - of a scalar, string, class
  /// or struct type, or a collection; a key or an element of a set no struct.
  /// </summary>
  /// <param name="type">The collection type.</param>
  /// <returns><see langword="true"/> when the IR has the type.</returns>
  public static bool IsElement(IrType type) {
    static bool Holds(IrType element) => element.IsStorable && !element.IsView && !element.IsOptional;
    static bool Keys(IrType key) => Holds(key) && (key.IsNumeric || key == IrType.Bool || key.IsString || key.IsObject);

    return type.Kind switch {
      IrTypeKind.List => Holds(type.Element!),
      IrTypeKind.Map => Keys(type.Key!) && Holds(type.Element!),
      _ => Keys(type.Element!)
    };
  }

  /// <summary>
  /// The type of a value that may be absent, as <see cref="IrCollectionOp.ListRemoveLast"/> and
  /// <see cref="IrCollectionOp.MapTryGet"/> give it: an optional value of a scalar or a struct, a
  /// reference itself (absent as <c>null</c>), <see langword="null"/> for a type without either.
  /// </summary>
  /// <param name="type">The type of the value when it is present.</param>
  /// <returns>The type, or <see langword="null"/>.</returns>
  public static IrType? Absent(IrType type) => type.IsOptionalValue ? IrType.Optional(type) : type.IsObject ? type : null;

  /// <summary>
  /// The kind of collection an operation takes, and the types of its operands and its value for a
  /// collection of a type; <see langword="null"/> types where the collection type is no fit.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <returns>The kind of the collection the operation acts on.</returns>
  public static IrTypeKind Kind(IrCollectionOp op) => op switch {
    <= IrCollectionOp.ListFromArray or IrCollectionOp.ListOf => IrTypeKind.List,
    <= IrCollectionOp.MapClear => IrTypeKind.Map,
    _ => IrTypeKind.Set
  };

  /// <summary>
  /// The operand types an operation takes after the collection, and the type of its value, for a
  /// collection type. The constructions take nothing (<see cref="IrCollectionOp.ListFromArray"/> an
  /// array) and are of the collection type itself.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <param name="collection">The type of the collection.</param>
  /// <returns>The operand types and the value type; a value type of <see langword="null"/> when the element has no absent value.</returns>
  public static (IReadOnlyList<IrType> Operands, IrType? Result) Signature(IrCollectionOp op, IrType collection) {
    var element = collection.Element!;
    var key = collection.Key ?? element;
    return op switch {
      IrCollectionOp.ListNew or IrCollectionOp.MapNew or IrCollectionOp.SetNew => ([], collection),
      // ListOf takes any number of elements, which the verifier counts.
      IrCollectionOp.ListOf => ([], collection),
      IrCollectionOp.ListFromArray => ([IrType.Array(element)], collection),
      IrCollectionOp.ListCount or IrCollectionOp.MapCount or IrCollectionOp.SetCount => ([], IrType.I32),
      IrCollectionOp.ListGet => ([IrType.I32], element),
      IrCollectionOp.ListSet or IrCollectionOp.ListInsert => ([IrType.I32, element], IrType.Void),
      IrCollectionOp.ListAdd => ([element], IrType.Void),
      IrCollectionOp.ListRemoveAt => ([IrType.I32], IrType.Void),
      IrCollectionOp.ListRemoveLast => ([], Absent(element)),
      IrCollectionOp.ListClear or IrCollectionOp.MapClear or IrCollectionOp.SetClear => ([], IrType.Void),
      IrCollectionOp.ListContains or IrCollectionOp.ListIncludesSameValueZero => ([element], IrType.Bool),
      IrCollectionOp.ListIndexOfEquals or IrCollectionOp.ListIndexOfStrict => ([element], IrType.I32),
      IrCollectionOp.ListCopyToArray => ([], IrType.Array(element)),
      IrCollectionOp.MapGet => ([key], element),
      IrCollectionOp.MapTryGet => ([key], Absent(element)),
      IrCollectionOp.MapContainsKey or IrCollectionOp.MapRemove or IrCollectionOp.SetAdd or IrCollectionOp.SetRemove or IrCollectionOp.SetContains => ([key], IrType.Bool),
      IrCollectionOp.MapPut or IrCollectionOp.MapAdd => ([key, element], IrType.Void),
      _ => ([key, element], IrType.Bool)
    };
  }

  /// <summary>Whether an operation makes a new collection and takes no collection first.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for the constructions.</returns>
  public static bool Constructs(IrCollectionOp op) => op is IrCollectionOp.ListNew or IrCollectionOp.ListFromArray or IrCollectionOp.ListOf or IrCollectionOp.MapNew or IrCollectionOp.SetNew;

  /// <summary>
  /// Whether an operation changes the collection in a way that a <see cref="EnumerationMode.Checked"/>
  /// loop over it notices: on a list every change, on a map or a set the insertion of a key.
  /// </summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> when a checked loop traps at its next step after it.</returns>
  public static bool Invalidates(IrCollectionOp op) => op is IrCollectionOp.ListSet or IrCollectionOp.ListAdd or IrCollectionOp.ListInsert
    or IrCollectionOp.ListRemoveAt or IrCollectionOp.ListRemoveLast or IrCollectionOp.ListClear
    or IrCollectionOp.MapPut or IrCollectionOp.MapAdd or IrCollectionOp.MapTryAdd or IrCollectionOp.SetAdd;

  /// <summary>Whether an operation changes the collection at all.</summary>
  /// <param name="op">The operation.</param>
  /// <returns><see langword="true"/> for an operation that writes.</returns>
  public static bool Mutates(IrCollectionOp op) => Invalidates(op) || op is IrCollectionOp.MapRemove or IrCollectionOp.MapClear or IrCollectionOp.SetRemove or IrCollectionOp.SetClear;

}
