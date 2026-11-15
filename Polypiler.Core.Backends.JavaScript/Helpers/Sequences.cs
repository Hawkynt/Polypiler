// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of lazy sequences below ECMAScript 2015, which has neither generators nor
/// <c>Symbol.iterator</c> (#1327). A cursor is an object with <c>next()</c>, which returns
/// <c>{ value, done }</c>, and <c>"return"()</c>, which closes it: ECMA-262 §7.4's iterator protocol.
/// A restartable sequence is an object whose <c>$iterator()</c> opens a new cursor. Written for this
/// repository from ECMA-262 §27.5 (generator objects) and §14.7.5 (IteratorClose).
/// </summary>
internal static partial class JavaScriptHelpers {

  /// <summary>The names of the helpers of sequences, in their order.</summary>
  private static IEnumerable<string> SequenceOrder => ["$generator", "$closeAll", "$iterate", "$arrayIterator"];

  /// <summary>The text of a helper of sequences, the same at every level; <see langword="null"/> for any other name.</summary>
  private static string? SequenceSource(string name) => name switch {
    // The cursor of a state machine: step runs the body from its resume point to the next value or the end;
    // enter and leave keep the finally blocks the body is inside of, which closing it runs innermost first.
    "$generator" => Lines(
      "function $generator(step) {",
      "  var pending = [];",
      "  var done = false;",
      "  var machine = {",
      "    enter: function (close) {",
      "      pending.push(close);",
      "    },",
      "    leave: function () {",
      "      pending.pop()();",
      "    },",
      "    end: function () {",
      "      done = true;",
      "      $closeAll(pending);",
      "      return { value: void 0, done: true };",
      "    },",
      "    next: function () {",
      "      if (done) return { value: void 0, done: true };",
      "      try {",
      "        return step(machine);",
      "      } catch (error) {",
      "        done = true;",
      "        $closeAll(pending);",
      "        throw error;",
      "      }",
      "    },",
      "    \"return\": function () {",
      "      if (!done) {",
      "        done = true;",
      "        $closeAll(pending);",
      "      }",
      "      return { value: void 0, done: true };",
      "    }",
      "  };",
      "  return machine;",
      "}"),
    // Runs every pending finally block, innermost first; one that throws replaces what was thrown before, as a finally block does.
    "$closeAll" => Lines(
      "function $closeAll(pending) {",
      "  var failed = false;",
      "  var error;",
      "  while (pending.length > 0) {",
      "    try {",
      "      pending.pop()();",
      "    } catch (thrown) {",
      "      failed = true;",
      "      error = thrown;",
      "    }",
      "  }",
      "  if (failed) throw error;",
      "}"),
    "$iterate" => Lines("function $iterate(sequence) {", "  return sequence.$iterator ? sequence.$iterator() : sequence;", "}"),
    "$arrayIterator" => Lines(
      "function $arrayIterator(array) {",
      "  return {",
      "    $iterator: function () {",
      "      var index = 0;",
      "      return {",
      "        next: function () {",
      "          return index < array.length ? { value: array[index++], done: false } : { value: void 0, done: true };",
      "        },",
      "        \"return\": function () {",
      "          index = array.length;",
      "          return { value: void 0, done: true };",
      "        }",
      "      };",
      "    }",
      "  };",
      "}"),
    _ => null
  };

}
