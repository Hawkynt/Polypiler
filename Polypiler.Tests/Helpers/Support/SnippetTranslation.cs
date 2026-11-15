using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Cli;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;

namespace Polypiler.Tests.Helpers.Support;

/// <summary>Translates a C# source with the command line, in process, the way <c>polypiler translate</c> does.</summary>
internal static class SnippetTranslation {

  /// <summary>Translates a source and gives the generated text and what was written to the error stream.</summary>
  /// <param name="source">The whole C# file.</param>
  /// <param name="to">The target language, <c>csharp</c> or <c>javascript</c>.</param>
  /// <param name="options">More options of the command line.</param>
  public static (string Text, string Error) Translate(string source, string to, params string[] options) {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-snippet-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var path = Path.Combine(directory, "Snippet.cs");
      File.WriteAllText(path, source);
      using var standard = new StringWriter();
      using var error = new StringWriter();
      var exitCode = Program.Run(["translate", path, "--from", "csharp", "--to", to, "--output", Path.Combine(directory, "out"), .. options], standard, error);
      Assert.That(exitCode, Is.EqualTo((int)ExitCode.Success), error.ToString());
      var file = Directory.GetFiles(Path.Combine(directory, "out")).Single(candidate => Path.GetFileName(candidate) != Manifest.FileName);
      return (File.ReadAllText(file), error.ToString());
    } finally {
      Directory.Delete(directory, true);
    }
  }

}
