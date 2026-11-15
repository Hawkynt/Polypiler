using NUnit.Framework;
using Polypiler.Core.Verification.Rebuild;
using static Polypiler.Tests.Verification.Rebuild.TreeHashTests;

namespace Polypiler.Tests.Verification.Rebuild;

/// <summary><see cref="TreeComparison"/>: every kind of difference, its offset, and case-sensitive names.</summary>
[TestFixture]
public sealed class TreeComparisonTests {

  private static TreeDigest Digest(params (string Path, string Content)[] files) => TreeHash.Compute(Tree(files), RebuildIgnore.Empty);

  [Test]
  public void EqualTrees_HaveNoDifference()
    => Assert.That(TreeComparison.Compare(Digest(("a.txt", "hello\n")), Digest(("a.txt", "hello\n"))), Is.Empty);

  /// <summary>Test design 3: one difference of each kind, all reported, in path order.</summary>
  [Test]
  public void EveryKind_IsReported()
    => Assert.That(TreeComparison.Compare(Digest(("a.txt", "x"), ("b.txt", "1"), ("d.txt", "same")), Digest(("b.txt", "2"), ("c.txt", "y"), ("d.txt", "same"))), Is.EqualTo(new[] {
      new TreeDifference(DifferenceKind.OnlyInFirst, "a.txt"),
      new TreeDifference(DifferenceKind.Content, "b.txt", 0),
      new TreeDifference(DifferenceKind.OnlyInSecond, "c.txt")
    }));

  /// <summary>Test design 11: a difference at the first and at the last byte.</summary>
  [TestCase("xbcd", "abcd", 0)]
  [TestCase("abcx", "abcd", 3)]
  public void TheOffset_IsTheFirstDifferingByte(string first, string second, long offset)
    => Assert.That(TreeComparison.Compare(Digest(("f", first)), Digest(("f", second))), Is.EqualTo(new[] { new TreeDifference(DifferenceKind.Content, "f", offset) }));

  /// <summary>Test design 12: a strict prefix differs where it ends, in either direction.</summary>
  [TestCase("hello", "hello\n")]
  [TestCase("hello\n", "hello")]
  public void AStrictPrefix_DiffersWhereItEnds(string first, string second)
    => Assert.That(TreeComparison.Compare(Digest(("f", first)), Digest(("f", second)))[0].Offset, Is.EqualTo(5));

  /// <summary>Test design 13: names differing only in case are different files on every host.</summary>
  [Test]
  public void NamesDifferingInCase_AreDifferentFiles()
    => Assert.That(TreeComparison.Compare(Digest(("A.txt", "x")), Digest(("a.txt", "x"))), Is.EqualTo(new[] {
      new TreeDifference(DifferenceKind.OnlyInFirst, "A.txt"),
      new TreeDifference(DifferenceKind.OnlyInSecond, "a.txt")
    }));

  [Test]
  public void AnEmptyTree_AgainstAFullOne_ListsEveryFile() {
    Assert.That(TreeComparison.Compare(Digest(), Digest(("a", ""), ("b", ""))), Is.EqualTo(new[] {
      new TreeDifference(DifferenceKind.OnlyInSecond, "a"),
      new TreeDifference(DifferenceKind.OnlyInSecond, "b")
    }));
    Assert.That(TreeComparison.Compare(Digest(("a", "")), Digest()), Is.EqualTo(new[] { new TreeDifference(DifferenceKind.OnlyInFirst, "a") }));
  }

  [Test]
  public void NullTrees_AreRefused() {
    Assert.That(() => TreeComparison.Compare(null!, Digest()), Throws.ArgumentNullException);
    Assert.That(() => TreeComparison.Compare(Digest(), null!), Throws.ArgumentNullException);
  }

}
