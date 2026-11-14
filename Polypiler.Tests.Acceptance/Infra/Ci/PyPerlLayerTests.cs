using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Core.Verification.Running;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The rows of the test design of the Python/Perl layer that <c>tests/features/infra/infra-ci-pyperl.feature</c>
/// does not hold: the same checksum in both languages, the 64-bit boundary of Perl, a test file without
/// tests, the failing Perl test, a module outside the standard library, an interpreter that is absent or
/// at another version, the isolation of both interpreters and the command lines themselves.
/// </summary>
[TestFixture, Layer("pyperl")]
public sealed class PyPerlLayerTests {

  private const string ProbeLocks = "tests/fixtures/infra/ci/pyperl/probe";

  [Test, RequiresToolchain("python"), RequiresToolchain("perl")]
  public async Task BothSmokeScripts_PrintTheSameChecksum() {
    var python = await PyPerlSandbox.RunCommandAsync(PythonCommands.Run("adler32.py"));
    var perl = await PyPerlSandbox.RunCommandAsync(PerlCommands.Run("adler32.pl"));
    Assert.That((python.ExitCode, perl.ExitCode), Is.EqualTo((0, 0)), python.Output + perl.Output);
    Assert.That(perl.Stdout, Is.EqualTo(python.Stdout).And.EqualTo("38600999\n"));
  }

  [Test, RequiresToolchain("perl")]
  public async Task LargestUnsigned64BitValue_IsPrintedExactlyByPerl() {
    var result = await PyPerlSandbox.RunCommandAsync(PerlCommands.Run("uint64max.pl"));
    Assert.That((result.ExitCode, result.Stdout), Is.EqualTo((0, "18446744073709551615\n")), result.Output);
  }

  [Test, RequiresToolchain("perl")]
  public async Task PerlModuleTest_WithExactlyOneTest_Passes() {
    var result = await PyPerlSandbox.RunCommandAsync(PerlCommands.Test("adler32.t"));
    Assert.That(result.ExitCode, Is.EqualTo(0), result.Output);
    Assert.That(result.Stdout, Does.Contain("Files=1, Tests=1,").And.Contain("All tests successful."));
  }

  [Test, RequiresToolchain("python")]
  public async Task PythonModuleTest_RunsExactlyOneTest() {
    var result = await PyPerlSandbox.RunCommandAsync(PythonCommands.Test("test_adler32.py"));
    Assert.That(result.ExitCode, Is.EqualTo(0), result.Output);
    Assert.That(result.Stderr, Does.Contain("test_checksums").And.Contain("Ran 1 test in").And.EndWith("OK\n"));
  }

  [Test, RequiresToolchain("perl")]
  public async Task PerlTestFile_WithoutTests_FailsTheRun() {
    var result = await PyPerlSandbox.RunCommandAsync(PerlCommands.Test("empty.t"));
    Assert.That(result.ExitCode, Is.EqualTo(1), result.Output);
    Assert.That(result.Output, Does.Contain("No plan found"));
  }

  [Test, RequiresToolchain("perl")]
  public async Task FailingPerlTest_FailsTheRun() {
    var result = await PyPerlSandbox.RunCommandAsync(PerlCommands.Test("failing.t"));
    Assert.That(result.ExitCode, Is.EqualTo(1), result.Output);
    Assert.That(result.Stdout, Does.Contain("Result: FAIL"));
  }

  [Test, RequiresToolchain("python")]
  public async Task FailingPythonTest_FailsTheRun() {
    var result = await PyPerlSandbox.RunCommandAsync(PythonCommands.Test("failing_test.py"));
    Assert.That(result.ExitCode, Is.EqualTo(1), result.Output);
    Assert.That(result.Stderr, Does.Contain("FAILED (failures=1)"));
  }

  [Test, RequiresToolchain("python")]
  public async Task ModuleOutsideTheStandardLibrary_IsNotFound() {
    var result = await PyPerlSandbox.RunCommandAsync(PythonCommands.Run("needs_numpy.py"));
    Assert.That(result.ExitCode, Is.EqualTo(1), result.Output);
    Assert.That(result.Stderr, Does.Contain("ModuleNotFoundError: No module named 'numpy'"));
  }

  [Test, RequiresToolchain("python")]
  public async Task PythonWritesNoBytecode_WhenItImportsAModule() {
    var result = await PyPerlSandbox.RunAsync(PythonCommands.Test("test_adler32.py"), "__pycache__");
    Assert.That(result.ExitCode, Is.EqualTo(0), PyPerlSandbox.Text(result.Stderr));
    Assert.That(result.Collected, Is.Empty);
  }

  [Test, RequiresToolchain("python")]
  public async Task PythonIgnoresTheEnvironment() {
    var command = new ProcessSpec(PythonCommands.Toolchain, [.. PythonCommands.Isolation, "-c", "import sys; print(int(sys.flags.isolated), int(sys.flags.dont_write_bytecode), int(sys.flags.ignore_environment))"]) {
      Environment = new Dictionary<string, string> { ["PYTHONPATH"] = "/nowhere" }
    };
    var result = await PyPerlSandbox.RunCommandAsync(command);
    Assert.That((result.ExitCode, result.Stdout), Is.EqualTo((0, "1 1 1\n")), result.Output);
  }

  [Test, RequiresToolchain("perl")]
  public async Task PerlSeesNoPerl5Lib() {
    var result = await PyPerlSandbox.RunCommandAsync(new ProcessSpec(PerlCommands.Toolchain, ["-e", "print exists $ENV{PERL5LIB} || exists $ENV{PERL5OPT} ? 'set' : 'unset'"]));
    Assert.That((result.ExitCode, result.Stdout), Is.EqualTo((0, "unset")), result.Output);
  }

  [TestCase("python")]
  [TestCase("perl")]
  public void AbsentInterpreter_FailsTheTestInCi(string key) => AssertProbeFails(key, "absent.lock", "PLY0021");

  [Test, RequiresToolchain("python")]
  public void PythonAtAnotherVersion_FailsTheTestInCi() => AssertProbeFails("python", "other-version.lock", "PLY0022");

  [Test, RequiresToolchain("perl")]
  public void PerlAtAnotherVersion_FailsTheTestInCi() => AssertProbeFails("perl", "other-version.lock", "PLY0022");

  private static void AssertProbeFails(string key, string lockFile, string code) {
    var attribute = new RequiresToolchainAttribute(key) { Lock = $"{ProbeLocks}/{lockFile}" };
    var failure = Assert.Throws<AssertionException>(() => attribute.Check("1"))!;
    Assert.That(failure.Message, Does.StartWith(code + ":"));
  }

  [Test]
  public void CommandLines_AreTheDocumentedOnes() {
    Assert.That(PythonCommands.Run("a.py").ToString(), Is.EqualTo("python -I -B a.py"));
    Assert.That(PythonCommands.Test("test_a.py").ToString(), Is.EqualTo("python -I -B -m unittest discover -v -s . -p test_a.py"));
    Assert.That(PerlCommands.Check("a.pl").ToString(), Is.EqualTo("perl -c a.pl"));
    Assert.That(PerlCommands.Run("a.pl").ToString(), Is.EqualTo("perl -I. a.pl"));
    Assert.That(PerlCommands.Test("a.t").ToString(), Is.EqualTo($"perl -MApp::Prove -e {PerlCommands.ProveProgram} -- -I. a.t"));
  }

  [TestCase("python -I -B a.py", "python -I -B a.py")]
  [TestCase("perl  -c a.pl", "perl -c a.pl")]
  [TestCase("prove -I. a.t", "perl -MApp::Prove -e " + PerlCommands.ProveProgram + " -- -I. a.t")]
  public void FeatureLine_IsParsedIntoItsCommand(string line, string command)
    => Assert.That(PyPerlSandbox.Parse(line).ToString(), Is.EqualTo(command));

  [TestCase("ruby a.rb")]
  [TestCase("")]
  public void FeatureLine_OfAnotherProgram_IsRejected(string line)
    => Assert.That(() => PyPerlSandbox.Parse(line), Throws.ArgumentException.With.Message.Contains("neither"));

}
