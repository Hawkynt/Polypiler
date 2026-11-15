using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.CSharp.Printing;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Collections;

/// <summary>
/// Lists written by the C# backend: every profile compiles the module of every operation, and the
/// compiled code computes what the interpreter computes, call by call.
/// </summary>
[TestFixture]
public sealed class CSharpCollectionEmissionTests {

  /// <summary>Test design 1, 6, 9 and 13: the module of every list operation compiles in every profile and computes what the interpreter computes.</summary>
  [Test, Category("Slow")]
  public void EveryListOperation_CompilesAndComputesInEveryProfile() {
    var module = CollectionModules.Lists();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var expected = CollectionModules.ListCalls().Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
      Assert.That(CollectionModules.ListCalls().Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>The interpreter's outcomes of the module, which the generated code of both backends is held to.</summary>
  [TestCase("PopValues", "9500"), TestCase("PopObjects", "61"), TestCase("StrictSearch", "-88"), TestCase("StrictSingles", "1"), TestCase("Includes", "1101")]
  [TestCase("LivePush", "44"), TestCase("LiveTemporary", "50"), TestCase("CheckedThroughCall", "-1"), TestCase("CheckedLast", "110"), TestCase("CopyStructs", "15")]
  [TestCase("CopyStrings", "10"), TestCase("CopyLongs", "4"), TestCase("SearchStructs", "10"), TestCase("SearchRecords", "0"), TestCase("ChangesCounted", "29")]
  public void Interpreter_ComputesTheStatedValue(string function, string outcome)
    => Assert.That(PrototypeFixture.InterpretedOutcome(CollectionModules.Lists(), new PrototypeCall(function, []) { Returns = typeof(int) }), Is.EqualTo(outcome));

  /// <summary>A checked loop is C#'s foreach, a live one a for loop that reads the count before every pass.</summary>
  [Test]
  public void Loops_AreForEachOrForByMode() {
    var text = CSharpEmitter.Emit(CollectionModules.Lists(), CSharpProfile.Default)[0].Content;
    Assert.That(text, Does.Contain("foreach (int each in target) {")
      .And.Contain("for (int index = 0; index < grown.Count; ++index) {\n        int item = grown[index];")
      .And.Contain("PopValue(list)").And.Contain("PopReference(bag)").And.Contain("IndexOfStrictDouble(numbers, double.NaN)").And.Contain("IndexOfStrictSingle(floats, 0.5f)"));
  }

  /// <summary>The foreach statement of the tree is printed and read back as it was built.</summary>
  [Test]
  public void ForEach_PrintsAndReadsBack() {
    var loop = new CsForEach(new CsType("int"), "x", new CsName("list"), new CsBlock([new CsBreak()]));
    var method = new CsMethod(["static"], CsType.Void, "M", [], [], new CsBlock([loop]), null);
    var text = CSharpPrinter.Print(method, CSharpProfile.Default);
    Assert.That(text, Does.Contain("foreach (int x in list) {"));
    Assert.That(SyntaxShape.Of(CSharpSyntaxReader.Member(text)), Is.EqualTo(SyntaxShape.Of(method)), text);
    Assert.That(CSharpSyntaxRewriter.Rewrite(loop, node => node is CsName { Identifier: "list" } ? new CsName("other") : node), Is.InstanceOf<CsForEach>().With.Property("Collection").EqualTo(new CsName("other")));
  }

}
