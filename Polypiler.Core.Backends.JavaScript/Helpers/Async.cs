// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of async functions (#1330), which work on the native <c>Promise</c> of ECMAScript 2015.
/// <c>$async</c> runs the generator an async function is at ECMAScript 2015, resuming it with the result
/// of every value it yields or throwing its rejection into it: the steps AsyncFunctionStart and Await of
/// ECMA-262 §27.7.5. <c>$whenAll</c> waits for every promise and rejects with the first rejection in
/// argument order, as <c>await Task.WhenAll</c> throws, where <c>Promise.all</c> would reject with the
/// first in time: on <c>Promise.allSettled</c> from ECMAScript 2020, on <c>Promise.all</c> of settled
/// records below. Written for this repository from ECMA-262 §27.2 and §27.7.
/// </summary>
internal static partial class JavaScriptHelpers {

  /// <summary>The names of the helpers of async functions, in their order.</summary>
  private static IEnumerable<string> AsyncOrder => ["$async", "$whenAll"];

  /// <summary>The text of a helper of async functions for the level; <see langword="null"/> for any other name.</summary>
  private static string? AsyncSource(string name, Func<string, bool> native) => name switch {
    "$async" => Lines(
      "function $async(body) {",
      "  return new Promise(function (resolve, reject) {",
      "    @const steps = body();",
      "    function step(method, value) {",
      "      @let result;",
      "      try {",
      "        result = steps[method](value);",
      "      } catch (error) {",
      "        reject(error);",
      "        return;",
      "      }",
      "      if (result.done) resolve(result.value);",
      "      else Promise.resolve(result.value).then(function (next) { step(\"next\", next); }, function (error) { step(\"throw\", error); });",
      "    }",
      "    step(\"next\", void 0);",
      "  });",
      "}"),
    "$whenAll" => native("Promise.allSettled")
      ? Lines(
        "function $whenAll(promises) {",
        "  return Promise.allSettled(promises).then(function (results) {",
        "    @const values = [];",
        "    for (@let i = 0; i < results.length; ++i) {",
        "      if (results[i].status === \"rejected\") throw results[i].reason;",
        "      values.push(results[i].value);",
        "    }",
        "    return values;",
        "  });",
        "}")
      : Lines(
        "function $whenAll(promises) {",
        "  @const settled = promises.map(function (promise) {",
        "    return promise.then(function (value) { return { status: \"fulfilled\", value: value }; }, function (reason) { return { status: \"rejected\", reason: reason }; });",
        "  });",
        "  return Promise.all(settled).then(function (results) {",
        "    @const values = [];",
        "    for (@let i = 0; i < results.length; ++i) {",
        "      if (results[i].status === \"rejected\") throw results[i].reason;",
        "      values.push(results[i].value);",
        "    }",
        "    return values;",
        "  });",
        "}"),
    _ => null
  };

}
