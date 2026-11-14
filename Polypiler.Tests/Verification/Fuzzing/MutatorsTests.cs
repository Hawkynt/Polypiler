using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;
using Polypiler.Core.Verification.Generators;

namespace Polypiler.Tests.Verification.Fuzzing;

/// <summary>Test design 4: each mutator in isolation on <c>41 42 43</c>, with fixed seeds.</summary>
[TestFixture]
public sealed class MutatorsTests {

  private static readonly byte[] _input = [0x41, 0x42, 0x43];
  private static readonly byte[][] _corpus = [[0x61, 0x62, 0x63, 0x64]];

  private static readonly ulong[] _seeds = [0, 1, 7, 42, 1234567];

  private static byte[] Apply(Mutator mutator, ulong seed, byte[]? input = null) {
    var original = (byte[])(input ?? _input).Clone();
    var output = mutator(original, new SeededRandom(seed), _corpus);
    Assert.That(original, Is.EqualTo(input ?? _input), "the input is not modified");
    Assert.That(output, Is.Not.SameAs(original));
    return output;
  }

  private static bool IsSubsequence(byte[] shorter, byte[] longer) {
    var at = 0;
    foreach (var value in longer)
      if (at < shorter.Length && shorter[at] == value)
        ++at;
    return at == shorter.Length;
  }

  [TestCaseSource(nameof(_seeds))]
  public void FlipBit_ChangesExactlyOneBit(ulong seed) {
    var output = Apply(Mutators.FlipBit, seed);
    Assert.That(output, Has.Length.EqualTo(3));
    Assert.That(output.Zip(_input, (a, b) => BitOperations.PopCount((uint)(a ^ b))).Sum(), Is.EqualTo(1));
  }

  [TestCaseSource(nameof(_seeds))]
  public void ReplaceInteresting_PutsAnInterestingValueIntoOneByte(ulong seed) {
    var output = Apply(Mutators.ReplaceInteresting, seed);
    Assert.That(output, Has.Length.EqualTo(3));
    var changed = Enumerable.Range(0, 3).Where(index => output[index] != _input[index]).ToList();
    Assert.That(changed, Has.Count.EqualTo(1), "none of 41 42 43 is an interesting value");
    Assert.That(Mutators.InterestingValues, Does.Contain(output[changed[0]]));
  }

  [TestCaseSource(nameof(_seeds))]
  public void InsertByte_AddsOneByte(ulong seed) {
    var output = Apply(Mutators.InsertByte, seed);
    Assert.That(output, Has.Length.EqualTo(4));
    Assert.That(IsSubsequence(_input, output), Is.True);
  }

  [TestCaseSource(nameof(_seeds))]
  public void DeleteByte_RemovesOneByte(ulong seed) {
    var output = Apply(Mutators.DeleteByte, seed);
    Assert.That(output, Has.Length.EqualTo(2));
    Assert.That(IsSubsequence(output, _input), Is.True);
  }

  [TestCaseSource(nameof(_seeds))]
  public void DuplicateChunk_RepeatsAChunkAfterItself(ulong seed) {
    var output = Apply(Mutators.DuplicateChunk, seed);
    var extra = output.Length - 3;
    Assert.That(extra, Is.InRange(1, 3));
    var found = Enumerable.Range(0, 3 - extra + 1).Any(start =>
      output.AsSpan(0, start + extra).SequenceEqual(_input.AsSpan(0, start + extra))
      && output.AsSpan(start + extra, extra).SequenceEqual(_input.AsSpan(start, extra))
      && output.AsSpan(start + 2 * extra).SequenceEqual(_input.AsSpan(start + extra)));
    Assert.That(found, Is.True, Convert.ToHexString(output));
  }

  [TestCaseSource(nameof(_seeds))]
  public void Splice_JoinsAPrefixWithASuffixOfAnEntry(ulong seed) {
    var output = Apply(Mutators.Splice, seed);
    var found = Enumerable.Range(0, 4).Any(cut =>
      output.AsSpan().StartsWith(_input.AsSpan(0, cut)) && _corpus[0].AsSpan().EndsWith(output.AsSpan(cut)) && output.Length - cut <= 4);
    Assert.That(found, Is.True, Convert.ToHexString(output));
  }

  [Test]
  public void Splice_WithoutACorpus_Copies()
    => Assert.That(Mutators.Splice(_input, new SeededRandom(1), []), Is.EqualTo(_input));

  [TestCaseSource(nameof(_seeds))]
  public void Truncate_KeepsAShorterPrefix(ulong seed) {
    var output = Apply(Mutators.Truncate, seed);
    Assert.That(output.Length, Is.LessThan(3));
    Assert.That(_input.AsSpan().StartsWith(output), Is.True);
  }

  [Test]
  public void TheMutatorsThatNeedAByte_LeaveTheEmptyInputEmpty() {
    foreach (var mutator in new Mutator[] { Mutators.FlipBit, Mutators.ReplaceInteresting, Mutators.DeleteByte, Mutators.DuplicateChunk, Mutators.Truncate })
      Assert.That(Apply(mutator, 3, []), Is.Empty, mutator.Method.Name);
  }

  [Test]
  public void InsertAndSplice_GrowTheEmptyInput() {
    Assert.That(Apply(Mutators.InsertByte, 3, []), Has.Length.EqualTo(1));
    Assert.That(_corpus[0].AsSpan().EndsWith(Apply(Mutators.Splice, 3, [])), Is.True);
  }

  [Test]
  public void All_ListsTheSevenMutators()
    => Assert.That(Mutators.All, Is.EqualTo(new Mutator[] {
      Mutators.FlipBit, Mutators.ReplaceInteresting, Mutators.InsertByte, Mutators.DeleteByte, Mutators.DuplicateChunk, Mutators.Splice, Mutators.Truncate
    }));

  [Test]
  public void Mutate_IsDeterministicPerSeed() {
    for (var seed = 0UL; seed < 50; ++seed) {
      var first = Mutators.Mutate(_input, new SeededRandom(seed), _corpus);
      var second = Mutators.Mutate(_input, new SeededRandom(seed), _corpus);
      Assert.That(second, Is.EqualTo(first));
    }
  }

  [Test]
  public void Mutate_ProducesManyDifferentInputs() {
    var outputs = Enumerable.Range(0, 200).Select(seed => Convert.ToHexString(Mutators.Mutate(_input, new SeededRandom((ulong)seed), _corpus))).ToHashSet();
    Assert.That(outputs, Has.Count.GreaterThan(100));
  }

  [Test]
  public void NullArguments_AreRefused() {
    Assert.That(() => Mutators.Mutate(_input, null!, _corpus), Throws.ArgumentNullException);
    Assert.That(() => Mutators.InsertByte(null!, new SeededRandom(1), _corpus), Throws.ArgumentNullException);
    Assert.That(() => Mutators.InsertByte(_input, null!, _corpus), Throws.ArgumentNullException);
    Assert.That(() => Mutators.FlipBit(null!, new SeededRandom(1), _corpus), Throws.ArgumentNullException);
    Assert.That(() => Mutators.FlipBit(_input, null!, _corpus), Throws.ArgumentNullException);
    Assert.That(() => Mutators.Splice(_input, new SeededRandom(1), null!), Throws.ArgumentNullException);
  }

}
