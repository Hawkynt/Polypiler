using System;
using System.Collections.Generic;
using System.Text;

namespace Polypiler.Core.Backends.Perl;

/// <summary>The Perl 5 language dialect targeted by the emitter.</summary>
public enum PerlDialect {

  /// <summary>Perl 5.6 (<c>use 5.006;</c>): no <c>//</c>, no <c>use constant { … }</c>, no <c>pack</c> <c>&lt;</c>/<c>&gt;</c>, no <c>Scalar::Util</c>.</summary>
  Perl5_6,

  /// <summary>Perl 5.8 (<c>use 5.008;</c>): hashref <c>use constant</c>, <c>Scalar::Util</c>, <c>utf8::downgrade</c>, Moose; no <c>//</c> or <c>pack</c> <c>&lt;</c>/<c>&gt;</c>.</summary>
  Perl5_8,

  /// <summary>Modern Perl 5 (5.10+): <c>//</c>, <c>pack</c> <c>&lt;</c>/<c>&gt;</c>, <c>use Exporter 'import'</c>, <c>Hash::Util::FieldHash</c>, C99 <c>POSIX</c>.</summary>
  Perl5

}

/// <summary>How <c>u8[]</c> byte buffers are stored in emitted Perl code.</summary>
public enum PerlByteStorage {

  /// <summary>Array reference of integers 0–255 (the default).</summary>
  Array,

  /// <summary>Reference to a mutable octet string accessed via <c>vec</c> and <c>substr</c>.</summary>
  String

}

/// <summary>How IR classes and interfaces are represented in emitted Perl 5 code.</summary>
public enum PerlObjectModel {

  /// <summary>Plain old Perl blessed hash references (<c>bless { … }, $class</c>) with <c>our @ISA</c>.</summary>
  Blessed,

  /// <summary>Moose classes (<c>use Moose;</c>) and roles (<c>use Moose::Role;</c>); requires Perl 5.8 or later.</summary>
  Moose,

  /// <summary>Inside-out objects (blessed scalar references with lexically scoped field hashes) enforcing strict field privacy.</summary>
  InsideOut

}

/// <summary>
/// The Perl 5 target profile a module is emitted for: selects the Perl dialect (<c>perl/5.6</c>,
/// <c>perl/5.8</c>, or <c>perl/5</c>), byte storage, and object model (<c>blessed</c>, <c>moose</c>,
/// or <c>inside-out</c>).
/// </summary>
public sealed record PerlProfile {

  private PerlProfile(string name, PerlDialect dialect, PerlByteStorage byteStorage, PerlObjectModel objectModel) {
    this.Name = name;
    this.Dialect = dialect;
    this.ByteStorage = byteStorage;
    this.ObjectModel = objectModel;
  }

  /// <summary>The canonical profile name (<c>perl/5.6</c>, <c>perl/5.8</c>, or <c>perl/5</c>).</summary>
  public string Name { get; }

  /// <summary>The Perl 5 dialect targeted by this profile.</summary>
  public PerlDialect Dialect { get; }

  /// <summary>How <c>u8[]</c> byte arrays are stored.</summary>
  public PerlByteStorage ByteStorage { get; init; }

  /// <summary>How classes and interfaces are represented.</summary>
  public PerlObjectModel ObjectModel { get; init; }

  /// <summary>Whether the defined-or operator <c>//</c> is available (Perl 5.10+).</summary>
  public bool HasDefinedOr => this.Dialect == PerlDialect.Perl5;

  /// <summary>Whether <c>pack</c>/<c>unpack</c> endian modifiers <c>&lt;</c> and <c>&gt;</c> are available (Perl 5.10+).</summary>
  public bool HasEndianPackModifiers => this.Dialect == PerlDialect.Perl5;

  /// <summary>Whether <c>use Exporter 'import';</c> is available without inheriting <c>@ISA</c> (Perl 5.8.3+).</summary>
  public bool HasExporterImport => this.Dialect == PerlDialect.Perl5;

  /// <summary>Whether hash-reference <c>use constant { … };</c> is available (Perl 5.8+).</summary>
  public bool HasConstantHashRef => this.Dialect != PerlDialect.Perl5_6;

  /// <summary>Whether <c>utf8::downgrade</c> is available in core (Perl 5.8+).</summary>
  public bool HasUtf8Downgrade => this.Dialect != PerlDialect.Perl5_6;

  /// <summary>Whether C99 <c>POSIX</c> math classification functions (<c>isnan</c>, <c>signbit</c>, <c>trunc</c>, …) are emitted directly.</summary>
  public bool HasC99Posix => this.Dialect == PerlDialect.Perl5;

  /// <summary>Whether <c>Hash::Util::FieldHash</c> is in core for inside-out objects (Perl 5.10+).</summary>
  public bool HasFieldHash => this.Dialect == PerlDialect.Perl5;

  /// <summary>Whether <c>Scalar::Util</c> is in core (Perl 5.8+).</summary>
  public bool HasScalarUtil => this.Dialect != PerlDialect.Perl5_6;

  /// <summary>Whether Moose is supported on this dialect (Perl 5.8+).</summary>
  public bool SupportsMoose => this.Dialect != PerlDialect.Perl5_6;

  /// <summary>The minimum version declaration emitted at the top of generated modules, or <see langword="null"/> for the default profile.</summary>
  public string? VersionPragma => this.Dialect switch {
    PerlDialect.Perl5_6 => "use 5.006;",
    PerlDialect.Perl5_8 => "use 5.008;",
    _ => null
  };

  /// <summary>The Perl 5.6 profile (<c>perl/5.6</c>).</summary>
  public static PerlProfile Perl56 { get; } = new("perl/5.6", PerlDialect.Perl5_6, PerlByteStorage.Array, PerlObjectModel.Blessed);

  /// <summary>The Perl 5.8 profile (<c>perl/5.8</c>).</summary>
  public static PerlProfile Perl58 { get; } = new("perl/5.8", PerlDialect.Perl5_8, PerlByteStorage.Array, PerlObjectModel.Blessed);

  /// <summary>The default Perl 5 profile (<c>perl/5</c> with array byte storage and blessed-hash objects).</summary>
  public static PerlProfile Default { get; } = new("perl/5", PerlDialect.Perl5, PerlByteStorage.Array, PerlObjectModel.Blessed);

  /// <summary>Every named Perl profile.</summary>
  public static IReadOnlyList<string> Names { get; } = ["perl/5.6", "perl/5.8", "perl/5"];

  /// <summary>Every accepted value for byte storage.</summary>
  public static IReadOnlyList<string> ByteStorageNames { get; } = ["array", "string"];

  /// <summary>Every accepted value for the object model.</summary>
  public static IReadOnlyList<string> ObjectModelNames { get; } = ["blessed", "moose", "inside-out"];

  /// <summary>Parses a profile name and optional byte-storage mode.</summary>
  /// <param name="name">The profile name (<c>perl/5.6</c>, <c>perl/5.8</c>, <c>perl/5</c>, or <see langword="null"/> for default).</param>
  /// <param name="byteStorage">The byte storage mode (<c>array</c>, <c>string</c>, or <see langword="null"/> for default).</param>
  /// <param name="profile">The parsed profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the arguments, or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when all arguments are valid.</returns>
  public static bool TryParse(string? name, string? byteStorage, out PerlProfile? profile, out string? error)
    => TryParse(name, byteStorage, null, out profile, out error);

  /// <summary>Parses a profile name, optional byte-storage mode, and optional object model.</summary>
  /// <param name="name">The profile name (<c>perl/5.6</c>, <c>perl/5.8</c>, <c>perl/5</c>, or <see langword="null"/> for default).</param>
  /// <param name="byteStorage">The byte storage mode (<c>array</c>, <c>string</c>, or <see langword="null"/> for default).</param>
  /// <param name="objectModel">The object model (<c>blessed</c>, <c>moose</c>, <c>inside-out</c>, or <see langword="null"/> for default).</param>
  /// <param name="profile">The parsed profile, or <see langword="null"/>.</param>
  /// <param name="error">What is wrong with the arguments, or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when all arguments are valid.</returns>
  public static bool TryParse(string? name, string? byteStorage, string? objectModel, out PerlProfile? profile, out string? error) {
    profile = null;
    error = null;
    var baseProfile = name switch {
      null or "perl/5" => Default,
      "perl/5.8" => Perl58,
      "perl/5.6" => Perl56,
      _ => null
    };
    if (baseProfile is null) {
      error = $"unknown Perl profile '{name}'; the profiles are perl/5.6, perl/5.8 and perl/5";
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

    var model = objectModel switch {
      null or "blessed" => PerlObjectModel.Blessed,
      "moose" => PerlObjectModel.Moose,
      "inside-out" => PerlObjectModel.InsideOut,
      _ => (PerlObjectModel)(-1)
    };
    if ((int)model < 0) {
      error = $"unknown Perl object model '{objectModel}'; the modes are blessed, moose and inside-out";
      return false;
    }

    if (model == PerlObjectModel.Moose && !baseProfile.SupportsMoose) {
      error = "the Moose object model requires Perl 5.8 or later; use profile 'perl/5.8' or 'perl/5', or choose 'blessed' or 'inside-out'";
      return false;
    }

    profile = baseProfile with { ByteStorage = storage, ObjectModel = model };
    return true;
  }

  /// <summary>Parses a profile name with default byte storage and object model.</summary>
  /// <param name="name">The profile name, or <see langword="null"/>.</param>
  /// <returns>The parsed profile.</returns>
  /// <exception cref="ArgumentException">The profile name is unknown.</exception>
  public static PerlProfile Parse(string? name)
    => TryParse(name, null, null, out var profile, out var error) ? profile! : throw new ArgumentException(error, nameof(name));

  /// <inheritdoc />
  public override string ToString() {
    var sb = new StringBuilder(this.Name);
    if (this.ByteStorage != PerlByteStorage.Array)
      sb.Append(";byteStorage=string");
    if (this.ObjectModel != PerlObjectModel.Blessed)
      sb.Append(";objects=").Append(this.ObjectModel == PerlObjectModel.Moose ? "moose" : "inside-out");
    return sb.ToString();
  }

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
