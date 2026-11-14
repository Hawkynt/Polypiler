using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Float.Support;
using Polypiler.Tooling.Licensing;

namespace Polypiler.Tests.Backends;

/// <summary>
/// The mapping from every helper the backends can emit to its origin and licence (owner decision of
/// 2026-11-14, <c>LICENSE-OUTPUT.md</c>): every helper of either backend and every portable
/// floating-point implementation has one; every source file it points to carries the
/// <c>SPDX-License-Identifier</c> header that states that licence, read as the licence scanner and
/// the fence around the compiler projects read it; and a file without one is found.
/// </summary>
[TestFixture]
public sealed class HelperLicenceTests {

  private static readonly string _root = PrototypeFixture.RepositoryRoot;

  /// <summary>The folders whose files a translation copies into its output, with the licence all of them state unless a file names its own.</summary>
  private static readonly (string Folder, string Licence)[] _folders = [
    (CSharpHelper.Folder, EmittedHelper.OwnLicence),
    ("Polypiler.Core.Backends.JavaScript/Helpers", EmittedHelper.OwnLicence),
    (FloatOperationSources.Folder, EmittedHelper.OwnLicence)
  ];

  /// <summary>The files of a folder below the repository root whose first line is not an <c>SPDX-License-Identifier</c> header, by their paths relative to the root.</summary>
  public static IReadOnlyList<string> Unmarked(string root, string folder)
    => [.. Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)
      .Where(file => LicenceDetector.Header(File.ReadLines(file).FirstOrDefault() ?? "") is null)
      .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
      .Order(StringComparer.Ordinal)];

  private static string HeaderOf(string path) => LicenceDetector.Header(File.ReadLines(Path.Combine(_root, path)).First())!;

  // Test design 2: every helper the backends can emit has an origin and a licence in the mapping.
  [Test]
  public void EveryJavaScriptHelper_HasItsOriginAndLicence() {
    Assert.That(JavaScriptEmitter.HelperOrigins, Has.Count.GreaterThan(100));
    Assert.That(JavaScriptEmitter.HelperOrigins.Keys, Has.Member("$div").And.Member("$view").And.Member("$clamp"));
    Assert.That(JavaScriptEmitter.HelperOrigins.Values.Distinct(), Is.EqualTo(new[] { ("MIT", "Hawkynt/Polypiler:Polypiler.Core.Backends.JavaScript/Helpers") }));
  }

  [Test]
  public void EveryCSharpHelperOfTheDefaultTable_HasItsOriginAndLicence() {
    Assert.That(CSharpEmitter.HelperOrigins.Keys, Has.Member("Saturated(double value, int low, int high)").And.Member("Clamp(double value, double min, double max)").And.Member("Clamp(float value, float min, float max)").And.Member("Ignore<T>(T value)"));
    Assert.That(CSharpEmitter.HelperOrigins.Values.Distinct(), Is.EqualTo(new[] { ("MIT", "Hawkynt/Polypiler:Polypiler.Core.Backends.CSharp/Helpers") }));
  }

  [Test]
  public void EveryPortableImplementation_HasTheFileThatDeclaresItAndItsLicence() {
    var missing = FloatImplementations.Module.Functions.Select(function => function.Name).Where(name => FloatOperationSources.Origin(name) is null).ToList();
    Assert.That(missing, Is.Empty);
    foreach (var function in FloatImplementations.Module.Functions) {
      var (path, licence) = FloatOperationSources.Origin(function.Name)!.Value;
      Assert.That((File.Exists(Path.Combine(_root, path)), HeaderOf(path)), Is.EqualTo((true, licence)), function.Name);
    }
  }

  [Test]
  public void TheFloatFusedMultiplyAdd_IsBsdOnlyWhereItIsConvertedFromFreeBsd() {
    Assert.That(FloatOperationSources.Origin(nameof(FloatOperations.FusedMultiplyAddF)), Is.EqualTo(("Polypiler.Core.IR/Float/Operations/Arithmetic.Single.cs", "MIT AND BSD-2-Clause")));
    Assert.That(FloatOperationSources.Origin(nameof(FloatOperations.FusedMultiplyAdd)), Is.EqualTo(("Polypiler.Core.IR/Float/Operations/Arithmetic.cs", "MIT")));
    Assert.That(FloatOperationSources.Origin("NoSuchFunction"), Is.Null);
  }

  // Test design 4: every source of an emitted helper carries its SPDX marker; the folders of the backends are MIT throughout.
  [Test]
  public void EveryHelperSource_CarriesItsSpdxMarker() {
    foreach (var (folder, licence) in _folders) {
      Assert.That(Unmarked(_root, folder), Is.Empty, folder);
      var licences = Directory.EnumerateFiles(Path.Combine(_root, folder), "*.cs", SearchOption.AllDirectories).Select(file => HeaderOf(Path.GetRelativePath(_root, file))).Distinct().Order(StringComparer.Ordinal);
      Assert.That(licences, Is.EqualTo(folder == FloatOperationSources.Folder ? new[] { "MIT", "MIT AND BSD-2-Clause" } : new[] { licence }), folder);
    }
  }

  [Test]
  public void AHelperSourceWithoutItsSpdxMarker_IsFound() {
    var root = Path.Combine(Path.GetTempPath(), "polypiler-helper-licence-" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(Path.Combine(root, "Helpers", "Integer"));
      File.WriteAllText(Path.Combine(root, "Helpers", "Marked.cs"), "// SPDX-License-Identifier: MIT\nnamespace A;\n");
      File.WriteAllText(Path.Combine(root, "Helpers", "Integer", "Unmarked.cs"), "namespace A;\n// SPDX-License-Identifier: MIT\n");
      File.WriteAllText(Path.Combine(root, "Helpers", "Empty.cs"), "");
      Assert.That(Unmarked(root, "Helpers"), Is.EqualTo(new[] { "Helpers/Empty.cs", "Helpers/Integer/Unmarked.cs" }));
    } finally {
      Directory.Delete(root, true);
    }
  }

  [Test]
  public void APortableImplementationSourceWithoutItsSpdxMarker_IsRefused()
    => Assert.That(() => FloatOperationSources.Origins([("Marked.cs", "// SPDX-License-Identifier: MIT\nstatic double A(double x) => x;\n"), ("Unmarked.cs", "using System;\n// SPDX-License-Identifier: MIT\n")]),
      Throws.TypeOf<InvalidDataException>().With.Message.EqualTo("'Polypiler.Core.IR/Float/Operations/Unmarked.cs' has no SPDX-License-Identifier header on its first line."));

  [Test]
  public void Origins_FindEveryStaticMethod_TheFirstFileWinning_AndNoField() {
    var origins = FloatOperationSources.Origins([
      ("A.cs", "// SPDX-License-Identifier: MIT\npublic static partial class FloatOperations {\n  private static readonly double[] _table = [1.0];\n  public static double F(double x) => x;\n  private static ulong G (ulong x) {\n    return x;\n  }\n}\n"),
      ("B.cs", "// SPDX-License-Identifier: MIT AND BSD-2-Clause\r\npublic static partial class FloatOperations {\n  internal static float F(float x) => x;\n  public static float H(float x) => x;\n}\n")
    ]);
    Assert.That(origins.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value.Path, pair.Value.Licence)), Is.EqualTo(new[] {
      ("F", "Polypiler.Core.IR/Float/Operations/A.cs", "MIT"),
      ("G", "Polypiler.Core.IR/Float/Operations/A.cs", "MIT"),
      ("H", "Polypiler.Core.IR/Float/Operations/B.cs", "MIT AND BSD-2-Clause")
    }));
  }

  [Test]
  public void Origins_RefusesNull() => Assert.That(() => FloatOperationSources.Origins(null!), Throws.ArgumentNullException);

  [Test]
  public void Helper_OfAPortableImplementation_CarriesItsFilesLicence() {
    Assert.That(FloatOperationSources.Helper("FloatSin", nameof(FloatOperations.Sin), "function FloatSin(x) {}"),
      Is.EqualTo(new HelperText("FloatSin", "FloatSin", "MIT", "Hawkynt/Polypiler:Polypiler.Core.IR/Float/Operations/Trigonometric.Sine.cs", "function FloatSin(x) {}")));
    Assert.That(() => FloatOperationSources.Helper("FloatX", "NoSuchFunction", ""), Throws.ArgumentException.With.Message.Contains("declares 'NoSuchFunction'"));
  }

}
