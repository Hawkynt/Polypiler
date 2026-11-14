using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;

namespace Polypiler.Tests.Verification.Fuzzing;

/// <summary>Budgets, outcomes, signatures and the in-process executor: test design rows 3, 10 and 11.</summary>
[TestFixture]
public sealed class FuzzOutcomeTests {

  private static readonly FuzzBudget _budget = new(TimeSpan.FromSeconds(1), 64L * 1024 * 1024, 65536);

  [Test]
  public void TheDefaultBudget_IsOneSecond64MiBAnd65536Bytes()
    => Assert.That(FuzzBudget.Default, Is.EqualTo(_budget));

  [Test]
  public void ABudgetOutsideItsRange_IsRefused() {
    Assert.That(() => new FuzzBudget(TimeSpan.Zero, 1, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new FuzzBudget(TimeSpan.FromSeconds(1), -1, 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new FuzzBudget(TimeSpan.FromSeconds(1), 1, -1), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(new FuzzBudget(TimeSpan.FromTicks(1), 0, 0).MaxInputLength, Is.Zero);
  }

  /// <summary>Test design 10: exactly the time budget is within it; a tick more is a hang.</summary>
  [Test]
  public void Judge_TimeOnAndAboveTheBudget() {
    Assert.That(new FuzzOutcome(null, "", 0, TimeSpan.FromSeconds(0.5)).Judge(_budget).IsFinding, Is.False);
    Assert.That(new FuzzOutcome(null, "", 0, TimeSpan.FromSeconds(1)).Judge(_budget).IsFinding, Is.False);
    var hang = new FuzzOutcome(null, "", 0, TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1)).Judge(_budget);
    Assert.That((hang.Kind, hang.Signature), Is.EqualTo(((FindingKind?)FindingKind.Hang, "hang")));
  }

  /// <summary>Test design 11: exactly 64 MiB is within the budget; one byte more is an allocation finding.</summary>
  [Test]
  public void Judge_AllocationOnAndAboveTheBudget() {
    Assert.That(new FuzzOutcome(null, "", 64L * 1024 * 1024, TimeSpan.Zero).Judge(_budget).IsFinding, Is.False);
    var allocation = new FuzzOutcome(null, "", 64L * 1024 * 1024 + 1, TimeSpan.Zero).Judge(_budget);
    Assert.That((allocation.Kind, allocation.Signature), Is.EqualTo(((FindingKind?)FindingKind.Allocation, "allocation")));
  }

  [Test]
  public void Judge_KeepsACrashOverTheBudgets() {
    var crash = new FuzzOutcome(FindingKind.Crash, "crash:x@y", long.MaxValue, TimeSpan.MaxValue);
    Assert.That(crash.Judge(_budget), Is.SameAs(crash));
    Assert.That(() => crash.Judge(null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 3: crash, allocation and mismatch in process; the hang needs a worker process.</summary>
  [Test]
  public void TheInProcessExecutor_SeesEachKind() {
    using var marker = new InProcessExecutor(new DemoMarkerTarget());
    var crash = marker.Execute("xxPLYxx"u8.ToArray(), _budget);
    Assert.That(crash.Kind, Is.EqualTo(FindingKind.Crash));
    Assert.That(crash.Signature, Is.EqualTo($"crash:System.InvalidOperationException@{typeof(DemoMarkerTarget).FullName}.Execute"));
    Assert.That(marker.Starts, Is.EqualTo(1));

    using var mismatch = new InProcessExecutor(new DemoMismatchTarget());
    Assert.That(mismatch.Execute("M"u8.ToArray(), _budget).Signature, Is.EqualTo($"mismatch:{typeof(FuzzMismatchException).FullName}@{typeof(DemoMismatchTarget).FullName}.Execute"));

    using var alloc = new InProcessExecutor(new DemoAllocTarget());
    var allocation = alloc.Execute([1], _budget);
    Assert.That(allocation.Kind, Is.EqualTo(FindingKind.Allocation));
    Assert.That(allocation.AllocatedBytes, Is.GreaterThanOrEqualTo(DemoAllocTarget.Allocation));
    Assert.That(alloc.Execute([2], _budget).IsFinding, Is.False);
  }

  [Test]
  public void AnExecutorBeforeItsFirstInput_HasNotStarted() {
    using var executor = new InProcessExecutor(new DemoCleanTarget());
    Assert.That(executor.Starts, Is.Zero);
    Assert.That(() => executor.Execute([], null!), Throws.ArgumentNullException);
    Assert.That(() => InProcessExecutor.Measure(null!, []), Throws.ArgumentNullException);
  }

  [Test]
  public void TheEmptyInput_IsExecutedAsAnEmptySpan() {
    var target = new RecordingTarget();
    InProcessExecutor.Measure(target, []);
    Assert.That(target.Lengths, Is.EqualTo(new[] { 0 }));
  }

  [Test]
  public void AnExceptionThrownOutsideTheTargetsAssembly_IsAttributedToTheTargetsFrame() {
    var outcome = InProcessExecutor.Measure(new ParsingTarget(), "x"u8);
    Assert.That(outcome.Signature, Is.EqualTo($"crash:System.FormatException@{typeof(ParsingTarget).FullName}.Execute"));
  }

  [Test]
  public void AnExceptionWithoutAFrameOfTheTarget_IsSignedWithAQuestionMark() {
    var outcome = FuzzOutcome.Thrown(new IOException("never thrown"), typeof(DemoMarkerTarget).Assembly, 0, TimeSpan.Zero);
    Assert.That(outcome.Signature, Is.EqualTo("crash:System.IO.IOException@?"));
    Assert.That(() => FuzzOutcome.Thrown(null!, typeof(DemoMarkerTarget).Assembly, 0, TimeSpan.Zero), Throws.ArgumentNullException);
    Assert.That(() => FuzzOutcome.Thrown(new IOException(), null!, 0, TimeSpan.Zero), Throws.ArgumentNullException);
  }

  /// <summary>Keeps the length of each input it is given.</summary>
  public sealed class RecordingTarget : IFuzzTarget {

    /// <summary>The lengths seen, in order.</summary>
    public System.Collections.Generic.List<int> Lengths { get; } = [];

    /// <inheritdoc />
    public string Id => "recording";

    /// <inheritdoc />
    public void Execute(ReadOnlySpan<byte> input) => this.Lengths.Add(input.Length);

  }

  /// <summary>Lets the base library throw: the frame inside the target is the caller of the parser.</summary>
  public sealed class ParsingTarget : IFuzzTarget {

    /// <inheritdoc />
    public string Id => "parsing";

    /// <inheritdoc />
    public void Execute(ReadOnlySpan<byte> input) => int.Parse(System.Text.Encoding.ASCII.GetString(input), System.Globalization.CultureInfo.InvariantCulture);

  }

}
