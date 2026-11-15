using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Tests.Verification.Images;

/// <summary><see cref="TrapClass"/>: the nine classes, <c>other:&lt;name&gt;</c> and the driver's trap line (test design 5).</summary>
[TestFixture]
public sealed class TrapClassTests {

  [TestCase("overflow", TrapKind.Overflow)]
  [TestCase("divide-by-zero", TrapKind.DivideByZero)]
  [TestCase("bounds", TrapKind.Bounds)]
  [TestCase("null", TrapKind.Null)]
  [TestCase("invalid-shift", TrapKind.InvalidShift)]
  [TestCase("unreachable", TrapKind.Unreachable)]
  [TestCase("invalid-argument", TrapKind.InvalidArgument)]
  [TestCase("result-type", TrapKind.ResultType)]
  public void EveryClass_ParsesAndSpellsItself(string text, TrapKind kind) {
    var trap = TrapClass.Parse(text);
    Assert.That((trap.Kind, trap.NativeName, trap.ToString()), Is.EqualTo((kind, (string?)null, text)));
  }

  [Test]
  public void Other_CarriesItsNativeName() {
    var trap = TrapClass.Parse("other:KeyError");
    Assert.That((trap.Kind, trap.NativeName, trap.ToString()), Is.EqualTo((TrapKind.Other, "KeyError", "other:KeyError")));
  }

  [TestCase("boom")]
  [TestCase("")]
  [TestCase("Overflow")]
  [TestCase("other:")]
  [TestCase("other:Key Error")]
  public void AnUnknownClass_IsRefused(string text)
    => Assert.That(Assert.Throws<ImageException>(() => TrapClass.Parse(text))!.Message, Is.EqualTo($"'{text}' is not a trap class"));

  [Test]
  public void TheTrapLine_GivesTheClassAndTheNativeName() {
    Assert.That((TrapClass.ParseLine("!divide-by-zero ZeroDivisionError", out var native), native), Is.EqualTo((new TrapClass(TrapKind.DivideByZero), "ZeroDivisionError")));
    Assert.That((TrapClass.ParseLine("!other:KeyError KeyError", out native), native), Is.EqualTo((new TrapClass(TrapKind.Other, "KeyError"), "KeyError")));
  }

  [TestCase("divide-by-zero ZeroDivisionError")]
  [TestCase("!overflow")]
  [TestCase("!overflow ")]
  [TestCase("!overflow a b")]
  public void AMalformedTrapLine_IsRefused(string line)
    => Assert.That(Assert.Throws<ImageException>(() => TrapClass.ParseLine(line, out _))!.Message, Is.EqualTo($"'{line}' is not a trap line '!<class> <native name>'"));

  [Test]
  public void AnUnknownClassInATrapLine_IsRefused()
    => Assert.That(Assert.Throws<ImageException>(() => TrapClass.ParseLine("!boom Error", out _))!.Message, Is.EqualTo("'boom' is not a trap class"));

  /// <summary>Comparison uses the class only, except for <c>other</c>, where the full text is compared.</summary>
  [Test]
  public void Comparison_IsByClass_AndByNameForOther() {
    var python = TrapClass.ParseLine("!divide-by-zero ZeroDivisionError", out _);
    var csharp = TrapClass.ParseLine("!divide-by-zero System.DivideByZeroException", out _);
    Assert.That(python, Is.EqualTo(csharp));
    Assert.That(TrapClass.Parse("other:KeyError"), Is.Not.EqualTo(TrapClass.Parse("other:IndexError")));
    Assert.That(TrapClass.Parse("other:KeyError"), Is.EqualTo(new TrapClass(TrapKind.Other, "KeyError")));
  }

  [Test]
  public void TheConstructor_ChecksTheNativeName() {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new TrapClass((TrapKind)99));
    Assert.That(Assert.Throws<ArgumentException>(() => _ = new TrapClass(TrapKind.Other))!.Message, Does.StartWith("an 'other' trap needs a native name"));
    Assert.That(Assert.Throws<ArgumentException>(() => _ = new TrapClass(TrapKind.Other, "a b"))!.Message, Does.StartWith("an 'other' trap needs a native name"));
    Assert.That(Assert.Throws<ArgumentException>(() => _ = new TrapClass(TrapKind.Bounds, "IndexError"))!.Message, Does.StartWith("only an 'other' trap carries a native name"));
    Assert.Throws<ArgumentNullException>(() => TrapClass.Parse(null!));
    Assert.Throws<ArgumentNullException>(() => TrapClass.ParseLine(null!, out _));
  }

}
