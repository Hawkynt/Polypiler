using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Interop;

/// <summary>
/// Native calls in the JavaScript backend (#1332, ADR 0007): refused with PLY1152 where no binding is named, and with
/// one a function of the module that calls the binding - imported, required or read from a global by the module format.
/// <c>node</c> runs the output against the binding module in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class ForeignJavaScriptTests {

  private static IrModule? _libc;

  private static IrModule LibC => _libc ??= InteropFixtures.Imported("LibC.Linux.cs");

  private static readonly Dictionary<string, string> _bound = new() { ["libc.so.6"] = "./libc-bindings.mjs" };

  private static IEnumerable<TestCaseData> Profiles() => new (string Level, string Module, string? Global)[] {
    ("es5", "cjs", null), ("es5", "iife", "LibCModule"), ("es2015", "esm", null), ("es2017", "cjs", null), ("es2020", "esm", null), ("es2022", "esm", null), ("es2022", "iife", "LibCModule")
  }.Select(profile => new TestCaseData(profile.Level, profile.Module, profile.Global).SetArgDisplayNames($"{profile.Level} {profile.Module}"));

  /// <summary>Test design 12: without a binding every profile and module format refuses every native function once, naming its symbol and library.</summary>
  [TestCaseSource(nameof(Profiles))]
  public void WithoutABinding_EveryNativeFunctionIsRefused(string level, string module, string? global) {
    var profile = JavaScriptProfile.Parse(level, module, global);
    var problems = LegalizedJavaScript.Check(LibC, profile).Select(diagnostic => diagnostic.ToString()).ToList();
    Assert.That(problems, Has.Count.EqualTo(5));
    Assert.That(problems, Has.All.Contains("PLY1152").And.All.Contains("(libc.so.6) is a native function, which JavaScript profile '" + level + "' cannot call"));
    Assert.That(problems[0], Does.Contain("'strlen'"));
    Assert.That(() => LegalizedJavaScript.Emit(LibC, profile), Throws.ArgumentException.With.Message.Contains("'strlen' (libc.so.6)"));
  }

  /// <summary>Test design 15: with a binding every profile writes the native functions as calls of it, in a file of its level.</summary>
  [TestCaseSource(nameof(Profiles))]
  public void WithABinding_EveryProfileCallsIt(string level, string module, string? global) {
    var bindings = global is null ? _bound : new Dictionary<string, string> { ["libc.so.6"] = "globalThis.LibC" };
    var profile = JavaScriptProfile.Parse(level, module, global) with { Bindings = bindings };
    Assert.That(LegalizedJavaScript.Check(LibC, profile), Is.Empty);
    var text = LegalizedJavaScript.Emit(LibC, profile).Single().Content;
    // What the file names of the binding - the module, require, the global the command line gave and the symbols - is the user's, not of the capability table.
    string[] external = ["$libc_so_6", "require", "globalThis", ".strlen", ".abs", ".div", ".memset", ".strtol"];
    Assert.That(GeneratedJavaScript.Problems(text, profile).Where(problem => !external.Any(name => problem.StartsWith($"'{name}", System.StringComparison.Ordinal))), Is.Empty, text);
    Assert.That(text, Does.Contain(module switch {
      "esm" => "import * as $libc_so_6 from \"./libc-bindings.mjs\";",
      "cjs" => "var $libc_so_6 = require(\"./libc-bindings.mjs\");",
      _ => "var $libc_so_6 = globalThis.LibC;"
    }), text);
    Assert.That(text, Does.Contain("$libc_so_6.strlen(s)").And.Contain("$libc_so_6.div(numerator, denominator)").And.Contain("$libc_so_6.memset(buffer, value, count);"), text);
    Assert.That(text, Does.Contain("A native function is the function of its symbol in the binding module of its library"), text);
    Assert.That(text, level is "es5" or "es2015" or "es2017" ? Does.Contain("Math.floor($r / 4294967296) >>> 0") : Does.Contain("typeof $r === \"number\" ? BigInt($r) : $r"), text);
    Assert.That(text, module == "esm" ? Does.Contain("export function StrLen(s)") : Does.Contain("StrLen: StrLen").Or.Contain("exports.StrLen = StrLen;"), text);
  }

  /// <summary>A symbol that is no identifier is read by its name in brackets; a library of any name has a binding variable of its own.</summary>
  [Test]
  public void ASymbolThatIsNoIdentifier_IsReadInBrackets() {
    var module = PrototypeFixture.Import("Snippet.cs", """
      using System.Runtime.InteropServices;
      public static class Snippet {
        [DllImport("my lib-1", EntryPoint = "do-it")] private static extern void Do();
        public static void G() => Do();
      }
      """);
    var text = LegalizedJavaScript.Emit(module, JavaScriptProfile.Default with { Bindings = new Dictionary<string, string> { ["my lib-1"] = "./my.mjs" } }).Single().Content;
    Assert.That(text, Does.Contain("import * as $my_lib_1 from \"./my.mjs\";").And.Contain("$my_lib_1[\"do-it\"]();").And.Contain("function Do() {"), text);
    Assert.That(text, Does.Not.Contain("export function Do"), text);
  }

  /// <summary>Two libraries each have a binding of their own, the comment above the first; a library that is called nothing of is not reached.</summary>
  [Test]
  public void TwoLibraries_HaveABindingEach() {
    var module = PrototypeFixture.Import("Snippet.cs", """
      using System.Runtime.InteropServices;
      public static class Snippet {
        [DllImport("a")] private static extern int f();
        [DllImport("b")] private static extern int g();
        public static int G() => f() + g();
      }
      """);
    var bindings = new Dictionary<string, string> { ["a"] = "./a.cjs", ["b"] = "./b.cjs", ["c"] = "./c.cjs" };
    var text = LegalizedJavaScript.Emit(module, JavaScriptProfile.Parse("es5") with { Bindings = bindings }).Single().Content;
    Assert.That(text, Does.Contain("// The native libraries, each through the binding the command line named (--bind): unverified externals.\nvar $a = require(\"./a.cjs\");\nvar $b = require(\"./b.cjs\");"), text);
    Assert.That(text, Does.Not.Contain("./c.cjs"), text);
  }

  /// <summary>A pointer argument and the last error have no form in JavaScript, whatever the binding.</summary>
  [Test]
  public void PointersAndTheLastError_AreRefused() {
    var module = PrototypeFixture.Import("Snippet.cs", """
      using System.Runtime.InteropServices;
      public static class Snippet {
        [DllImport("lib", SetLastError = true)] private static extern unsafe int f(byte* p);
        public static unsafe int G(byte[] a) { fixed (byte* p = a) { return f(p) + Marshal.GetLastWin32Error(); } }
      }
      """);
    var problems = LegalizedJavaScript.Check(module, JavaScriptProfile.Default with { Bindings = new Dictionary<string, string> { ["lib"] = "./lib.mjs" } })
      .Select(diagnostic => diagnostic.ToString()).ToList();
    Assert.That(problems, Has.Some.Contains("'the pointer p passed to the native function f' cannot be expressed"));
    Assert.That(problems, Has.Some.Contains("'the last error of a native call (Marshal.GetLastWin32Error)' cannot be expressed"));
  }

  /// <summary>The well-formed and the malformed bindings of a module format.</summary>
  [TestCase("libc.so.6", "./b.mjs", JavaScriptModuleFormat.Esm, null)]
  [TestCase("libc.so.6", "globalThis.LibC", JavaScriptModuleFormat.Iife, null)]
  [TestCase("", "./b.mjs", JavaScriptModuleFormat.Esm, "binds no library or to nothing")]
  [TestCase("libc.so.6", "", JavaScriptModuleFormat.Cjs, "binds no library or to nothing")]
  [TestCase("libc.so.6", "./b.js", JavaScriptModuleFormat.Iife, "is no global object")]
  public void BindingProblem_FollowsTheModuleFormat(string library, string target, JavaScriptModuleFormat format, string? problem)
    => Assert.That(JavaScriptProfile.BindingProblem(library, target, format), problem is null ? Is.Null : Does.Contain(problem));

}
