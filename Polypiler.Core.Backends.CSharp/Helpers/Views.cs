// SPDX-License-Identifier: MIT
namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The helpers of the views (#1325). Below .NET Standard 2.1 a view is a <c>global::System.ArraySegment&lt;T&gt;</c>,
/// which .NET Framework 2.0 has but which checks nothing of what a span checks: these helpers make it,
/// slice it, index it, copy, fill, compare and reinterpret it with the bounds and the traps of a span.
/// They are written in C# 2 against the base library of .NET Framework 2.0, as every helper is, except
/// <see cref="StoreSpan"/>, which only a profile with spans calls.
/// </summary>
public static class CSharpViewHelpers {

  private const string Segment = "global::System.ArraySegment<T>";
  private const string Range = "throw new global::System.ArgumentOutOfRangeException();";

  /// <summary><c>array.AsSpan(start, length)</c>: the part of an array, checked as a span checks it.</summary>
  public static CSharpHelper ViewOf { get; } = new("ViewOf", Segment, "T[] array, int start, int length", [
    "if (start < 0 || start > array.Length || length < 0 || length > array.Length - start) {",
    "  " + Range,
    "}",
    "return new " + Segment + "(array, start, length);"
  ], "<T>");

  /// <summary><c>array.AsSpan(start)</c>: the rest of an array from an element on.</summary>
  public static CSharpHelper ViewFrom { get; } = new("ViewFrom", Segment, "T[] array, int start", [
    "if (start < 0 || start > array.Length) {",
    "  " + Range,
    "}",
    "return new " + Segment + "(array, start, array.Length - start);"
  ], "<T>");

  /// <summary><c>view.Slice(start, length)</c>.</summary>
  public static CSharpHelper Slice { get; } = new("Slice", Segment, Segment + " view, int start, int length", [
    "if (start < 0 || start > view.Count || length < 0 || length > view.Count - start) {",
    "  " + Range,
    "}",
    "return new " + Segment + "(view.Array, view.Offset + start, length);"
  ], "<T>");

  /// <summary><c>view.Slice(start)</c>.</summary>
  public static CSharpHelper SliceFrom { get; } = new("SliceFrom", Segment, Segment + " view, int start", [
    "if (start < 0 || start > view.Count) {",
    "  " + Range,
    "}",
    "return new " + Segment + "(view.Array, view.Offset + start, view.Count - start);"
  ], "<T>");

  /// <summary>The place in the array of an element of a view, checked as the indexer of a span checks it.</summary>
  public static CSharpHelper ViewIndex { get; } = new("ViewIndex", "int", Segment + " view, int index", [
    "if (index < 0 || index >= view.Count) {",
    "  throw new global::System.IndexOutOfRangeException();",
    "}",
    "return view.Offset + index;"
  ], "<T>");

  /// <summary><c>view[index]</c>.</summary>
  public static CSharpHelper ViewGet { get; } = new("ViewGet", "T", Segment + " view, int index", [
    "if (index < 0 || index >= view.Count) {",
    "  throw new global::System.IndexOutOfRangeException();",
    "}",
    "return view.Array[view.Offset + index];"
  ], "<T>");

  /// <summary><c>view[index] = value</c>.</summary>
  public static CSharpHelper ViewSet { get; } = new("ViewSet", "void", Segment + " view, int index, T value", [
    "if (index < 0 || index >= view.Count) {",
    "  throw new global::System.IndexOutOfRangeException();",
    "}",
    "view.Array[view.Offset + index] = value;"
  ], "<T>");

  /// <summary><c>source.CopyTo(target)</c>: <c>Array.Copy</c> moves overlapping elements as memmove does.</summary>
  public static CSharpHelper ViewCopy { get; } = new("ViewCopy", "void", Segment + " source, " + Segment + " target", [
    "if (source.Count > target.Count) {",
    "  throw new global::System.ArgumentException(\"Destination is too short.\", \"destination\");",
    "}",
    "global::System.Array.Copy(source.Array, source.Offset, target.Array, target.Offset, source.Count);"
  ], "<T>");

  /// <summary><c>source.TryCopyTo(target)</c>.</summary>
  public static CSharpHelper ViewTryCopy { get; } = new("ViewTryCopy", "bool", Segment + " source, " + Segment + " target", [
    "if (source.Count > target.Count) {",
    "  return false;",
    "}",
    "global::System.Array.Copy(source.Array, source.Offset, target.Array, target.Offset, source.Count);",
    "return true;"
  ], "<T>");

  /// <summary><c>view.Fill(value)</c>.</summary>
  public static CSharpHelper ViewFill { get; } = new("ViewFill", "void", Segment + " view, T value", [
    "for (int index = 0; index < view.Count; ++index) {",
    "  view.Array[view.Offset + index] = value;",
    "}"
  ], "<T>");

  /// <summary><c>a.SequenceEqual(b)</c>: the elements compared by the default comparer, which is <c>IEquatable&lt;T&gt;.Equals</c> of a number, as <c>MemoryExtensions</c> compares them.</summary>
  public static CSharpHelper ViewEqual { get; } = new("ViewEqual", "bool", Segment + " a, " + Segment + " b", [
    "if (a.Count != b.Count) {",
    "  return false;",
    "}",
    "for (int index = 0; index < a.Count; ++index) {",
    "  if (!global::System.Collections.Generic.EqualityComparer<T>.Default.Equals(a.Array[a.Offset + index], b.Array[b.Offset + index])) {",
    "    return false;",
    "  }",
    "}",
    "return true;"
  ], "<T>");

  /// <summary><c>view.ToArray()</c>.</summary>
  public static CSharpHelper ViewToArray { get; } = new("ViewToArray", "T[]", Segment + " view", [
    "T[] copy = new T[view.Count];",
    "global::System.Array.Copy(view.Array, view.Offset, copy, 0, view.Count);",
    "return copy;"
  ], "<T>");

  /// <summary>
  /// <c>MemoryMarshal.Cast&lt;TFrom, TTo&gt;(view)</c> where a profile has no span: the bytes of the view, in
  /// the order of the host - little-endian on every host .NET runs on - copied into an array of the
  /// other type, as many whole elements as they hold. A copy, not a view: docs/reference/views.md.
  /// </summary>
  public static CSharpHelper ViewCast { get; } = new("ViewCast", "global::System.ArraySegment<TTo>", "global::System.ArraySegment<TFrom> view, int fromSize, int toSize", [
    "int count = (int)((long)view.Count * fromSize / toSize);",
    "TTo[] target = new TTo[count];",
    "global::System.Buffer.BlockCopy(view.Array, view.Offset * fromSize, target, 0, count * toSize);",
    "return new global::System.ArraySegment<TTo>(target);"
  ], "<TFrom, TTo>");

  /// <summary>The value of <c>size</c> bytes of a view from the index on, the lowest first or the highest first; a value outside the view throws as <c>BinaryPrimitives</c> on a span does.</summary>
  public static CSharpHelper LoadView { get; } = new("LoadView", "ulong", "global::System.ArraySegment<byte> view, int index, int size, bool little", [
    "if (index < 0 || index > view.Count - size) {",
    "  " + Range,
    "}",
    "ulong value = 0UL;",
    "for (int offset = 0; offset < size; ++offset) {",
    "  value = (value << 8) | view.Array[view.Offset + index + (little ? size - 1 - offset : offset)];",
    "}",
    "return value;"
  ]);

  /// <summary>Writes the lowest <c>size</c> bytes of a value into a view from the index on; nothing when they do not fit.</summary>
  public static CSharpHelper StoreView { get; } = new("StoreView", "void", "global::System.ArraySegment<byte> view, int index, ulong value, int size, bool little", [
    "if (index < 0 || index > view.Count - size) {",
    "  " + Range,
    "}",
    "for (int offset = 0; offset < size; ++offset) {",
    "  view.Array[view.Offset + index + (little ? offset : size - 1 - offset)] = unchecked((byte)(value >> (8 * offset)));",
    "}"
  ]);

  /// <summary>
  /// The store of <see cref="StoreView"/> on a span. <c>BinaryPrimitives.Write…</c> would take the span, and
  /// check it, before the value is evaluated, where the IR evaluates every argument first.
  /// </summary>
  public static CSharpHelper StoreSpan { get; } = new("StoreSpan", "void", "global::System.Span<byte> view, int index, ulong value, int size, bool little", [
    "if (index < 0 || index > view.Length - size) {",
    "  " + Range,
    "}",
    "for (int offset = 0; offset < size; ++offset) {",
    "  view[index + (little ? offset : size - 1 - offset)] = unchecked((byte)(value >> (8 * offset)));",
    "}"
  ]);

}
