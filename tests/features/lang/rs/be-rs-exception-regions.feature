@EndToEnd @Layer.rust
Feature: Rust backend: exception regions with finally blocks
  A try with a finally is a labelled block every way out of which - an exception, a return, a break,
  a continue - leaves it first, so that the finally runs, and is made after it. A filter with a
  finally between it and the throw chooses its clause at the throw, before the finally, as .NET's
  first pass does. No crate unwinds: there is no catch_unwind under either trap policy.

  Scenario Outline: The filter runs before the finally of exceptions/order
    Given the exception fixture "order"
    When it is translated to Rust under the trap policy <policy>
    Then nothing is refused
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the calls give what .NET gives
      | call | arguments | result |
      | Run  |           | W,F,C  |

    Examples:
      | policy |
      | panic  |
      | result |

  Scenario Outline: Every way out of a try passes its finally
    Given the exception fixture "Regions"
    When it is translated to Rust under the trap policy <policy>
    Then nothing is refused
    And the crate is clean under cargo fmt --check and cargo clippy -D warnings
    And with overflow checks panic and wrap the calls give what .NET gives
      | call     | arguments | result                 |
      | Ways     | 0         | r                      |
      | Ways     | 1         | fe.fe.                 |
      | Ways     | 2         | f                      |
      | Ways     | 3         | ff                     |
      | Ways     | 4         | nf.nf.                 |
      | Observed | true      | t1 caught boom finally |
      | Observed | false     | t1 ok finally          |
      | Returned | 3         | 6                      |
      | Returned | 0         | 99                     |
      | Jumps    | 7         | 604                    |
      | Nested   |           | WABC                   |
      | Declined | false     | WFD                    |
      | Replaced |           | second                 |

    Examples:
      | policy |
      | panic  |
      | result |

  Scenario Outline: A filter that a called function's finally would run after stays refused
    Given the C# source "using System; public sealed class X : Exception { } public static class S { static int H(int k) { if (k > 0) throw new X(); return k; } static int G(int k) { try { return H(k); } finally { k = 0; } } public static int F(int k) { try { return G(k); } catch (X) when (k > 2) { return 3; } } }"
    When it is translated to Rust under the trap policy <policy>
    Then it is refused with PLY1560 naming "an exception filter that .NET runs before the finally blocks the exception leaves, or that may trap"

    Examples:
      | policy |
      | panic  |
      | result |
