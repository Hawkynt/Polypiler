import assert from "node:assert/strict";
import test from "node:test";
import { adler32 } from "./adler32.mjs";

test("adler32 of 'abc' and of the empty array", () => {
  assert.equal(adler32(new TextEncoder().encode("abc")), 38600999);
  assert.equal(adler32(new Uint8Array(0)), 1);
});
