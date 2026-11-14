using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Diagnostics;

namespace Polypiler.Tests.Diagnostics;

/// <summary>
/// <c>spec/diagnostics.yaml</c> against the code: every <c>[Diagnostic]</c> descriptor of every
/// Polypiler assembly is registered with the same severity, exit code and rule; every registered code
/// that is neither reserved nor retired is declared; and every code constant names a declared code.
/// The test assembly is searched too, for the codes the test harness raises.
/// </summary>
[TestFixture]
public sealed class RegistryMatchesCodeTests {

  private static readonly Lazy<Assembly[]> _assemblies = new(LoadAssemblies);

  /// <summary>Every assembly of the product and of this test project in the output directory.</summary>
  private static Assembly[] LoadAssemblies()
    => [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
      .Where(path => Path.GetFileName(path).StartsWith("polypiler", StringComparison.OrdinalIgnoreCase))
      .Order(StringComparer.Ordinal)
      .Select(path => AssemblyLoadContext.Default.LoadFromAssemblyPath(path))];

  private static DiagnosticRegistry Registry()
    => DiagnosticRegistryValidator.Validate(Path.Combine(PrototypeFixture.RepositoryRoot, DiagnosticRegistry.RepositoryPath));

  [Test]
  public void TheAssembliesSearched_IncludeEveryLayer()
    => Assert.That(_assemblies.Value.Select(assembly => assembly.GetName().Name), Is.SupersetOf(new[] {
      "Polypiler.Core", "Polypiler.Core.IR", "Polypiler.Core.Frontends.CSharp", "Polypiler.Core.Frontends.JavaScript",
      "Polypiler.Core.Backends.CSharp", "Polypiler.Core.Backends.JavaScript", "polypiler", "polypiler-tooling", "Polypiler.Tests"
    }));

  [Test]
  public void RegistryAndCode_Agree() {
    var declared = DiagnosticRegistry.Declared(_assemblies.Value);
    Assert.That(declared, Is.Not.Empty);
    Assert.That(Registry().Compare(declared), Is.Empty);
  }

  /// <summary>A <c>const string</c> that holds a code is a code something emits: it must have a declared descriptor.</summary>
  [Test]
  public void EveryCodeConstant_IsDeclared() {
    var declared = DiagnosticRegistry.Declared(_assemblies.Value).Select(descriptor => descriptor.Code).ToHashSet(StringComparer.Ordinal);
    var constants = _assemblies.Value
      .SelectMany(assembly => assembly.GetTypes())
      .Where(type => type.Namespace != typeof(RegistryMatchesCodeTests).Namespace)
      .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string) && DiagnosticDescriptor.IsCode((string?)field.GetRawConstantValue()))
        .Select(field => $"{type.FullName}.{field.Name} = {field.GetRawConstantValue()}"))
      .ToList();
    Assert.That(constants, Is.Not.Empty);
    Assert.That(constants.Where(constant => !declared.Contains(constant[^7..])), Is.Empty);
  }

  /// <summary>Test design 16: a descriptor the registry lacks is named.</summary>
  [Test]
  public void ADescriptorMissingFromTheRegistry_IsNamed()
    => Assert.That(Registry().Compare([.. DiagnosticRegistry.Declared(_assemblies.Value), TestOnly.Unregistered]),
      Is.EqualTo(new[] { $"PLY0098 is declared in code but not registered in {Registry().File}" }));

  [Test]
  public void Compare_ReportsEveryKindOfDifference() {
    var registry = DiagnosticRegistryValidator.Validate("""
      schema: 1
      blocks:
        - { first: PLY0001, last: PLY0099, area: cli }
      codes:
        - { code: PLY0010, severity: error, exit: UsageError, rule: r, title: t, message: m, area: cli, owner: o }
        - { code: PLY0011, severity: warning, title: t, message: m, area: cli, owner: o }
        - { code: PLY0012, severity: error, exit: UsageError, rule: r, title: t, message: m, area: cli, owner: o, reserved: true }
        - { code: PLY0013, severity: error, exit: UsageError, rule: r, title: t, message: m, area: cli, owner: o, retired: true }
        - { code: PLY0014, severity: error, exit: UsageError, rule: r, title: t, message: m, area: cli, owner: o }
        - { code: PLY0015, severity: error, exit: UsageError, title: t, message: m, area: cli, owner: o, reserved: true }
        - { code: PLY0016, severity: error, exit: UsageError, title: t, message: m, area: cli, owner: o, retired: true }
        - { code: PLY0017, severity: error, exit: UsageError, rule: r, title: t, message: m, area: cli, owner: o }
      """, "r.yaml");
    Assert.That(registry.Problems, Is.Empty);
    Assert.That(registry.Compare([
      new("PLY0010", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "r"),
      new("PLY0010", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "r"),
      new("PLY0011", DiagnosticSeverity.Error, ExitCode.InvalidSource, "{0}", "s"),
      new("PLY0012", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "r"),
      new("PLY0013", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "r"),
      new("PLY0014", DiagnosticSeverity.Warning, null, "{0}", "r"),
      new("PLY0017", DiagnosticSeverity.Error, ExitCode.UsageError, "{0}", "r"),
      new("PLY0017", DiagnosticSeverity.Error, ExitCode.InternalError, "{0}", "r"),
      new("PLY0099", DiagnosticSeverity.Info, null, "{0}", "r")
    ]), Is.EqualTo(new[] {
      "PLY0011: severity error in code, warning in the registry",
      "PLY0011: exit code InvalidSource in code, none in the registry",
      "PLY0011: rule 's' in code, '' in the registry",
      "PLY0012 is declared in code but registered as reserved",
      "PLY0013 is declared in code but registered as retired",
      "PLY0014: severity warning in code, error in the registry",
      "PLY0014: exit code none in code, UsageError in the registry",
      "PLY0017 is declared 2 times with different severity, exit code, message or rule",
      "PLY0099 is declared in code but not registered in r.yaml"
    }));
  }

  [Test]
  public void Compare_ARegisteredCodeNothingDeclares_IsNamed() {
    var registry = DiagnosticRegistryValidator.Validate("""
      schema: 1
      blocks:
        - { first: PLY0001, last: PLY0099, area: cli }
      codes:
        - { code: PLY0010, severity: error, exit: UsageError, rule: r, title: t, message: m, area: cli, owner: o }
      """, "r.yaml");
    Assert.That(registry.Compare([]), Is.EqualTo(new[] { "PLY0010 is registered but nothing declares it; mark it 'reserved' or 'retired'" }));
  }

  /// <summary>Holds the test-only descriptor of test design 16; it carries no attribute, so the registry test does not find it.</summary>
  private static class TestOnly {

    public static readonly DiagnosticDescriptor Unregistered = new("PLY0098", DiagnosticSeverity.Error, ExitCode.InternalError, "{0}", "test-only");

  }

}
