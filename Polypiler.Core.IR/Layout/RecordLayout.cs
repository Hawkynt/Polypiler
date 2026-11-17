using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Polypiler.Core.IR;

/// <summary>A range of bytes of a record that no field covers (#110): padding, never read as data.</summary>
/// <param name="Offset">The first byte, from the start of the record.</param>
/// <param name="Length">How many bytes, at least one.</param>
public sealed record IrPadding(long Offset, long Length) {

  /// <summary>The range as the layout tables write it, <c>offset+length</c>.</summary>
  public override string ToString() => $"{this.Offset}+{this.Length}";

}

/// <summary>What a layout query asks of a type (#110).</summary>
public enum IrLayoutQuery {

  /// <summary>The size in bytes, the tail padding included.</summary>
  Size,

  /// <summary>The alignment in bytes.</summary>
  Alignment,

  /// <summary>The offset of a field from the start of its record, in bytes.</summary>
  Offset

}

/// <summary>
/// The byte layout of a record under a <see cref="LayoutProfile"/> (#110): where each field lies, how large and
/// how aligned the record is, and every range of padding, the tail included, so that no consumer infers it.
/// <para>
/// The rule is the record's <see cref="IrStructType.Layout"/>. None, or a sequential layout without packing, is
/// <em>natural</em>: each field at the next multiple of its alignment, the size rounded up to the largest
/// alignment (ISO C §6.7.2.1 as the System V ABIs apply it). A sequential layout packed by <c>n</c> is
/// <em>packed(n)</em>: every alignment capped at <c>n</c>. A stated size of a sequential layout enlarges the record
/// and is never smaller than its fields. An explicit layout puts every field at its stated offset; it is aligned
/// to its packing, or to 1 without one, and is as large as its stated size or else as the end of its last field -
/// independent of the profile but for the sizes of the records it holds. Overlapping fields, an explicit size
/// short of a field's end, a packing that is no power of two and a field that has no bytes - a string, an array,
/// an object - are problems, which <see cref="Problems"/> lists and <see cref="Compute"/> throws for.
/// </para>
/// </summary>
public sealed class RecordLayout {

  private static readonly ConditionalWeakTable<IrStructType, Dictionary<LayoutProfile, RecordLayout>> _cache = new();

  private readonly IReadOnlyList<long> _offsets;

  private RecordLayout(IrStructType record, LayoutProfile profile, IReadOnlyList<long> offsets, long size, int alignment, IReadOnlyList<IrPadding> padding) {
    this.Record = record;
    this.Profile = profile;
    this._offsets = offsets;
    this.Size = size;
    this.Alignment = alignment;
    this.Padding = padding;
  }

  /// <summary>The record.</summary>
  public IrStructType Record { get; }

  /// <summary>The profile the layout is computed for.</summary>
  public LayoutProfile Profile { get; }

  /// <summary>The offset of every field, in field order.</summary>
  public IReadOnlyList<long> Offsets => this._offsets;

  /// <summary>The size in bytes, the tail padding included.</summary>
  public long Size { get; }

  /// <summary>The alignment in bytes, a power of two.</summary>
  public int Alignment { get; }

  /// <summary>Every range of bytes no field covers, in address order, the tail padding last; empty for none.</summary>
  public IReadOnlyList<IrPadding> Padding { get; }

  /// <summary>The offset of a field of the record.</summary>
  /// <param name="field">The field, one of the record's own.</param>
  /// <returns>The offset in bytes.</returns>
  /// <exception cref="ArgumentException">The field is not one of the record's.</exception>
  public long OffsetOf(IrField field) {
    var fields = this.Record.Fields;
    for (var index = 0; index < fields.Count; ++index)
      if (ReferenceEquals(fields[index], field))
        return this._offsets[index];

    throw new ArgumentException($"'{field.Name}' is no field of {this.Record.Name}", nameof(field));
  }

  /// <summary>The layout of a record under a profile.</summary>
  /// <param name="record">The record, its fields defined.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The layout.</returns>
  /// <exception cref="ArgumentException">The record has a problem of <see cref="Problems"/>; the message lists them.</exception>
  public static RecordLayout Compute(IrStructType record, LayoutProfile profile) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(profile);
    var cached = _cache.GetValue(record, _ => []);
    lock (cached) {
      if (cached.TryGetValue(profile, out var known))
        return known;
    }

    var problems = new List<string>();
    var layout = Build(record, profile, problems, new HashSet<IrStructType>(ReferenceEqualityComparer.Instance));
    if (problems.Count > 0)
      throw new ArgumentException($"layout of '{record.Name}': {string.Join("; ", problems)}", nameof(record));

    lock (cached)
      cached[profile] = layout!;

    return layout!;
  }

  /// <summary>What keeps a record from having a layout under a profile.</summary>
  /// <param name="record">The record, its fields defined.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>One message per problem, empty when the record has a layout.</returns>
  public static IReadOnlyList<string> Problems(IrStructType record, LayoutProfile profile) {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentNullException.ThrowIfNull(profile);
    var problems = new List<string>();
    Build(record, profile, problems, new HashSet<IrStructType>(ReferenceEqualityComparer.Instance));
    return problems;
  }

  /// <summary>The size of a value of a type in memory: a truth value is one byte, a number its own, a pointer the profile's, a record its layout's.</summary>
  /// <param name="type">A truth value, a number, an enum, a pointer or a record.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The size in bytes.</returns>
  /// <exception cref="ArgumentException">The type has no bytes, or the record has a problem.</exception>
  public static long SizeOf(IrType type, LayoutProfile profile) => Measure(type, profile).Size;

  /// <summary>The alignment of a value of a type in memory.</summary>
  /// <param name="type">A truth value, a number, an enum, a pointer or a record.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The alignment in bytes.</returns>
  /// <exception cref="ArgumentException">The type has no bytes, or the record has a problem.</exception>
  public static int AlignOf(IrType type, LayoutProfile profile) => Measure(type, profile).Alignment;

  /// <summary>The offset of a field of a record under a profile.</summary>
  /// <param name="record">The record.</param>
  /// <param name="field">One of its fields.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The offset in bytes.</returns>
  /// <exception cref="ArgumentException">The record has a problem, or the field is not one of its own.</exception>
  public static long OffsetOf(IrStructType record, IrField field, LayoutProfile profile) => Compute(record, profile).OffsetOf(field);

  /// <summary>The answer to a query, as an <see cref="IrLayoutOf"/> asks it.</summary>
  /// <param name="query">What is asked.</param>
  /// <param name="type">The type asked about; a record for <see cref="IrLayoutQuery.Offset"/>.</param>
  /// <param name="field">The field for <see cref="IrLayoutQuery.Offset"/>, <see langword="null"/> otherwise.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>The size, the alignment or the offset, in bytes.</returns>
  public static long Answer(IrLayoutQuery query, IrType type, IrField? field, LayoutProfile profile) => query switch {
    IrLayoutQuery.Size => SizeOf(type, profile),
    IrLayoutQuery.Alignment => AlignOf(type, profile),
    _ => OffsetOf(type.StructType!, field!, profile)
  };

  /// <summary>
  /// Whether a query has the same answer under every profile: the size of a truth value, a number or an enum; the
  /// alignment of one of a single byte; and any query of a record whose rule is packed(1) or explicit and whose
  /// records within are so too. Such a query may be answered before the target is known.
  /// </summary>
  /// <param name="query">What is asked.</param>
  /// <param name="type">The type asked about.</param>
  /// <returns><see langword="true"/> when no profile changes the answer.</returns>
  public static bool IsProfileIndependent(IrLayoutQuery query, IrType type) => type.StructType is { } record
    ? IsProfileIndependent(record, new HashSet<IrStructType>(ReferenceEqualityComparer.Instance))
    : (type == IrType.Bool || type.IsNumeric) && (query == IrLayoutQuery.Size || type.Size <= 1);

  /// <summary>Whether a type has bytes a layout can place: a truth value, a number, an enum or a record.</summary>
  /// <param name="type">The type.</param>
  /// <returns><see langword="true"/> for a type a field of a laid-out record may have.</returns>
  public static bool HasBytes(IrType type) => type == IrType.Bool || type.IsNumeric || type.IsStruct;

  private static bool IsProfileIndependent(IrStructType record, HashSet<IrStructType> visiting) {
    if (!visiting.Add(record) || !record.IsDefined)
      return false;

    var fixedRule = record.Layout is { Kind: IrLayoutKind.Explicit } or { Kind: IrLayoutKind.Sequential, Pack: 1 };
    return fixedRule && record.Fields.All(field => field.Type.StructType is not { } nested || IsProfileIndependent(nested, visiting));
  }

  private static (long Size, int Alignment) Measure(IrType type, LayoutProfile profile) {
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(profile);
    if (type.StructType is { } record) {
      var layout = Compute(record, profile);
      return (layout.Size, layout.Alignment);
    }

    if (type.IsPointer)
      return (profile.PointerSize, profile.AlignmentOf(profile.PointerSize));

    var size = Scalar(type) ?? throw new ArgumentException($"the type {type} has no byte layout", nameof(type));
    return (size, profile.AlignmentOf(size));
  }

  /// <summary>The size of a truth value, a number or an enum; <see langword="null"/> for any other type.</summary>
  private static int? Scalar(IrType type) => type == IrType.Bool ? 1 : type.IsNumeric ? type.Size : null;

  /// <summary>The layout of a record, or <see langword="null"/> with the problems added; a record within it that holds it is a problem, not a recursion.</summary>
  private static RecordLayout? Build(IrStructType record, LayoutProfile profile, List<string> problems, HashSet<IrStructType> visiting) {
    if (!record.IsDefined) {
      problems.Add($"'{record.Name}' has no fields yet");
      return null;
    }

    if (!visiting.Add(record)) {
      problems.Add($"'{record.Name}' contains itself");
      return null;
    }

    var count = problems.Count;
    var rule = record.Layout;
    if (rule is { Pack: not 0 } && !(rule.Pack > 0 && BitOperations.IsPow2(rule.Pack)))
      problems.Add($"a packing of {rule.Pack}, which is no power of two");

    var measures = new List<(long Size, int Alignment)>();
    foreach (var field in record.Fields) {
      if (field.Type.StructType is { } nested) {
        var inner = Build(nested, profile, problems, visiting);
        measures.Add(inner is null ? (0, 1) : (inner.Size, inner.Alignment));
      } else if (Scalar(field.Type) is { } size) {
        measures.Add((size, profile.AlignmentOf(size)));
      } else {
        problems.Add($"the field '{field.Name}' has the type {field.Type}, which has no byte layout");
        measures.Add((0, 1));
      }
    }

    visiting.Remove(record);
    var layout = rule is { Kind: IrLayoutKind.Explicit } ? Explicit(record, rule, measures, problems) : Sequential(record, rule, measures, problems);
    return problems.Count > count ? null : new RecordLayout(record, profile, layout.Offsets, layout.Size, layout.Alignment, Gaps(layout.Offsets, measures, layout.Size));
  }

  /// <summary>Natural or packed(n): every field at the next multiple of its alignment, capped by the packing.</summary>
  private static (long[] Offsets, long Size, int Alignment) Sequential(IrStructType record, IrStructLayout? rule, List<(long Size, int Alignment)> measures, List<string> problems) {
    var cap = rule is { Pack: > 0 } ? rule.Pack : int.MaxValue;
    var offsets = new long[measures.Count];
    var end = 0L;
    var alignment = 1;
    for (var index = 0; index < measures.Count; ++index) {
      var (size, natural) = measures[index];
      var aligned = Math.Min(natural, cap);
      offsets[index] = Round(end, aligned);
      end = offsets[index] + size;
      alignment = Math.Max(alignment, aligned);
    }

    var total = Round(end, alignment);
    if (rule is { Size: > 0 } && rule.Size < end)
      problems.Add($"the size {rule.Size} is smaller than the end {end} of the fields of '{record.Name}'");

    return (offsets, Math.Max(total, rule?.Size ?? 0), alignment);
  }

  /// <summary>Explicit: every field at its stated offset, none overlapping another, all within the stated size.</summary>
  private static (long[] Offsets, long Size, int Alignment) Explicit(IrStructType record, IrStructLayout rule, List<(long Size, int Alignment)> measures, List<string> problems) {
    if (rule.Offsets.Count != measures.Count) {
      problems.Add($"{rule.Offsets.Count} offsets for {measures.Count} fields");
      return ([], 0, 1);
    }

    var offsets = rule.Offsets.Select(offset => (long)offset).ToArray();
    foreach (var index in Enumerable.Range(0, offsets.Length).Where(index => offsets[index] < 0))
      problems.Add($"the field '{record.Fields[index].Name}' has the negative offset {offsets[index]}");

    var order = Enumerable.Range(0, offsets.Length).Where(index => measures[index].Size > 0).OrderBy(index => offsets[index]).ToList();
    for (var position = 1; position < order.Count; ++position) {
      int before = order[position - 1], after = order[position];
      if (offsets[before] + measures[before].Size > offsets[after])
        problems.Add($"the fields '{record.Fields[before].Name}' and '{record.Fields[after].Name}' overlap");
    }

    var end = order.Select(index => offsets[index] + measures[index].Size).DefaultIfEmpty(0).Max();
    if (rule.Size > 0 && rule.Size < end)
      problems.Add($"the size {rule.Size} is smaller than the end {end} of the fields of '{record.Name}'");

    return (offsets, rule.Size > 0 ? rule.Size : end, rule.Pack > 0 ? rule.Pack : 1);
  }

  /// <summary>The ranges of a record no field covers, the fields in address order, the tail last.</summary>
  private static List<IrPadding> Gaps(long[] offsets, List<(long Size, int Alignment)> measures, long size) {
    var gaps = new List<IrPadding>();
    var cursor = 0L;
    foreach (var index in Enumerable.Range(0, offsets.Length).Where(index => measures[index].Size > 0).OrderBy(index => offsets[index])) {
      if (offsets[index] > cursor)
        gaps.Add(new IrPadding(cursor, offsets[index] - cursor));

      cursor = Math.Max(cursor, offsets[index] + measures[index].Size);
    }

    if (size > cursor)
      gaps.Add(new IrPadding(cursor, size - cursor));

    return gaps;
  }

  private static long Round(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

}
