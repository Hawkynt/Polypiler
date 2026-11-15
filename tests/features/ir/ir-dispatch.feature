@EndToEnd @Layer.core
Feature: Inheritance, interfaces and virtual dispatch

  Scenario Outline: An abstract C# hierarchy dispatches the same in every JavaScript profile
    Given the fixture "dispatch/Shapes.cs"
    When it is translated with "--to javascript --profile <profile>"
    Then the output contains "<form>"
    And in node the sum of Twice() over Rect(2, 3) and Circle(1) is 18 and the sum of Sides() is 4, as in .NET

    Examples:
      | profile | form                                            |
      | es5     | Rect.prototype = Object.create(Shape.prototype) |
      | es2015  | class Rect extends Shape                        |
      | es2022  | class Rect extends Shape                        |

  Scenario Outline: JavaScript hierarchies become C# classes with virtual and override
    Given the fixture "<fixture>"
    When it is translated with "--to csharp --profile <profile>"
    Then the generated file compiles against the reference assemblies of <profile> at its language version without diagnostics
    And the output contains "public virtual double area()" in Shape and "public override double area()" in Rect
    And the sum of twice() is 18 and the sum of sides() is 4, as in node

    Examples:
      | fixture                | profile |
      | dispatch/shapes.js     | net2.0  |
      | dispatch/shapes-es5.js | net4.8  |
      | dispatch/shapes.js     | net10.0 |

  Scenario: Base calls select the base implementation
    Given the fixtures "dispatch/Animals.cs" and "dispatch/animals.js"
    Then new Dog().Describe() returns 42 and new Animal().Describe() returns 1 in .NET, node, the interpreter, every C# profile and node at es5 and es2022

  Scenario: Type tests and casts behave alike
    Given the fixture "dispatch/Shapes.cs" with o the Circle(1) as an object of static type Shape
    Then "o is Rect" is false, "o is IShape" is true and "o as Rect" is null in .NET, the interpreter and every profile of both backends
    And "(Rect)o" throws InvalidCastException in .NET and every C# profile, traps with InvalidCast in the interpreter and throws a TypeError in node at es5 and es2022

  Scenario: A virtual call from a C# base constructor is refused
    Given the fixture "dispatch/CtorVirtual.cs" in which Base() calls the virtual Init() that Derived overrides to read a field initialized to 42
    When I run the translate command to JavaScript
    Then the exit code is 3
    And the output contains "PLY1002" and "a virtual call on 'this' during construction"

  Scenario: An overridden method called from a JavaScript base constructor is refused
    Given the fixture "dispatch/ctor-virtual.js" in which the constructor of Base calls this.init() and Derived overrides init
    When I run the translate command to C#
    Then the exit code is 3
    And the output contains "PLY1102" and "a call of an overridden method on 'this' during construction"
