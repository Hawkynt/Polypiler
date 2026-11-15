using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Exceptions;

/// <summary>
/// The variable of a catch clause in the generated C#: written only where the clause reads it - an
/// unused <c>catch (e)</c> of JavaScript would be C#'s warning CS0168, which the profiles treat as an error.
/// </summary>
[TestFixture]
public sealed class CSharpCatchBindingTests {

  private static string Emitted(string source, CSharpProfile profile) {
    var result = JavaScriptFrontend.Import("caught.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("\n", result.Diagnostics));
    return CSharpEmitter.Emit(result.Module!, profile)[0].Content;
  }

  [Test]
  public void UnusedCatchVariable_IsDropped() {
    const string source = "export function f(n) { try { if (n > 0) { throw new Error('x'); } return 0; } catch (e) { return 1; } }";
    foreach (var profile in CSharpProfile.All) {
      var text = Emitted(source, profile);
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile.Name}\n{text}");
      Assert.That(text, Does.Not.Contain("catch (global::System.Exception e)"), profile.Name);
    }
  }

  [Test]
  public void UsedCatchVariable_IsKept() {
    var text = Emitted("export function f(n) { try { if (n > 0) { throw new Error('x'); } return ''; } catch (e) { return e.message; } }", CSharpProfile.All.Last());
    Assert.That(text, Does.Contain("catch (global::System.Exception e)"));
  }

}
