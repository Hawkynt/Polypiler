using System;
using System.Collections.Generic;

namespace Polypiler.Cli.Options;

/// <summary>
/// The values the contributors of a command read from its command line, by their typed keys. A
/// command passes the bag on without knowing what is in it; the area that declared a key reads it.
/// </summary>
public sealed class OptionBag {

  private readonly Dictionary<object, object?> _values = [];

  /// <summary>How many values the bag holds.</summary>
  public int Count => this._values.Count;

  /// <summary>Sets the value of a key, replacing one set before.</summary>
  /// <typeparam name="T">The type of the value.</typeparam>
  /// <param name="key">The key.</param>
  /// <param name="value">The value.</param>
  /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
  public void Set<T>(OptionKey<T> key, T value) {
    ArgumentNullException.ThrowIfNull(key);
    this._values[key] = value;
  }

  /// <summary>Gets the value of a key.</summary>
  /// <typeparam name="T">The type of the value.</typeparam>
  /// <param name="key">The key.</param>
  /// <param name="value">The value, or the default of <typeparamref name="T"/> when the key was not set.</param>
  /// <returns>Whether the key was set.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
  public bool TryGet<T>(OptionKey<T> key, out T value) {
    ArgumentNullException.ThrowIfNull(key);
    if (this._values.TryGetValue(key, out var stored)) {
      value = (T)stored!;
      return true;
    }

    value = default!;
    return false;
  }

  /// <summary>The value of a key, or a fallback when the key was not set.</summary>
  /// <typeparam name="T">The type of the value.</typeparam>
  /// <param name="key">The key.</param>
  /// <param name="fallback">What to return when the key was not set.</param>
  /// <returns>The value or <paramref name="fallback"/>.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
  public T GetOrDefault<T>(OptionKey<T> key, T fallback) => this.TryGet(key, out var value) ? value : fallback;

}
