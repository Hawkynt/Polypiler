using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.Perl;

/// <summary>How <c>u8[]</c> byte buffers are stored in emitted Perl code.</summary>
public enum PerlByteStorage {

  /// <summary>Array reference of integers 0–255 (the default).</summary>
  Array,

  /// <summary>Reference to a mutable octet string accessed via <c>vec</c> and <c>substr</c>.</summary>
  String

}

/// <summary>
/// The Perl 5 target profile a module is emitted for: plain Perl 5 requiring 64-bit integers and
/// core modules only, with configurable byte storage.
/// </summary>
public sealed record PerlProfile {

  private PerlProfile(string name, PerlByteStorage byteStorage) {
    this.Name = name;
    this.ByteStorage = byteStorage;
  }

  /// <summary>The canonical profile name (<c>perl/5</c>).</summary>
  public string Name { get; }

  /// <summary>How <c>u8[]</c> byte arrays are stored.</summary>
  public PerlByteStorage ByteStorage { get; init; }

  /// <summary>The default Perl 5 profile (<c>perl/5</c> with array byte storage).</summary>
  public static PerlProfile Default { get; } = new("perl/5", PerlByteStorage.Array);

  /// <summary>Every named Perl profile.</summary>
  public static IReadOnlyList<string> Names { get; } = ["perl/5"];

  /// <summary>Every accepted value for byte storage.</summary>
  public static IReadOnlyList<string> ByteStorageNames { get; } = ["array", "string"];

  /// <summary>Parses a profile name and optional byte-storage mode.</summary>
  /// <param name="name">The profile name (<c>perl/5</c> or <see langword="null"/> for default).</param>
  /// <param name="byteStorage">The byte storage mode (<c>array</c>, <c>string</c>, or <see langword="null"/> for default).</param>
  /// <param name="profile">The parsed profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the arguments, or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when both arguments are valid.</returns>
  public static bool TryParse(string? name, string? byteStorage, out PerlProfile? profile, out string? error) {
    profile = null;
    error = null;
    if (name is not null && name != "perl/5") {
      error = $"unknown Perl profile '{name}'; the profiles are perl/5";
      return false;
    }

    var storage = byteStorage switch {
      null or "array" => PerlByteStorage.Array,
      "string" => PerlByteStorage.String,
      _ => (PerlByteStorage)(-1)
    };
    if ((int)storage < 0) {
      error = $"unknown Perl byte storage '{byteStorage}'; the modes are array and string";
      return false;
    }

    profile = Default with { ByteStorage = storage };
    return true;
  }

  /// <summary>Parses a profile name with default byte storage.</summary>
  /// <param name="name">The profile name, or <see langword="null"/>.</param>
  /// <returns>The parsed profile.</returns>
  /// <exception cref="ArgumentException">The profile name is unknown.</exception>
  public static PerlProfile Parse(string? name)
    => TryParse(name, null, out var profile, out var error) ? profile! : throw new ArgumentException(error, nameof(name));

  /// <inheritdoc />
  public override string ToString()
    => this.ByteStorage == PerlByteStorage.Array ? this.Name : $"{this.Name};byteStorage=string";

}

/// <summary>Options controlling module layout, naming and test generation for Perl emission.</summary>
public sealed record PerlEmitOptions {

  /// <summary>Default emission options.</summary>
  public static PerlEmitOptions Default { get; } = new();

  /// <summary>Overrides the Perl package name (e.g. <c>Hawkynt::Checksums::Adler32</c>); <see langword="null"/> derives it from the module name.</summary>
  public string? PackageName { get; init; }

  /// <summary>The <c>our $VERSION</c> value written into the module header.</summary>
  public string ModuleVersion { get; init; } = "0.001";

  /// <summary>Names of IR functions that are test-role functions emitted into <c>t/&lt;module&gt;.t</c>.</summary>
  public IReadOnlySet<string> TestFunctions { get; init; } = new HashSet<string>(StringComparer.Ordinal);

  /// <summary>Whether read-only <c>u8[]</c> parameters of exported functions accept both octet strings and array references.</summary>
  public bool ShapeByteApi { get; init; } = true;

}
