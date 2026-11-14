using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;

namespace Polypiler.Tests.Properties;

/// <summary>
/// Runs properties: takes the inputs in order, shrinks the first failing one greedily, writes the
/// failure record and throws PLY0071. <see cref="Property"/> uses <see cref="Default"/>; a test of the
/// harness builds its own runner to choose the record directory and the environment it reads.
/// </summary>
internal sealed class PropertyRunner {

  /// <summary>The variable whose decimal or <c>0x</c> hexadecimal value replaces every declared seed.</summary>
  public const string SeedVariable = "POLYPILER_PROPERTY_SEED";

  /// <summary>The most shrink steps one failure takes.</summary>
  public const int MaxShrinkSteps = 1000;

  /// <summary>The size FsCheck generates its values at.</summary>
  public const int ArbitrarySize = 100;

  private readonly Func<string, string?> _environment;

  /// <summary>Creates a runner that records into <paramref name="recordDirectory"/> and reads variables from <paramref name="environment"/>.</summary>
  public PropertyRunner(string recordDirectory, Func<string, string?> environment) {
    this.RecordDirectory = recordDirectory;
    this._environment = environment;
  }

  /// <summary>The runner of the process: records into <c>artifacts/properties/</c> of the repository and reads the real environment.</summary>
  public static PropertyRunner Default { get; } = new(DefaultRecordDirectory(), Environment.GetEnvironmentVariable);

  /// <summary>The directory the failure records go to.</summary>
  public string RecordDirectory { get; }

  /// <summary>See <see cref="Property.ForAll{T}(ulong, int, Func{ulong, IEnumerable{T}}, Func{T, IEnumerable{T}}, Func{T, bool}, string)"/>; returns how many inputs held.</summary>
  public int ForAll<T>(ulong seed, int count, Func<ulong, IEnumerable<T>> inputs, Func<T, IEnumerable<T>> shrink, Func<T, bool> predicate, [CallerMemberName] string test = "") {
    ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(shrink);
    ArgumentNullException.ThrowIfNull(predicate);
    var effective = this.EffectiveSeed(seed);
    var index = 0;
    foreach (var input in inputs(effective).Take(count)) {
      var error = Check(predicate, input, out var holds);
      if (!holds)
        throw this.Falsified(test, effective, seed, index, input, error, shrink, predicate);

      ++index;
    }

    return index;
  }

  /// <summary>See <see cref="Property.ForAll{T}(ulong, int, Arbitrary{T}, Func{T, bool}, string)"/>; returns how many inputs held.</summary>
  public int ForAll<T>(ulong seed, int count, Arbitrary<T> arbitrary, Func<T, bool> predicate, [CallerMemberName] string test = "") {
    ArgumentNullException.ThrowIfNull(arbitrary);
    return this.ForAll(seed, count, effective => Sample(arbitrary, effective, count), arbitrary.Shrinker, predicate, test);
  }

  /// <summary><paramref name="count"/> values of an FsCheck arbitrary, made from the seed alone.</summary>
  public static IEnumerable<T> Sample<T>(Arbitrary<T> arbitrary, ulong seed, int count)
    => arbitrary.Generator.Sample(ArbitrarySize, new Rnd(seed), count);

  /// <summary>The seed the inputs are made from: the declared one, unless the environment names another.</summary>
  /// <exception cref="FormatException">The variable is set but is no decimal or <c>0x</c> hexadecimal 64-bit number.</exception>
  public ulong EffectiveSeed(ulong declared) {
    var text = this._environment(SeedVariable);
    return string.IsNullOrWhiteSpace(text) ? declared : ParseSeed(text);
  }

  /// <summary>Reads a seed written in decimal or as <c>0x</c> hexadecimal.</summary>
  /// <exception cref="FormatException">The text is no such number below 2^64.</exception>
  public static ulong ParseSeed(string text) {
    var trimmed = text.Trim();
    var parsed = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? ulong.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var seed)
      : ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
    return parsed ? seed : throw new FormatException($"{SeedVariable} must be a decimal or 0x-prefixed hexadecimal number below 2^64, not '{text}'.");
  }

  /// <summary>A value as the failure record and the message show it: integers in hexadecimal, buffers as hexadecimal bytes, tuples element by element.</summary>
  public static string Format(object? value) => value switch {
    null => "null",
    byte[] bytes => "[" + string.Join(" ", bytes.Select(item => item.ToString("X2", CultureInfo.InvariantCulture))) + "]",
    byte item => Hex(item),
    sbyte item => Hex((byte)item),
    ushort item => Hex(item),
    short item => Hex((ushort)item),
    uint item => Hex(item),
    int item => Hex((uint)item),
    ulong item => Hex(item),
    long item => Hex((ulong)item),
    float item => $"{item.ToString("R", CultureInfo.InvariantCulture)} ({Hex(BitConverter.SingleToUInt32Bits(item))})",
    double item => $"{item.ToString("R", CultureInfo.InvariantCulture)} ({Hex(BitConverter.DoubleToUInt64Bits(item))})",
    ITuple tuple => "(" + string.Join(", ", Enumerable.Range(0, tuple.Length).Select(index => Format(tuple[index]))) + ")",
    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
    _ => value.ToString() ?? string.Empty
  };

  private static string Hex(ulong value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

  private static Exception? Check<T>(Func<T, bool> predicate, T input, out bool holds) {
    try {
      holds = predicate(input);
      return null;
    } catch (Exception exception) {
      holds = false;
      return exception;
    }
  }

  private PropertyFalsifiedException Falsified<T>(string test, ulong seed, ulong declared, int index, T original, Exception? error, Func<T, IEnumerable<T>> shrink, Func<T, bool> predicate) {
    var shrunk = original;
    var steps = 0;
    while (steps < MaxShrinkSteps && Smaller(shrink, predicate, shrunk) is { } smaller) {
      (shrunk, error) = smaller;
      ++steps;
    }

    var failure = new PropertyFailure(test, seed, declared, index, Format(original), Format(shrunk), steps, error?.GetType().FullName, null);
    var message = new StringBuilder($"PLY0071: property '{test}' falsified after {index + 1} inputs (seed {seed}): {failure.Shrunk} (original {failure.Original}, {steps} shrink steps); replay with {SeedVariable}={seed}");
    if (seed != declared)
      message.Append($"; the seed came from {SeedVariable} and replaced the declared seed {declared}");

    if (failure.Exception is not null)
      message.Append($"; the predicate threw {failure.Exception}: {error!.Message}");

    try {
      failure = failure with { RecordPath = this.Write(failure) };
      message.Append($"; recorded in {failure.RecordPath}");
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      message.Append($"; the failure record was not written: {exception.Message}");
    }

    return new PropertyFalsifiedException(message.ToString(), failure);
  }

  private static (T Value, Exception? Error)? Smaller<T>(Func<T, IEnumerable<T>> shrink, Func<T, bool> predicate, T value) {
    foreach (var candidate in shrink(value)) {
      var error = Check(predicate, candidate, out var holds);
      if (!holds)
        return (candidate, error);
    }

    return null;
  }

  private string Write(PropertyFailure failure) {
    Directory.CreateDirectory(this.RecordDirectory);
    var path = Path.Combine(this.RecordDirectory, FileName(failure.Test) + ".json");
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) {
      writer.WriteStartObject();
      writer.WriteString("test", failure.Test);
      writer.WriteNumber("seed", failure.Seed);
      if (failure.Seed != failure.DeclaredSeed)
        writer.WriteNumber("declaredSeed", failure.DeclaredSeed);

      writer.WriteNumber("index", failure.Index);
      writer.WriteString("original", failure.Original);
      writer.WriteString("shrunk", failure.Shrunk);
      writer.WriteNumber("shrinkSteps", failure.ShrinkSteps);
      if (failure.Exception is not null)
        writer.WriteString("exception", failure.Exception);

      writer.WriteEndObject();
    }

    File.WriteAllBytes(path, stream.ToArray());
    return path;
  }

  private static string FileName(string test) {
    var name = new string([.. test.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_')]);
    return name.Length == 0 ? "property" : name;
  }

  private static string DefaultRecordDirectory() {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, "Polypiler.slnx")))
        return Path.Combine(directory.FullName, "artifacts", "properties");

    return Path.Combine(Environment.CurrentDirectory, "artifacts", "properties");
  }

}
