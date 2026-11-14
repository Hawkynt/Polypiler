using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Running;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;
using Polypiler.Tests.Verification.Running.Support;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The Kotlin/Java layer beyond its feature file (<c>tests/features/infra/infra-ci-jvm.feature</c>):
/// both compilers' artefacts, the class directory against the runnable jar, the Java/Kotlin output
/// comparison when the two differ, the boundaries of 32-bit wrap-around and of the unsigned shift,
/// the empty checksum, warnings under <c>-Werror</c>, a missing main class and a missing or other JDK.
/// Every program is compiled and executed for real, on the toolchains of <c>tooling/toolchains.lock</c>.
/// </summary>
[Layer("jvm")]
public sealed class JvmLayerTests {

  /// <summary>The folder of the layer's fixtures, relative to the repository root.</summary>
  public const string FixtureFolder = "tests/fixtures/infra/ci/jvm";

  private const string EndToEnd = "EndToEnd";

  private static readonly Lazy<JvmCommands> _commands = new(() => new JvmCommands(RunnerTesting.Toolchains));

  /// <summary>The commands over the repository lock, shared by every test of the layer.</summary>
  internal static JvmCommands Commands => _commands.Value;

  /// <summary>The bytes of a fixture of the layer.</summary>
  /// <param name="name">The fixture's file name.</param>
  internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(PrototypeFixture.RepositoryRoot, FixtureFolder, name));

  /// <summary>Ends the test unless the toolchains of a source are at their pinned versions: the JDK, and the Kotlin compiler for Kotlin.</summary>
  /// <param name="source">The source's file name.</param>
  internal static void Require(string source) {
    var require = Environment.GetEnvironmentVariable(ToolchainLocator.RequireVariable);
    new RequiresToolchainAttribute(JvmCommands.Jdk).Check(require);
    if (JvmCommands.IsKotlin(source))
      new RequiresToolchainAttribute(JvmCommands.Kotlinc).Check(require);
  }

  /// <summary>
  /// Compiles a source and executes the result in the sandbox of this host, after its toolchains are
  /// checked, and writes the outcome, the exit code and both outputs to the test's output as evidence.
  /// </summary>
  /// <param name="source">The source's file name, a fixture of the layer unless <paramref name="text"/> is given.</param>
  /// <param name="text">The source's bytes, when they are not the fixture's.</param>
  /// <param name="execute">The execute step, when it is not the default of the source.</param>
  internal static async Task<RunResult> Run(string source, byte[]? text = null, ProcessSpec? execute = null) {
    Require(source);
    var result = await Commands.RunAsync(Commands.Plan(source, text ?? Fixture(source), execute), RunnerTesting.Host);
    TestContext.Out.WriteLine($"{source}: {(execute ?? JvmCommands.Execute(source))} -> {result.Outcome}, exit code {result.ExitCode}, build {result.BuildTime.TotalMilliseconds:0} ms, run {result.WallTime.TotalMilliseconds:0} ms");
    TestContext.Out.WriteLine($"stdout:\n{Encoding.UTF8.GetString(result.Stdout.Span)}");
    TestContext.Out.WriteLine($"stderr:\n{Encoding.UTF8.GetString(result.Stderr.Span)}");
    foreach (var diagnostic in result.Diagnostics)
      TestContext.Out.WriteLine($"{diagnostic.Code}: {diagnostic.Message}\n{diagnostic.Cause}");
    return result;
  }

  private static string Stdout(RunResult result) => Encoding.UTF8.GetString(result.Stdout.Span);

  private static void AssertCompleted(RunResult result, string stdout) {
    Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed), () => string.Join('\n', result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}\n{diagnostic.Cause}")));
    Assert.That(result.ExitCode, Is.Zero, Encoding.UTF8.GetString(result.Stderr.Span));
    Assert.That(Stdout(result), Is.EqualTo(stdout));
  }

  #region the toolchains run

  [TestCase("Adler32.java", "out/Adler32.class", TestName = "javac writes the classes of a Java source")]
  [TestCase("adler32.kt", "out.jar", TestName = "kotlinc writes a runnable jar of a Kotlin source")]
  [Category(EndToEnd)]
  public async Task EachCompiler_ProducesARunnableArtefact(string source, string artefact) {
    var result = await Run(source);
    AssertCompleted(result, "38600999\n");
    Assert.That(result.Collected.Keys, Has.Member(artefact));
  }

  [Test, Category(EndToEnd)]
  public async Task ClassDirectoryAndRunnableJar_PrintTheSame() {
    var classes = await Run("Adler32.java");
    var jar = await Run("adler32.kt");
    var jarOnClassPath = await Run("adler32.kt", execute: new(JvmCommands.Java, "-cp", JvmCommands.Jar, JvmCommands.MainClass("adler32.kt")));
    Assert.Multiple(() => {
      AssertCompleted(classes, "38600999\n");
      Assert.That(JvmCommands.Difference(classes.Stdout.Span, jar.Stdout.Span), Is.Null);
      Assert.That(JvmCommands.Difference(jar.Stdout.Span, jarOnClassPath.Stdout.Span), Is.Null);
    });
  }

  [Test, Category(EndToEnd)]
  public async Task EmptyInput_HasTheChecksumOne()
    => AssertCompleted(await Run("Adler32.java", execute: JvmCommands.RunClass("Adler32", "--empty")), "1\n");

  #endregion

  #region the Java/Kotlin comparison

  [Test, Category(EndToEnd)]
  public async Task WrapAndShift_AtTheirBoundaries_AgreeBetweenJavaAndKotlin() {
    string[] arguments = ["add:0", "add:1", "ushr:0", "ushr:28", "ushr:31", "ushr:32"];
    var java = await Run("Wrap.java", execute: JvmCommands.Execute("Wrap.java", arguments));
    var kotlin = await Run("wrap.kt", execute: JvmCommands.Execute("wrap.kt", arguments));
    Assert.Multiple(() => {
      AssertCompleted(java, "2147483647\n-2147483648\n-1\n15\n1\n-1\n");
      AssertCompleted(kotlin, "2147483647\n-2147483648\n-1\n15\n1\n-1\n");
      Assert.That(JvmCommands.Difference(java.Stdout.Span, kotlin.Stdout.Span), Is.Null);
    });
  }

  [Test, Category(EndToEnd)]
  public async Task Comparison_WhenTheKotlinProgramPrintsSomethingElse_FailsShowingBothOutputs() {
    var wrong = Encoding.UTF8.GetString(Fixture("wrap.kt")).Replace("println(-1 ushr 28)", "println((-1 ushr 28) + 1)", StringComparison.Ordinal);
    var java = await Run("Wrap.java");
    var kotlin = await Run("wrap.kt", Encoding.UTF8.GetBytes(wrong));
    AssertCompleted(kotlin, "-2147483648\n16\n");
    var difference = JvmCommands.Difference(java.Stdout.Span, kotlin.Stdout.Span);
    Assert.That(difference, Does.Contain("differs at byte offset 13").And.Contain("--- Java ---\n-2147483648\n15\n").And.Contain("--- Kotlin ---\n-2147483648\n16\n"));
  }

  [Test]
  public void Comparison_OfIdenticalOutputs_FindsNoDifference()
    => Assert.That(JvmCommands.Difference("-2147483648\n15\n"u8, "-2147483648\n15\n"u8), Is.Null);

  [Test]
  public void Comparison_DoesNotTrimTheFinalNewline()
    => Assert.That(JvmCommands.Difference("15\n"u8, "15"u8), Does.Contain("differs at byte offset 2: expected 0x0A, actual end of output"));

  #endregion

  #region failures

  [TestCase("Warn.java", "[unchecked]", TestName = "javac -Xlint:all -Werror fails on an unchecked cast")]
  [TestCase("warn.kt", "unchecked cast", TestName = "kotlinc -Werror fails on an unchecked cast")]
  [Category(EndToEnd)]
  public async Task Warning_UnderWerror_IsATargetBuildFailure(string source, string warning) {
    var result = await Run(source);
    Assert.Multiple(() => {
      Assert.That(result.Outcome, Is.EqualTo(RunOutcome.BuildFailed));
      Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0030" }));
      Assert.That(result.Diagnostics[0].Cause, Does.Contain(warning).IgnoreCase.And.Contain("-Werror"));
      Assert.That(result.Stdout.Length + result.Stderr.Length, Is.Zero);
    });
  }

  [Test, Category(EndToEnd)]
  public async Task MissingMainClass_IsACompletedRunThatExitsWithOne() {
    var result = await Run("Adler32.java", execute: JvmCommands.RunClass("Missing"));
    Assert.Multiple(() => {
      Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Completed));
      Assert.That(result.ExitCode, Is.EqualTo(1));
      Assert.That(Encoding.UTF8.GetString(result.Stderr.Span), Does.Contain("java.lang.ClassNotFoundException: Missing"));
    });
  }

  #endregion

  #region missing toolchains

  [TestCase(JvmCommands.Jdk, TestName = "Without a JDK the layer's tests are inconclusive, and fail in CI")]
  [TestCase(JvmCommands.Kotlinc, TestName = "Without kotlinc the layer's tests are inconclusive, and fail in CI")]
  public void AbsentToolchain_IsNotRunRatherThanPassed(string key) {
    var attribute = new RequiresToolchainAttribute(key) { Lock = FixtureFolder + "/absent.lock" };
    Assert.Multiple(() => {
      Assert.That(Assert.Throws<InconclusiveException>(() => attribute.Check(null))!.Message, Does.Contain($"toolchain '{key}'").And.Contain("PLY0021"));
      Assert.That(Assert.Throws<AssertionException>(() => attribute.Check("1"))!.Message, Does.Contain("PLY0021"));
    });
  }

  [Test, Category(EndToEnd), RequiresToolchain(JvmCommands.Jdk)]
  public void JdkAtAnotherVersion_FailsInCiWithAMismatch() {
    var attribute = new RequiresToolchainAttribute(JvmCommands.Jdk) { Lock = FixtureFolder + "/other-version.lock" };
    Assert.That(Assert.Throws<AssertionException>(() => attribute.Check("1"))!.Message, Does.Contain("PLY0022").And.Contain("0.0.0-polypiler"));
  }

  [Test]
  public async Task AbsentJdk_RefusesThePlanBeforeAnythingStarts() {
    var commands = new JvmCommands(RunnerTesting.Resolver(Path.Combine(PrototypeFixture.RepositoryRoot, FixtureFolder, "absent.lock")));
    var result = await commands.RunAsync(commands.Plan("Adler32.java", Fixture("Adler32.java")), RunnerTesting.Host);
    Assert.Multiple(() => {
      Assert.That(commands.JdkHome, Is.Null);
      Assert.That(result.Outcome, Is.EqualTo(RunOutcome.Refused));
      Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { "PLY0021", "PLY0021" }));
      Assert.That(commands.CompileKotlin("adler32.kt").Environment, Is.Empty);
    });
  }

  #endregion

  #region the commands

  /// <summary>Resolves the JDK to a <c>javac</c> in a folder of its own and nothing else.</summary>
  private sealed class JdkOnly(string javac) : IToolchainResolver {

    public List<string> Asked { get; } = [];

    public ToolchainResolution Resolve(string toolchain) {
      this.Asked.Add(toolchain);
      return toolchain == JvmCommands.Jdk ? ToolchainResolution.Found(javac) : ToolchainResolution.Failed(Diagnostic.Create(ToolchainDiagnostic.NotInstalledDescriptor, null, $"toolchain '{toolchain}' is not installed"));
    }

  }

  private static string FakeJdk(bool withJava) {
    var home = Directory.CreateTempSubdirectory("polypiler-jdk-").FullName;
    Directory.CreateDirectory(Path.Combine(home, "bin"));
    File.WriteAllText(Path.Combine(home, "bin", "javac"), "");
    if (withJava)
      File.WriteAllText(Path.Combine(home, "bin", "java"), "");
    return home;
  }

  [Test]
  public void Java_IsTheLauncherBesideTheJdksJavac() {
    var home = FakeJdk(withJava: true);
    try {
      var commands = new JvmCommands(new JdkOnly(Path.Combine(home, "bin", "javac")));
      Assert.Multiple(() => {
        Assert.That(commands.JdkHome, Is.EqualTo(home));
        Assert.That(commands.Toolchains.Resolve(JvmCommands.Java).Executable, Is.EqualTo(Path.Combine(home, "bin", "java")));
        Assert.That(commands.CompileKotlin("adler32.kt").Environment, Is.EqualTo(new Dictionary<string, string> { ["JAVA_HOME"] = home }));
      });
    } finally {
      Directory.Delete(home, true);
    }
  }

  [Test]
  public void Java_WhenTheJdkHasNoLauncher_IsNotInstalled() {
    var home = FakeJdk(withJava: false);
    try {
      var resolution = new JvmCommands(new JdkOnly(Path.Combine(home, "bin", "javac"))).Toolchains.Resolve(JvmCommands.Java);
      Assert.Multiple(() => {
        Assert.That(resolution.Executable, Is.Null);
        Assert.That(resolution.Problem!.Code, Is.EqualTo("PLY0021"));
        Assert.That(resolution.Problem.Message, Does.Contain("has no java launcher"));
      });
    } finally {
      Directory.Delete(home, true);
    }
  }

  [Test]
  public void Toolchains_PassEveryOtherKeyToTheLock() {
    var inner = new JdkOnly("/nowhere/bin/javac");
    var resolution = new JvmCommands(inner).Toolchains.Resolve(JvmCommands.Kotlinc);
    Assert.Multiple(() => {
      Assert.That(resolution.Problem!.Message, Does.Contain("toolchain 'kotlinc'"));
      Assert.That(inner.Asked, Is.EqualTo(new[] { JvmCommands.Kotlinc }));
    });
  }

  [Test]
  public void Constructor_WithoutToolchains_Throws()
    => Assert.That(() => new JvmCommands(null!), Throws.ArgumentNullException);

  [TestCase("Adler32.java", ExpectedResult = "Adler32")]
  [TestCase("adler32.kt", ExpectedResult = "Adler32Kt")]
  [TestCase("type-error.kt", ExpectedResult = "Type_errorKt")]
  public string MainClass_FollowsTheNamingOfEachLanguage(string source) => JvmCommands.MainClass(source);

  [TestCase("hello.py")]
  [TestCase("Main")]
  public void Source_OfAnotherLanguage_IsRejected(string source)
    => Assert.That(() => JvmCommands.IsKotlin(source), Throws.ArgumentException.With.Message.Contains(source));

  [Test]
  public void Commands_AreTheOnesOfTheLayer() {
    var commands = new JvmCommands(new JdkOnly("/nowhere/bin/javac"));
    Assert.Multiple(() => {
      Assert.That(commands.Compile("Adler32.java").ToString(), Is.EqualTo("jdk -Xlint:all -Werror -d out Adler32.java"));
      Assert.That(commands.Compile("adler32.kt").ToString(), Is.EqualTo("kotlinc adler32.kt -Werror -include-runtime -d out.jar"));
      Assert.That(JvmCommands.Execute("Adler32.java", "--empty").ToString(), Is.EqualTo("java -cp out Adler32 --empty"));
      Assert.That(JvmCommands.Execute("adler32.kt", "--empty").ToString(), Is.EqualTo("java -jar out.jar --empty"));
      Assert.That(commands.Plan("Adler32.java", Fixture("Adler32.java")).Collect, Is.EqualTo(new[] { "out", "out.jar" }));
    });
  }

  #endregion

}
