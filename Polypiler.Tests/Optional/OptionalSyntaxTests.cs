using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;

namespace Polypiler.Tests.Optional;

/// <summary>The null-conditional and null-coalescing syntax of the backends' trees: read, printed and read back.</summary>
[TestFixture]
public sealed class OptionalSyntaxTests {

  [TestCase("a?.Length")]
  [TestCase("a?[i]")]
  [TestCase("a?.Length ?? -1")]
  [TestCase("(a?.Length).Value")]
  [TestCase("(a?[0]).GetValueOrDefault()")]
  [TestCase("(a?.b)[0]")]
  [TestCase("q ??= 7")]
  public void CSharp_ReadsAndPrintsBack(string source) {
    var read = CSharpSyntaxReader.Expression(source);
    Assert.That(CSharpPrinter.Print(read, CSharpProfile.Default), Is.EqualTo(source));
  }

  [Test]
  public void CSharp_ConditionalAccess_IsMarked() {
    Assert.That(CSharpSyntaxReader.Expression("a?.Length"), Is.EqualTo(new CsMemberAccess(new CsName("a"), "Length") { Conditional = true }));
    Assert.That(CSharpSyntaxReader.Expression("a?[i]"), Is.EqualTo(new CsElementAccess(new CsName("a"), new CsName("i")) { Conditional = true }));
    Assert.That(CSharpPrinter.Print(new CsMemberAccess(new CsMemberAccess(new CsName("a"), "Length") { Conditional = true }, "Value"), CSharpProfile.Default), Is.EqualTo("(a?.Length).Value"));
  }

}
