using NUnit.Framework;

namespace RunnerFixture;

public sealed class ArithmeticTests {

  [Test]
  public void AddsTwoNumbers() => Assert.That(2 + 3, Is.EqualTo(5));

  [Test]
  public void WrapsAround() => Assert.That(unchecked(int.MaxValue + 1), Is.EqualTo(int.MinValue));

  [Test]
  public void FailsOnPurpose() => Assert.That(2 + 2, Is.EqualTo(5), "this test fails on purpose");

}
