@EndToEnd @Layer.rust
Feature: Rust backend: the Cipher framework
  The registry of the Cipher framework is a global list of objects, its metadata classes hold lists of
  objects and names that may be null, and an algorithm's instance is made from the algorithm itself.
  Collections a field or a global holds are shared, module state of objects lives in a static of each
  thread with a public reader, and the file's top level runs once before that state is first used.

  Scenario Outline: A checksum file of the corpus passes its known-answer tests in Rust
    Given the Cipher file "<file>" of the pinned corpus
    When polypiler-tooling cipher kat runs it with --targets rust
    Then the translation builds and <passed> of <passed> checked vectors pass

    Examples:
      | file                    | passed |
      | checksum/adler.js       | 11     |
      | checksum/check-digit.js | 12     |
      | checksum/isbn.js        | 6      |

  Scenario: A registry of objects is module state of each thread with a reader
    Given the C# source "using System.Collections.Generic; public sealed class Item { public int V; public Item(int v) { V = v; } } public static class S { static List<Item> items = new List<Item>(); public static int Add(int v) { items.Add(new Item(v)); return items.Count; } }"
    When it is translated to Rust
    Then the module contains "static ITEMS: RefCell<Rc<RefCell<Vec<Rc<Item>>>>> = RefCell::new(Rc::new(RefCell::new(Vec::new())));"
    And the module contains "pub fn items() -> Vec<Rc<Item>> {"
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings

  Scenario: Writing into an array a shared list holds is refused
    Given the JavaScript source "const T = [[0, 1], [1, 0]]; class H { constructor() { this.t = T; } poke() { this.t[0][1] = 5; return this.t[0][1]; } } /** @returns {number} */ export function f() { return new H().poke(); }"
    When it is checked for Rust
    Then PLY1550 names "an array a shared list holds written in place"
