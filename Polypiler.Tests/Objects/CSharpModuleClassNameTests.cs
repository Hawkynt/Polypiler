using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Objects;

/// <summary>
/// The class of the module as a class of the module reaches it: a member named like it would hide it
/// inside that class (checksum/lrc.js, whose instance has a field <c>lrc</c>), so the class is reached
/// through <c>global::</c> there, and by its name alone everywhere else (#1365).
/// </summary>
[TestFixture]
public sealed class CSharpModuleClassNameTests {

  private const string Source = """
    /** @param {int32} a @returns {int32} */
    function twice(a) { return (a + a) | 0; }

    export class Sum {
      constructor() { /** @type {int32} */ this.{{member}} = 1; }
      /** @param {int32} v */
      add(v) { this.{{member}} = (this.{{member}} + twice(v)) | 0; return this.{{member}}; }
    }

    export function run() { const s = new Sum(); s.add(2); return s.add(3); }
    """;

  private static string Emitted(string member) {
    var result = JavaScriptFrontend.Import("lrc.mjs", Source.Replace("{{member}}", member));
    Assert.That(result.Module, Is.Not.Null, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(IrInterpreter.Call(result.Module!, "run", []), Is.EqualTo(11));
    return LegalizedCSharp.Emit(result.Module!, CSharpProfile.Default)[0].Content;
  }

  /// <summary>A field of the name of the module's class: the class is reached through global::, and the code computes what node computes (1 + 4 + 6 = 11).</summary>
  [Test]
  public void AMemberOfTheClassName_IsPassedByGlobal() {
    var text = Emitted("lrc");
    Assert.That(text, Does.Contain("global::@lrc.twice("));
    Assert.That(GeneratedCSharp.Compile(text, false).Method("run").Invoke(null, []), Is.EqualTo(11), text);
  }

  /// <summary>No member of that name: the class is reached by its name alone.</summary>
  [Test]
  public void OtherMembers_ReachTheClassByItsName() {
    var text = Emitted("total");
    Assert.That(text, Does.Not.Contain("global::@lrc"));
    Assert.That(text, Does.Contain("@lrc.twice("));
    Assert.That(GeneratedCSharp.Compile(text, false).Method("run").Invoke(null, []), Is.EqualTo(11), text);
  }

}
