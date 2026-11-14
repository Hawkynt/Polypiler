using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Running;

/// <summary>The working directory of a run: created fresh with the plan's files, and deleted with everything in it.</summary>
[TestFixture]
public sealed class RunDirectoryTests {

  [Test]
  public void ADirectory_IsCreatedWithTheFiles_AndDeleted() {
    var directory = RunDirectory.Create(new Dictionary<string, ReadOnlyMemory<byte>> { ["a.txt"] = new byte[] { 1, 2 }, ["src/deep/b.bin"] = new byte[] { 0xFF } });
    try {
      Assert.That(Path.GetFileName(directory), Does.Match("^polypiler-run-[0-9a-f]{16}$"));
      Assert.That(Path.GetDirectoryName(directory), Is.EqualTo(Path.TrimEndingDirectorySeparator(Path.GetTempPath())));
      Assert.That(File.ReadAllBytes(Path.Combine(directory, "a.txt")), Is.EqualTo(new byte[] { 1, 2 }));
      Assert.That(File.ReadAllBytes(Path.Combine(directory, "src", "deep", "b.bin")), Is.EqualTo(new byte[] { 0xFF }));
    } finally {
      RunDirectory.Delete(directory);
    }

    Assert.That(Directory.Exists(directory), Is.False);
  }

  [Test]
  public void TwoDirectories_AreDifferent() {
    var first = RunDirectory.Create(new Dictionary<string, ReadOnlyMemory<byte>>());
    var second = RunDirectory.Create(new Dictionary<string, ReadOnlyMemory<byte>>());
    Assert.That(first, Is.Not.EqualTo(second));
    RunDirectory.Delete(first);
    RunDirectory.Delete(second);
  }

  [Test]
  public void FilesThatCannotBeWritten_LeaveNoDirectoryBehind() {
    var parent = Directory.CreateTempSubdirectory("polypiler-parent-").FullName;
    try {
      Assert.That(() => RunDirectory.Create(new Dictionary<string, ReadOnlyMemory<byte>> { ["a"] = new byte[] { 1 }, ["a/b"] = new byte[] { 2 } }, parent), Throws.InstanceOf<IOException>());
      Assert.That(Directory.GetFileSystemEntries(parent), Is.Empty);
    } finally {
      Directory.Delete(parent, true);
    }
  }

  [Test]
  public void ADirectoryThatCannotBeDeleted_IsTriedAgainAndThenAnError() {
    if (OperatingSystem.IsWindows()) {
      Assert.Pass("a folder whose entries cannot be deleted needs a Unix permission; on Windows the retry serves files held open");
      return;
    }

    var parent = Directory.CreateTempSubdirectory("polypiler-parent-").FullName;
    var directory = RunDirectory.Create(new Dictionary<string, ReadOnlyMemory<byte>> { ["a"] = new byte[] { 1 } }, parent);
    File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    try {
      Assert.That(() => RunDirectory.Delete(directory), Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
    } finally {
      File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      Directory.Delete(parent, true);
    }
  }

  [Test]
  public void AReadOnlyTree_IsDeleted() {
    var directory = RunDirectory.Create(new Dictionary<string, ReadOnlyMemory<byte>> { ["locked/inner/file"] = new byte[] { 1 }, ["top"] = new byte[] { 2 } });
    File.SetAttributes(Path.Combine(directory, "top"), FileAttributes.ReadOnly);
    File.SetAttributes(Path.Combine(directory, "locked", "inner", "file"), FileAttributes.ReadOnly);
    if (!OperatingSystem.IsWindows()) {
      File.CreateSymbolicLink(Path.Combine(directory, "link"), "/");
      File.SetUnixFileMode(Path.Combine(directory, "locked", "inner"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
      File.SetUnixFileMode(Path.Combine(directory, "locked"), UnixFileMode.None);
    }

    RunDirectory.Delete(directory);
    Assert.That(Directory.Exists(directory), Is.False);
  }

  [Test]
  public void AMissingDirectory_IsNoError()
    => Assert.That(() => RunDirectory.Delete(Path.Combine(Path.GetTempPath(), RunDirectory.Prefix + "never-created")), Throws.Nothing);

}
