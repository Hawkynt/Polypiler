using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Core.MachineSemantics;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.MachineSemantics.Support;

/// <summary>
/// What the MachineIR tests share: the profiles of <c>spec/machine</c> and of the fixture folder
/// <c>tests/fixtures/native/mach-ir/profiles</c>, parsing, and verification that must succeed.
/// </summary>
internal static class MachineTesting {

  /// <summary>The fixture folder of MachineIR.</summary>
  public static readonly string Fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native");

  /// <summary>The repository's profiles, then the fixture profiles.</summary>
  public static readonly MachineProfileCatalog Catalog = new([
    Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "machine"),
    Path.Combine(Fixtures, "mach-ir", "profiles")
  ]);

  /// <summary>A profile that must exist.</summary>
  public static IsaProfile Profile(string id) => Catalog.Find(id)!;

  /// <summary>Parses a module against the catalog.</summary>
  public static MachineModule Parse(string text) => MachineSyntax.Parse(text, Catalog.Find);

  /// <summary>Parses a module and asserts that it verifies.</summary>
  public static MachineModule Verified(string text) {
    var module = Parse(text);
    var problems = MachineVerifier.Verify(module);
    if (!problems.IsEmpty)
      throw new NUnit.Framework.AssertionException(string.Join("\n", problems.Select(problem => problem.ToString())));
    return module;
  }

  /// <summary>The codes of the problems of a module.</summary>
  public static string[] Codes(string text) => [.. MachineVerifier.Verify(Parse(text)).Select(problem => problem.Code)];

  /// <summary>The messages of the problems of a module.</summary>
  public static Diagnostic[] Problems(string text) => [.. MachineVerifier.Verify(Parse(text))];

}
