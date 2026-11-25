using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.Cpp;

/// <summary>The ISO C++ language standard targeted by the emitter.</summary>
public enum CppStandard {

  /// <summary>ISO C++17: portable bit helpers and <c>std::memcpy</c> for bit-casts.</summary>
  Cpp17,

  /// <summary>ISO C++20: <c>&lt;bit&gt;</c> bit operations and <c>std::bit_cast</c>.</summary>
  Cpp20,

  /// <summary>ISO C++23: C++20 plus <c>std::byteswap</c>.</summary>
  Cpp23

}

/// <summary>The C++ target profile a module is emitted for (<c>cpp/cpp17</c>, <c>cpp/cpp20</c>, or <c>cpp/cpp23</c>).</summary>
/// <param name="Name">The canonical profile name.</param>
/// <param name="Standard">The targeted ISO C++ standard.</param>
public sealed record CppProfile(string Name, CppStandard Standard) {

  /// <summary>The ISO C++17 profile (<c>cpp/cpp17</c>).</summary>
  public static CppProfile Cpp17 { get; } = new("cpp/cpp17", CppStandard.Cpp17);

  /// <summary>The ISO C++20 profile (<c>cpp/cpp20</c>).</summary>
  public static CppProfile Cpp20 { get; } = new("cpp/cpp20", CppStandard.Cpp20);

  /// <summary>The ISO C++23 profile (<c>cpp/cpp23</c>).</summary>
  public static CppProfile Cpp23 { get; } = new("cpp/cpp23", CppStandard.Cpp23);

  /// <summary>The default C++ profile (<c>cpp/cpp20</c>).</summary>
  public static CppProfile Default => Cpp20;

  /// <summary>Every named C++ profile.</summary>
  public static IReadOnlyList<string> Names { get; } = ["cpp/cpp17", "cpp/cpp20", "cpp/cpp23"];

  /// <summary>Whether the <c>&lt;bit&gt;</c> header (<c>std::rotl</c>, <c>std::popcount</c>, <c>std::bit_cast</c>, …) is available.</summary>
  public bool HasBitHeader => this.Standard >= CppStandard.Cpp20;

  /// <summary>Whether <c>std::byteswap</c> is available (C++23).</summary>
  public bool HasByteSwap => this.Standard >= CppStandard.Cpp23;

  /// <summary>Parses a C++ profile name, defaulting to <see cref="Default"/> when <see langword="null"/>.</summary>
  /// <param name="profile">The profile name, or <see langword="null"/>.</param>
  /// <returns>The parsed profile.</returns>
  /// <exception cref="ArgumentException">The profile name is unknown.</exception>
  public static CppProfile Parse(string? profile = null)
    => TryParse(profile, out var result, out var error) ? result! : throw new ArgumentException(error, nameof(profile));

  /// <summary>Tries to parse a C++ profile name.</summary>
  /// <param name="profile">The profile name, or <see langword="null"/> for <see cref="Default"/>.</param>
  /// <param name="result">The parsed profile, or <see langword="null"/>.</param>
  /// <param name="error">The reason parsing failed, or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> when the profile name is valid.</returns>
  public static bool TryParse(string? profile, out CppProfile? result, out string? error) {
    result = profile switch {
      null or "cpp/cpp20" => Cpp20,
      "cpp/cpp17" => Cpp17,
      "cpp/cpp23" => Cpp23,
      _ => null
    };
    if (result is null) {
      error = $"unknown C++ profile '{profile}'; the profiles are cpp/cpp17, cpp/cpp20 and cpp/cpp23";
      return false;
    }

    error = null;
    return true;
  }

  /// <inheritdoc />
  public override string ToString() => this.Name;

}

/// <summary>Options controlling namespace and layout for C++ emission.</summary>
public sealed record CppEmitOptions {

  /// <summary>Default emission options.</summary>
  public static CppEmitOptions Default { get; } = new();

  /// <summary>Overrides the emitted C++ namespace (e.g. <c>my::pkg</c>); <see langword="null"/> derives it from the module name.</summary>
  public string? Namespace { get; init; }

}
