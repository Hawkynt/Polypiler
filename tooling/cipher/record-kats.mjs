// Records the known-answer tests of every Cipher algorithm as node runs them (#1365): the golden files
// `polypiler-tooling cipher kat` checks the translations against.
//
//   node tooling/cipher/record-kats.mjs --corpus <Cipher/AlgorithmFramework.js> --out <dir> [--check] [--only <path>]...
//
// --corpus names the framework file of the pinned corpus; the algorithm files are the `.js` files below
// `algorithms/` next to it. Every file runs in a node of its own, so that what one file registers or
// breaks never reaches the next. A file's algorithms are those its own code registers - not those of a
// file it requires - and each of their vectors is applied to a fresh instance exactly as the corpus's
// own test engine applies it (TestEngine.ConfigureInstance), through a proxy that records every
// property set and every method call the engine makes, in order. Then the vector's input is fed and
// the result taken, twice on two instances, to tell a deterministic algorithm from one that is not.
//
// One file per algorithm file is written: `<out>/<path below algorithms/ without .js>.kat.json`. With
// --check nothing is written: a file that differs, is missing or is not produced any more fails the
// run. --only restricts the run to files below algorithms/ (`checksum/adler.js`), which never removes
// the golden files of the others.

import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { gunzipSync, gzipSync } from 'node:zlib';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

/** How long one algorithm file may run, in milliseconds: the slowest post-quantum ones take seconds per vector. */
const FILE_TIMEOUT = 600000;

/** What separates the record a file's node writes from anything the algorithms print before it. */
const MARKER = '\n@@record-kats@@\n';

/** The schema of a golden file; a change of the format changes it. */
const SCHEMA = 1;

class RecordError extends Error {}

const fail = reason => { throw new RecordError(reason); };

/** The command line as an object; an unknown or incomplete option is an error. */
export const parseArguments = argv => {
  const options = { check: false, only: [] };
  const value = index => argv[index + 1] ?? fail(`${argv[index]} needs a value`);
  for (let index = 0; index < argv.length; ++index) {
    switch (argv[index]) {
      case '--corpus': options.corpus = value(index++); break;
      case '--out': options.out = value(index++); break;
      case '--check': options.check = true; break;
      case '--only': options.only.push(value(index++).replaceAll('\\', '/')); break;
      case '--file': options.file = value(index++); break;
      default: fail(`unknown argument '${argv[index]}'`);
    }
  }
  if (options.corpus === undefined || (options.out === undefined && options.file === undefined))
    fail('usage: record-kats.mjs --corpus <Cipher/AlgorithmFramework.js> --out <dir> [--check] [--only <path>]...');
  return options;
};

/** Every `.js` file below a directory, as `/`-separated paths relative to it, in ordinal order. */
export const algorithmFiles = directory => {
  const found = [];
  const walk = folder => {
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      const full = join(folder, entry.name);
      if (entry.isDirectory())
        walk(full);
      else if (entry.name.endsWith('.js'))
        found.push(relative(directory, full).split(sep).join('/'));
    }
  };
  walk(directory);
  return found.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
};

const hex = bytes => Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');

const isByte = value => Number.isInteger(value) && value >= 0 && value <= 255;

/**
 * A value as JSON that says what it is: bytes as hexadecimal - an array whose every element is an
 * integer from 0 to 255, or a Uint8Array - other typed arrays and arrays element by element, and an
 * object or a function only by the name of its class, which no translation can be given.
 */
export const encode = value => {
  if (value === undefined)
    return { t: 'undefined' };
  if (value === null)
    return { t: 'null' };
  switch (typeof value) {
    case 'boolean': return { t: 'bool', v: value };
    case 'number': return Number.isFinite(value) ? { t: 'number', v: value } : { t: 'number', v: String(value) };
    case 'bigint': return { t: 'bigint', v: value.toString() };
    case 'string': return { t: 'string', v: value };
    case 'function': return { t: 'function', v: value.name || '' };
  }
  if (value instanceof Uint8Array)
    return { t: 'bytes', v: hex(value), typed: 'Uint8Array' };
  if (ArrayBuffer.isView(value) && !(value instanceof DataView))
    return { t: 'typed', kind: value.constructor.name, v: Array.from(value, element => (typeof element === 'bigint' ? element.toString() : element)) };
  if (Array.isArray(value))
    return value.every(isByte) ? { t: 'bytes', v: hex(value) } : { t: 'array', v: value.map(encode) };
  return { t: 'object', v: value.constructor?.name ?? 'Object' };
};

/** Whether node's result equals the expected bytes as the corpus's test engine compares them: element by element with ===. */
const sameElements = (a, b) => {
  if (!a || !b || a.length !== b.length)
    return false;
  for (let index = 0; index < a.length; ++index)
    if (a[index] !== b[index])
      return false;
  return true;
};

/**
 * A proxy of an instance that records, in order, every property the code configuring it sets and
 * every method it calls - only those of the configuring code itself, not those the instance's own
 * setters and methods make while they run.
 */
const recording = (target, steps) => {
  let depth = 0;
  const state = { recording: true };
  const proxy = new Proxy(target, {
    get(object, property, receiver) {
      const value = Reflect.get(object, property, receiver);
      if (typeof value !== 'function' || typeof property !== 'string' || property === 'constructor')
        return value;
      return function (...args) {
        const outermost = depth === 0 && state.recording;
        if (outermost)
          steps.push({ call: property, args: args.map(encode) });
        ++depth;
        try {
          return value.apply(this, args);
        } finally {
          --depth;
        }
      };
    },
    set(object, property, value, receiver) {
      if (depth === 0 && state.recording && typeof property === 'string')
        steps.push({ set: property, value: encode(value) });
      ++depth;
      try {
        return Reflect.set(object, property, value, receiver);
      } finally {
        --depth;
      }
    }
  });
  return { proxy, stop: () => { state.recording = false; } };
};

/** Runs one vector on a fresh instance: the steps the test engine takes to configure it, then Feed and Result. */
const runVector = (engine, algorithm, vector) => {
  const steps = [];
  const instance = algorithm.CreateInstance(vector.inverse === true);
  if (!instance)
    return { steps, outcome: 'threw', error: 'Failed to create algorithm instance' };
  const { proxy, stop } = recording(instance, steps);
  try {
    engine.ConfigureInstance(algorithm, proxy, vector);
    stop();
    proxy.Feed(vector.input);
    return { steps, outcome: 'returned', output: proxy.Result() };
  } catch (error) {
    stop();
    return { steps, outcome: 'threw', error: String(error && error.message !== undefined ? error.message : error) };
  }
};

/** The vector as the golden file holds it: what configures the instance, what is fed, what is expected and what node gave. */
const recordVector = (engine, algorithm, vector) => {
  const first = runVector(engine, algorithm, vector);
  const second = runVector(engine, algorithm, vector);
  const hasExpected = Boolean(vector.expected && vector.expected.length > 0);
  const record = {
    text: typeof vector.text === 'string' ? vector.text : '',
    inverse: vector.inverse === true,
    steps: first.steps,
    input: encode(vector.input)
  };
  if (hasExpected)
    record.expected = encode(vector.expected);
  const outcome = first.outcome === 'returned'
    ? (!hasExpected || sameElements(first.output, vector.expected) ? 'passed' : 'failed')
    : 'threw';
  record.node = { outcome };
  // Where node gave the expected bytes, its result says nothing more.
  if (first.outcome === 'returned' && !(outcome === 'passed' && hasExpected))
    record.node.output = encode(first.output);
  else
    record.node.error = first.error;
  record.node.deterministic = first.outcome === second.outcome
    && JSON.stringify(encode(first.output)) === JSON.stringify(encode(second.output))
    && JSON.stringify(first.steps) === JSON.stringify(second.steps);
  return record;
};

/** The frames of the stack that are files below a directory, innermost first. */
const callers = directory => {
  const files = [];
  for (const line of (new Error().stack ?? '').split('\n')) {
    const match = /\(?((?:[A-Za-z]:)?[^():]+\.js):\d+:\d+\)?$/.exec(line.trim());
    if (match && resolve(match[1]).startsWith(directory + sep))
      files.push(resolve(match[1]));
  }
  return files;
};

/** Records one algorithm file in this node: the algorithms its own code registers, each with its vectors. */
export const recordFile = (corpus, file) => {
  const root = dirname(realpathSync(resolve(corpus)));
  const algorithms = join(root, 'algorithms');
  const target = resolve(algorithms, file);
  const require = createRequire(target);
  const framework = require(resolve(corpus));
  const registered = [];
  const original = framework.RegisterAlgorithm;
  framework.RegisterAlgorithm = function (algorithm) {
    const result = original.apply(this, arguments);
    registered.push({ algorithm, file: callers(algorithms)[0] });
    return result;
  };
  globalThis.AlgorithmFramework = framework;
  globalThis.OpCodes = require(join(root, 'OpCodes.js'));
  const engine = require(join(root, 'tests', 'TestEngine.js'));
  const record = { schema: SCHEMA, file: `algorithms/${file}`, algorithms: [] };
  try {
    require(target);
  } catch (error) {
    record.error = String(error && error.message !== undefined ? error.message : error);
    return record;
  }

  for (const { algorithm } of registered.filter(entry => entry.file === target)) {
    const vectors = Array.isArray(algorithm.tests) ? algorithm.tests : [];
    record.algorithms.push({
      name: String(algorithm.name),
      vectors: vectors.map(vector => recordVector(engine, algorithm, vector))
    });
  }
  return record;
};

/** The golden file of an algorithm file, below the output folder. */
export const goldenPath = (out, file) => join(out, ...file.replace(/\.js$/, '.kat.json').split('/'));

/** The size from which a golden file is compressed: a few hold inputs of megabytes, mostly repetitive. */
const COMPRESS_FROM = 1 << 20;

/** A record as text: one line per vector field, each recorded value and each step on a line of its own. */
export const text = record => {
  const inline = value => value !== null && typeof value === 'object' && !Array.isArray(value) && ('t' in value || 'set' in value || 'call' in value);
  const write = (value, indent) => {
    if (inline(value) || value === null || typeof value !== 'object')
      return JSON.stringify(value);
    const inner = ' '.repeat(indent + 1);
    if (Array.isArray(value))
      return value.length === 0 ? '[]' : `[\n${value.map(item => inner + write(item, indent + 1)).join(',\n')}\n${' '.repeat(indent)}]`;
    const entries = Object.entries(value);
    return entries.length === 0 ? '{}' : `{\n${entries.map(([key, item]) => `${inner}${JSON.stringify(key)}: ${write(item, indent + 1)}`).join(',\n')}\n${' '.repeat(indent)}}`;
  };
  return write(record, 0) + '\n';
};

/** The bytes of a golden file and its name: the text as it is, or gzip-compressed with `.gz` appended from COMPRESS_FROM on. */
export const golden = (path, content) => Buffer.byteLength(content) < COMPRESS_FROM
  ? { path, bytes: Buffer.from(content) }
  : { path: path + '.gz', bytes: gzipSync(Buffer.from(content), { level: 9, mtime: 0 }) };

/** Runs one file in a node of its own and returns its record; a node that fails or times out is a record with an error. */
const spawnFile = (corpus, file) => {
  const script = fileURLToPath(import.meta.url);
  const result = spawnSync(process.execPath, [script, '--corpus', corpus, '--file', file], {
    encoding: 'utf8', timeout: FILE_TIMEOUT, maxBuffer: 1 << 30
  });
  const marker = result.stdout ? result.stdout.lastIndexOf(MARKER) : -1;
  if (result.status === 0 && marker >= 0)
    return JSON.parse(result.stdout.slice(marker + MARKER.length));
  const reason = result.error ? `node did not finish: ${result.error.code ?? result.error.message}` : `node exited with ${result.status ?? result.signal}: ${(result.stderr || '').trim().split('\n').pop()}`;
  return { schema: SCHEMA, file: `algorithms/${file}`, algorithms: [], error: reason };
};

/** Writes or checks the golden files; returns the problems of a check, empty after a write. */
export const run = options => {
  const algorithms = join(dirname(resolve(options.corpus)), 'algorithms');
  if (!existsSync(algorithms))
    fail(`no algorithms folder next to ${options.corpus}`);
  const all = algorithmFiles(algorithms);
  const unknown = options.only.filter(file => !all.includes(file));
  if (unknown.length > 0)
    fail(`no algorithm file ${unknown.join(', ')}`);
  const files = options.only.length > 0 ? options.only : all;
  const problems = [];
  const produced = new Set();
  for (const file of files) {
    const { path, bytes } = golden(goldenPath(options.out, file), text(spawnFile(options.corpus, file)));
    produced.add(resolve(path));
    if (options.check) {
      if (!existsSync(path))
        problems.push(`${path} is missing`);
      else if (Buffer.compare(path.endsWith('.gz') ? gunzipSync(readFileSync(path)) : readFileSync(path), path.endsWith('.gz') ? gunzipSync(bytes) : bytes) !== 0)
        problems.push(`${path} differs from what node records now`);
      continue;
    }
    mkdirSync(dirname(path), { recursive: true });
    for (const other of [path.endsWith('.gz') ? path.slice(0, -3) : path + '.gz'])
      if (existsSync(other))
        rmSync(other);
    writeFileSync(path, bytes);
  }

  if (options.only.length === 0 && existsSync(options.out)) {
    const walk = folder => {
      for (const entry of readdirSync(folder, { withFileTypes: true })) {
        const full = join(folder, entry.name);
        if (entry.isDirectory())
          walk(full);
        else if ((entry.name.endsWith('.kat.json') || entry.name.endsWith('.kat.json.gz')) && !produced.has(resolve(full))) {
          if (options.check)
            problems.push(`${full} is not produced any more`);
          else
            rmSync(full);
        }
      }
    };
    walk(options.out);
  }
  return problems;
};

const main = () => {
  try {
    const options = parseArguments(process.argv.slice(2));
    if (options.file !== undefined) {
      // What an algorithm prints while it loads or runs stays ahead of the record.
      process.stdout.write(MARKER + JSON.stringify(recordFile(options.corpus, options.file)));
      return 0;
    }
    const problems = run(options);
    for (const problem of problems)
      console.error(problem);
    return problems.length === 0 ? 0 : 1;
  } catch (error) {
    if (!(error instanceof RecordError))
      throw error;
    console.error(`record-kats: ${error.message}`);
    return 2;
  }
};

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  process.exitCode = main();
