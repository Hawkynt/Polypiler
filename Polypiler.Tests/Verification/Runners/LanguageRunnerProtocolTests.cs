using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Runners;

namespace Polypiler.Tests.Verification.Runners;

[TestFixture]
public sealed class LanguageRunnerProtocolTests {

  private static readonly CallSignature _signature = new([], ImageType.Parse("i32"));

  [Test]
  public void WritesLowercaseHexAndRetainsEmptyInputs() {
    var bytes = LanguageRunnerProtocol.Write(_signature, [[], []]);

    Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("\n\n"));
    var signature = new CallSignature([new("x", ImageType.Parse("i32"), ParameterMode.In)], null);
    Assert.That(Encoding.UTF8.GetString(LanguageRunnerProtocol.Write(signature, [[0xAB, 0xCD, 0, 0]])), Is.EqualTo("abcd0000\n"));
    Assert.That(() => LanguageRunnerProtocol.Write(signature, [[0xAB]]), Throws.TypeOf<ImageException>());
  }

  [Test]
  public void ReadsImageAndTrapInOrder() {
    var outcomes = LanguageRunnerProtocol.Read(Encoding.UTF8.GetBytes("03000000\r\n!divide-by-zero Illegal division by zero\r\n"), _signature, 2);

    Assert.That(outcomes[0].Image, Is.EqualTo(new byte[] { 3, 0, 0, 0 }));
    Assert.That(outcomes[1].Trap, Is.EqualTo(new TrapClass(TrapKind.DivideByZero)));
    Assert.That(outcomes[1].NativeTrapName, Is.EqualTo("Illegal division by zero"));
  }

  [TestCase("03000000\n", 2)]
  [TestCase("03000000\n\n", 1)]
  [TestCase("FF000000\n", 1)]
  [TestCase("0300000\n", 1)]
  [TestCase("0300\n", 1)]
  [TestCase("!nonsense X\n", 1)]
  [TestCase("!divide-by-zero \n", 1)]
  public void RefusesMalformedProtocol(string stdout, int calls) =>
    Assert.That(() => LanguageRunnerProtocol.Read(Encoding.UTF8.GetBytes(stdout), _signature, calls), Throws.TypeOf<ImageException>());

  [Test]
  public void RefusesInvalidUtf8() =>
    Assert.That(() => LanguageRunnerProtocol.Read([0xFF, 0x0A], _signature, 1), Throws.TypeOf<ImageException>());

  [Test]
  public void DistinguishesZeroCallsFromOneVoidResult() {
    var signature = new CallSignature([], null);

    Assert.That(LanguageRunnerProtocol.Read([], signature, 0), Is.Empty);
    Assert.That(LanguageRunnerProtocol.Read("\n"u8, signature, 1)[0].Image, Is.Empty);
  }

}
