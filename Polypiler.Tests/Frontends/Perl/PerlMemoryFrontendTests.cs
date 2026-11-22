using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>Tests for Perl arrays, references, byte strings and portable pack/unpack lowering (#437).</summary>
[TestFixture]
public sealed class PerlMemoryFrontendTests {

  private static IrModule Import(string source, string contracts = "") {
    var result = PerlFrontend.Import("mem.pl", source, new PerlImportOptions { Contracts = contracts });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  [Test]
  public void Arrays_Refs_ByteStrings_AndPortablePackUnpack_LowerAndInterpret() {
    const string src = """
      use strict;
      use warnings;
      sub sum_array {
        my ($arr) = @_;
        my $total = 0;
        for my $i (0 .. scalar(@$arr) - 1) {
          $total += $arr->[$i];
        }
        return $total;
      }
      sub adler32 {
        my ($data) = @_;
        my $s1 = 1;
        my $s2 = 0;
        my $n = length($data);
        for my $i (0 .. $n - 1) {
          $s1 = ($s1 + ord(substr($data, $i, 1))) % 65521;
          $s2 = ($s2 + $s1) % 65521;
        }
        return ($s2 << 16) | $s1;
      }
      sub read_le32 {
        my ($buf) = @_;
        my ($v) = unpack('V', $buf);
        return $v;
      }
      1;
      """;
    const string contracts = """
      sum_array.arr: seq<i32>
      sum_array.return: i32
      adler32.data: seq<u8>
      adler32.return: u32
      read_le32.buf: seq<u8>
      read_le32.return: u32
      """;
    var mod = Import(src, contracts);
    Assert.That(IrVerifier.Verify(mod), Is.Empty);

    Assert.That(IrInterpreter.Call(mod, "sum_array", [new[] { 10, 20, 12 }]), Is.EqualTo(42));
    var wiki = Encoding.ASCII.GetBytes("Wikipedia");
    Assert.That(IrInterpreter.Call(mod, "adler32", [wiki]), Is.EqualTo(0x11E60398u));
    Assert.That(IrInterpreter.Call(mod, "read_le32", [new byte[] { 0x78, 0x56, 0x34, 0x12 }]), Is.EqualTo(0x12345678u));
  }

  [Test]
  public void HostEndianPack_UnsupportedTemplate_AndWideStrings_AreRefused() {
    var hostEndian = PerlFrontend.Import("p.pl", "sub f { my ($x) = @_; return pack('L', $x); }",
      new PerlImportOptions { Contracts = "f.x: u32\nf.return: seq<u8>" });
    Assert.That(hostEndian.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1723"));

    var badTemplate = PerlFrontend.Import("p2.pl", "sub f { my ($x) = @_; return pack('H*', $x); }",
      new PerlImportOptions { Contracts = "f.x: str\nf.return: seq<u8>" });
    Assert.That(badTemplate.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1723"));

    var wideStr = PerlFrontend.Import("w.pl", "use utf8;\nsub f { return \"€\"; }",
      new PerlImportOptions { Contracts = "f.return: str" });
    Assert.That(wideStr.Diagnostics.Select(d => d.Code), Has.Some.EqualTo("PLY1722"));
  }

}
