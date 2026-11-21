using System;

namespace Polypiler.Cli.Options;

/// <summary>
/// The typed key of one value in an <see cref="OptionBag"/>, declared by the area that owns the
/// option, in a project the pipeline can reference, so that a command never names the value's type.
/// Two keys are the same key when they have the same name and the same value type.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
public sealed record OptionKey<T> {

  /// <summary>Creates a key.</summary>
  /// <param name="name">The name, usually the option's name without its leading <c>--</c>.</param>
  /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
  public OptionKey(string name) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    this.Name = name;
  }

  /// <summary>The name of the key.</summary>
  public string Name { get; }

}
