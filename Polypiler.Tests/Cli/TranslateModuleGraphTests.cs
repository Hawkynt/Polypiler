using System;
using System.IO;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;

namespace Polypiler.Tests.Cli;

/// <summary>
/// <c>translate</c> of a JavaScript module that requires other files (#1365): the files are read next
/// to it, and a refusal names the file it is about.
/// </summary>
[TestFixture]
public sealed class TranslateModuleGraphTests {

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-graph-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._directory, "lib"));
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  private (int ExitCode, string Output, string Error) Translate(string main) {
    File.WriteAllText(Path.Combine(this._directory, "main.js"), main);
    File.WriteAllText(Path.Combine(this._directory, "lib", "ops.js"), """
      /** @param {int32} a @param {int32} b @returns {int32} */
      function add(a, b) { return (a + b) | 0; }
      function unsupported(a) { return typeof a; }
      module.exports = { add, unsupported };
      """);
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = Program.Run(["translate", Path.Combine(this._directory, "main.js"), "--from", "javascript", "--to", "javascript", "--output", Path.Combine(this._directory, "out")], output, error);
    return (exitCode, output.ToString(), error.ToString());
  }

  [Test]
  public void RequiredFiles_AreReadNextToTheFile() {
    var (exitCode, _, error) = this.Translate("const Ops = require('./lib/ops');\n/** @param {int32} x @returns {int32} */\nfunction f(x) { return Ops.add(x, 1); }\nmodule.exports = { f };\n");
    Assert.That((exitCode, error), Is.EqualTo(((int)ExitCode.Success, "")));
    Assert.That(File.ReadAllText(Path.Combine(this._directory, "out", "main.mjs")), Does.Contain("function add(a, b)"));
  }

  [Test]
  public void RefusalInARequiredFile_NamesThatFile() {
    var (exitCode, _, error) = this.Translate("const Ops = require('./lib/ops');\nfunction f(x) { return Ops.unsupported(x); }\nmodule.exports = { f };\nconst M = require('./lib/missing');\n");
    Assert.That(exitCode, Is.EqualTo((int)ExitCode.UnsupportedConstruct));
    Assert.That(error, Does.Contain("'the required file './lib/missing', which cannot be read' is not supported yet"));
  }

}
