using System;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Prototype;

[TestFixture]
public sealed class PrototypeFixtureTrapTests {

  [TestCase("index", "trap: argument out of range")]
  [TestCase("sourceIndex", "trap: index out of range")]
  [TestCase("Index", "trap: index out of range")]
  public void ClassifiesRangeErrorsWithoutAListFrame(string parameter, string expected) {
    var method = typeof(PrototypeFixtureTrapTests).GetMethod(nameof(ThrowWithoutAListFrame), BindingFlags.NonPublic | BindingFlags.Static)!;
    var outcome = PrototypeFixture.InvokedOutcome(method, new(nameof(ThrowWithoutAListFrame), [parameter]));
    Assert.That(outcome, Is.EqualTo(expected));
  }

  private static int ThrowWithoutAListFrame(string parameter) => throw new ArgumentOutOfRangeException(parameter);

}
