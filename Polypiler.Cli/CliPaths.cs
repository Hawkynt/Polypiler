using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The checks of the paths a command line names, each reported as its registered diagnostic with
/// the path as typed: <c>PLY0004</c> for an input, <c>PLY0005</c> for an output directory.
/// </summary>
public static class CliPaths {

  /// <summary>Checks that an input exists and can be opened for reading; reports <c>PLY0004</c> when not.</summary>
  /// <param name="path">The path as typed, relative to <see cref="CliContext.WorkingDirectory"/>.</param>
  /// <param name="context">The context to report to.</param>
  /// <returns>Whether the input can be read.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static bool RequireInput(string path, CliContext context) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(context);
    try {
      if (path.Length > 0) {
        using var stream = File.OpenRead(context.Resolve(path));
        return true;
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
      // Reported below, with the path as typed and without the operating system's words.
    }

    context.Report(Diagnostic.Create(CliDiagnostics.Unreadable, null, string.Format(CultureInfo.InvariantCulture, CliDiagnostics.InputUnreadable, path)));
    return false;
  }

  /// <summary>
  /// Makes an output directory ready: creates it when it does not exist, and accepts an empty one, or
  /// with <paramref name="force"/> one that has files, which are kept until the command overwrites
  /// them. Reports <c>PLY0005</c> for a directory that has files without <paramref name="force"/>, for
  /// a path that is a file, and for a directory that cannot be created.
  /// </summary>
  /// <param name="path">The path as typed, relative to <see cref="CliContext.WorkingDirectory"/>.</param>
  /// <param name="force">Whether <c>--force</c> was given.</param>
  /// <param name="context">The context to report to.</param>
  /// <returns>Whether the directory is ready.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static bool PrepareOutputDirectory(string path, bool force, CliContext context) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(context);
    if (path.Length == 0)
      return NotCreatable(path, "the path is empty", context);

    try {
      var full = context.Resolve(path);
      if (File.Exists(full))
        return NotCreatable(path, "a file of that name exists", context);

      if (Directory.Exists(full)) {
        if (force || !Directory.EnumerateFileSystemEntries(full).Any())
          return true;

        context.Report(Diagnostic.Create(CliDiagnostics.Unwritable, null, string.Format(CultureInfo.InvariantCulture, CliDiagnostics.OutputNotEmpty, path)));
        return false;
      }

      Directory.CreateDirectory(full);
      return true;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
      return NotCreatable(path, exception.Message, context);
    }
  }

  /// <summary>Reports that an output path cannot be created or written, for a reason.</summary>
  /// <param name="path">The path as typed.</param>
  /// <param name="reason">Why.</param>
  /// <param name="context">The context to report to.</param>
  /// <returns><see langword="false"/>.</returns>
  internal static bool NotCreatable(string path, string reason, CliContext context) {
    context.Report(Diagnostic.Create(CliDiagnostics.Unwritable, null, string.Format(CultureInfo.InvariantCulture, CliDiagnostics.OutputNotCreatable, path, reason)));
    return false;
  }

}
