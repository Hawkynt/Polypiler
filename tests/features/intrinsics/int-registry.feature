@Layer.core
Feature: Intrinsic registry

  # The IR is a typed tree (ADR 0009): an intrinsic is an IrIntrinsic node of an IrIntrinsicOp, and its
  # contract has the name of the operation. The self-test contract identity.yaml declares Abs over the
  # unsigned types, where it is the identity.

  Scenario: A valid contract is loaded and queried
    Given the contract file "identity.yaml" declaring intrinsic "Abs" in group "IntMath" over unsigned types with exactness mode "exact"
    And an evaluator and the decomposition "x | 0" for it
    When the registry is built
    Then Get("Abs").Group is "IntMath"
    And evaluating "Abs" on u32 0xDEADBEEF gives 0xDEADBEEF

  Scenario: Expansion replaces the intrinsic by its decomposition
    Given the contract file "identity.yaml" declaring intrinsic "Abs" in group "IntMath" over unsigned types with exactness mode "exact"
    And an evaluator and the decomposition "x | 0" for it
    When the registry is built
    And ExpandIntrinsics runs for every intrinsic of a function returning "Abs" of a u8
    Then the function returns "x | 0"
    And calling it with 0x7F gives 0x7F

  Scenario: An operand type outside the contract is a verifier error
    Given the contract file "identity.yaml" declaring intrinsic "Abs" in group "IntMath" over unsigned types with exactness mode "exact"
    And an evaluator and the decomposition "x | 0" for it
    When the registry is built
    And the intrinsic verifier checks a function returning "Abs" of a f32
    Then it reports PLY0252 "intrinsic 'Abs' does not admit f32 as operand 0"

  Scenario: An unregistered intrinsic is a verifier error
    Given the contract file "identity.yaml" declaring intrinsic "Abs" in group "IntMath" over unsigned types with exactness mode "exact"
    And an evaluator and the decomposition "x | 0" for it
    When the registry is built
    And the intrinsic verifier checks a function returning "Parity" of a u32
    Then it reports PLY0250 "unknown intrinsic 'Parity'"

  Scenario: A contract without fallback is refused at start-up
    Given the contract file "nofallback.yaml" with no decomposition and requiresRuntime false
    When the registry is built
    Then building fails with PLY0254 "intrinsic 'NoFallback' has no fallback"

  Scenario: A contract file that violates the schema is refused
    Given the contract file "badexactness.yaml" with exactness mode "roughly"
    When the registry is built
    Then building fails with PLY0251 naming "badexactness.yaml" and the property "exactness"
