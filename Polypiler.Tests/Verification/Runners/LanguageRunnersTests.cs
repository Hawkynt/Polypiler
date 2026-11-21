using System;
using System.Collections.Generic;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Core.Verification.Oracles;
using Polypiler.Core.Verification.Runners;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Verification.Runners;

[TestFixture]
public sealed class LanguageRunnersTests {

  [Test]
  public void ResolvesRegisteredRunnerAndItsOptionalCapability() {
    var registry = new LanguageRunners();
    var runner = new HelperRunner();
    registry.Register(runner);

    Assert.That(registry.Resolve("test"), Is.SameAs(runner));
    Assert.That(registry.TryGet<IHelperRunner>("test", out var helper), Is.True);
    Assert.That(helper, Is.SameAs(runner));
    Assert.That(registry.TryGet<IFormatChecker>("test", out var formatter), Is.False);
    Assert.That(formatter, Is.Null);
  }

  [Test]
  public void RejectsDuplicateLanguageAndReportsMissingLanguage() {
    var registry = new LanguageRunners();
    var runner = new HelperRunner();
    registry.Register(runner);

    Assert.That(() => registry.Register(new HelperRunner()), Throws.TypeOf<InvalidOperationException>());
    Assert.That(registry.Resolve("test"), Is.SameAs(runner));
    Assert.That(() => registry.Resolve("missing"), Throws.TypeOf<KeyNotFoundException>().With.Message.Contains("missing"));
    Assert.That(registry.TryGet<IHelperRunner>("missing", out _), Is.False);
  }

  private sealed class HelperRunner : ILanguageRunner, IHelperRunner {

    public string Language => "test";
    public IReadOnlyList<string> Toolchains => [];
    public CompileResult Compile(RunnerInput input) => throw new NotSupportedException();
    public RunResult Execute(CompileResult compiled, IReadOnlyList<string> arguments) => throw new NotSupportedException();
    public IReadOnlyList<CallOutcome> ExecuteFunction(string directory, string export, CallSignature signature, IReadOnlyList<byte[]> inputImages) => throw new NotSupportedException();
    public ComparisonReport RunVectors(RunnerInput input, VectorFile vectors) => throw new NotSupportedException();
    public VectorFile Record(RunnerInput input, VectorFile vectors) => throw new NotSupportedException();
    public TestReport RunTests(string projectDirectory) => throw new NotSupportedException();
    public RunResult RunHelper(string helperPath, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> stdin) => throw new NotSupportedException();

  }

}
