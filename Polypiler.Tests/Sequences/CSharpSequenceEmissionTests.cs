using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Sequences;

/// <summary>
/// Lazy sequences written by the C# backend (#1327): every profile compiles the module of iterators,
/// and the compiled code computes what the interpreter computes, call by call.
/// </summary>
[TestFixture]
public sealed class CSharpSequenceEmissionTests {

  /// <summary>Test design 1 to 8 and 12: the module of iterators compiles in every profile and computes what the interpreter computes.</summary>
  [Test, Category("Slow")]
  public void Iterators_CompileAndComputeInEveryProfile() {
    var module = SequenceModules.Module();
    var calls = SequenceModules.Calls().Select(call => call with { Returns = call.Method == "Longs" ? typeof(long) : typeof(int) }).ToList();
    var expected = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
      var text = CSharpEmitter.Emit(module, profile)[0].Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
      Assert.That(calls.Select(GeneratedCSharp.Compile(text, false).Outcome), Is.EqualTo(expected), $"{profile.Name}\n{text}");
    }
  }

  /// <summary>Test design 8 and 12: the rows say that every profile has iterators and <c>IEnumerable&lt;T&gt;</c>, so nothing of a sequence is inexpressible.</summary>
  [Test]
  public void EveryProfile_HasIterators() {
    foreach (var profile in CSharpProfile.All)
      Assert.That(CSharpCapabilities.Default.Has(CSharpFeature.Iterator, profile), Is.True, profile.Name);

    Assert.That(CSharpCapabilities.Default.Rows.Single(row => row.Name == "IEnumerable<T>").IsIn(CSharpProfile.All[0]), Is.True);
  }

  [Test]
  public void Iterator_IsAnIteratorMethod() {
    var text = CSharpEmitter.Emit(SequenceModules.Module(), CSharpProfile.Parse("net2.0"))[0].Content;
    Assert.That(text, Does.Contain("static global::System.Collections.Generic.IEnumerable<int> Traced(int[] t)"));
    Assert.That(text, Does.Contain("yield return 1;"));
    Assert.That(text, Does.Contain("yield break;"));
    Assert.That(text, Does.Contain("SinglePass(TracedOnce(t).GetEnumerator())"));
    Assert.That(text, Does.Contain("foreach (int value in Up(2))"));
  }

}
