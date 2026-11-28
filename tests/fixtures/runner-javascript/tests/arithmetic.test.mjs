import { test } from 'node:test';
import assert from 'node:assert/strict';

test('adds two numbers', () => assert.equal(2 + 3, 5));

test('wraps around in int32', () => assert.equal((0x7fffffff + 1) | 0, -0x80000000));

test('fails on purpose', () => assert.equal(2 + 2, 5, 'this test fails on purpose'));
