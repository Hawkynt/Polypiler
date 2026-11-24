using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Acceptance.Batch;

internal sealed class DosCommandOracle(string bootImage, string? qemuData) {

  private const string _bootSha256 = "b88030401122d234ea6aafba3cfed7de2b7b1782700a67be5498edca6f9fec5d";
  private const string _shellSha256 = "65fa71b0a34e91feffc31297fb5e24d40767c72fb513e46aaac721bda7836110";
  private const string _marker = "PLY_DONE\r\n";
  private static readonly TimeSpan _toolTimeout = TimeSpan.FromSeconds(5);

  private readonly ProcessRunner _tools = new();

  internal static string Fixture(string name)
    => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "batch", "dos622", name);

  /// <summary>A host file and its relative 8.3 path on the writable guest disk.</summary>
  internal readonly record struct FixtureFile(string Source, string GuestPath);

  public (bool TimedOut, string Trace) Run(string script, IReadOnlyList<FixtureFile> supportFiles, TimeSpan timeout) {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
    var staged = StagedPaths(supportFiles);
    if (!File.Exists(bootImage))
      throw new InvalidDataException("The licensed MS-DOS 6.22 boot image is required.");
    var original = File.ReadAllBytes(bootImage);
    VerifyHash(original, _bootSha256, "boot image");

    var directory = Directory.CreateTempSubdirectory("polypiler-dos622-");
    try {
      var boot = Path.Combine(directory.FullName, "boot.img");
      var shell = Path.Combine(directory.FullName, "COMMAND.COM");
      var scratch = Path.Combine(directory.FullName, "scratch.img");
      File.WriteAllBytes(boot, original);
      this.Tool(["mcopy", "-i", boot, "::COMMAND.COM", shell]);
      VerifyHash(File.ReadAllBytes(shell), _shellSha256, "COMMAND.COM");
      this.Tool(["mcopy", "-o", "-i", boot, Fixture("BOOTSTRP.BAT"), "::AUTOEXEC.BAT"]);

      this.Tool(["mformat", "-C", "-i", scratch, "-f", "1440", "::"]);
      this.Tool(["mcopy", "-i", scratch, Path.GetFullPath(script), "::PROBE.BAT"]);
      var created = new HashSet<string>(StringComparer.Ordinal);
      foreach (var file in staged) {
        foreach (var parent in Parents(file.GuestPath))
          if (created.Add(parent))
            this.Tool(["mmd", "-i", scratch, "::/" + parent]);

        this.Tool(["mcopy", "-i", scratch, Path.GetFullPath(file.Source), "::/" + file.GuestPath]);
      }

      return this.Boot(boot, scratch, timeout);
    } finally {
      directory.Delete(recursive: true);
    }
  }

  private static List<FixtureFile> StagedPaths(IReadOnlyList<FixtureFile> supportFiles) {
    var files = new HashSet<string>(StringComparer.Ordinal) { "PROBE.BAT", "TRACE.TXT" };
    var directories = new HashSet<string>(StringComparer.Ordinal);
    var staged = new List<FixtureFile>(supportFiles.Count);
    foreach (var file in supportFiles) {
      var parts = file.GuestPath.Split('/', '\\');
      foreach (var part in parts)
        if (!IsShortName(part))
          throw new ArgumentException($"Invalid DOS 8.3 fixture path: {file.GuestPath}.", nameof(supportFiles));

      var path = string.Join('/', parts).ToUpperInvariant();
      if (!files.Add(path) || directories.Contains(path))
        throw new ArgumentException($"Reserved, duplicate, or conflicting DOS fixture path: {file.GuestPath}.", nameof(supportFiles));

      foreach (var parent in Parents(path)) {
        if (files.Contains(parent))
          throw new ArgumentException($"Conflicting DOS fixture directory: {parent}.", nameof(supportFiles));

        directories.Add(parent);
      }
      staged.Add(file with { GuestPath = path });
    }
    return staged;
  }

  private static bool IsShortName(string part) {
    static bool Valid(string value, int limit)
      => value.Length > 0 && value.Length <= limit && value.All(
        character => character is >= '!' and <= '~' && !"\"*+,/:;<=>?[\\]|".Contains(character));

    return part.Split('.') switch {
      [var name] => Valid(name, 8),
      [var name, var extension] => Valid(name, 8) && Valid(extension, 3),
      _ => false
    };
  }

  private static IEnumerable<string> Parents(string path) {
    for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
      yield return path[..slash];
  }

  private static void VerifyHash(byte[] bytes, string expected, string name) {
    var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (actual != expected)
      throw new InvalidDataException($"MS-DOS 6.22 {name} SHA-256 is {actual}; expected {expected}.");
  }

  private void Tool(string[] argv) {
    var result = this._tools.Run(argv, _toolTimeout);
    if (result.TimedOut)
      throw new TimeoutException($"{argv[0]} timed out while staging the DOS guest.");
    if (result.ExitCode != 0)
      throw new InvalidOperationException($"{argv[0]} exited with {result.ExitCode}: {result.StandardError.Trim()}");
  }

  private (bool TimedOut, string Trace) Boot(string boot, string scratch, TimeSpan timeout) {
    var start = new ProcessStartInfo("qemu-system-i386") { UseShellExecute = false, RedirectStandardError = true };
    if (!string.IsNullOrEmpty(qemuData)) {
      start.ArgumentList.Add("-L");
      start.ArgumentList.Add(qemuData);
    }

    foreach (var argument in new[] { "-machine", "pc", "-accel", "tcg", "-m", "16", "-boot", "order=a", "-display", "none",
      "-monitor", "none", "-serial", "none", "-parallel", "none", "-nic", "none", "-no-reboot",
      "-drive", $"file={DrivePath(boot)},index=0,if=floppy,format=raw,readonly=on",
      "-drive", $"file={DrivePath(scratch)},index=1,if=floppy,format=raw,cache=directsync" })
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var clock = Stopwatch.StartNew();
    var trace = "";
    try {
      while (timeout - clock.Elapsed > TimeSpan.Zero) {
        if (process.HasExited)
          throw new InvalidOperationException($"QEMU stopped before the DOS completion marker ({process.ExitCode}): {error.GetAwaiter().GetResult().Trim()}");
        var remaining = timeout - clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
          break;
        var current = this._tools.Run(["mtype", "-i", scratch, "::TRACE.TXT"], remaining);
        if (!current.TimedOut && current.ExitCode == 0) {
          trace = current.StandardOutput;
          if (trace == _marker || trace.EndsWith("\n" + _marker, StringComparison.Ordinal))
            return (false, trace);
        }
        Thread.Sleep(50);
      }
      return (true, trace);
    } finally {
      if (!process.HasExited)
        process.Kill(entireProcessTree: true);
      process.WaitForExit();
    }
  }

  private static string DrivePath(string path) => path.Replace(",", ",,", StringComparison.Ordinal);

}
