using NUnit.Framework;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Tooling.Corpus;

/// <summary><see cref="GlobPattern"/>: <c>*</c> and <c>?</c> within a segment, <c>**</c> across segments.</summary>
[TestFixture]
public sealed class GlobPatternTests {

  [TestCase("dir/*.txt", "dir/b.txt", true)]
  [TestCase("dir/*.txt", "a.txt", false)]
  [TestCase("dir/*.txt", "dir/sub/c.txt", false)]
  [TestCase("**/*.txt", "a.txt", true)]
  [TestCase("**/*.txt", "dir/b.txt", true)]
  [TestCase("**/*.txt", "dir/sub/c.txt", true)]
  [TestCase("**/*.txt", "a.txtx", false)]
  [TestCase("dir/**", "dir/sub/c.txt", true)]
  [TestCase("dir/**", "other/c.txt", false)]
  [TestCase("a/**/z", "a/z", true)]
  [TestCase("a/**/z", "a/b/c/z", true)]
  [TestCase("?.txt", "a.txt", true)]
  [TestCase("?.txt", "ab.txt", false)]
  [TestCase("a.txt", "abtxt", false)]
  [TestCase("a+(b).txt", "a+(b).txt", true)]
  [TestCase("A.txt", "a.txt", false)]
  [TestCase("Cipher/algorithms/checksum/*.js", "Cipher/algorithms/checksum/adler.js", true)]
  [TestCase("nothing/*.zzz", "a.txt", false)]
  public void IsMatch(string pattern, string path, bool expected) {
    var glob = GlobPattern.Parse(pattern);
    Assert.That((glob.Text, glob.IsMatch(path)), Is.EqualTo((pattern, expected)));
  }

}
