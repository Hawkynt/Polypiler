@Layer.core
Feature: Reference interpreter

  The reference interpreter runs a function of a verified module on concrete arguments and reports how
  the run ended - Returned, Trapped, Aborted or UndefinedBehaviourReached - never by an exception, with
  the effects it had and the probes of the run (#119). The IR is the structured tree of ADR 0009: each
  scenario writes it as C#, which the C# frontend imports, and calls into native code are foreign calls
  that the run resolves through its external model.

  Scenario Outline: Structural control flow computes the selected value
    Given the C# methods "public static uint Pick(bool c, uint a, uint b) { if (c) return a; return b; }"
    When Pick is run with <c>, 10, 20
    Then the outcome is Returned with the value <v>

    Examples:
      | c     | v  |
      | true  | 10 |
      | false | 20 |

  Scenario: An external call is traced through its model
    Given the C# methods "[DllImport(\"c64\")] private static extern void Poke(ushort address, byte value); public static void Border() { Poke(53280, 0); Poke(53281, 6); }"
    And Poke modelled as a Device.Write:vic2 event
    When Border is run
    Then the trace is "Device.Write:vic2 53280 0" followed by "Device.Write:vic2 53281 6"

  Scenario: An endless loop ends at the step budget
    Given the C# methods "public static void Spin() { while (true) { } }"
    And MaxSteps 1000
    When Spin is run
    Then the outcome is Aborted with PLY0121 after exactly 1000 steps

  Scenario: A call to an unmodelled external aborts
    Given the C# methods "[DllImport(\"lib\")] private static extern uint Mystery(); public static uint Ask() => Mystery();"
    And no external model
    When Ask is run
    Then the outcome is Aborted with PLY0123 naming "@Mystery"
    And the trace contains no event

  Scenario: The probes report frame depth and allocations
    Given the C# methods "public static uint Pick(bool c, uint a, uint b) { if (c) return a; return b; } public static uint Outer() => Pick(true, 10, 20);"
    When Pick is run with true, 10, 20
    Then MaxFrameDepth is 1 and Allocations is 0
    When Outer is run
    Then MaxFrameDepth is 2 and Allocations is 0

  Scenario: Wrong arguments are refused
    Given the C# methods "public static uint Pick(bool c, uint a, uint b) { if (c) return a; return b; }"
    When Pick is run with true, 10
    Then the outcome is Aborted with PLY0122
