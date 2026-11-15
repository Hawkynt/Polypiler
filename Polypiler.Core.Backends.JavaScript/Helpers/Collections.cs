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
    "$lvadd", "$lvset", "$lvinsert", "$lvremoveAt", "$lvpop", "$lvclear", "$lvcheck"
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
    _ => null
  };

}
