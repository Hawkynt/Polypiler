using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The code of Polypiler's own that legalization copies into a module, in both of its libraries: the
/// portable floating-point implementations of <see cref="FloatOperationSources"/> and the helpers of
/// <see cref="HelperOperationSources"/>. A backend asks here where a function a module carries comes
/// from, under which licence, and which notices the output keeps for it.
/// </summary>
public static class ImplementationSources {

  /// <summary>The file that declares a function of either library, with the licence its header states.</summary>
  /// <param name="function">The name of the function in its library.</param>
  /// <returns>The path of the file in the repository and its licence; <see langword="null"/> for a name neither library declares.</returns>
  public static (string Path, string Licence)? Origin(string function)
    => FloatOperationSources.Origin(function) ?? HelperOperationSources.Origin(function);

  /// <summary>Whether a function is one of the portable floating-point implementations.</summary>
  /// <param name="function">The name of the function in its library.</param>
  public static bool IsFloat(string function) => FloatOperationSources.Origin(function) is not null;

  /// <summary>A function of either library as a backend wrote it into a file, as a helper of the file.</summary>
  /// <param name="name">The name the function has in the file, which is also its symbol.</param>
  /// <param name="implementation">The name of the function in its library.</param>
  /// <param name="text">Its text, as it stands in the file.</param>
  /// <exception cref="ArgumentException">Neither library declares <paramref name="implementation"/>.</exception>
  public static HelperText Helper(string name, string implementation, string text) {
    var (path, licence) = Origin(implementation) ?? throw new ArgumentException($"No library declares '{implementation}'.", nameof(implementation));
    return new(name, name, licence, EmittedHelper.OriginOf(path), text);
  }

  /// <summary>The lines of the notices an output keeps for the functions of both libraries it carries: those of the floating-point library first.</summary>
  /// <param name="implementations">The names of the functions in their libraries.</param>
  public static IReadOnlyList<string> Notices(IEnumerable<string> implementations) {
    ArgumentNullException.ThrowIfNull(implementations);
    var names = implementations.ToList();
    return [.. FloatOperationSources.Notices(names), .. HelperOperationSources.Notices(names)];
  }

  /// <summary>
  /// The two libraries as one module: the functions, constants and types of the floating-point
  /// implementations and then of the helpers, under the name of the first.
  /// </summary>
  /// <param name="floats">The portable floating-point implementations, as the C# frontend imports them.</param>
  /// <param name="helpers">The helpers, as the C# frontend imports them.</param>
  /// <returns>The module.</returns>
  /// <exception cref="ArgumentException">A function or constant of the same name is in both.</exception>
  public static IrModule Merge(IrModule floats, IrModule helpers) {
    ArgumentNullException.ThrowIfNull(floats);
    ArgumentNullException.ThrowIfNull(helpers);
    var functions = floats.Functions.Select(function => function.Name).Intersect(helpers.Functions.Select(function => function.Name));
    var constants = floats.Constants.Select(constant => constant.Name).Intersect(helpers.Constants.Select(constant => constant.Name));
    var both = functions.Concat(constants).ToList();
    if (both.Count > 0)
      throw new ArgumentException($"Both libraries declare {string.Join(", ", both.Select(name => $"'{name}'"))}.", nameof(helpers));

    return floats with {
      Functions = [.. floats.Functions, .. helpers.Functions],
      Constants = [.. floats.Constants, .. helpers.Constants],
      Types = [.. floats.Types, .. helpers.Types.Where(type => !floats.Types.Contains(type))]
    };
  }

}
