using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Polypiler.Tests.Acceptance.Startup;

/// <summary>
/// Feature: The executable starts quickly (#1339). The executable is published the way it ships, for
/// the operating system the tests run on - ReadyToRun, with the portable floating-point
/// implementations precomputed - and the slowest fixture, a strict float translation, is translated
/// by a cold process five times per target and floating-point mode. The median wall-clock time is the
/// start-up the test records and holds to the budget; every run writes exactly what the same command
/// line writes in process.
/// </summary>
[TestFixture, Category("Acceptance"), Category("Performance"), NonParallelizable]
[Layer("core")]
public sealed class StartupBudgetTests {

  private const int Runs = 5;

  /// <summary>The budget of the issue, measured on the 16-core workstation.</summary>
  private static readonly TimeSpan _budget = TimeSpan.FromSeconds(1.0);

  /// <summary>
  /// A shared CI runner has two cores and runs the other test assembly at the same time; there the
  /// median is recorded and held to a budget that still catches a return of the several seconds a
  /// start-up took before (7 to 17 s on the runners).
  /// </summary>
  private static readonly TimeSpan _sharedRunnerBudget = TimeSpan.FromSeconds(3.0);

  private static string _root = "";
  private static string _directory = "";
  private static string _executable = "";

  [OneTimeSetUp]
  public static void Publish() {
    _root = RepositoryRoot();
    _directory = Path.Combine(Path.GetTempPath(), "polypiler-startup-" + Guid.NewGuid().ToString("N"));
    var published = Path.Combine(_directory, "publish");
    var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    var system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
    var (exitCode, output) = Run("dotnet", _root, Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name => name.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)),
      "publish", Path.Combine(_root, "Polypiler.Cli", "Polypiler.Cli.csproj"), "-c", "Release", "-r", $"{system}-{architecture}", "--self-contained", "false",
      "-o", published, "-nodeReuse:false", "--nologo");
    Assert.That(exitCode, Is.Zero, $"the executable could not be published:{Environment.NewLine}{output}");
    _executable = Path.Combine(published, OperatingSystem.IsWindows() ? "polypiler.exe" : "polypiler");
  }

  [OneTimeTearDown]
  public static void Delete() {
    if (Directory.Exists(_directory))
      Directory.Delete(_directory, true);
  }

  // Scenario: A strict float translation starts fast - and the same for the other target and mode.
  [TestCase("javascript", "es5", "strict")]
  [TestCase("javascript", "es5", "accurate")]
  [TestCase("csharp", "net2.0", "strict")]
  [TestCase("csharp", "net2.0", "accurate")]
  public void TheSlowestFixture_StartsWithinTheBudget(string target, string profile, string mode) {
    var fixture = Path.Combine(_root, "tests", "fixtures", "prototype", "float", "Dct8.cs");
    var expected = Path.Combine(_directory, $"{target}-{mode}-in-process");
    string[] Arguments(string output) => ["translate", fixture, "--from", "csharp", "--to", target, "--profile", profile, "--float", mode, "--output", output];

    using (var text = new StringWriter())
      Assert.That(Polypiler.Cli.Program.Run(Arguments(expected), text, text), Is.Zero, text.ToString());

    var times = new List<double>();
    for (var run = 0; run < Runs; ++run) {
      var output = Path.Combine(_directory, $"{target}-{mode}-{run}");
      var clock = Stopwatch.StartNew();
      var (exitCode, written) = Run(_executable, _directory, [], Arguments(output));
      times.Add(clock.Elapsed.TotalSeconds);
      Assert.That(exitCode, Is.Zero, written);
      Assert.That(Files(output), Is.EqualTo(Files(expected)), "the executable writes what the same command line writes in process");
    }

    var median = times.Order().ElementAt(Runs / 2);
    var budget = Environment.GetEnvironmentVariable("CI") == "true" ? _sharedRunnerBudget : _budget;
    TestContext.Out.WriteLine($"cold start on {RuntimeInformation.OSDescription}, {target} {profile} --float {mode}: median {median:F3} s of {string.Join(", ", times.Select(time => time.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))} s; budget {budget.TotalSeconds:F1} s");
    Assert.That(median, Is.LessThanOrEqualTo(budget.TotalSeconds));
  }

  /// <summary>Every file of a directory by name, with its bytes.</summary>
  private static Dictionary<string, byte[]> Files(string directory)
    => Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);

  private static string RepositoryRoot() {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
      if (File.Exists(Path.Combine(directory.FullName, "Polypiler.slnx")))
        return directory.FullName;

    throw new InvalidOperationException($"no Polypiler.slnx above {AppContext.BaseDirectory}");
  }

  /// <summary>Runs a process to its end and returns its exit code and everything it wrote.</summary>
  /// <param name="removed">Environment variables the process must not inherit.</param>
  private static (int ExitCode, string Output) Run(string file, string workingDirectory, IEnumerable<string> removed, params string[] arguments) {
    var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);
    foreach (var name in removed)
      start.Environment.Remove(name);

    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output + error.Result);
  }

}
