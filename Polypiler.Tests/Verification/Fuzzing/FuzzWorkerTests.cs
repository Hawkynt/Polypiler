using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Verification.Fuzzing;

namespace Polypiler.Tests.Verification.Fuzzing;

/// <summary>The worker protocol, served on memory streams, and the discovery of targets.</summary>
[TestFixture]
public sealed class FuzzWorkerTests {

  private static byte[] Request(byte[] input) {
    var request = new byte[4 + input.Length];
    BinaryPrimitives.WriteInt32LittleEndian(request, input.Length);
    input.CopyTo(request, 4);
    return request;
  }

  private static (byte Kind, long Allocated, string Signature) Response(BinaryReader reader) {
    var kind = reader.ReadByte();
    var allocated = reader.ReadInt64();
    reader.ReadInt64();
    var signature = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
    return (kind, allocated, signature);
  }

  [Test]
  public void Serve_AnnouncesItselfAndAnswersEachRequest() {
    using var requests = new MemoryStream([.. Request([1, 2]), .. Request("PLY"u8.ToArray()), .. Request([]), .. Request([0xFF])[..2]]);
    using var responses = new MemoryStream();
    FuzzWorker.Serve(new DemoMarkerTarget(), requests, responses);

    responses.Position = 0;
    using var reader = new BinaryReader(responses);
    Assert.That(reader.ReadByte(), Is.EqualTo(FuzzWorker.Ready));
    var passed = Response(reader);
    Assert.That((passed.Kind, passed.Signature), Is.EqualTo(((byte)0, "")));
    var crash = Response(reader);
    Assert.That((crash.Kind, crash.Signature), Is.EqualTo(((byte)1, $"crash:System.InvalidOperationException@{typeof(DemoMarkerTarget).FullName}.Execute")));
    Assert.That(Response(reader).Kind, Is.Zero);
    Assert.That(responses.Position, Is.EqualTo(responses.Length), "a request cut short ends the worker");
  }

  [Test]
  public void Serve_ReportsAMismatchAndStopsAtANegativeLength() {
    var end = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(end, -1);
    using var requests = new MemoryStream([.. Request("M"u8.ToArray()), .. end, .. Request([1])]);
    using var responses = new MemoryStream();
    FuzzWorker.Serve(new DemoMismatchTarget(), requests, responses);

    responses.Position = 1;
    using var reader = new BinaryReader(responses);
    Assert.That(Response(reader).Kind, Is.EqualTo(2));
    Assert.That(responses.Position, Is.EqualTo(responses.Length));
  }

  [Test]
  public void Serve_RefusesNullArguments() {
    Assert.That(() => FuzzWorker.Serve(null!, Stream.Null, Stream.Null), Throws.ArgumentNullException);
    Assert.That(() => FuzzWorker.Serve(new DemoCleanTarget(), null!, Stream.Null), Throws.ArgumentNullException);
    Assert.That(() => FuzzWorker.Serve(new DemoCleanTarget(), Stream.Null, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Discover_FindsThePublicTargetsWithAParameterlessConstructor() {
    var ids = FuzzTargets.Discover(typeof(DemoMarkerTarget).Assembly).Select(target => target.Id).ToList();
    Assert.That(ids, Is.SupersetOf(new[] { "demo-alloc", "demo-always", "demo-clean", "demo-failfast", "demo-hang", "demo-marker", "demo-mismatch", "demo-sleep", "demo-zero" }));
    Assert.That(ids, Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
    Assert.That(ids, Does.Not.Contain("hidden").And.Not.Contain("abstract").And.Not.Contain("parameterised"));
    Assert.That(() => FuzzTargets.Discover(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Discover_FindsTheCliTarget()
    => Assert.That(FuzzTargets.Discover(typeof(Polypiler.Cli.Fuzzing.CliArgsFuzzTarget).Assembly).Select(target => target.Id), Is.EqualTo(new[] { "cli-args" }));

  [Test]
  public void TheCliTarget_SplitsItsInputIntoArguments() {
    Assert.That(Polypiler.Cli.Fuzzing.CliArgsFuzzTarget.Arguments([]), Is.Empty);
    Assert.That(Polypiler.Cli.Fuzzing.CliArgsFuzzTarget.Arguments("--help"u8), Is.EqualTo(new[] { "--help" }));
    Assert.That(Polypiler.Cli.Fuzzing.CliArgsFuzzTarget.Arguments("frobnicate\0--force\0"u8), Is.EqualTo(new[] { "frobnicate", "--force", "" }));
    Assert.That(InProcessExecutor.Measure(new Polypiler.Cli.Fuzzing.CliArgsFuzzTarget(), "--version"u8).IsFinding, Is.False);
  }

  /// <summary>Not discovered: not public.</summary>
  internal sealed class HiddenTarget : IFuzzTarget {

    public string Id => "hidden";

    public void Execute(ReadOnlySpan<byte> input) { }

  }

  /// <summary>Not discovered: abstract.</summary>
  public abstract class AbstractTarget : IFuzzTarget {

    /// <inheritdoc />
    public string Id => "abstract";

    /// <inheritdoc />
    public void Execute(ReadOnlySpan<byte> input) { }

  }

  /// <summary>Not discovered: no parameterless constructor.</summary>
  public sealed class ParameterisedTarget(int value) : IFuzzTarget {

    /// <inheritdoc />
    public string Id => "parameterised" + value;

    /// <inheritdoc />
    public void Execute(ReadOnlySpan<byte> input) { }

  }

}
