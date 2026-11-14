using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Float.Support;
using Polypiler.Tests.Precomputed.Support;

namespace Polypiler.Tests.Precomputed;

/// <summary>
/// The portable floating-point implementations that the executable carries precomputed equal the
/// module the C# frontend imports from their source at run time, are read once for the whole process,
/// and are refused with an internal error, never used, when they are missing or damaged.
/// </summary>
[TestFixture]
public sealed class PrecomputedImplementationsTests {

  private static byte[] Embedded() {
    using var stream = typeof(PrecomputedImplementations).Assembly.GetManifestResourceStream(PrecomputedImplementations.ResourceName)!;
    using var bytes = new MemoryStream();
    stream.CopyTo(bytes);
    return bytes.ToArray();
  }

  // Scenario: Precomputed data equals the run-time computation.
  [Test]
  public void TheEmbeddedBytes_AreTheModuleImportedFromTheSource()
    => Assert.That(Embedded(), Is.EqualTo(IrModuleFormat.Write(FloatImplementations.Module)), "the build embedded another module than the source imports to");

  [Test]
  public void TheModuleRead_IsTheModuleImportedFromTheSource()
    => Assert.That(IrGraph.Difference(PrecomputedImplementations.Module, FloatImplementations.Module), Is.Null);

  [Test]
  public void TheModuleRead_Verifies()
    => Assert.That(IrVerifier.Verify(PrecomputedImplementations.Module), Is.Empty);

  [Test]
  public void FirstUseFromManyThreads_ReadsTheModuleOnce() {
    var seen = new ConcurrentBag<IrModule>();
    Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, _ => seen.Add(PrecomputedImplementations.Module));
    Assert.That(seen.Distinct(ReferenceEqualityComparer.Instance).Count(), Is.EqualTo(1));
  }

  [Test]
  public void ParallelStrictTranslations_WriteTheSameFiles() {
    var root = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    try {
      var outcomes = new ConcurrentBag<(int ExitCode, string Text)>();
      Parallel.For(0, 8, index => {
        var directory = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = TranslateCommand.Run(
          [PrototypeFixture.PathOf("float/Dct8"), "--from", "csharp", "--to", index % 2 == 0 ? "javascript" : "csharp", "--float", "strict", "--output", directory],
          output, error);
        outcomes.Add((exitCode, string.Concat(Directory.GetFiles(directory).Order(StringComparer.Ordinal).Select(File.ReadAllText))));
      });
      Assert.That(outcomes.Select(outcome => outcome.ExitCode), Is.All.EqualTo((int)ExitCode.Success));
      Assert.That(outcomes.Select(outcome => outcome.Text).Distinct().Count(), Is.EqualTo(2), "one text per target");
    } finally {
      Directory.Delete(root, true);
    }
  }

  [Test]
  public void NoBytes_AreAnInternalErrorThatNamesTheResource()
    => Assert.That(() => PrecomputedImplementations.Load(() => null), Throws.TypeOf<InvalidDataException>().With.Message.Contains(PrecomputedImplementations.ResourceName));

  [Test]
  public void DamagedBytes_AreRefused() {
    var bytes = Embedded();
    bytes[^1] ^= 1;
    Assert.That(() => PrecomputedImplementations.Load(() => new MemoryStream(bytes)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("damaged"));
  }

  [Test]
  public void IntactBytes_AreRead()
    => Assert.That(PrecomputedImplementations.Load(() => new MemoryStream(Embedded())).Functions, Has.Count.EqualTo(PrecomputedImplementations.Module.Functions.Count));

  /// <summary>A translation that needs the implementations writes nothing when they are damaged: exit 70 and the reason.</summary>
  [TestCase("javascript")]
  [TestCase("csharp")]
  public void ATranslationWithDamagedImplementations_FailsWithAnInternalErrorAndWritesNothing(string target) {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = TranslateCommand.Run(
      [PrototypeFixture.PathOf("float/Dct8"), "--from", "csharp", "--to", target, "--float", "strict", "--output", directory],
      output, error, () => PrecomputedImplementations.Load(() => new MemoryStream([1, 2, 3])));
    Assert.That(exitCode, Is.EqualTo(TranslateCommand.InternalError));
    Assert.That(error.ToString(), Does.Contain($"error {PrecomputedImplementations.Damaged}: The bytes are no IR module."));
    Assert.That(Directory.Exists(directory), Is.False);
  }

  /// <summary>A translation that needs none of the implementations does not read them.</summary>
  [Test]
  public void ATranslationThatNeedsNoImplementation_DoesNotAskForThem() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    try {
      using var output = new StringWriter();
      using var error = new StringWriter();
      var exitCode = TranslateCommand.Run(
        [PrototypeFixture.PathOf("Crc32"), "--from", "csharp", "--to", "javascript", "--output", directory],
        output, error, () => throw new InvalidDataException("asked"));
      Assert.That((exitCode, error.ToString()), Is.EqualTo(((int)ExitCode.Success, "")));
    } finally {
      Directory.Delete(directory, true);
    }
  }

}
