@Layer.core
Feature: Closed tagged unions

  A value proven to be one of a few types is a closed tagged union of them: made from one member, its
  tag read, its payload taken out, which traps on another member.

  Scenario: A union carries its member
    Given a function returning union<i32, str> that returns the union member 1 of "a"
    When the interpreter runs it and reads the union tag and the union payload of member 1
    Then the union tag is 1 and the union payload is "a"

  Scenario: Taking the wrong member traps
    Given the union value of member 0 holding 7 of union<i32, str>
    When the union payload of member 1 is evaluated
    Then the interpreter traps with InvalidCast

  Scenario Outline: A union outside two to four members is refused
    Given a local of a union of <count> integer members
    When the module of the union is verified
    Then PLY0100 reports "union of <count> members"

    Examples:
      | count |
      | 1     |
      | 5     |
