using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/strings</c> and the calls every implementation is
/// compared on: the values of the issue, the boundaries of the members that differ between the two
/// languages, and strings drawn with the fixed seed 2026. The C# compiler's build of a C# fixture in
/// this assembly, and <c>node</c> running a JavaScript fixture, are the oracles.
/// </summary>
internal static class StringFixtures {

  /// <summary>The C# fixtures, each a static class named like its file.</summary>
  public static readonly string[] CSharp = ["Fnv1aText", "Tokens", "Hex", "Traps"];

  /// <summary>The JavaScript fixtures by file name, without the extension.</summary>
  public static readonly string[] JavaScript = ["fnv1a-text", "tokens", "hex", "traps"];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "strings", file);

  /// <summary>A method of a C# fixture as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => typeof(StringFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype." + className, true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>Strings of ASCII and of the Basic Multilingual Plane with pairs of surrogates, seed 2026: no lone surrogate, which no file or pipe carries unchanged.</summary>
  public static IEnumerable<string> Drawn(int count) {
    var random = new Random(2026);
    for (var draw = 0; draw < count; ++draw) {
      var units = new List<char>();
      for (var index = random.Next(9); index > 0; --index)
        switch (random.Next(4)) {
          case 0:
            units.AddRange(char.ConvertFromUtf32(random.Next(0x10000, 0x110000)));
            break;
          case 1:
            units.Add((char)random.Next(0xA0, 0xD800));
            break;
          default:
            units.Add((char)random.Next(0x20, 0x7F));
            break;
        }

      yield return new string([.. units]);
    }
  }

  /// <summary>The calls of the C# fixtures, each with the class it calls and the type the method returns.</summary>
  public static IEnumerable<(string Class, PrototypeCall Call)> CSharpCalls() {
    (string, PrototypeCall) Call(string className, string method, params object[] arguments)
      => (className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    foreach (var text in new[] { "hello", "h\u00E9llo\U0001F600", "" }.Concat(Drawn(6))) {
      yield return Call("Fnv1aText", "Hash", text);
      yield return Call("Fnv1aText", "HashIndexed", text);
    }

    foreach (var line in new[] { " add plus  sub x ", "", "mul", "sub,sub" }) {
      yield return Call("Tokens", "Score", line);
      yield return Call("Tokens", "Normalize", line);
    }

    foreach (var index in new[] { 0, 2, 3 })
      yield return Call("Tokens", "Keyword", index);

    yield return Call("Tokens", "IsKeyword", "sub");
    yield return Call("Tokens", "IsKeyword", "Sub");
    yield return Call("Hex", "Encode", new byte[] { 0, 255, 16, 171 });
    yield return Call("Hex", "Encode", Array.Empty<byte>());
    yield return Call("Hex", "Decode", "00ff10ab");
    yield return Call("Hex", "Decode", "0");
    yield return Call("Traps", "ReplaceAll", "abcb");
    yield return Call("Traps", "TrimNet", "\uFEFFa\uFEFF");
    yield return Call("Traps", "TrimNet", "\u0085a\u0085");
    yield return Call("Traps", "CountUnits", "a\U0001F600");
    yield return Call("Traps", "Less", "a", "B");
    yield return Call("Traps", "Less", "B", "a");
    yield return Call("Traps", "StartsOrdinal", "e\u0301");
    foreach (var index in new[] { -1, 0, 2, 3 })
      yield return Call("Traps", "At", "abc", index);

    foreach (var (start, length) in new[] { (1, 2), (3, 0), (2, 2), (-1, 1) })
      yield return Call("Traps", "Cut", "abc", start, length);

    yield return Call("Traps", "Pad", "ab");
    yield return Call("Traps", "Pad", "abcdef");
    yield return Call("Traps", "Repeat", 'x', 3);
    yield return Call("Traps", "Repeat", 'x', 0);
    yield return Call("Traps", "Repeat", 'x', -1);
    yield return Call("Traps", "LastComma", "a,b,c");
    yield return Call("Traps", "LastComma", "abc");
  }

  /// <summary>The calls of the JavaScript fixtures, each with the file it calls; the outcomes are what node prints.</summary>
  public static IEnumerable<(string File, PrototypeCall Call)> JavaScriptCalls() {
    (string, PrototypeCall) Call(string file, string method, params object[] arguments) => (file, new PrototypeCall(method, arguments));
    foreach (var text in new[] { "hello", "h\u00E9llo\U0001F600", "" }.Concat(Drawn(6)))
      yield return Call("fnv1a-text", "hash", text);

    foreach (var line in new[] { " add plus  sub x ", "", "mul", "sub,sub" }) {
      yield return Call("tokens", "score", line);
      yield return Call("tokens", "normalize", line);
    }

    yield return Call("tokens", "keyword", 0);
    yield return Call("tokens", "keyword", 2);
    yield return Call("hex", "encode", new byte[] { 0, 255, 16, 171 });
    yield return Call("hex", "encode", Array.Empty<byte>());
    yield return Call("hex", "decode", "00ff10ab");
    yield return Call("traps", "replaceFirst", "abcb");
    yield return Call("traps", "replaceEvery", "abcb");
    yield return Call("traps", "trimEcma", "\uFEFFa\uFEFF");
    yield return Call("traps", "trimEcma", "\u0085a\u0085");
    yield return Call("traps", "countPoints", "a\U0001F600");
    yield return Call("traps", "less", "a", "B");
    yield return Call("traps", "less", "B", "a");
    foreach (var (a, b) in new[] { (4, 1), (2, 7), (-3, 2), (0, 0) })
      yield return Call("traps", "cut", "abcdef", a, b);

    foreach (var a in new[] { -2, 0, 4, 9 })
      yield return Call("traps", "tail", "abcdef", a);

    yield return Call("traps", "pad", "x");
    yield return Call("traps", "pad", "abcdef");
    yield return Call("traps", "parts", "a,b,c");
    yield return Call("traps", "parts", "");
    yield return Call("traps", "unit", "abc", 1);
    yield return Call("traps", "unit", "abc", 5);
    yield return Call("traps", "unit", "abc", -1);
    yield return Call("traps", "point", "a\U0001F600", 1);
    yield return Call("traps", "point", "a\U0001F600", 2);
    yield return Call("traps", "last", "a,b,c");
    yield return Call("traps", "letter", 65);
    yield return Call("traps", "letter", 65601);
  }

  /// <summary>An outcome with the kind of a trap left out: .NET, the IR and JavaScript name the same failure differently until the traps are mapped.</summary>
  public static string Comparable(string outcome) => outcome.StartsWith("trap: ", StringComparison.Ordinal) ? "trap" : outcome;

}
