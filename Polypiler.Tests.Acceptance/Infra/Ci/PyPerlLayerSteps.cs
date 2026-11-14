using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Steps;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>
/// The steps of <c>tests/features/infra/infra-ci-pyperl.feature</c>. Every command runs through
/// <see cref="PyPerlSandbox"/> under the pinned interpreter, and its result goes to the scenario's
/// command log, where the shared steps check exit code and standard output.
/// </summary>
[Binding, Scope(Feature = "Python/Perl CI layer")]
public sealed class PyPerlLayerSteps(CommandLog log) {

  private readonly Dictionary<string, CommandResult> _byScript = new(StringComparer.Ordinal);
  private string _script = "";

  private async Task RunAsync(string line) => log.Add(await PyPerlSandbox.RunCommandAsync(PyPerlSandbox.Parse(line)));

  [Given("^the script " + StepText.Quoted + "$")]
  public void TheScript(string path) {
    Assert.That(File.Exists(Path.Combine(PrototypeFixture.RepositoryRoot, path)), Is.True, path);
    this._script = Path.GetFileName(path);
  }

  [When("^I run it with " + StepText.Quoted + " in the sandbox$")]
  public Task RunItWith(string command) => this.RunAsync($"{command} {this._script}");

  [Given("^the Python test " + StepText.Quoted + " and the Perl test " + StepText.Quoted + "$")]
  public void TheTests(string python, string perl)
    => Assert.That(new[] { python, perl }, Is.SubsetOf(PyPerlSandbox.Files.Keys));

  [When("^I run " + StepText.Quoted + " and " + StepText.Quoted + " in the sandbox$")]
  public async Task RunBoth(string first, string second) {
    foreach (var line in new[] { first, second }) {
      var command = line.Contains(' ', StringComparison.Ordinal)
        ? PyPerlSandbox.Parse(line)
        : line.EndsWith(".py", StringComparison.Ordinal) ? PythonCommands.Run(line) : PerlCommands.Run(line);
      var result = await PyPerlSandbox.RunCommandAsync(command);
      log.Add(result);
      this._byScript[line] = result;
    }
  }

  [When("^I run " + StepText.Quoted + " in the sandbox$")]
  public Task RunOne(string line) => this.RunAsync(line);

  [Then("^the first command reports " + StepText.Quoted + " after " + StepText.Quoted + "$")]
  public void FirstReports(string verdict, string before) {
    var first = log.Commands[0];
    Assert.That(first.ExitCode, Is.EqualTo(0), first.Output);
    var output = first.Output;
    var at = output.IndexOf(before, StringComparison.Ordinal);
    Assert.That(at, Is.GreaterThanOrEqualTo(0), output);
    Assert.That(output[at..].Split('\n')[0], Is.EqualTo(before).Or.StartsWith(before + " in "), "exactly this count of tests ran");
    Assert.That(output.IndexOf(verdict, at, StringComparison.Ordinal), Is.GreaterThan(at), output);
  }

  [Then("^the second command reports " + StepText.Quoted + "$")]
  public void SecondReports(string verdict) {
    var second = log.Commands[1];
    Assert.That(second.ExitCode, Is.EqualTo(0), second.Output);
    Assert.That(second.Stdout, Does.Contain(verdict));
  }

  [Then("^" + StepText.Quoted + " prints " + StepText.Quoted + "$")]
  public void Prints(string script, string line) => this.PrintsLines(script, line);

  [Then("^" + StepText.Quoted + " prints " + StepText.Quoted + " followed by " + StepText.Quoted + "$")]
  public void PrintsTwo(string script, string first, string second) => this.PrintsLines(script, first, second);

  private void PrintsLines(string script, params string[] lines) {
    var result = this._byScript[script];
    Assert.That(result.ExitCode, Is.EqualTo(0), result.Output);
    Assert.That(result.Stdout, Is.EqualTo(string.Join("", Array.ConvertAll(lines, line => line + "\n"))));
  }

  [Then("^the output contains " + StepText.Quoted + "$")]
  public void OutputContains(string text) => Assert.That(log.Last.Output, Does.Contain(StepText.Unescape(text)));

}
