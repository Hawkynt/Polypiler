using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Collections;

/// <summary>
/// Lists written by the JavaScript backend in every profile: nothing inexpressible, the text parsed at
/// the level it claims, and the forms of the lowering table on both sides of each first level. What
/// the code computes is decided by node in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class JavaScriptCollectionEmissionTests {

  /// <summary>The capabilities of the table only a JavaScript source reaches, which this class checks at their first level and the one before.</summary>
  public static readonly IReadOnlySet<string> Capabilities = new HashSet<string> { ".includes of an array" };

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "G" : null, typed)
  ];

  private static string Emit(IrModule module, JavaScriptProfile profile) {
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty);
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  private static string Emit(IrModule module, string level) => Emit(module, JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm"));

  /// <summary>Test design 9 and 13: every operation on lists in every profile and module format; nothing is inexpressible.</summary>
  [Test]
  public void EveryListOperation_IsWrittenInEveryProfile() {
    var module = CollectionModules.Lists();
    foreach (var profile in _profiles)
      Assert.That(Emit(module, profile), Does.Contain("function $lget(list, index)"), profile.ToString());
  }

  /// <summary>Test design 9 and 13: every operation on maps and sets in every profile and module format; nothing is inexpressible.</summary>
  [Test]
  public void EveryMapOperation_IsWrittenInEveryProfile() {
    var module = CollectionModules.Maps();
    foreach (var profile in _profiles)
      Assert.That(Emit(module, profile), Does.Contain("function $mget(map, key)"), profile.ToString());
  }

  /// <summary>Test design 9: Map and Set from ECMAScript 2015; $Keyed below, for pairs of halves, and for .NET's order where a removal or float keys need it.</summary>
  [TestCase("es5", "new $Keyed(false, 0)", "new $Keyed(true, 2)", "new $Keyed(true, 1)")]
  [TestCase("es2017", "new Map()", "new $Keyed(true, 2)", "new Map()")]
  [TestCase("es2020", "new Map()", "new Set()", "new Map()")]
  public void Maps_AreNativeWhereTheOrderAndTheKeysAllow(string level, string insertion, string longs, string objects) {
    var text = Emit(CollectionModules.Maps(), level);
    Assert.That(text, Does.Contain($"let live = {insertion};".Replace("let", level == "es5" ? "var" : "let")).Or.Contain($"var live = {insertion};"), text);
    Assert.That(text, Does.Contain(longs).And.Contain(objects).And.Contain("new $Keyed(true, 0)"), text);
  }

  /// <summary>Test design 9: includes of an array from ECMAScript 2016, the first level of the profiles es2017; the helper at es2015.</summary>
  [TestCase("es2015", false)]
  [TestCase("es2017", true)]
  public void Includes_IsNativeFromEs2016(string level, bool native) {
    var text = Emit(CollectionModules.Lists(), level);
    Assert.That(text.Contains("found.includes(NaN)"), Is.EqualTo(native), text);
    Assert.That(text.Contains("$lincludes(found, NaN)"), Is.EqualTo(!native), text);
  }

  /// <summary>A module whose checked loop changes a list counts every change of every list, and the loop compares the count.</summary>
  [Test]
  public void CheckedLoopThatChanges_CountsTheChanges() {
    var text = Emit(CollectionModules.Lists(), "es2022");
    Assert.That(text, Does.Contain("$lvadd(target, 1)").And.Contain("$lvcheck(target, version)").And.Contain("$lvpop(").And.Contain("$lvclear(changed)")
      .And.Contain("$lvinsert(counting, 0, 2)").And.Contain("$lvremoveAt(counting, 1)").And.Contain("$lvset(changed, 0, 2)")
      .And.Contain("for (let index = 0, version = target.$v | 0; ; index++) {"));
  }

  /// <summary>Without a checked loop that changes a list, the changes are the methods of an array and the loops plain.</summary>
  [Test]
  public void WithoutAChangingCheckedLoop_ChangesArePlain() {
    var module = CollectionModules.Lists();
    module = module with { Functions = [.. module.Functions.Where(function => function.Name is not ("CheckedThroughCall" or "CheckedLast" or "Append"))] };
    var text = Emit(module, "es2022");
    Assert.That(text, Does.Contain("grown.push(item)").And.Contain("list.pop()").And.Contain("$linsert(counting, 0, 2)").And.Contain("$lremoveAt(counting, 1)")
      .And.Contain("for (let index = 0; index < counting.length; index++) {").And.Not.Contain("$lv"));
  }

  /// <summary>Struct elements are copied on the way into and out of an array; strings and 64-bit integers are copied as they stand.</summary>
  [TestCase("es5", "wide.slice()")]
  [TestCase("es2022", "new BigInt64Array(wide)")]
  public void Copies_FollowTheElement(string level, string longs) {
    var text = Emit(CollectionModules.Lists(), level);
    Assert.That(text, Does.Contain("path.map($copy_Point)").And.Contain("Array.prototype.map.call(copy, $copy_Point)").And.Contain("words.slice()").And.Contain(longs));
  }

  /// <summary>Searches by Equals: a NaN found among floating-point numbers, the fields compared of structs and record classes.</summary>
  [Test]
  public void SearchesByEquals_UseTheEqualityOfTheElement() {
    var text = Emit(CollectionModules.Lists(), "es2022");
    Assert.That(text, Does.Contain("$lindexBy(pointList, { X: 2 }, $equals_Point)").And.Contain("$lindexBy(tagList,").And.Contain("small.indexOf(3)"));
  }

}
