using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Frontends.CSharp;

/// <summary>
/// The members of LINQ to Objects and the array searches of <c>System.Array</c> the frontend imports
/// (#1329), each as the operation of the IR it means (<see cref="Polypiler.Core.IR.IrSequenceOp"/>), and
/// those it refuses, each with the reason - the table the tests check against the lowering. A member of
/// <c>System.Linq.Enumerable</c> listed nowhere is refused by name.
/// </summary>
public static class CSharpSequenceApi {

  /// <summary>Every construct the table states.</summary>
  public static IReadOnlyList<CSharpStringMember> Members { get; } = [
    new("a T[], a List<T> or an IEnumerable<T> as the source of a member below, AsEnumerable()", "FromArray, FromList, the sequence"),
    new("Enumerable.Range(start, count)", "Range"),
    new("Select(x => …), Select((x, i) => …)", "Select, SelectIndexed"),
    new("Where(x => …), Where((x, i) => …)", "Where, WhereIndexed"),
    new("SelectMany(x => …), SelectMany(x => …, (x, y) => …)", "SelectMany, SelectManyResult"),
    new("Take(n), Skip(n)", "Take, Skip"),
    new("Zip(second, (x, y) => …) with a T[] or a List<T> as the second sequence", "Zip"),
    new("OrderBy, OrderByDescending, ThenBy, ThenByDescending with a key of a number or a truth value, or a string key with StringComparer.Ordinal", "OrderBy, OrderByDescending, ThenBy, ThenByDescending"),
    new("ToArray(), ToList()", "ToArray, ToList"),
    new("Count(), Count(p), Any(), Any(p), All(p)", "Count, CountWhere, Any, AnyWhere, All"),
    new("First(), First(p), FirstOrDefault(), FirstOrDefault(p) of numbers and truth values", "First, FirstWhere, FirstOrDefault, FirstOrDefaultWhere"),
    new("Sum(), Min(), Max() of int, long, float, double - Min and Max of every number type - and each with a selector", "Sum, Min, Max (after a Select)"),
    new("Aggregate(f), Aggregate(seed, f), Aggregate(seed, f, result)", "Aggregate, AggregateSeed"),
    new("Contains(v) of numbers, truth values and strings", "Contains"),
    new("query syntax: from, where, select, orderby [descending], let, a second from", "the operations above; let and a second from as SelectMany of the rest of the query"),
    new("Array.IndexOf(a, v), Array.Find(a, p), Array.FindIndex(a, p), Array.Exists(a, p), Array.TrueForAll(a, p)", "IndexOf, FirstOrDefaultWhere, FindIndex, AnyWhere, All"),
    new("OrderBy and the further keys with a string key and no comparer", "", "culture-sensitive comparison"),
    new("an orderby after a let or a second from, join, group, into", "", "of a query"),
    new("Zip with a second sequence that is no array and no list, Zip without a result selector", "", "Zip"),
    new("AsParallel, AsQueryable and the members of ParallelEnumerable and Queryable", "", "a parallel or a queryable sequence"),
    new("GroupBy, Join, GroupJoin, Distinct, Union, Intersect, Except, ToDictionary, ToLookup, Last, Single, ElementAt, Reverse, Average and every other member of Enumerable", "", "Enumerable.")
  ];

  /// <summary>The entries the frontend imports.</summary>
  public static IEnumerable<CSharpStringMember> Supported => Members.Where(member => member.Refusal.Length == 0);

  /// <summary>The entries the frontend refuses.</summary>
  public static IEnumerable<CSharpStringMember> Refused => Members.Where(member => member.Refusal.Length > 0);

}
