using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Polypiler.Core.Verification.Running;

/// <summary>The working directory of one run: a fresh <c>polypiler-run-&lt;random&gt;</c> under the system's temporary folder.</summary>
public static class RunDirectory {

  /// <summary>The prefix of every run directory's name.</summary>
  public const string Prefix = "polypiler-run-";

  private const int DeleteAttempts = 5;
  private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(100);

  /// <summary>Creates a new run directory under the system's temporary folder and writes the files into it.</summary>
  /// <param name="files">The files, by name relative to the directory; the directories they lie in are created.</param>
  /// <returns>The directory's full path.</returns>
  public static string Create(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files) => Create(files, Path.GetTempPath());

  /// <summary>Creates a new run directory under a given folder and writes the files into it.</summary>
  /// <param name="files">The files, by name relative to the directory; the directories they lie in are created.</param>
  /// <param name="parent">The folder the directory is made in.</param>
  /// <returns>The directory's full path.</returns>
  public static string Create(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string parent) {
    var directory = Path.Combine(Path.GetFullPath(parent), Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
    Directory.CreateDirectory(directory);
    try {
      foreach (var (name, bytes) in files) {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.ToArray());
      }
    } catch {
      Delete(directory);
      throw;
    }

    return directory;
  }

  /// <summary>
  /// Deletes a run directory with everything in it. Read-only entries are made writable first, and a
  /// file that is still held open, which on Windows outlives a killed process for a moment, is tried
  /// again.
  /// </summary>
  /// <param name="directory">The directory; a directory that does not exist is no error.</param>
  /// <exception cref="IOException">The directory could still not be deleted after the last attempt.</exception>
  public static void Delete(string directory) {
    for (var attempt = 1; ; ++attempt) {
      try {
        if (Directory.Exists(directory)) {
          MakeWritable(new DirectoryInfo(directory));
          Directory.Delete(directory, true);
        }

        return;
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < DeleteAttempts) {
        Thread.Sleep(_retryDelay);
      }
    }
  }

  /// <summary>Opens a directory tree for deletion: every directory readable, writable and searchable for its owner, no entry read-only. Links are not followed.</summary>
  private static void MakeWritable(DirectoryInfo directory) {
    if (OperatingSystem.IsWindows())
      directory.Attributes &= ~FileAttributes.ReadOnly;
    else
      directory.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    foreach (var entry in directory.EnumerateFileSystemInfos())
      if (entry.LinkTarget != null)
        continue;
      else if (entry is DirectoryInfo child)
        MakeWritable(child);
      else if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
        entry.Attributes &= ~FileAttributes.ReadOnly;
  }

}
