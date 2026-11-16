using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Tooling.Corpus;
using Polypiler.Tooling.Cipher;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Cipher;

/// <summary>
/// The Rust side of <c>polypiler-tooling cipher kat</c> (#1365) without a toolchain: what the tests
/// reach of a translated crate, the test crate written for a fixed set of vectors, every recorded value
/// as a Rust literal of the type the translation takes, escaping, no vectors, a result that is no
/// sequence of bytes, and the reading of what <c>cargo test</c> reports - a pass, a failed comparison, a
/// panic of the translation, a build that failed. The real cargo runs in
/// <c>Polypiler.Tests.Acceptance/Rust/CipherKatRustTests</c>.
/// </summary>
[TestFixture]
public sealed class CipherKatRustTests {

  private static readonly string _mini = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "cipher-mini");

  private string _root = "";

  [SetUp]
  public void CreateRoot() => this._root = Scratch.Create("polypiler-cipher-kat-rust-");

  [TearDown]
  public void DeleteRoot() => Scratch.Delete(this._root);

  private static KatValue Recorded(string json) {
    using var document = JsonDocument.Parse(json);
    return KatValue.Of(document.RootElement);
  }

  private static IReadOnlyList<(KatAlgorithm Algorithm, KatVector Vector)> Checkable(string goldens)
    => [.. KatFile.Load(Path.Combine(_mini, goldens), "checksum/xorsum.js").Vectors.Where(entry => entry.Vector.Unchecked is null)];

  /// <summary>The API of a crate of one module, <c>m</c>, of the given text.</summary>
  private static KatRustApi Api(string module, int edition = 2024) => KatRustApi.Of("kat-crate", edition, [("kat_crate", new RsFile([], [], [])), ("kat_crate::m", RustSyntaxReader.Read(module))]);

  private const string Framework = """
    use std::cell::{Cell, RefCell};
    use std::rc::Rc;

    pub trait Algorithm {
        fn field_name(&self) -> &RefCell<Vec<u16>>;

        fn create_instance(&self, is_inverse: bool) -> Rc<dyn Instance>;
    }

    pub fn algorithms() -> Vec<Rc<dyn Algorithm>> {
        Vec::new()
    }

    """;

  private static KatVector Vector(string steps = "[]", string input = """{"t":"bytes","v":"0102"}""", string expected = "0a", bool inverse = false)
    => KatFile.Parse($$$"""{"schema":1,"file":"a.js","algorithms":[{"name":"A","vectors":[{"text":"t","inverse":{{{(inverse ? "true" : "false")}}},"steps":{{{steps}}},"input":{{{input}}},"expected":{"t":"bytes","v":"{{{expected}}}"},"node":{"outcome":"passed","deterministic":true}}]}]}""").Algorithms[0].Vectors[0];

  private static string Body(KatRustApi api, KatVector vector, string algorithm = "A")
    => RustPrinter.Print(KatRustCrate.Test(api, new KatAlgorithm(algorithm, [vector]), vector, 0));

  /// <summary>The framework with an instance trait of the given methods.</summary>
  private static KatRustApi Instance(string methods) => Api(Framework + "pub trait Instance {\n" + methods + "\n}\n");

  private const string Plain = """
        fn feed(&self, data: &[u8]);

        fn result(&self) -> Vec<u8>;
    """;

  // --- what the tests reach of a translation ----------------------------------------------------

  [Test]
  public void Api_OfTheStandInTranslation_IsItsRegistryNameAndModules() {
    var api = KatRustApi.Read(Path.Combine(_mini, "rust"));
    Assert.That((api.Crate, api.Edition, api.Algorithm, api.Registered, api.NameType), Is.EqualTo(("xorsum", 2024, "Algorithm", "Rc<dyn Algorithm>", "&RefCell<Vec<u16>>")));
    Assert.That(api.Imports, Is.EqualTo(new[] { "xorsum::xorsum" }), "lib.rs declares nothing but its module");
  }

  [Test]
  public void Api_ReadsThePackageName_AndTakesEdition2015WhereNoneIsGiven() {
    Scratch.Write(this._root, "Cargo.toml", "[package]\nname = \"kat-crate\"\n");
    Scratch.Write(this._root, "src/lib.rs", Framework.Replace("fn create_instance(&self, is_inverse: bool) -> Rc<dyn Instance>;", "", StringComparison.Ordinal));
    var api = KatRustApi.Read(this._root);
    Assert.That((api.Crate, api.Edition, api.Imports.Single()), Is.EqualTo(("kat_crate", 2015, "kat_crate")));
  }

  [Test]
  public void Api_OfACrateWithoutAPackageName_OrAFile_IsRefused() {
    Scratch.Write(this._root, "Cargo.toml", "[package]\nversion = \"0.1.0\"\n");
    Assert.That(() => KatRustApi.Read(this._root), Throws.TypeOf<KatRustException>().With.Message.EqualTo("the Cargo.toml of the translation names no package"));
    Scratch.Write(this._root, "Cargo.toml", "[package]\nname = \"k\"\n");
    Assert.That(() => KatRustApi.Read(this._root), Throws.InstanceOf<IOException>());
  }

  [TestCase("pub fn other() -> Vec<Rc<dyn Algorithm>> { Vec::new() }", "the translation has no registry the tests can reach, a pub fn algorithms()")]
  [TestCase("fn algorithms() -> Vec<Rc<dyn Algorithm>> { Vec::new() }", "the translation has no registry the tests can reach, a pub fn algorithms()")]
  [TestCase("pub fn algorithms(n: i32) -> Vec<Rc<dyn Algorithm>> { Vec::new() }", "the translation has no registry the tests can reach, a pub fn algorithms()")]
  [TestCase("pub fn algorithms() -> i32 { 0 }", "the registry algorithms() gives i32, not a Vec of objects")]
  [TestCase("pub fn algorithms() {}", "the registry algorithms() gives nothing, not a Vec of objects")]
  [TestCase("pub fn algorithms() -> Vec<i32> { Vec::new() }", "the registry algorithms() gives Vec<i32>, not a Vec of objects")]
  public void Api_WithoutARegistryOfObjects_IsRefused(string registry, string reason)
    => Assert.That(() => Api("pub trait Algorithm {\n    fn field_name(&self) -> Vec<u16>;\n}\n\n" + registry + "\n"), Throws.TypeOf<KatRustException>().With.Message.EqualTo(reason));

  [TestCase("fn field_name(&self) -> &RefCell<Vec<u16>>;", true)]
  [TestCase("fn field_name(&self) -> Vec<u16>;", true)]
  [TestCase("fn field_name(&self) -> &[u16];", true)]
  [TestCase("fn field_name(&self) -> &Cell<i32>;", false)]
  [TestCase("fn field_label(&self) -> Vec<u16>;", false)]
  [TestCase("fn field_name(&self, n: i32) -> Vec<u16>;", false)]
  public void Api_NeedsANameOfTheAlgorithmsAsText(string accessor, bool read) {
    var text = "pub trait Algorithm {\n    " + accessor + "\n}\n\npub fn algorithms() -> Vec<Rc<dyn Algorithm>> {\n    Vec::new()\n}\n";
    if (read)
      Assert.That(Api(text).NameType, Is.EqualTo(accessor[(accessor.IndexOf("-> ", StringComparison.Ordinal) + 3)..^1]));
    else
      Assert.That(() => Api(text), Throws.TypeOf<KatRustException>().With.Message.EqualTo("the algorithms of the translation have no name the tests can read, an accessor field_name of Algorithm"));
  }

  [Test]
  public void Api_FindsMembers_OfSupertraits_OfImplBlocks_AndOfTraitsAStructImplements() {
    var api = Api("""
      pub trait Named {
          fn field_name(&self) -> Vec<u16>;
      }

      pub trait Base: Named + Missing {
          fn base(&self) {}
      }

      pub trait Algorithm: Base + Named {}

      pub struct Concrete;

      impl Concrete {
          pub fn own(self: &Rc<Self>, n: i32) {}
      }

      impl Algorithm for Concrete {}

      pub fn algorithms() -> Vec<Rc<Concrete>> {
          Vec::new()
      }

      """);
    Assert.That((api.Algorithm, api.NameType), Is.EqualTo(("Concrete", "Vec<u16>")));
    Assert.That(api.Member("Concrete", "own", 1)?.Name, Is.EqualTo("own"));
    Assert.That(api.Member("Concrete", "own", 0), Is.Null, "a method of another number of parameters is another");
    Assert.That(api.Member("Concrete", "base", 0)?.Name, Is.EqualTo("base"));
    Assert.That(api.Member("Algorithm", "field_name", 0)?.Name, Is.EqualTo("field_name"));
    Assert.That(api.Member("Algorithm", "none", 0), Is.Null);
    Assert.That(KatRustApi.Arguments(api.Member("Concrete", "own", 1)!).Select(parameter => parameter.Name), Is.EqualTo(new[] { "n" }));
  }

  [TestCase("Rc<dyn Algorithm>", "Algorithm")]
  [TestCase("Rc<Concrete>", "Concrete")]
  [TestCase("Box<dyn Algorithm>", null)]
  [TestCase("i32", null)]
  public void ObjectType_IsTheTypeAnRcHolds(string type, string? held) => Assert.That(KatRustApi.ObjectType(type), Is.EqualTo(held));

  // --- the test crate ---------------------------------------------------------------------------

  [Test]
  public void Manifest_DependsOnTheTranslation_InAWorkspaceOfItsOwn()
    => Assert.That(KatRustCrate.Manifest("adler"), Is.EqualTo("[package]\nname = \"kat-tests\"\nversion = \"0.1.0\"\nedition = \"2024\"\npublish = false\n\n[dependencies]\nadler = { path = \"crate\" }\n\n[workspace]\n"));

  [Test]
  public void Source_OfAFixedVectorSet_IsTheHelpers_TheLookupAndATestPerVector() {
    var source = KatRustCrate.Source(KatRustApi.Read(Path.Combine(_mini, "rust")), Checkable("altered"));
    var template = KatHarness.Template("KatCheck.rs.txt");
    Assert.That(source, Does.StartWith(template));
    Assert.That(source[template.Length..], Is.EqualTo("""
      use std::rc::Rc;
      use xorsum::xorsum::*;

      fn registered(name: &str) -> Rc<dyn Algorithm> {
          let wanted = utf16(name);
          for algorithm in algorithms() {
              if *algorithm.field_name().borrow() == wanted {
                  return algorithm;
              }
          }
          panic!("the translation registers no algorithm named '{name}'");
      }

      /// XorSum-0 #1
      #[test]
      fn kat_1() {
          let algorithm = registered("XorSum-0");
          let instance = algorithm.create_instance(false);
          instance.feed(&[1, 2, 3]);
          check(&instance.result()[..], &[1]);
      }

      /// XorSum-0 #2
      #[test]
      fn kat_2() {
          let algorithm = registered("XorSum-0");
          let instance = algorithm.create_instance(false);
          instance.explode();
          instance.feed(&[255]);
          check(&instance.result()[..], &[255]);
      }

      /// XorSum-0 #3
      #[test]
      fn kat_3() {
          let algorithm = registered("XorSum-0");
          let instance = algorithm.create_instance(false);
          panic!(
              "{}",
              "the translation of IAlgorithmInstance has no property 'rounds' to set",
          );
      }

      /// XorSum-9 #1
      #[test]
      fn kat_4() {
          let algorithm = registered("XorSum-9");
          let instance = algorithm.create_instance(false);
          instance.feed(&[1]);
          check(&instance.result()[..], &[1]);
      }

      """.Replace("\r\n", "\n", StringComparison.Ordinal)));
  }

  /// <summary>Test design 3: no vector at all is a crate with the lookup and no test.</summary>
  [Test]
  public void Source_WithoutVectors_HasNoTest() {
    var source = KatRustCrate.Source(KatRustApi.Read(Path.Combine(_mini, "rust")), []);
    Assert.That((source.Contains("fn registered(", StringComparison.Ordinal), source.Contains("#[test]", StringComparison.Ordinal)), Is.EqualTo((true, false)));
  }

  [Test]
  public void Write_PutsTheManifestAndTheTestsBesideTheTranslation_AndNamesEveryVector() {
    foreach (var file in Directory.EnumerateFiles(Path.Combine(_mini, "rust"), "*", SearchOption.AllDirectories))
      Scratch.Write(Path.Combine(this._root, KatRustCrate.Translation), Path.GetRelativePath(Path.Combine(_mini, "rust"), file), File.ReadAllBytes(file));
    var ids = KatRustCrate.Write(this._root, Checkable("goldens"));
    Assert.That(ids, Is.EqualTo(new[] { "XorSum-0 #1", "XorSum-0 #2", "XorSum-7 #1" }));
    Assert.That(File.ReadAllText(Path.Combine(this._root, "Cargo.toml")), Does.Contain("xorsum = { path = \"crate\" }"));
    Assert.That(File.ReadAllText(Path.Combine(this._root, "tests", "kat.rs")), Does.Contain("fn kat_3()"));
    Assert.That(KatRustCrate.Write(this._root, []), Is.Empty, "test design 3: no vector");
  }

  [Test]
  public void Test_EscapesTheNameOfTheAlgorithm_AndAPanicsText() {
    var api = Instance(Plain);
    var text = Body(api, Vector(steps: """[{"call":"Strange{x}","args":[]}]"""), "A \"quoted\" \\ name {0}\nnext");
    Assert.That(text, Does.Contain("/// A \"quoted\" \\ name {0} next #1\n"), "a line break of the name does not end the comment");
    Assert.That(text, Does.Contain("registered(\"A \\\"quoted\\\" \\\\ name {0}\\nnext\")"));
    Assert.That(text, Does.Contain("\"the translation of Instance has no method 'Strange{x}' of 0 arguments\""), "braces of a text are no placeholders");
    Assert.That(text, Does.Contain("\"{}\","));
  }

  [Test]
  public void Test_OfAnInverse_PassesTrue_AndOfAFactoryWithoutParameters_NothingAtAll() {
    Assert.That(Body(Instance(Plain), Vector(inverse: true)), Does.Contain("algorithm.create_instance(true);"));
    var api = Api(Framework.Replace("is_inverse: bool", "", StringComparison.Ordinal) + "pub trait Instance {\n" + Plain + "\n}\n");
    Assert.That(Body(api, Vector(inverse: true)), Does.Contain("algorithm.create_instance();"));
  }

  [TestCase("fn create_instance(&self, a: bool, b: bool) -> Rc<dyn Instance>;", "\"the translation of Algorithm has no method 'CreateInstance' of 1 arguments\"")]
  [TestCase("fn create_instance(&self, a: bool) -> i32;", "\"CreateInstance gives i32, not an object\"")]
  [TestCase("fn create_instance(&self, a: bool) -> Option<Rc<dyn Instance>>;", ".expect(\"CreateInstance gave no instance\");")]
  public void Test_TakesTheInstanceTheFactoryGives(string factory, string expected) {
    var api = Api(Framework.Replace("fn create_instance(&self, is_inverse: bool) -> Rc<dyn Instance>;", factory, StringComparison.Ordinal) + "pub trait Instance {\n" + Plain + "\n}\n");
    Assert.That(Body(api, Vector()), Does.Contain(expected));
  }

  [Test]
  public void Test_SetsAPropertyThroughItsCell_OrItsRefCell_AndCallsAStep() {
    var api = Instance(Plain + """

        fn field_rounds(&self) -> &Cell<i32>;

        fn field_key(&self) -> &RefCell<Vec<u8>>;

        fn field_label(&self) -> i32;

        fn set_iv(&self, iv: &[u8]) -> Result<(), Rc<PlyError>>;
    """);
    var text = Body(api, Vector(steps: """[{"set":"rounds","value":{"t":"number","v":12}},{"set":"key","value":{"t":"bytes","v":"00ff"}},{"call":"setIV","args":[{"t":"bytes","v":"01"}]}]"""));
    Assert.That(text, Does.Contain("instance.field_rounds().set(12);"));
    Assert.That(text, Does.Contain("*instance.field_key().borrow_mut() = vec![0, 255];"));
    Assert.That(text, Does.Contain("let _ = match instance.set_iv(&[1]) {\n        Ok(value) => value,\n        Err(_) => panic!(\"{}\", \"setIV threw\"),\n    };"));
    Assert.That(Body(api, Vector(steps: """[{"set":"label","value":{"t":"number","v":1}}]""")), Does.Contain("\"the translation of Instance has no property 'label' to set\""), "an accessor of no cell is no property");
  }

  /// <summary>Test design 4: a result that is no sequence of numbers fails its test, which says what it is.</summary>
  [TestCase("fn result(&self) -> i32;", "\"Result gives i32, not a sequence of bytes\"")]
  [TestCase("fn result(&self) -> Vec<Vec<u8>>;", "\"Result gives Vec<Vec<u8>>, not a sequence of bytes\"")]
  [TestCase("fn result(&self);", "\"Result gives nothing, not a sequence of bytes\"")]
  [TestCase("fn result(&self) -> Vec<f64>;", "check(&instance.result()[..], &[10]);")]
  [TestCase("fn result(&self) -> &[i32];", "check(&instance.result()[..], &[10]);")]
  [TestCase("fn result(&self) -> &Vec<u8>;", "check(&instance.result()[..], &[10]);")]
  [TestCase("fn result(&self) -> [u32; 4];", "check(&instance.result()[..], &[10]);")]
  [TestCase("fn result(&self) -> Option<Vec<u8>>;", ".expect(\"Result gave nothing, not a sequence of bytes\")")]
  [TestCase("fn result(&self) -> Result<Vec<u8>, Rc<PlyError>>;", "Err(_) => panic!(\"{}\", \"Result threw\"),")]
  [TestCase("fn result(&self) -> Result<Vec<u8>>;", "Err(_) => panic!(\"{}\", \"Result threw\"),")]
  public void Test_ComparesWhatResultGives_AsASequenceOfNumbers(string result, string expected)
    => Assert.That(Body(Instance("    fn feed(&self, data: &[u8]);\n\n    " + result), Vector()), Does.Contain(expected));

  [Test]
  public void Test_OfAResultTheTranslationHandsBackAsAResult_TakesItsValueInParentheses() {
    var text = Body(Instance("    fn feed(&self, data: &[u8]);\n\n    fn result(&self) -> Result<Vec<u8>, Rc<PlyError>>;"), Vector());
    Assert.That(text, Does.Contain("&(match instance.result() {"));
  }

  // --- recorded values as literals ----------------------------------------------------------------

  [TestCase("""{"t":"number","v":255}""", "u8", "255")]
  [TestCase("""{"t":"number","v":0}""", "u8", "0")]
  [TestCase("""{"t":"number","v":-128}""", "i8", "-128")]
  [TestCase("""{"t":"number","v":4294967295}""", "u32", "4294967295")]
  [TestCase("""{"t":"number","v":16}""", " usize ", "16")]
  [TestCase("""{"t":"number","v":0.5}""", "f64", "0.5")]
  [TestCase("""{"t":"number","v":3}""", "f32", "3.0")]
  [TestCase("""{"t":"number","v":1e21}""", "f64", "1e21")]
  [TestCase("""{"t":"number","v":1e-7}""", "f64", "1e-07")]
  [TestCase("""{"t":"number","v":"NaN"}""", "f64", "f64::NAN")]
  [TestCase("""{"t":"number","v":"Infinity"}""", "f32", "f32::INFINITY")]
  [TestCase("""{"t":"number","v":"-Infinity"}""", "f64", "f64::NEG_INFINITY")]
  [TestCase("""{"t":"number","v":"-0"}""", "f64", "-0.0")]
  [TestCase("""{"t":"bigint","v":"18446744073709551615"}""", "u64", "18446744073709551615")]
  [TestCase("""{"t":"bigint","v":"-170141183460469231731687303715884105728"}""", "i128", "-170141183460469231731687303715884105728")]
  [TestCase("""{"t":"bigint","v":"2"}""", "f64", "2.0")]
  [TestCase("""{"t":"bool","v":true}""", "bool", "true")]
  [TestCase("""{"t":"bool","v":false}""", "bool", "false")]
  [TestCase("""{"t":"null","v":null}""", "Option<u8>", "None")]
  [TestCase("""{"t":"undefined","v":null}""", "Option<Vec<u8>>", "None")]
  [TestCase("""{"t":"number","v":7}""", "Option<u8>", "Some(7)")]
  [TestCase("""{"t":"bytes","v":"00ff10"}""", "&[u8]", "&[0, 255, 16]")]
  [TestCase("""{"t":"bytes","v":""}""", "&[u8]", "&[]")]
  [TestCase("""{"t":"bytes","v":"01"}""", "&mut [u32]", "&mut [1]")]
  [TestCase("""{"t":"bytes","v":"0102"}""", "Vec<u8>", "vec![1, 2]")]
  [TestCase("""{"t":"bytes","v":"0102"}""", "[u8; 2]", "[1, 2]")]
  [TestCase("""{"t":"typed","v":[1,"NaN",-2]}""", "&[f64]", "&[1.0, f64::NAN, -2.0]")]
  [TestCase("""{"t":"typed","v":[65535]}""", "Vec<u16>", "vec![65535]")]
  [TestCase("""{"t":"array","v":[{"t":"bytes","v":"01"},{"t":"typed","v":[2]}]}""", "&[Vec<u8>]", "&[vec![1], vec![2]]")]
  [TestCase("""{"t":"array","v":[{"t":"number","v":1},{"t":"number","v":-2}]}""", "&[i8]", "&[1, -2]")]
  [TestCase("""{"t":"array","v":[]}""", "Vec<i32>", "vec![]")]
  [TestCase("""{"t":"string","v":"ab\"\\\n\b\f\r\t\/"}""", "&[u16]", "&utf16(\"ab\\\"\\\\\\n\\u{8}\\u{c}\\r\\t/\")")]
  [TestCase("""{"t":"string","v":""}""", "Vec<u16>", "utf16(\"\")")]
  [TestCase("""{"t":"string","v":"😀"}""", "Vec<u16>", "utf16(\"\U0001F600\")")]
  [TestCase("""{"t":"string","v":"a\ud800"}""", "&[u16]", "&[97, 55296]")]
  [TestCase("""{"t":"string","v":"\udc00\ud800"}""", "Vec<u16>", "vec![56320, 55296]")]
  public void Value_IsALiteralOfTheTypeTheTranslationTakes(string json, string type, string literal)
    => Assert.That(RustPrinter.Print(KatRustCrate.Value(Recorded(json), type)), Is.EqualTo(literal));

  [TestCase("""{"t":"number","v":256}""", "u8", "the number 256 is outside the range of u8 the translation takes")]
  [TestCase("""{"t":"number","v":-1}""", "u8", "the number -1 is outside the range of u8 the translation takes")]
  [TestCase("""{"t":"number","v":-129}""", "i8", "the number -129 is outside the range of i8 the translation takes")]
  [TestCase("""{"t":"number","v":1.5}""", "i32", "the number 1.5 is given where the translation takes an integer i32")]
  [TestCase("""{"t":"number","v":"NaN"}""", "i32", "the number NaN is given where the translation takes an integer i32")]
  [TestCase("""{"t":"number","v":"Infinity"}""", "i64", "the number Infinity is given where the translation takes an integer i64")]
  [TestCase("""{"t":"bigint","v":"18446744073709551616"}""", "u64", "the number 18446744073709551616 is outside the range of u64 the translation takes")]
  [TestCase("""{"t":"bytes","v":"0102"}""", "&[bool]", "the number 1 is given where the translation takes bool")]
  [TestCase("""{"t":"number","v":1}""", "bool", "a recorded value of the kind 'number' cannot be given as bool")]
  [TestCase("""{"t":"bool","v":true}""", "i32", "a recorded value of the kind 'bool' cannot be given as i32")]
  [TestCase("""{"t":"string","v":"x"}""", "&[u8]", "a recorded value of the kind 'string' cannot be given as [u8]")]
  [TestCase("""{"t":"string","v":"x"}""", "i32", "a recorded value of the kind 'string' cannot be given as i32")]
  [TestCase("""{"t":"null","v":null}""", "i32", "a recorded value of the kind 'null' cannot be given as i32")]
  [TestCase("""{"t":"bytes","v":"01"}""", "Rc<dyn Key>", "a recorded value of the kind 'bytes' cannot be given as Rc<dyn Key>")]
  [TestCase("""{"t":"object","v":"X"}""", "i32", "a recorded value of the kind 'object' cannot be given as i32")]
  public void Value_ThatCannotBeOfTheType_IsRefused(string json, string type, string reason)
    => Assert.That(() => KatRustCrate.Value(Recorded(json), type), Throws.TypeOf<KatRustException>().With.Message.EqualTo(reason));

  [Test]
  public void Test_OfAValueThatCannotBeGiven_PanicsWithWhy() {
    var text = Body(Instance("    fn feed(&self, data: i32);\n\n    fn result(&self) -> Vec<u8>;"), Vector());
    Assert.That(text, Does.Contain("\"a recorded value of the kind 'bytes' cannot be given as i32\""));
  }

  // --- what cargo test reports ------------------------------------------------------------------

  private const string Report = """

    running 4 tests
    test kat_1 ... ok
    test kat_2 ... FAILED
    test kat_3 ... FAILED
    test kat_4 ... FAILED

    failures:

    ---- kat_2 stdout ----

    thread 'kat_2' (1732691) panicked at crate/src/xorsum.rs:26:9:
    boom
    note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace

    ---- kat_3 stdout ----

    thread 'kat_3' panicked at tests/kat.rs:40:13:
    first differing byte at 0: expected 0x01, got 0x00 (1 bytes expected, 1 given)
    second line


    failures:
        kat_2
        kat_3
        kat_4

    test result: FAILED. 1 passed; 3 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s

    """;

  [Test]
  public void CargoTest_IsReadAsAPassAFailureAPanicAndAFailureWithoutAMessage() {
    var outcomes = KatRustCrate.ReadTests(Report.ReplaceLineEndings("\r\n"));
    Assert.That(outcomes, Is.EqualTo(new Dictionary<string, KatRustOutcome> {
      ["kat_1"] = new(true, ""),
      ["kat_2"] = new(false, "the translation panicked: boom"),
      ["kat_3"] = new(false, "first differing byte at 0: expected 0x01, got 0x00 (1 bytes expected, 1 given)"),
      ["kat_4"] = new(false, "failed without a message")
    }));
    Assert.That(KatRustCrate.ReadTests("error[E0425]: cannot find value `x`\n"), Is.Empty, "a build that failed ran no test");
  }

  [TestCase("   Compiling k v0.1.0\nerror[E0425]: cannot find value `x` in this scope\n --> src/m.rs:1:1\nerror: could not compile `k` (lib) due to 1 previous error\n", "E0425: cannot find value `x` in this scope")]
  [TestCase("error: failed to parse manifest at `/w/Cargo.toml`\r\n", "failed to parse manifest at `/w/Cargo.toml`")]
  [TestCase("error: could not compile `k` (lib) due to 1 previous error\n", "could not compile `k` (lib) due to 1 previous error")]
  [TestCase("error: test failed, to rerun pass `--test kat`\nerror: aborting due to 2 previous errors\nerror: 1 target failed:\nerror: the real one\n", "the real one")]
  [TestCase("\n   something odd\n", "something odd")]
  [TestCase("", "no output")]
  public void FirstError_IsTheCompilersFirst_NotCargosSummary(string text, string error) => Assert.That(KatRustCrate.FirstError(text), Is.EqualTo(error));

  // --- the harness ------------------------------------------------------------------------------

  /// <summary>Stands in for polypiler, which writes the stand-in crate, and cargo, which answers as it is told.</summary>
  private sealed class FakeCargo(bool writeCrate = true) : IProcessRunner {

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public ProcessResult Test { get; set; } = new(0, "", "", false);

    public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) {
      this.Calls.Add(argv);
      if (!argv.Contains("translate"))
        return this.Test;

      var output = argv[argv.ToList().IndexOf("--output") + 1];
      if (writeCrate)
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_mini, "rust"), "*", SearchOption.AllDirectories))
          Scratch.Write(output, Path.GetRelativePath(Path.Combine(_mini, "rust"), file), File.ReadAllBytes(file));
      return new(0, "", "", false);
    }

  }

  private KatTargetResult Check(FakeCargo runner)
    => new KatHarness(runner, ["polypiler"], _mini, Path.Combine(_mini, "goldens"), Path.Combine(this._root, "work")).Check("checksum/xorsum.js", [KatTarget.Rust]).Targets[0];

  [Test]
  public void Harness_TranslatesIntoTheCrateFolder_RunsCargoOffline_AndCountsEveryVector() {
    var runner = new FakeCargo { Test = new(101, "test kat_1 ... ok\ntest kat_2 ... FAILED\n", "", false) };
    var result = this.Check(runner);
    var folder = Path.Combine(this._root, "work", "checksum", "xorsum", "rust");
    Assert.That(runner.Calls[0].Skip(5), Is.EqualTo(new[] { "--to", "rust", "--output", Path.Combine(folder, "crate") }));
    Assert.That(runner.Calls[1], Is.EqualTo(new[] { "cargo", "test", "--offline", "--no-fail-fast", "--manifest-path", Path.Combine(folder, "Cargo.toml"), "--test", "kat" }));
    Assert.That((result.Stage, result.Reason, result.Passed, result.AllPassed), Is.EqualTo((KatStage.Ran, (string?)null, 1, false)));
    Assert.That(result.Failures, Is.EqualTo(new[] { "XorSum-0 #2: failed without a message", "XorSum-7 #1: did not run" }));
    Assert.That(File.Exists(Path.Combine(folder, "tests", "kat.rs")), Is.True);
  }

  [Test]
  public void Harness_OfACrateThatDoesNotBuild_NamesTheFirstError_OrTheTimeout() {
    var failed = this.Check(new FakeCargo { Test = new(101, "", "error[E0308]: mismatched types\nerror: could not compile `xorsum`\n", false) });
    Assert.That((failed.Stage, failed.Reason, failed.AllPassed), Is.EqualTo((KatStage.BuildFailed, (string?)"E0308: mismatched types", false)));
    var late = this.Check(new FakeCargo { Test = new(-1, "", "", true) });
    Assert.That((late.Stage, late.Reason), Is.EqualTo((KatStage.BuildFailed, (string?)"the build and the tests timed out")));
  }

  [Test]
  public void Harness_OfATranslationTheTestsCannotReach_IsTranslatedAndNotRun() {
    var runner = new FakeCargo(writeCrate: false);
    var result = this.Check(runner);
    Assert.That((result.Stage, result.AllPassed), Is.EqualTo((KatStage.Translated, false)));
    Assert.That(result.Reason, Does.StartWith("the tests cannot reach the translation: ").And.Contain("Cargo.toml"));
    Assert.That(runner.Calls, Has.Count.EqualTo(1), "cargo does not run");
  }

}
