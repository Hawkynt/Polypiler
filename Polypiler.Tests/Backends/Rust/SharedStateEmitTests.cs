using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The forms the Cipher framework needs of the Rust backend (#1422): collections a field, a global or
/// another collection holds as <c>Rc&lt;RefCell&lt;Vec&lt;T&gt;&gt;&gt;</c>, module state of objects in a
/// static of each thread with a public reader, the module's initializer run once, strings, array fields
/// and optional fields that may be null, a method of a struct that passes its object on, the conversion
/// of a struct's <c>Rc</c> to a base's trait object, the throw of a library exception; and the refusals.
/// </summary>
[TestFixture]
public sealed class SharedStateEmitTests {

  private const string Item = "using System; using System.Collections.Generic; public sealed class Item { public int V; public Item(int v) { V = v; } } ";

  private static IReadOnlyList<Polypiler.Core.GeneratedFile> CSharp(string source, RustProfile? profile = null) => RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", source), profile);

  private static string Module(string source, RustProfile? profile = null) => RustCrates.ModuleText(CSharp(source, profile));

  private static RustProfile Profile(string name, string? version = null) {
    RustProfile.TryParse(name, version, out var profile, out _);
    return profile!;
  }

  private static IrModule JavaScript(string source) => JavaScriptFrontend.Import("registry.mjs", source).Module!;

  /// <summary>A registry of objects filled by a JavaScript file's top level, with a base class, a method that passes its object on and a name that may be null.</summary>
  private const string Registry = """
    class Base {
      constructor() { this.name = null; this.tags = []; }
      make() { throw new Error("make() not implemented"); }
    }
    class Maker {
      constructor(owner) { this.owner = owner; }
      size() { return this.owner.name.length; }
    }
    class Thing extends Base {
      constructor(n) { super(); this.name = n; this.tags = [1, 2]; }
      make() { return new Maker(this); }
    }
    const all = [];
    function register(b) {
      if (b.name === null) throw new Error("no name");
      all.push(b);
    }
    function find(name) { return all.find(x => x.name === name) || null; }
    register(new Thing("a"));
    register(new Thing("bb"));
    /** @param {string} name @returns {number} */
    export function sizeOf(name) {
      const t = find(name);
      return t === null ? -1 : t.make().size();
    }
    """;

  [Test]
  public void GlobalListOfObjects_IsModuleStateOfEachThread_WithAReader() {
    var text = Module(Item + "public static class S { static List<Item> items = new List<Item>(); public static int Add(int v) { items.Add(new Item(v)); return items.Count; } }");
    Assert.That(text, Does.Contain("thread_local! {\n    static ITEMS: RefCell<Rc<RefCell<Vec<Rc<Item>>>>> = RefCell::new(Rc::new(RefCell::new(Vec::new())));\n}"));
    Assert.That(text, Does.Contain("pub fn items() -> Vec<Rc<Item>> {\n    ITEMS.with(|items| items.borrow().borrow().clone())\n}"));
    Assert.That(text, Does.Contain("ITEMS.with(|items| items.borrow().clone()).borrow().len() as i32"));
    Assert.That(text, Does.Not.Contain("ply_load"), "a module without an initializer runs none");
  }

  [Test]
  public void ListThatMayHoldNull_HoldsOptions() {
    var text = Module(Item + "public static class S { public static int Nulls() { var l = new List<Item>(); l.Add(null); l.Add(new Item(2)); return l[0] == null ? 1 : 0; } }");
    Assert.That(text, Does.Contain("let l: Vec<Option<Rc<Item>>> = vec![None, Some(Item::new(2))];"), "the adds right after the declaration are its elements");
    Assert.That(text, Does.Contain("l[ply_list_index(0, l.len())].clone().is_none()"));
  }

  [Test]
  public void FieldsOfACollection_AnOptionalValue_AndAnArrayThatMayBeNull() {
    var files = CSharp(Item + "public sealed class Holder { public List<byte> Bytes = new List<byte>(); public int? Year; public byte[] Data; } "
      + "public static class S { public static int Hold(int y, bool d) { var h = new Holder(); h.Bytes.Add((byte)y); if (d) h.Year = y; if (d) h.Data = new byte[] { 1 }; "
      + "return h.Bytes.Count + (h.Year ?? 7) + (h.Data == null ? 0 : h.Data.Length); } }");
    var text = RustCrates.ModuleText(files);
    Assert.That(text, Does.Contain("    bytes: RefCell<Rc<RefCell<Vec<u8>>>>,\n    year: Cell<Option<i32>>,\n    data: RefCell<Option<Vec<u8>>>,"));
    Assert.That(text, Does.Contain("let value = y as u8;\n    h.bytes.borrow().clone().borrow_mut().push(value);"), "the element is made before the exclusive borrow");
    Assert.That(text, Does.Contain("h.year.set(Some(y));"));
    Assert.That(text, Does.Contain("*h.data.borrow_mut() = Some(vec![1]);"));
    Assert.That(text, Does.Contain("if h.data.borrow().is_none() {"));
    Assert.That(text, Does.Contain("ply_non_null(&h.data.borrow()).len() as i32"));
    Assert.That(text, Does.Contain("h.year.get().unwrap_or(7)"));
  }

  [Test]
  public void ThrowOfALibraryException_PanicsWithItsClassName() {
    var text = Module("using System; public static class S { public static int Fail(int x) { if (x < 0) throw new Exception(\"negative: \" + x); return x; } }");
    Assert.That(text, Does.Contain("if x < 0 {\n        panic!(\"Exception\");\n    }"), "named after its class, as the verification reads a panic back");
  }

  [Test]
  public void StructGivenWhereABaseIsTaken_IsConverted() {
    var text = Module("public abstract class A { public abstract int Get(); } public sealed class B : A { public override int Get() => 2; } "
      + "public static class S { static int Take(A a) => a.Get(); public static int F() { var b = new B(); return Take(b) + Take(b); } }");
    Assert.That(text, Does.Contain("take(Rc::clone(&b) as Rc<dyn A>)"));
  }

  [Test]
  public void TopLevelOfAJavaScriptFile_RunsOnceBeforeTheModuleStateIsUsed() {
    var files = RustCrates.Emit(JavaScript(Registry));
    var text = RustCrates.ModuleText(files);
    Assert.That(text, Does.Contain("thread_local! {\n    static PLY_LOADED: Cell<bool> = const { Cell::new(false) };\n}"));
    Assert.That(text, Does.Contain("fn ply_load() {\n    if !PLY_LOADED.with(|loaded| loaded.replace(true)) {\n        initialize();\n    }\n}"));
    Assert.That(text, Does.Contain("pub fn all() -> Vec<Rc<Thing>> {\n    ply_load();\n    ALL.with(|all| all.borrow().borrow().clone())\n}"));
    Assert.That(text, Does.Contain("fn register(b: &Rc<Thing>) {\n    ply_load();"));
    Assert.That(text, Does.Contain("fn initialize() {\n    register(&Thing::new(&ply_utf16(\"a\")));"));
    Assert.That(text, Does.Not.Contain("fn initialize() {\n    ply_load();"), "the initializer does not run itself");
  }

  [Test]
  public void ConstFirstValueOfAStaticOfEachThread_FromRust159() {
    Assert.That(RustCrates.ModuleText(RustCrates.Emit(JavaScript(Registry), Profile("rust2021"))), Does.Contain("static PLY_LOADED: Cell<bool> = Cell::new(false);"));
    Assert.That(RustCrates.ModuleText(RustCrates.Emit(JavaScript(Registry), Profile("rust2021", "1.59"))), Does.Contain("static PLY_LOADED: Cell<bool> = const { Cell::new(false) };"));
  }

  [Test]
  public void MethodThatPassesItsObjectOn_TakesItsRc() {
    var text = RustCrates.ModuleText(RustCrates.Emit(JavaScript(Registry)));
    Assert.That(text, Does.Contain("    fn make(self: Rc<Self>) -> Rc<Maker> {\n        base_make(&*self)\n    }"), "the trait's slot");
    Assert.That(text, Does.Contain("    fn make(self: Rc<Self>) -> Rc<Maker> {\n        thing_make(&self)\n    }"), "the override that passes its object on");
    Assert.That(text, Does.Contain("pub fn thing_make(this: &Rc<Thing>) -> Rc<Maker> {\n    Maker::new(this)\n}"));
    Assert.That(text, Does.Contain("Base::make(t.clone().expect(\"NullReference\")).size()"));
  }

  [Test]
  public void StringThatMayBeNull_IsAnOption_ComparedNullSafe() {
    var text = RustCrates.ModuleText(RustCrates.Emit(JavaScript(Registry)));
    Assert.That(text, Does.Contain("    fn field_name(&self) -> &RefCell<Option<Vec<u16>>>;"));
    Assert.That(text, Does.Contain("*Base::field_name(this).borrow_mut() = None;"));
    Assert.That(text, Does.Contain("*this.name.borrow_mut() = Some(n.to_vec());"));
    Assert.That(text, Does.Contain("if b.name.borrow().is_none() {\n        panic!(\"Exception\");"));
    Assert.That(text, Does.Contain("let find_lambda: bool = x.name.borrow().as_deref() == Some(name);"));
  }

  [Test]
  public void TheCrates_AllowTheTypeComplexityTheyHave() {
    var root = RustCrates.Text(CSharp(Item + "public static class S { static List<Item> items = new List<Item>(); public static int Nulls() { items.Add(null); return items.Count; } }"), "src/lib.rs");
    Assert.That(root, Does.Contain("#![allow(clippy::type_complexity)]"), "RefCell<Rc<RefCell<Vec<Option<Rc<Item>>>>>> scores above 250");
    var plain = RustCrates.Text(CSharp(Item + "public static class S { static List<Item> items = new List<Item>(); public static int Add() { items.Add(new Item(1)); return items.Count; } }"), "src/lib.rs");
    Assert.That(plain, Does.Not.Contain("type_complexity"), "RefCell<Rc<RefCell<Vec<Rc<Item>>>>> scores 210");
  }

  /// <summary>Tables of the module a constructor keeps: a list of arrays, an array of numbers.</summary>
  private const string Tables = """
    const TABLE = [[0, 1, 2], [1, 2, 0]];
    const INVERSE = [0, 2, 1];
    class H {
      constructor() { this.table = TABLE; this.inverse = INVERSE; }
      check(i) { return this.table[i % 2][i % 3] + this.inverse[i % 3]; }
      poke() { this.table[0][1] = 5; return this.table[0][1]; }
    }
    /** @param {number} i @returns {number} */
    export function f(i) { const h = new H(); return h.check(i); }
    """;

  [Test]
  public void ArrayOfAList_IsReadInPlace_AndAGlobalTable_IsCopied() {
    var text = RustCrates.ModuleText(RustCrates.Emit(JavaScript(Tables.Replace("poke() { this.table[0][1] = 5; return this.table[0][1]; }", "", StringComparison.Ordinal))));
    Assert.That(text, Does.Contain("static TABLE: RefCell<Rc<RefCell<Vec<Vec<i32>>>>> = RefCell::new(Rc::new(RefCell::new(vec![vec![0, 1, 2], vec![1, 2, 0]])));"));
    Assert.That(text, Does.Contain("*self.table.borrow_mut() = TABLE.with(|table| table.borrow().clone());"), "the field shares the global's list");
    Assert.That(text, Does.Contain("*self.inverse.borrow_mut() = ply_with(&INVERSE, |inverse| inverse.to_vec());"), "a table nothing writes is copied");
    Assert.That(text, Does.Contain("self.table.borrow().clone().borrow()[ply_list_index("));
  }

  [Test]
  public void ArrayOfASharedList_WrittenInPlace_IsRefused()
    => Assert.That(RustCrates.Check(JavaScript(Tables)).Select(problem => problem.Message), Has.Some.Contain("'an array a shared list holds written in place'"));

  [Test]
  public void StaticOfEachThread_IsPrintedAndReadBack() {
    var local = new RsConst("NAME", "", "RefCell<Option<Rc<Item>>>", new RsCall(new RsPath("RefCell::new"), [new RsPath("None")])) { IsStatic = true, IsThreadLocal = true, IsConstInitialized = true };
    var text = RustPrinter.Print(new RsFile([], [], [local]));
    Assert.That(text, Is.EqualTo("thread_local! {\n    static NAME: RefCell<Option<Rc<Item>>> = const { RefCell::new(None) };\n}\n"));
    var read = (RsConst)RustSyntaxReader.Read(text).Items[0];
    Assert.That((read.Name, read.TypeName, read.IsThreadLocal, read.IsConstInitialized), Is.EqualTo(("NAME", "RefCell<Option<Rc<Item>>>", true, true)));
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read("thread_local! {\n    static N: Cell<u8> = Cell::new(0);\n}\n")), Is.EqualTo("thread_local! {\n    static N: Cell<u8> = Cell::new(0);\n}\n"));
  }

  [Test]
  public void CastToAGenericType_IsReadBack() {
    const string text = "fn f(x: Rc<B>) -> u8 {\n    g(x as Rc<dyn A>, (y as u32) < 3)\n}\n";
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
  }

  [Test]
  public void OverflowedCall_KeepsTheLineWithItsClosers() {
    // rustfmt: a nested call too wide for a call's arguments overflows, and so does the call inside it, its arguments vertical.
    RsExpression Call(string name, params RsExpression[] arguments) => new RsCall(new RsPath(name), arguments);
    var value = Call("Some", Call("unpack16_be", Call("or32", new RsCast(Call("shl32", new RsPath("this.b.get()"), new RsLiteral("8")), "i32"), new RsCast(new RsPath("this.a.get()"), "i32"))));
    var statement = new RsExpressionStatement(new RsAssign(new RsPath("result"), "=", value));
    var function = new RsFunction("f", "", [], null, new RsBlock([statement], null));
    Assert.That(RustPrinter.Print(new RsFile([], [], [function])), Is.EqualTo("fn f() {\n    result = Some(unpack16_be(or32(\n        shl32(this.b.get(), 8) as i32,\n        this.a.get() as i32,\n    )));\n}\n"));
  }

  [Test]
  public void ChainOfAVariable_DoesNotOverflowTheCall() {
    var chain = new RsMethodCall(new RsMethodCall(new RsMethodCall(new RsMethodCall(new RsPath("self"), "owner_of_the_thing", []), "borrow", []), "expect_the_value_to_be_there", []), "len", []);
    var function = new RsFunction("f", "", [], "usize", new RsBlock([], new RsCall(new RsPath("ply_non_null"), [new RsReference(false, chain)])));
    Assert.That(RustPrinter.Print(new RsFile([], [], [function])), Is.EqualTo(
      "fn f() -> usize {\n    ply_non_null(\n        &self\n            .owner_of_the_thing()\n            .borrow()\n            .expect_the_value_to_be_there()\n            .len(),\n    )\n}\n"));
  }

}
