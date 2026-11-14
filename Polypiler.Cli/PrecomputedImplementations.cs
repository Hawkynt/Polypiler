using System;
using System.IO;
using Polypiler.Core.IR;

namespace Polypiler.Cli;

/// <summary>
/// The portable floating-point implementations as the build imported them from
/// <see cref="FloatOperationSources"/>: <c>polypiler-tooling float-implementations</c> writes the
/// module as <see cref="IrModuleFormat"/> bytes, and the executable carries them as a resource. They
/// depend only on the build, so no run imports their source again; the first translation that needs
/// one reads the bytes, once for the whole process.
/// </summary>
public static class PrecomputedImplementations {

  /// <summary>The code for precomputed data of the executable that is missing or damaged, an internal error.</summary>
  public const string Damaged = "PLY0101";

  /// <summary>The name of the resource that carries the module.</summary>
  public const string ResourceName = "Polypiler.Cli.float-implementations.irm";

  private static readonly Lazy<IrModule> _module = new(() => Load(() => typeof(PrecomputedImplementations).Assembly.GetManifestResourceStream(ResourceName)));

  /// <summary>The module, read on first use; every caller on every thread gets the same one.</summary>
  /// <exception cref="InvalidDataException">The resource is missing or damaged.</exception>
  public static IrModule Module => _module.Value;

  /// <summary>Reads the module from the bytes a stream gives.</summary>
  /// <param name="open">Opens the bytes, or gives <see langword="null"/> when there are none.</param>
  /// <returns>The module.</returns>
  /// <exception cref="InvalidDataException">There are no bytes, or they are no intact module; the message says which.</exception>
  public static IrModule Load(Func<Stream?> open) {
    using var stream = open() ?? throw new InvalidDataException($"The executable carries no '{ResourceName}'; it was built without its precomputed float implementations.");
    using var bytes = new MemoryStream();
    stream.CopyTo(bytes);
    return IrModuleFormat.Read(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
  }

}
