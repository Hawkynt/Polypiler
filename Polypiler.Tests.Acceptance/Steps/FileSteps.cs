using System.IO;
using NUnit.Framework;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Steps;

/// <summary>The shared steps that write and check files of the scenario's workspace.</summary>
[Binding]
public sealed class FileSteps(ScenarioWorkspace workspace) {

  /// <summary>Writes a file; the content takes the escapes of <see cref="StepText.Unescape"/>.</summary>
  [Given("^the file " + StepText.Quoted + " with content " + StepText.Quoted + "$")]
  public void FileWithContent(string path, string content) => workspace.Write(StepText.Unescape(path), StepText.Unescape(content));

  /// <summary>Checks that a file exists.</summary>
  [Then("^the file " + StepText.Quoted + " exists$")]
  public void FileExists(string path) => Assert.That(File.Exists(workspace.Resolve(StepText.Unescape(path))), Is.True, path);

  /// <summary>Checks that a file does not exist.</summary>
  [Then("^the file " + StepText.Quoted + " does not exist$")]
  public void FileDoesNotExist(string path) => Assert.That(File.Exists(workspace.Resolve(StepText.Unescape(path))), Is.False, path);

  /// <summary>Checks that a file contains a text.</summary>
  [Then("^the file " + StepText.Quoted + " contains " + StepText.Quoted + "$")]
  public void FileContains(string path, string text)
    => Assert.That(File.ReadAllText(workspace.Resolve(StepText.Unescape(path))), Does.Contain(StepText.Unescape(text)));

}
