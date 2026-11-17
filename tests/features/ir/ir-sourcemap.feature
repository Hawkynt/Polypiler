@Layer.core
Feature: Source maps

  The IR is a tree: an instruction is a statement, and its origins are the suffixes !loc, !addr and
  !synthetic that Origin.Format prints and Origin.TryParse reads.

  Scenario: A location survives parse, rewrite and print
    Given the module declares the source "demo.bas"
    And the assignment "%r = Select %c, %a, 7" with the origins "!loc(0:12:5-12:18)"
    When its constant is replaced by 8 through IrOrigins.Replace
    Then the printed origins of the replacement are "!loc(0:12:5-12:18)"

  Scenario: Merging two instructions keeps both origins in order
    Given instructions %a at "0:3:1-3:9" and %b at "0:4:1-4:9"
    When a rewrite replaces both by one instruction
    Then the new instruction prints "!loc(0:3:1-3:9) !loc(0:4:1-4:9)"

  Scenario: A synthesized instruction must say who made it
    Given a module with sources whose instruction %t has no origin
    When the module is verified
    Then verification fails with PLY0115 naming "#1 (ExprStmt)"
    And after marking %t with Synthetic("O0003") verification succeeds

  Scenario: The exported map validates and points at the right text
    Given "tests/fixtures/ir/sourcemap/fixbcd.locations" whose spans refer to the pinned FIXBCD.BAS
    When the source map is written
    Then the JSON validates against spec/ir/sourcemap.schema.json
    And for every span the text of FIXBCD.BAS at that span equals the fixture's expected lexeme

  Scenario: A location pointing at an undeclared source is rejected
    Given a module declaring 1 source and an instruction with "!loc(3:1:1-1:2)"
    When it is parsed
    Then parsing fails with PLY0117
