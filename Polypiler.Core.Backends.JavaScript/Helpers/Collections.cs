using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of lists, which are arrays: the element access and the changes at an index, which trap
/// with the <c>ArgumentOutOfRangeError</c> outside the list as .NET's <c>List&lt;T&gt;</c> does; the
/// searches by <c>Equals</c>, under which a NaN is found; and the changes that count themselves in a
/// property <c>$v</c> of the array, with the check a loop makes of that count, which traps with the
/// <c>InvalidOperationError</c> as .NET's enumerator throws after a change.
/// </summary>
internal static partial class JavaScriptHelpers {

  private const string OutsideTheList = "  if (index < 0 || index >= list.length) throw new ArgumentOutOfRangeError(\"argument out of range\");";
  private const string OutsideTheEnds = "  if (index < 0 || index > list.length) throw new ArgumentOutOfRangeError(\"argument out of range\");";
  private const string Counted = "  list.$v = (list.$v | 0) + 1;";

  /// <summary>The names of the helpers of lists, in their order.</summary>
  private static IEnumerable<string> CollectionOrder => [
    "$lget", "$lset", "$linsert", "$lremoveAt", "$lindexEq", "$lindexBy", "$lincludes",
    "$lvadd", "$lvset", "$lvinsert", "$lvremoveAt", "$lvpop", "$lvclear", "$lvcheck",
    "$Keyed", "$mget", "$madd", "$mtryadd", "$sadd", "$mvcheck"
  ];

  /// <summary>The text of a helper of lists, the same at every level; <see langword="null"/> for any other name.</summary>
  private static string? CollectionSource(string name) => name switch {
    "$lget" => Lines("function $lget(list, index) {", OutsideTheList, "  return list[index];", "}"),
    "$lset" => Lines("function $lset(list, index, value) {", OutsideTheList, "  list[index] = value;", "}"),
    "$linsert" => Lines("function $linsert(list, index, value) {", OutsideTheEnds, "  list.splice(index, 0, value);", "}"),
    "$lremoveAt" => Lines("function $lremoveAt(list, index) {", OutsideTheList, "  list.splice(index, 1);", "}"),
    "$lindexEq" => Lines(
      "function $lindexEq(list, value) {",
      "  for (@let index = 0; index < list.length; ++index) {",
      "    @const element = list[index];",
      "    if (element === value || (element !== element && value !== value)) return index;",
      "  }",
      "  return -1;",
      "}"),
    "$lindexBy" => Lines(
      "function $lindexBy(list, value, equals) {",
      "  for (@let index = 0; index < list.length; ++index) {",
      "    if (equals(list[index], value)) return index;",
      "  }",
      "  return -1;",
      "}"),
    "$lincludes" => Lines("function $lincludes(list, value) {", "  return $lindexEq(list, value) >= 0;", "}"),
    "$lvadd" => Lines("function $lvadd(list, value) {", "  list.push(value);", Counted, "}"),
    "$lvset" => Lines("function $lvset(list, index, value) {", OutsideTheList, "  list[index] = value;", Counted, "}"),
    "$lvinsert" => Lines("function $lvinsert(list, index, value) {", OutsideTheEnds, "  list.splice(index, 0, value);", Counted, "}"),
    "$lvremoveAt" => Lines("function $lvremoveAt(list, index) {", OutsideTheList, "  list.splice(index, 1);", Counted, "}"),
    "$lvpop" => Lines("function $lvpop(list) {", "  if (list.length === 0) return void 0;", Counted, "  return list.pop();", "}"),
    "$lvclear" => Lines("function $lvclear(list) {", "  list.length = 0;", Counted, "}"),
    "$lvcheck" => Lines("function $lvcheck(list, version) {", "  if ((list.$v | 0) !== version) throw new InvalidOperationError(\"collection modified\");", "}"),
    "$Keyed" => Keyed,
    "$mget" => Lines("function $mget(map, key) {", "  if (!map.has(key)) throw new KeyNotFoundError(\"key not found\");", "  return map.get(key);", "}"),
    "$madd" => Lines("function $madd(map, key, value) {", "  if (map.has(key)) throw new ArgumentError(\"duplicate key\");", "  map.set(key, value);", "}"),
    "$mtryadd" => Lines("function $mtryadd(map, key, value) {", "  if (map.has(key)) return false;", "  map.set(key, value);", "  return true;", "}"),
    "$sadd" => Lines("function $sadd(set, value) {", "  if (set.has(value)) return false;", "  set.add(value);", "  return true;", "}"),
    "$mvcheck" => Lines("function $mvcheck(map, version) {", "  if (map.$v !== version) throw new InvalidOperationError(\"collection modified\");", "}"),
    _ => null
  };

  /// <summary>
  /// A map - or a set, whose values are true - with the methods of <c>Map</c> and <c>Set</c> whose
  /// entries stand in slots: <c>$k</c> the keys as inserted, <c>$w</c> the values, <c>$u</c> whether
  /// a slot is used. With <c>reuse</c> a new key takes the slot freed last, or the next, and
  /// <c>clear</c> drops every slot - the order of .NET's <c>Dictionary</c>, whose enumerator compares
  /// <c>$v</c>, which only an insertion of a new key counts. Without it every new key takes the next
  /// slot and <c>clear</c> empties them - the order of JavaScript's <c>Map</c>, at a level without one.
  /// A key is found by a text: a value by its type and its text, under which every NaN is one key and
  /// <c>-0</c> is <c>0</c>; a pair of halves by its two halves; an object by an identifier it is given.
  /// </summary>
  private static string Keyed => Lines(
    "function $Keyed(reuse, kind) {",
    "  this.$r = reuse;",
    "  this.$t = kind;",
    "  this.$i = Object.create(null);",
    "  this.$k = [];",
    "  this.$w = [];",
    "  this.$u = [];",
    "  this.$f = [];",
    "  this.$v = 0;",
    "  this.size = 0;",
    "}",
    "$Keyed.$n = 0;",
    "$Keyed.prototype.$key = function (key) {",
    "  if (this.$t === 2) return key.hi + \":\" + key.lo;",
    "  if (this.$t !== 1 || key === null) return typeof key + \":\" + key;",
    "  if (key.$id === void 0) key.$id = ++$Keyed.$n;",
    "  return \"o\" + key.$id;",
    "};",
    "$Keyed.prototype.has = function (key) {",
    "  return this.$i[this.$key(key)] !== void 0;",
    "};",
    "$Keyed.prototype.get = function (key) {",
    "  @const slot = this.$i[this.$key(key)];",
    "  return slot === void 0 ? void 0 : this.$w[slot];",
    "};",
    "$Keyed.prototype.set = function (key, value) {",
    "  @const text = this.$key(key);",
    "  @let slot = this.$i[text];",
    "  if (slot !== void 0) {",
    "    this.$w[slot] = value;",
    "    return this;",
    "  }",
    "  if (this.$r && this.$f.length > 0) {",
    "    slot = this.$f.pop();",
    "  } else {",
    "    slot = this.$u.length;",
    "  }",
    "  this.$k[slot] = key;",
    "  this.$w[slot] = value;",
    "  this.$u[slot] = true;",
    "  this.$i[text] = slot;",
    "  this.$v++;",
    "  this.size++;",
    "  return this;",
    "};",
    "$Keyed.prototype.add = function (key) {",
    "  return this.has(key) ? this : this.set(key, true);",
    "};",
    "$Keyed.prototype.delete = function (key) {",
    "  @const text = this.$key(key);",
    "  @const slot = this.$i[text];",
    "  if (slot === void 0) return false;",
    "  delete this.$i[text];",
    "  this.$u[slot] = false;",
    "  this.$k[slot] = void 0;",
    "  this.$w[slot] = void 0;",
    "  if (this.$r) this.$f.push(slot);",
    "  this.size--;",
    "  return true;",
    "};",
    "$Keyed.prototype.clear = function () {",
    "  this.$i = Object.create(null);",
    "  this.$f = [];",
    "  this.size = 0;",
    "  if (this.$r) {",
    "    this.$k = [];",
    "    this.$w = [];",
    "    this.$u = [];",
    "    return;",
    "  }",
    "  for (@let slot = 0; slot < this.$u.length; ++slot) {",
    "    this.$u[slot] = false;",
    "    this.$k[slot] = void 0;",
    "    this.$w[slot] = void 0;",
    "  }",
    "};");

}
