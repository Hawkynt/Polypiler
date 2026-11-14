using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;

namespace Polypiler.Tests.Backends.CSharp.Syntax;

/// <summary>
/// Every helper of the capability table is C# source that Roslyn reads into the syntax tree, and the
/// printer writes the tree back as exactly that source: what the class gets is the helper as it is
/// written down.
/// </summary>
[TestFixture]
public sealed class CSharpHelperSyntaxTests {

  private static IEnumerable<TestCaseData> Helpers()
    => CSharpCapabilities.Default.Rows.Select(row => row.Helper).OfType<CSharpHelper>().Distinct()
      .Select(helper => new TestCaseData(helper).SetName($"Helper({helper.Name}, {helper.Parameters})"));

  [TestCaseSource(nameof(Helpers))]
  public void Helper_PrintsAsItsSource(CSharpHelper helper) {
    var source = string.Join('\n', helper.Lines(helper.Name)) + "\n";
    Assert.That(CSharpPrinter.Print(CSharpSyntaxReader.Member(source), CSharpProfile.Net20), Is.EqualTo(source));
  }

}
