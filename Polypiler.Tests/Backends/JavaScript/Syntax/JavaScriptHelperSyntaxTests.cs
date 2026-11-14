using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.JavaScript.Syntax;

namespace Polypiler.Tests.Backends.JavaScript.Syntax;

/// <summary>
/// Every helper of the JavaScript backend, as every profile has it, is source that Acornima reads
/// into the syntax tree, and the printer writes the tree back as exactly that source.
/// </summary>
[TestFixture]
public sealed class JavaScriptHelperSyntaxTests {

  /// <summary>One profile of each level, ECMAScript 5 with and without typed arrays: what decides the text of a helper.</summary>
  private static readonly JavaScriptProfile[] _profiles = [
    JavaScriptProfile.Parse("es5", "cjs"), JavaScriptProfile.Parse("es5", "cjs", typedArrays: true),
    .. JavaScriptProfile.LevelNames.Skip(1).Select(level => JavaScriptProfile.Parse(level))
  ];

  private static IEnumerable<TestCaseData> Profiles() => _profiles.Select(profile => new TestCaseData(profile).SetName($"Helpers({profile})"));

  [TestCaseSource(nameof(Profiles))]
  public void EveryHelper_PrintsAsItsSource(JavaScriptProfile profile) {
    var differing = new List<string>();
    foreach (var (name, source) in JavaScriptEmitter.HelperSources(profile)) {
      var printed = string.Concat(JavaScriptSyntaxReader.Statements(source).Select(JavaScriptPrinter.Print));
      if (printed != source + "\n")
        differing.Add($"{name}:\n{source}\n--- printed:\n{printed}");
    }

    Assert.That(differing, Is.Empty);
  }

}
