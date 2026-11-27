using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NUnit.Framework;
using Polypiler.Core.BinaryFormats;

namespace Polypiler.Tests.BinaryFormats;

[TestFixture]
public sealed class DosFamilyCodecTests {

  private const string _dosProgram = "4d5a25000100000002001100ffff010000010000000000001c00000000000000b82a4ccd21";

  [Test]
  public void TheNativeDosImageUsesMzEvenWithPeRegisteredFirst() {
    var bytes = Convert.FromHexString(_dosProgram);
    var registry = new BinaryCodecRegistry();
    var pe = new RecordingCodec("pe", claimsPrefix: true);
    var mz = new RecordingCodec("mz");
    registry.Register(pe);
    registry.Register(mz);

    Assert.Multiple(() => {
      Assert.That(BinaryCodecRegistry.KnownFormat(bytes), Is.EqualTo("MZ"));
      Assert.That(registry.Detect(bytes), Is.SameAs(mz));
      Assert.That(registry.Select(bytes, null, "exit42.exe").Value, Is.SameAs(mz));
      Assert.That(pe.LastPrefixLength, Is.Null);
      Assert.That(mz.LastPrefixLength, Is.Null);
    });
  }

  [TestCase(0x78, "50450000", 0x40, "PE", "4d9cd3aba72858e2836ebf1d5861d0b9f640374bf181e10a8aaf016b0bc00e24")]
  [TestCase(0x78, "50450000", 0, "PE", "fa3d30a0fa57a3f46b443413d63a71666f7d5ce9f4376d3ce5fbb92b02cc86c2")]
  [TestCase(0x1080, "50450000", 0x40, "PE", "e25aebfce4833250d0a087c252cd52124e59bf6479a05ce0d61a6f6dcb75cb89")]
  [TestCase(0x78, "504500", 0x40, "MZ", "710960d0dd21085ffd9ab6b6f285f471472f0145916f42682cc6984450641bb2")]
  [TestCase(0x78, "4e45", 0x40, "NE", "b1c0defc375dfe91179d55a12025308b8e0913e4d3e278ac4d4e7cea95d26528")]
  [TestCase(0x78, "4c45", 0x40, "LE", "451c3ec83247f4a2d91523b0d391a6b5d6500a6cdbf641ea901928bb8743b54b")]
  [TestCase(0x78, "4c58", 0x40, "LX", "585e2908a24697201e896b74be6de41526cd9bd45acd2e12c66a5f65edc44e6b")]
  public void PinnedSignaturesSelectTheIdentifiedKind(int offset, string marker, int relocation, string format, string hash) {
    var bytes = WithMarker(offset, marker, checked((ushort)relocation));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(hash));
    Assert.That(BinaryCodecRegistry.KnownFormat(bytes), Is.EqualTo(format));

    var registry = new BinaryCodecRegistry();
    var codec = new RecordingCodec(format.ToLowerInvariant());
    registry.Register(codec);
    Assert.That(registry.Select(bytes, null, "fixture.exe").Value, Is.SameAs(codec));
    Assert.That(codec.LastPrefixLength, Is.Null);
  }

  [TestCase(0x40, "50450000", 0, "PE")]
  [TestCase(0x30, "50450000", 0x40, "MZ")]
  [TestCase(0x78, "50450001", 0x40, "MZ")]
  [TestCase(0x78, "5045", 0x40, "MZ")]
  [TestCase(0x78, "4e45", 0, "MZ")]
  [TestCase(0x78, "4c00", 0x40, "MZ")]
  public void OnlyInRangeCompleteMarkersIdentifyAnExtendedHeader(int offset, string marker, int relocation, string format)
    => Assert.That(BinaryCodecRegistry.KnownFormat(WithMarker(offset, marker, checked((ushort)relocation))), Is.EqualTo(format));

  [TestCase(2, 0U)]
  [TestCase(63, 0U)]
  [TestCase(64, 0x3FU)]
  [TestCase(64, 0x40U)]
  [TestCase(65, 0x40U)]
  [TestCase(120, 0x80U)]
  [TestCase(130, 0x80000000U)]
  [TestCase(130, uint.MaxValue)]
  public void MissingOrOutOfRangePointersKeepTheDosKind(int length, uint offset)
    => Assert.That(BinaryCodecRegistry.KnownFormat(Header(length, offset, 0x40)), Is.EqualTo("MZ"));

  [Test]
  public void SwappedDosMagicCannotClaimAWindowsHeader() {
    var bytes = WithMarker(0x78, "50450000", 0x40);
    "ZM"u8.CopyTo(bytes);
    Assert.That(BinaryCodecRegistry.KnownFormat(bytes), Is.EqualTo("MZ"));
  }

  [TestCase("50450000", "PE")]
  [TestCase("4e45", "NE")]
  [TestCase("4c45", "LE")]
  [TestCase("4c58", "LX")]
  public void UnregisteredExtendedFormatsDoNotFallBackToMz(string marker, string format) {
    var registry = new BinaryCodecRegistry();
    var mz = new RecordingCodec("mz", claimsPrefix: true);
    registry.Register(mz);
    var bytes = WithMarker(0x78, marker, 0x40);
    var selection = registry.Select(bytes, null, "fixture.exe");
    Assert.Multiple(() => {
      Assert.That(registry.Detect(bytes), Is.Null);
      Assert.That(selection.Problems.Length, Is.EqualTo(1));
      Assert.That(selection.Problems[0].Code, Is.EqualTo("PLY4059"));
      Assert.That(selection.Problems[0].Message, Does.Contain($"is a {format} file"));
      Assert.That(mz.LastPrefixLength, Is.Null);
    });
  }

  [Test]
  public void ExplicitSelectionAndTheGenericPrefixLimitStillApply() {
    var registry = BinaryCodecRegistry.CreateDefault();
    var generic = new RecordingCodec("generic", claimsPrefix: true);
    registry.Register(generic);
    var bytes = WithMarker(0x78, "50450000", 0x40);
    Assert.That(registry.Select(bytes, "raw", "fixture.exe").Value, Is.SameAs(registry.Find("raw")));
    Assert.That(registry.Detect(new byte[256]), Is.SameAs(generic));
    Assert.That(generic.LastPrefixLength, Is.EqualTo(64));
    Assert.That(BinaryCodecRegistry.KnownFormat("ELF"u8), Is.Null);
    Assert.That(BinaryCodecRegistry.KnownFormat([0x7f, 0x45, 0x4c, 0x46]), Is.EqualTo("ELF"));
    Assert.That(BinaryCodecRegistry.KnownFormat([0xcf, 0xfa, 0xed, 0xfe]), Is.EqualTo("Mach-O"));
  }

  private static byte[] Header(int length, uint offset, ushort relocation) {
    var bytes = new byte[length];
    "MZ"u8.CopyTo(bytes);
    if (length >= 64) {
      BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x18), relocation);
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3c), offset);
    }
    return bytes;
  }

  private static byte[] WithMarker(int offset, string hex, ushort relocation) {
    var marker = Convert.FromHexString(hex);
    var bytes = Header(Math.Max(64, offset + marker.Length), (uint)offset, relocation);
    marker.CopyTo(bytes, offset);
    return bytes;
  }

  private sealed class RecordingCodec(string id, bool claimsPrefix = false) : IBinaryCodec {

    public string Id => id;
    public int? LastPrefixLength { get; private set; }

    public bool CanRead(ReadOnlySpan<byte> prefix) {
      this.LastPrefixLength = prefix.Length;
      return claimsPrefix;
    }

    public BinaryResult<BinaryImage> Read(ReadOnlyMemory<byte> file, BinaryManifest? manifest)
      => throw new NotSupportedException("Registry probing must not read a file.");

  }

}
