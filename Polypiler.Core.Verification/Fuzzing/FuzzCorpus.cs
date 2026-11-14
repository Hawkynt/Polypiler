using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Polypiler.Core.Verification.Fuzzing;

/// <summary>One input of a target's seed corpus.</summary>
/// <param name="Name">The path below the target's corpus directory with <c>/</c> separators, e.g. <c>corpus/seed.bin</c>.</param>
/// <param name="Content">The bytes of the file.</param>
/// <param name="Sha256">The SHA-256 of the bytes, lower-case hex.</param>
/// <param name="Upstream">
/// Where the entry was taken from, <c>owner/repo@sha:path</c>, as the file beside it named
/// <c>&lt;entry&gt;.upstream</c> says; <see langword="null"/> for an entry written for this repository.
/// </param>
public sealed record FuzzCorpusEntry(string Name, byte[] Content, string Sha256, string? Upstream);

/// <summary>
/// The seed corpus of a target: every <c>*.bin</c> file of <c>corpus/</c> and of <c>regressions/</c>
/// below the target's directory, sorted ordinally by that path, so <c>corpus/</c> comes first.
/// </summary>
public static class FuzzCorpus {

  /// <summary>The folder of the seed inputs.</summary>
  public const string CorpusFolder = "corpus";

  /// <summary>The folder of the inputs of fixed findings.</summary>
  public const string RegressionsFolder = "regressions";

  /// <summary>Reads a target's corpus.</summary>
  /// <param name="directory">The target's directory, e.g. <c>tests/fixtures/fuzz/cli-args</c>.</param>
  /// <exception cref="FuzzCorpusException">The directory does not exist, or holds no entry.</exception>
  public static IReadOnlyList<FuzzCorpusEntry> Load(string directory) {
    ArgumentNullException.ThrowIfNull(directory);
    if (!Directory.Exists(directory))
      throw new FuzzCorpusException($"corpus directory '{directory}' does not exist");

    var entries = new[] { CorpusFolder, RegressionsFolder }
      .Select(folder => Path.Combine(directory, folder))
      .Where(Directory.Exists)
      .SelectMany(folder => Directory.EnumerateFiles(folder, "*.bin"))
      .Select(file => (Name: Path.GetRelativePath(directory, file).Replace('\\', '/'), File: file))
      .OrderBy(entry => entry.Name, StringComparer.Ordinal)
      .Select(entry => Entry(entry.Name, entry.File))
      .ToList();
    if (entries.Count == 0)
      throw new FuzzCorpusException($"corpus is empty: '{directory}' has no {CorpusFolder}/*.bin or {RegressionsFolder}/*.bin");
    return entries;
  }

  /// <summary>The lower-case hex SHA-256 of some bytes.</summary>
  public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  private static FuzzCorpusEntry Entry(string name, string file) {
    var content = File.ReadAllBytes(file);
    var upstream = file + ".upstream";
    return new(name, content, Sha256(content), File.Exists(upstream) ? File.ReadAllText(upstream).Trim() : null);
  }

}

/// <summary>Thrown when a target's corpus directory is missing or empty.</summary>
public sealed class FuzzCorpusException : Exception {

  /// <summary>Creates the exception with the reason.</summary>
  public FuzzCorpusException(string message) : base(message) { }

}
