using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>The profiles of the C# backend as data: which there are, what each fixes, and what a name or a language version is refused for.</summary>
[TestFixture]
public sealed class CSharpProfileTests {

  /// <summary>Each profile fixes a framework and the language version of that framework's time.</summary>
  [TestCase("net2.0", ".NET Framework 2.0", "2")]
  [TestCase("net3.5", ".NET Framework 3.5", "3")]
  [TestCase("net4.6", ".NET Framework 4.6", "6")]
  [TestCase("net4.8", ".NET Framework 4.8", "7.3")]
  [TestCase("netstandard2.0", ".NET Standard 2.0", "7.3")]
  [TestCase("netstandard2.1", ".NET Standard 2.1", "8")]
  [TestCase("netcoreapp3.1", ".NET Core 3.1", "8")]
  [TestCase("net8.0", ".NET 8", "12")]
  [TestCase("net10.0", ".NET 10", "14")]
  public void Profile_FixesFrameworkAndLanguageVersion(string name, string framework, string languageVersion) {
    var profile = CSharpProfile.Parse(name);
    Assert.That((profile.Name, profile.Framework, profile.LanguageVersionName), Is.EqualTo((name, framework, languageVersion)));
    Assert.That(profile.MinimumLanguageVersion, Is.EqualTo(profile.LanguageVersion));
    Assert.That(profile.ToString(), Is.EqualTo($"{name}, C# {languageVersion}"));
    Assert.That(CSharpProfile.All, Has.Member(profile));
  }

  /// <summary>The profiles are the nine of the issue, oldest first, and the newest is the default.</summary>
  [Test]
  public void Profiles_AreOrderedAndTheNewestIsTheDefault() {
    Assert.That(CSharpProfile.All.Select(profile => profile.Name),
      Is.EqualTo(new[] { "net2.0", "net3.5", "net4.6", "net4.8", "netstandard2.0", "netstandard2.1", "netcoreapp3.1", "net8.0", "net10.0" }));
    Assert.That(CSharpProfile.Default, Is.SameAs(CSharpProfile.Net100).And.SameAs(CSharpProfile.All[^1]));
    Assert.That(CSharpProfile.All.Select(profile => profile.LanguageVersion), Is.Ordered);
    Assert.That(CSharpProfile.LanguageVersions, Is.Ordered.And.Unique);
    Assert.That(CSharpProfile.LanguageVersions.Select(CSharpProfile.NameOf),
      Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6", "7", "7.1", "7.2", "7.3", "8", "9", "10", "11", "12", "13", "14" }));
  }

  /// <summary>Test design 9: a name that is no profile - the frameworks out of scope included - is refused with the list of those there are.</summary>
  [TestCase("net1.1")]
  [TestCase("net11.0")]
  [TestCase("NET8.0")]
  [TestCase("net8")]
  [TestCase("netcoreapp2.1")]
  [TestCase("uap10.0")]
  [TestCase("")]
  public void UnknownProfile_IsRefused(string name) {
    Assert.That(CSharpProfile.TryParse(name, null, out var profile, out var problem), Is.False);
    Assert.That(profile, Is.SameAs(CSharpProfile.Default));
    Assert.That(problem, Does.StartWith($"unknown C# profile '{name}'; the profiles are 'net2.0', 'net3.5', ").And.EndWith("'net8.0', 'net10.0'"));
    Assert.That(() => CSharpProfile.Parse(name), Throws.ArgumentException.With.Message.Contains(problem));
  }

  /// <summary>
  /// The language version is raised, never lowered: at the profile's own version (the boundary),
  /// just above it and at the newest it is taken; a minor version of zero may be written out.
  /// </summary>
  [TestCase("net2.0", "2", "2")]
  [TestCase("net2.0", "3", "3")]
  [TestCase("net2.0", "14", "14")]
  [TestCase("net4.8", "7.3", "7.3")]
  [TestCase("net4.8", "8", "8")]
  [TestCase("net4.8", "8.0", "8")]
  [TestCase("netstandard2.0", "10.0", "10")]
  [TestCase("net10.0", "14", "14")]
  [TestCase("net10.0", "14.0", "14")]
  public void LanguageVersion_IsRaised(string name, string languageVersion, string expected) {
    Assert.That(CSharpProfile.TryParse(name, languageVersion, out var profile, out var problem), Is.True, problem);
    Assert.That(problem, Is.Empty);
    Assert.That((profile.Name, profile.LanguageVersionName), Is.EqualTo((name, expected)));
    Assert.That(profile.MinimumLanguageVersion, Is.EqualTo(CSharpProfile.Parse(name).LanguageVersion));
    Assert.That(profile.Framework, Is.EqualTo(CSharpProfile.Parse(name).Framework));
    Assert.That(CSharpProfile.Parse(name, languageVersion).ToString(), Is.EqualTo($"{name}, C# {expected}"));
  }

  /// <summary>Test design 9: a language version just below the profile's, and any lower one, is refused.</summary>
  [TestCase("net3.5", "2", "3")]
  [TestCase("net4.6", "5", "6")]
  [TestCase("net4.8", "7.2", "7.3")]
  [TestCase("netstandard2.1", "7.3", "8")]
  [TestCase("net8.0", "11", "12")]
  [TestCase("net10.0", "13", "14")]
  [TestCase("net10.0", "7.3", "14")]
  [TestCase("net2.0", "1", "2")]
  public void LanguageVersionBelowTheProfile_IsRefused(string name, string languageVersion, string minimum) {
    Assert.That(CSharpProfile.TryParse(name, languageVersion, out var profile, out var problem), Is.False);
    Assert.That(profile.LanguageVersionName, Is.EqualTo(minimum));
    Assert.That(problem, Is.EqualTo($"the C# profile '{name}' is written in C# {minimum} or later, not in C# {languageVersion}"));
    Assert.That(() => CSharpProfile.Parse(name, languageVersion), Throws.ArgumentException);
  }

  /// <summary>Test design 9: what is not a version of C# is refused with the list of those there are.</summary>
  [TestCase("15")]
  [TestCase("7.4")]
  [TestCase("latest")]
  [TestCase("")]
  [TestCase("8.0.0")]
  [TestCase("C# 8")]
  public void WhatIsNoLanguageVersion_IsRefused(string languageVersion) {
    Assert.That(CSharpProfile.TryParse("net10.0", languageVersion, out _, out var problem), Is.False);
    Assert.That(problem, Does.StartWith($"'{languageVersion}' is not a C# language version; the versions are 1, 2, 3, ").And.EndWith(", 13, 14"));
  }

  /// <summary>A version is written without a minor version of zero.</summary>
  [Test]
  public void NameOf_DropsAMinorVersionOfZero()
    => Assert.That((CSharpProfile.NameOf(new Version(7, 3)), CSharpProfile.NameOf(new Version(8, 0))), Is.EqualTo(("7.3", "8")));

}
