@EndToEnd @Layer.rust
Feature: Rust backend: helpers that return their traps under the result trap policy
  Under --trap-policy result a helper of strings and collections that traps gives the trap as its
  value - an Option, or a Result naming the trap where it has two - and the call turns it into the
  crate's error, so the crate does not panic; under panic the helpers stay as they were.

  Scenario: A list index outside the list is the ArgumentOutOfRange error
    Given the C# source "public static int At(int i) { var l = new System.Collections.Generic.List<int>(new[] { 1, 2, 3 }); return l[i]; }"
    When it is translated to Rust under the trap policy result
    Then the module contains "ply_list_index(i, l.len()).ok_or(Error::ArgumentOutOfRange)?"
    And with overflow checks panic and wrap the calls give
      | call | arguments | result                      |
      | At   | 2         | 3                           |
      | At   | 5         | trap ArgumentOutOfRange     |
      | At   | -1        | trap ArgumentOutOfRange     |

  Scenario: The parse of an integer gives each of its two traps
    Given the C# source "public static int Parse(int which) { var s = which == 0 ? \"42\" : which == 1 ? \"4x\" : \"99999999999\"; return int.Parse(s); }"
    When it is translated to Rust under the trap policy result
    Then the module contains "Error::InvalidFormat,"
    And with overflow checks panic and wrap the calls give
      | call  | arguments | result                |
      | Parse | 0         | 42                    |
      | Parse | 1         | trap InvalidFormat    |
      | Parse | 2         | trap IntegerOverflow  |

  Scenario: A trap of a helper inside a closure stays refused
    Given the C# source "public static int F(int i) { var l = new System.Collections.Generic.List<int> { 1 }; System.Func<int, int> g = x => l[x]; return g(i); }"
    When it is translated to Rust under the trap policy result
    Then it is refused with PLY1552 naming "a trap inside a closure"
