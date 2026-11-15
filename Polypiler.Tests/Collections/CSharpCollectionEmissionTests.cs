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

  /// <summary>Test design 2, 3, 4, 9 and 10: the module of every map and set operation compiles in every profile and computes what the interpreter computes.</summary>
  [Test, Category("Slow")]
  public void EveryMapOperation_CompilesAndComputesInEveryProfile() {
    var module = CollectionModules.Maps();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    var expected = CollectionModules.MapCalls().Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
      Assert.That(CollectionModules.MapCalls().Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>The interpreter's outcomes of the map module.</summary>
  [TestCase("InsertionRemoved", "2456"), TestCase("InsertionLive", "135"), TestCase("InsertionSet", "132"), TestCase("Lookups", "7544"), TestCase("ObjectKeys", "221")]
  [TestCase("LongKeys", "21"), TestCase("SingleKeys", "124"), TestCase("StructValues", "11"), TestCase("ValuesOnly", "5612"), TestCase("SlotsLive", "123"), TestCase("MadeMap", "78")]
  public void MapInterpreter_ComputesTheStatedValue(string function, string outcome)
    => Assert.That(PrototypeFixture.InterpretedOutcome(CollectionModules.Maps(), new PrototypeCall(function, []) { Returns = typeof(int) }), Is.EqualTo(outcome));

  private static string Emitted(string body, string profile) {
    var source = PrototypeFixture.Wrap(body);
    var result = Core.Frontends.CSharp.CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("\n", result.Diagnostics));
    var text = CSharpEmitter.Emit(result.Module!, CSharpProfile.Parse(profile))[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile, CSharpProfile.Parse(profile).LanguageVersionName), Is.Empty, text);
    return text;
  }

  /// <summary>
  /// Test design 9: HashSet from net3.5 and the slots below, TryAdd from netstandard2.1 and a helper
  /// below, the comparer of floating-point keys where .NET Framework hashes NaNs and zeros apart.
  /// </summary>
  [TestCase("net2.0", true, true, true)]
  [TestCase("net3.5", false, true, true)]
  [TestCase("netstandard2.0", false, true, true)]
  [TestCase("netstandard2.1", false, false, false)]
  [TestCase("net10.0", false, false, false)]
  public void Backing_FollowsTheProfile(string profile, bool slotSets, bool comparer, bool helperTryAdd) {
    var text = Emitted("public static int F() { var d = new System.Collections.Generic.Dictionary<double, int>(); d.TryAdd(1.0, 1); "
      + "var s = new System.Collections.Generic.HashSet<long>(); s.Add(1); return d.Count + s.Count; }", profile);
    Assert.That(text.Contains("new KeyedSlots<long, bool>(true, null)"), Is.EqualTo(slotSets), text);
    Assert.That(text.Contains("HashSet<long>()"), Is.EqualTo(!slotSets), text);
    Assert.That(text.Contains("Dictionary<double, int>(new FloatKeys())"), Is.EqualTo(comparer), text);
    Assert.That(text.Contains("TryAdd(d, 1.0, 1)"), Is.EqualTo(helperTryAdd), text);
  }

  /// <summary>Test design 4 and 9: a checked loop that removes takes the slots where .NET Framework's enumerator would refuse the removal.</summary>
  [TestCase("net4.8", true)]
  [TestCase("netstandard2.0", true)]
  [TestCase("netstandard2.1", false)]
  [TestCase("net8.0", false)]
  public void CheckedLoopThatRemoves_TakesTheSlotsOnFramework(string profile, bool slots) {
    var text = Emitted("public static int F() { var d = new System.Collections.Generic.Dictionary<int, int> { [1] = 1, [2] = 2 }; int n = 0; "
      + "foreach (var k in d.Keys) { n++; d.Remove(k); } return n; }", profile);
    Assert.That(text.Contains("KeyedSlots<int, int>"), Is.EqualTo(slots), text);
    Assert.That(text.Contains("foreach (int k in d.Keys)"), Is.EqualTo(!slots), text);
  }

  /// <summary>A nested class of the collections gives way to a member of its name.</summary>
  [Test]
  public void NestedClass_GivesWayToAMember() {
    var text = Emitted("public static int KeyedSlots() { var s = new System.Collections.Generic.HashSet<int>(); s.Add(1); return s.Count; }", "net2.0");
    Assert.That(text, Does.Contain("public static int KeyedSlots()").And.Contain("sealed class KeyedSlots_<TKey, TValue>").And.Contain("public KeyedSlots_(bool reuse"));
  }

  /// <summary>The interpreter's outcomes of the module, which the generated code of both backends is held to.</summary>
  [TestCase("PopValues", "9500"), TestCase("PopObjects", "61"), TestCase("StrictSearch", "-88"), TestCase("StrictSingles", "1"), TestCase("Includes", "1101")]
  [TestCase("LivePush", "44"), TestCase("LiveTemporary", "50"), TestCase("CheckedThroughCall", "-1"), TestCase("CheckedLast", "110"), TestCase("CopyStructs", "15")]
  [TestCase("CopyStrings", "10"), TestCase("CopyLongs", "4"), TestCase("SearchStructs", "10"), TestCase("SearchRecords", "0"), TestCase("ChangesCounted", "29")]
  [TestCase("StructLoop", "93"), TestCase("StrictReferences", "9")]
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
