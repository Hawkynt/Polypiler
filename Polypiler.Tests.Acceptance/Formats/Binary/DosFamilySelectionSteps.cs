using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.BinaryFormats;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Formats.Binary;

/// <summary>Exercises shared DOS-family dispatch without needing either executable reader.</summary>
[Binding, Scope(Feature = "Select a container that shares the DOS signature")]
public sealed class DosFamilySelectionSteps {

  private readonly BinaryCodecRegistry _registry = new();
  private byte[] _bytes = [];
  private BinaryResult<IBinaryCodec> _result = null!;

  [Given("MZ bytes with PE signature at 0x78 and registered pe and mz codecs")]
  public void PeMarker() {
    this._bytes = new byte[0x7c];
    "MZ"u8.CopyTo(this._bytes);
    this._bytes[0x18] = 0x40;
    this._bytes[0x3c] = 0x78;
    "PE\0\0"u8.CopyTo(this._bytes.AsSpan(0x78));
    this._registry.Register(new NamedCodec("mz"));
    this._registry.Register(new NamedCodec("pe"));
  }

  [Given("the pinned DOS MZ bytes and a registered pe codec only")]
  public void NativeDosImage() {
    this._bytes = Convert.FromHexString("4d5a25000100000002001100ffff010000010000000000001c00000000000000b82a4ccd21");
    this._registry.Register(new NamedCodec("pe", claimsPrefix: true));
  }

  [When("the registry automatically selects a container")]
  public void Select() => this._result = this._registry.Select(this._bytes, null, "image.exe");

  [Then("it selects the pe codec")]
  public void SelectsPe() => Assert.That(this._result.Value?.Id, Is.EqualTo("pe"));

  [Then("it reports PLY4059 naming MZ")]
  public void NamesMz() {
    var problem = this._result.Problems.Single();
    Assert.That(problem.Code, Is.EqualTo("PLY4059"));
    Assert.That(problem.Message, Does.Contain("is a MZ file"));
  }

  private sealed class NamedCodec(string id, bool claimsPrefix = false) : IBinaryCodec {

    public string Id => id;

    public bool CanRead(ReadOnlySpan<byte> prefix) => claimsPrefix;

    public BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest)
      => throw new NotSupportedException("Dispatch does not read the container.");

  }

}
