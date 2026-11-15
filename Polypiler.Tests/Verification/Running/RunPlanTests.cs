using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Running;

/// <summary>The plan and the process of a run: what they accept and what they refuse at construction.</summary>
[TestFixture]
public sealed class RunPlanTests {

  private static readonly ProcessSpec _execute = new("dotnet-sdk", "out/hello.dll");

  private static RunPlan Plan(params string[] names) {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>();
    foreach (var name in names)
      files[name] = new byte[] { 1 };
    return new(files, [], _execute, RunLimits.Default, TrustLevel.Trusted);
  }

  [TestCase("Program.cs")]
  [TestCase("src/Program.cs")]
  [TestCase(@"src\Program.cs")]
  [TestCase("./x")]
  [TestCase("a..b")]
  [TestCase("..x")]
  public void ARelativeName_IsAccepted(string name)
    => Assert.That(Plan(name).Files.Keys, Is.EqualTo(new[] { name }));

  /// <summary>Test design 21: a name escaping the directory.</summary>
  [TestCase("../x")]
  [TestCase("/etc/x")]
  [TestCase("a/../../x")]
  [TestCase(@"a\..\x")]
  [TestCase("..")]
  [TestCase(@"\x")]
  [TestCase(@"C:\x")]
  [TestCase("c:x")]
  [TestCase("")]
  public void ANameLeavingTheDirectory_IsAnArgumentException(string name)
    => Assert.That(() => Plan(name), Throws.ArgumentException.With.Message.Contains($"'{name}' leaves the working directory"));

  [Test]
  public void ThePlan_KeepsItsPartsAndRunsTheBuildBeforeTheExecuteStep() {
    var build = new ProcessSpec("dotnet-sdk", "build");
    var plan = new RunPlan(new Dictionary<string, ReadOnlyMemory<byte>>(), [build], _execute, RunLimits.Default, TrustLevel.Untrusted);
    Assert.That(plan.Steps, Is.EqualTo(new[] { build, _execute }));
    Assert.That(plan.Build, Is.EqualTo(new[] { build }));
    Assert.That(plan.Execute, Is.SameAs(_execute));
    Assert.That(plan.Limits, Is.SameAs(RunLimits.Default));
    Assert.That(plan.Trust, Is.EqualTo(TrustLevel.Untrusted));
    Assert.That(plan.Files, Is.Empty);
  }

  [Test]
  public void AMissingPart_IsRefused() {
    var files = new Dictionary<string, ReadOnlyMemory<byte>>();
    Assert.That(() => new RunPlan(null!, [], _execute, RunLimits.Default, TrustLevel.Trusted), Throws.ArgumentNullException);
    Assert.That(() => new RunPlan(files, null!, _execute, RunLimits.Default, TrustLevel.Trusted), Throws.ArgumentNullException);
    Assert.That(() => new RunPlan(files, [], null!, RunLimits.Default, TrustLevel.Trusted), Throws.ArgumentNullException);
    Assert.That(() => new RunPlan(files, [], _execute, null!, TrustLevel.Trusted), Throws.ArgumentNullException);
  }

  [Test]
  public void AProcess_IsNamedByItsToolchainAndArguments() {
    var spec = new ProcessSpec("dotnet-sdk", "build", "-c", "Release");
    Assert.That(spec.ToString(), Is.EqualTo("dotnet-sdk build -c Release"));
    Assert.That(spec.Stdin.IsEmpty, Is.True);
    Assert.That(new ProcessSpec("node", ["x.js"], new byte[] { 7 }).Stdin.ToArray(), Is.EqualTo(new byte[] { 7 }));
  }

  [Test]
  public void AProcess_CopiesItsArguments() {
    var arguments = new List<string> { "a" };
    var spec = new ProcessSpec("node", arguments, ReadOnlyMemory<byte>.Empty);
    arguments.Add("b");
    Assert.That(spec.Arguments, Is.EqualTo(new[] { "a" }));
  }

  [TestCase("out/hello", "out/hello")]
  [TestCase(@"out\hello.exe", @"out\hello.exe")]
  public void ABuiltProgram_IsNamedByItsPlaceInTheRunDirectory(string program, string expected) {
    var spec = ProcessSpec.Built(program, "a");
    Assert.That((spec.Toolchain, spec.BuiltProgram, spec.Arguments), Is.EqualTo(("built:" + expected, expected, new[] { "a" })));
    Assert.That(new ProcessSpec("dotnet-sdk", "x").BuiltProgram, Is.Null);
  }

  /// <summary>A built program is a file of the run's directory, by the same rule as the plan's files.</summary>
  [TestCase("../x")]
  [TestCase("/usr/bin/env")]
  [TestCase(@"C:\x.exe")]
  [TestCase("")]
  public void ABuiltProgramOutsideTheRunDirectory_IsAnArgumentException(string program)
    => Assert.That(() => ProcessSpec.Built(program), Throws.ArgumentException.With.Message.Contains($"'{program}' leaves the working directory"));

  [Test]
  public void AProcessWithoutToolchainOrArguments_IsRefused() {
    Assert.That(() => new ProcessSpec("", "x"), Throws.ArgumentException);
    Assert.That(() => new ProcessSpec(null!, "x"), Throws.ArgumentException);
    Assert.That(() => new ProcessSpec("node", null!, ReadOnlyMemory<byte>.Empty), Throws.ArgumentNullException);
  }

}
