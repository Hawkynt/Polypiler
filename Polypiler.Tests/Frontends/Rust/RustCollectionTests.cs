using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Rust;

/// <summary>
/// Maps and sets beside the <c>collections</c> fixture: the paths that name them, their
/// construction, the key and value types the IR holds, and every refusal - iteration first, whose
/// order a <c>HashMap</c> leaves unspecified.
/// </summary>
[TestFixture]
public sealed class RustCollectionTests {

  private static ImportResult Import(string source) => RustFrontend.Import("lib.rs", source, OverflowChecks.Panic);

  [TestCase("pub fn f() -> usize { let mut m = std::collections::HashMap::<u8, u8>::new(); m.insert(1, 2); m.len() }", "1")]
  [TestCase("use std::collections::HashSet as Set; pub fn f() -> bool { let mut s = Set::new(); s.insert('x'); s.contains(&'x') && !s.is_empty() }", "true")]
  [TestCase("use std::collections::BTreeSet; pub fn f() -> usize { let mut s: BTreeSet<String> = BTreeSet::new(); s.insert(String::from(\"a\")); s.insert(\"a\".to_string()); s.remove(\"b\"); s.clear(); s.len() }", "0")]
  [TestCase("use std::collections::HashMap; pub fn f() -> u8 { let mut m: HashMap<u8, u8> = HashMap::with_capacity(2); m.insert(1, 9); *m.entry(1).or_default() + m.get(&2).copied().unwrap_or(5) }", "14")]
  [TestCase("use std::collections::HashMap; pub fn f() -> bool { let mut m: HashMap<i64, bool> = HashMap::new(); m.insert(-1, true); m.remove(&-1).unwrap() && m.remove(&-1).is_none() }", "true")]
  public void Collection_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

  [TestCase("use std::collections::HashMap; pub fn f() -> u8 { let m: HashMap<u8, u8> = HashMap::new(); let mut s = 0; for (_, v) in m.iter() { s += v; } s }", "iteration of a HashMap, whose order is unspecified")]
  [TestCase("use std::collections::BTreeMap; pub fn f() -> usize { let m: BTreeMap<u8, u8> = BTreeMap::new(); m.keys().count() }", "iteration of a BTreeMap")]
  [TestCase("use std::collections::HashSet; pub fn f() -> usize { let s: HashSet<u8> = HashSet::new(); s.iter().count() }", "iteration of a HashSet, whose order is unspecified")]
  [TestCase("use std::collections::BTreeSet; pub fn f() -> usize { let s: BTreeSet<u8> = BTreeSet::new(); s.iter().count() }", "iteration of a BTreeSet")]
  [TestCase("use std::collections::HashMap; pub fn f() -> usize { let m: HashMap<u8, u8> = HashMap::new(); let n = m.clone(); n.len() }", "HashMap::clone")]
  [TestCase("use std::collections::HashSet; pub fn f() -> bool { let s: HashSet<u8> = HashSet::new(); s.is_subset(&s) }", "HashSet::is_subset")]
  [TestCase("use std::collections::HashMap; pub fn f(m: HashMap<f64, u8>) {}", "HashMap keyed by 'f64'")]
  [TestCase("use std::collections::HashMap; pub fn f(m: HashMap<u8, String>) {}", "HashMap of 'String' values")]
  [TestCase("use std::collections::HashMap; pub fn f() { let mut m = HashMap::new(); m.insert(1.5, 1u8); }", "HashMap keyed by 'f64'")]
  [TestCase("use std::collections::HashMap; pub fn f() { let mut m = HashMap::new(); m.insert(1u8, vec![1u8]); }", "HashMap of 'Vec<u8>' values")]
  [TestCase("pub fn f(m: HashMap<u8, u8>) {}", "E0412")]
  [TestCase("use std::collections::HashMap; pub fn f(m: HashMap<u8>) {}", "E0107")]
  [TestCase("use std::collections::HashMap; struct S { m: HashMap<u8, u8> } pub fn f() {}", "field 'm'")]
  [TestCase("use std::collections::HashMap; pub fn f() { let m: HashMap<u8, u8> = HashMap::new(); m.insert(1, 2); }", "E0596")]
  [TestCase("use std::collections::HashMap; pub fn f() { let m: HashMap<u8, u8> = HashMap::new(); m.entry(1, 2).or_insert(3); }", "E0061")]
  [TestCase("use std::collections::VecDeque; pub fn f() {}", RustFrontend.ForeignPath)]
  public void Collection_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

  [TestCase("pub fn f() -> usize { let v: Vec<_> = [1u8, 2].iter().map(|x| x * 2).collect(); let w = [3u8].iter().copied().collect::<Vec<u8>>(); v.len() + w[0] as usize }", "5")]
  [TestCase("pub fn f() -> usize { let v: (u8, _) = (1, 2u16); v.1 as usize }", "2")]
  [TestCase("fn id<T>(x: T) -> T { x } pub fn f() -> u8 { id::<_>(4u8) }", "4")]
  [TestCase("use std::collections::BTreeSet; pub fn f() -> bool { let s: BTreeSet<_> = (1..4u64).map(|x| x * x).collect(); s.contains(&9) && !s.contains(&3) }", "true")]
  public void Collect_HasTheValueRustGives(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    Assert.That(RustFixture.Interpreted(result.Module!, new PrototypeCall("f", [])), Is.EqualTo(expected));
  }

  [TestCase("pub fn f() -> usize { let v = (0..3u8).collect(); 1 }", "E0283: type annotations needed: cannot infer the collection `collect` makes")]
  [TestCase("pub fn f() -> usize { (0..3u8).collect::<_>(); 1 }", "E0283: type annotations needed: cannot infer the collection `collect` makes")]
  [TestCase("pub fn f() -> usize { let s: String = \"ab\".chars().collect(); s.len() }", "collect into String")]
  [TestCase("pub fn f() -> usize { let mut a = [1u8, 2]; let v: Vec<&mut u8> = a.iter_mut().collect(); v.len() }", "Vec of &mut u8")]
  [TestCase("pub fn f() -> usize { let mut a = [1u8, 2]; let v = a.iter_mut().collect::<Vec<_>>(); v.len() }", "collect of the &mut items of '&mut u8'")]
  [TestCase("pub fn f() -> usize { let v: Vec<u8> = (0..3u16).collect(); v.len() }", "E0277: a value of type `Vec<u8>` cannot be built from an iterator over elements of type `u16`")]
  [TestCase("use std::collections::HashMap; pub fn f() -> usize { let m: HashMap<u8, u8> = (0..3u8).collect(); m.len() }", "E0277: a value of type `HashMap<u8, u8>` cannot be built from an iterator over elements of type `u8`")]
  [TestCase("use std::collections::HashMap; pub fn f() -> usize { let m: HashMap<_, u8> = [(1.5f64, 2u8)].iter().copied().collect(); m.len() }", "HashMap keyed by 'f64'")]
  [TestCase("fn g(v: Vec<_>) -> usize { v.len() } pub fn f() -> usize { 1 }", "E0121: the placeholder `_` is not allowed within types on item signatures")]
  [TestCase("pub fn f() -> usize { let v: Vec<u8> = (0..3u8).collect::<Unknown>(); 1 }", "E0412")]
  public void Collect_IsRefused(string source, string expected) {
    var result = Import(source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message), Has.Some.Contains(expected));
  }

}
