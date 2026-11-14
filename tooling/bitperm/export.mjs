// Exports what the pinned BitPerm page knows as fixture files (PRD §24 Q0, issue #269): the building-block
// registry, the CPU presets, the solver limits, the page's own test cases, the mappings of its quick
// patterns and of the route maps its tests build, and raw evaluation vectors for each mapping.
//
//   node tooling/bitperm/export.mjs --page <index.html> --out <dir> [--check]
//        [--expect-blocks <n>] [--expect-presets <n>] [--expect-tests <n>]
//
// Every value is read from the page's live objects, which the engine region of the page defines when it
// runs in a fresh node:vm context; nothing is retyped. The region is the text from the line
// `// --- CONSTANTS ---` up to the line `// --- VISUALIZER ---`. The page uses its `const CPU_… =`
// declarations inside BUILDING_BLOCKS before it declares them, which ECMAScript rejects outside the
// browser's Babel step, so those lines move to the front; nothing else changes, and the region keeps
// the page's line numbers. With --check nothing is written: a file that differs, is missing or is not
// produced any more fails the run.

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { Script, createContext, runInContext, runInNewContext } from 'node:vm';

const START_MARKER = '// --- CONSTANTS ---';
const END_MARKER = '// --- VISUALIZER ---';
const CPU_DECLARATIONS = 25;
const PAGE_NAME = 'BitPerm/index.html';
const SEED = 0xB17BE9n;
const RANDOM_INPUTS = 256;
const NOTICE = 'THIRD-PARTY-NOTICE.BitPerm.txt';

class ExportError extends Error {}

const fail = reason => { throw new ExportError(reason); };

/** The command line as an object; an unknown or incomplete option is an error. */
export const parseArguments = argv => {
  const options = { check: false, expect: {} };
  const value = index => argv[index + 1] ?? fail(`${argv[index]} needs a value`);
  const count = index => /^\d+$/.test(value(index)) ? Number(value(index)) : fail(`${argv[index]} needs a count, not '${value(index)}'`);
  for (let index = 0; index < argv.length; ++index) {
    switch (argv[index]) {
      case '--page': options.page = value(index++); break;
      case '--out': options.out = value(index++); break;
      case '--check': options.check = true; break;
      case '--expect-blocks': options.expect.blocks = count(index++); break;
      case '--expect-presets': options.expect.presets = count(index++); break;
      case '--expect-tests': options.expect.tests = count(index++); break;
      default: fail(`unknown argument '${argv[index]}'`);
    }
  }
  if (options.page === undefined || options.out === undefined)
    fail('usage: export.mjs --page <index.html> --out <dir> [--check] [--expect-blocks <n>] [--expect-presets <n>] [--expect-tests <n>]');
  return options;
};

const read = path => {
  try {
    return readFileSync(path);
  } catch {
    return fail(`cannot read ${path}`);
  }
};

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

/** The line index of a marker that must stand on a line of its own. */
const markerLine = (lines, marker, from = 0) => {
  const index = lines.findIndex((line, at) => at >= from && line.trim() === marker);
  return index >= 0 ? index : fail(`cut marker '${marker}' not found`);
};

/**
 * The engine region as a script: the CPU declarations on the first line, the region after it with
 * those lines left empty, so line n of the script is line n + offset of the page.
 */
export const engineScript = lines => {
  const start = markerLine(lines, START_MARKER);
  const end = markerLine(lines, END_MARKER, start + 1);
  const region = lines.slice(start, end);
  const declaration = /^const CPU_\w+ = .*;\s*$/;
  const moved = region.filter(line => declaration.test(line));
  if (moved.length !== CPU_DECLARATIONS)
    fail(`expected ${CPU_DECLARATIONS} CPU declarations, found ${moved.length}`);
  const code = [moved.map(line => line.trim()).join(' '), ...region.map(line => declaration.test(line) ? '' : line)].join('\n');
  // The script's line 2 is the page's line start + 1 (both 1-based).
  return { code, lineOffset: start - 1, start, end };
};

/** Runs the engine region in a fresh context and returns the context. */
const loadEngine = (lines, script) => {
  const quiet = () => {};
  const context = createContext({ console: { log: quiet, warn: quiet, error: quiet, info: quiet } });
  new Script(script.code, { filename: PAGE_NAME, lineOffset: script.lineOffset }).runInContext(context);
  return context;
};

/** The frames of a stack trace that lie in the page: function name and page line. */
const pageFrames = stack => [...stack.matchAll(/at (?:(\S+) \()?BitPerm\/index\.html:(\d+):\d+\)?/g)]
  .map(match => ({ name: match[1] ?? '', line: Number(match[2]) }));

/** The `test(...)` calls of the page's test suite, in source order, with the section heading before each. */
export const extractTests = lines => {
  const suite = lines.findIndex(line => /^const runBuildingBlockTests = \(\) => \{\s*$/.test(line));
  if (suite < 0)
    fail("'const runBuildingBlockTests' not found");
  const tests = [];
  let section = null;
  let defaultCosts = null;
  for (let index = suite + 1; index < lines.length && !/^\};\s*$/.test(lines[index]); ++index) {
    const line = lines[index];
    const heading = /^\s*console\.log\('(?:\\n)*--- (.+) ---'\);\s*$/.exec(line);
    if (heading) {
      section = heading[1];
      continue;
    }
    const costs = /^\s*const defaultCosts = (\{.*\});\s*$/.exec(line);
    if (costs) {
      defaultCosts = runInNewContext(`(${costs[1]})`);
      continue;
    }
    const call = /^(\s*)test\(('(?:[^'\\]|\\.)*'), \(\) => \{\s*$/.exec(line);
    if (!call)
      continue;
    const close = `${call[1]}});`;
    let last = index + 1;
    while (last < lines.length && lines[last].trimEnd() !== close)
      ++last;
    if (last === lines.length)
      fail(`the test at line ${index + 1} is not closed`);
    tests.push({
      index: tests.length + 1, section, name: runInNewContext(call[2]), sourceLine: index + 1,
      endLine: last + 1, body: lines.slice(index + 1, last).join('\n')
    });
    index = last;
  }
  if (defaultCosts === null)
    fail("'const defaultCosts' not found in the test suite");
  return { tests, defaultCosts };
};

/** A name for a file: lower case, words joined by '-'. */
const slug = text => text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

/** A mapping entry of a route map entry: the source bit, or nothing routed. */
const routeEntry = source => source >= 0 ? { type: 'src', val: source, inv: false } : { type: 'empty' };

/**
 * Runs the page's test suite with Array instrumented and keeps the route maps it builds: every array
 * created by a `= Array(n).fill(-1);` line, as it stands when the suite has finished. A map built by
 * makeRouteMap is attributed to the line that called makeRouteMap. Equal maps are kept once.
 */
const extractRouteMaps = (lines, context, tests) => {
  const intrinsic = runInContext('Array', context);
  const created = [];
  context.Array = new Proxy(intrinsic, {
    apply(target, self, args) {
      const array = Reflect.apply(target, self, args);
      created.push({ array, stack: new Error().stack });
      return array;
    }
  });
  try {
    runInContext('runBuildingBlockTests()', context);
  } finally {
    context.Array = intrinsic;
  }

  const routeLine = /= Array\(\w+\)\.fill\(-1\);/;
  const maps = [];
  const seen = new Set();
  for (const { array, stack } of created) {
    const frames = pageFrames(stack);
    if (frames.length === 0 || !routeLine.test(lines[frames[0].line - 1]))
      continue;
    const line = frames[0].name === 'makeRouteMap' ? frames[1].line : frames[0].line;
    const test = tests.find(candidate => candidate.sourceLine < line && line < candidate.endLine);
    if (!test)
      continue;
    const mapping = Array.from(array, routeEntry);
    const key = JSON.stringify(mapping);
    if (seen.has(key))
      continue;
    seen.add(key);
    const name = `test-${String(test.index).padStart(2, '0')}-${slug(test.name)}`;
    maps.push({ name, source: { line }, width: mapping.length, mapping });
  }
  return maps;
};

/**
 * The preset IDs in the order the page writes them, each with the comment written directly above it.
 * Enumerating PRESETS would put '6502' first, as an integer-like key, so the order is read from the
 * `[CPU_…]:` lines of the PRESETS literal. The comment is the run of `//` lines immediately preceding
 * the key line, without the slashes and joined by '; ': the page's citation of the preset's costs.
 */
const presetOrder = (lines, context, presets) => {
  const start = lines.findIndex(line => /^const PRESETS = \{\s*$/.test(line));
  if (start < 0)
    fail("'const PRESETS' not found");
  const entries = [];
  for (let index = start + 1; index < lines.length && !/^\};\s*$/.test(lines[index]); ++index) {
    const key = /^\s*\[(CPU_\w+)\]:/.exec(lines[index]);
    if (!key)
      continue;
    const comment = [];
    for (let above = index - 1; above > start && /^\s*\/\//.test(lines[above]); --above)
      comment.unshift(lines[above].trim().replace(/^\/\/\s*/, ''));
    entries.push({ id: runInContext(key[1], context), comment: comment.join('; ') });
  }
  const listed = new Set(entries.map(entry => entry.id));
  if (entries.length !== listed.size || Object.keys(presets).some(id => !listed.has(id)))
    fail('the [CPU_…] keys of PRESETS do not match its entries');
  return entries;
};

/** The quick patterns of the page's mapping editor, applied at every width a preset allows. */
const extractPatterns = (lines, context, widths) => {
  const patterns = [];
  lines.forEach((line, index) => {
    const match = /^\s*case (PATTERN_\w+): newMap = newMap\.map\((.*)\); break;\s*$/.exec(line);
    if (match)
      patterns.push({ name: runInContext(match[1], context), line: index + 1, apply: runInContext(`bitWidth => Array(bitWidth).fill(null).map(${match[2]})`, context) });
  });
  if (patterns.length === 0)
    fail('no quick pattern found in the page');
  return patterns.flatMap(pattern => widths.map(width => ({
    name: `pattern-${pattern.name}-${width}`, source: { line: pattern.line }, width, mapping: pattern.apply(width)
  })));
};

/** The page's raw evaluation of a mapping (its LUT loop), widened to any width. */
export const evaluate = (mapping, x) => {
  let result = 0n;
  mapping.forEach((entry, destination) => {
    const at = BigInt(destination);
    if (entry.type === 'src') {
      let bit = (x >> BigInt(entry.val)) & 1n;
      if (entry.inv)
        bit ^= 1n;
      result |= bit << at;
    } else if (entry.type === 'const') {
      result |= BigInt(entry.val) << at;
    }
  });
  return result;
};

/** The bits of a mapping that are routed or constant: everything but `empty`. */
export const maskOf = mapping => mapping.reduce((mask, entry, destination) => entry.type === 'empty' ? mask : mask | (1n << BigInt(destination)), 0n);

/** xorshift64 (13, 7, 17) from a seed. */
export function* xorshift64(seed) {
  const limit = (1n << 64n) - 1n;
  let state = seed;
  for (;;) {
    state ^= (state << 13n) & limit;
    state ^= state >> 7n;
    state ^= (state << 17n) & limit;
    yield state;
  }
}

/**
 * The inputs of a width: all of them up to 8 bits; otherwise 0, all ones, every single bit, every
 * single bit clear and 256 draws of xorshift64, each draw as many 64-bit words as the width needs,
 * low word first, truncated to the width.
 */
export const inputsOf = width => {
  const w = BigInt(width);
  const ones = (1n << w) - 1n;
  if (width <= 8)
    return Array.from({ length: 1 << width }, (_, x) => BigInt(x));
  const inputs = [0n, ones];
  for (let bit = 0n; bit < w; ++bit)
    inputs.push(1n << bit);
  for (let bit = 0n; bit < w; ++bit)
    inputs.push(ones ^ (1n << bit));
  const random = xorshift64(SEED);
  for (let index = 0; index < RANDOM_INPUTS; ++index) {
    let value = 0n;
    for (let at = 0n; at < w; at += 64n)
      value |= random.next().value << at;
    inputs.push(value & ones);
  }
  return inputs;
};

const hex = (value, width) => '0x' + value.toString(16).toUpperCase().padStart(Math.ceil(width / 4), '0');

/** The vectors file of a mapping. */
export const vectorsOf = mapping => {
  const mask = hex(maskOf(mapping.mapping), mapping.width);
  return {
    mapping: mapping.name, width: mapping.width,
    vectors: inputsOf(mapping.width).map(x => ({ input: hex(x, mapping.width), output: hex(evaluate(mapping.mapping, x), mapping.width), mask }))
  };
};

/**
 * JSON with a two-space indent, keys in insertion order, a BigInt as a "0x…" string and an undefined
 * field left out. An object or array of nothing but scalars stands on one line (a mapping entry, a
 * vector, a cost table) unless it is the whole file or a record of a file that is an array.
 */
export const serialize = value => {
  const scalar = item => item === null || typeof item !== 'object';
  const topArray = Array.isArray(value);
  const write = (item, indent, depth) => {
    if (typeof item === 'bigint')
      return JSON.stringify('0x' + item.toString(16).toUpperCase());
    if (scalar(item))
      return JSON.stringify(item);
    const array = Array.isArray(item);
    const entries = array ? item.map(element => [null, element]) : Object.entries(item).filter(([, field]) => field !== undefined && typeof field !== 'function');
    const [open, close] = array ? ['[', ']'] : ['{', '}'];
    if (entries.length === 0)
      return open + close;
    const show = ([key, field], inner, level) => (array ? '' : JSON.stringify(key) + ': ') + write(field, inner, level);
    const record = depth === 0 || (depth === 1 && topArray);
    if (!record && entries.every(([, field]) => scalar(field) || typeof field === 'bigint'))
      return open + entries.map(entry => show(entry, '', depth + 1)).join(', ') + close;
    const inner = indent + '  ';
    return open + '\n' + entries.map(entry => inner + show(entry, inner, depth + 1)).join(',\n') + '\n' + indent + close;
  };
  return write(value, '', 0) + '\n';
};

/** The repository, commit and path of a page in the corpus layout `…/<owner>/<repo>@<commit>/<path>`. */
export const pinOf = pagePath => {
  const match = /(?:^|\/)([^/]+)\/([^/@]+)@([0-9a-f]{40})\/(.+)$/.exec(pagePath.split(sep).join('/'));
  return match ? { repository: `${match[1]}/${match[2]}`, commit: match[3], path: match[4] }
    : fail(`cannot derive the pin from ${pagePath}: expected a corpus path <owner>/<repo>@<commit>/<path>`);
};

/** The folder of the pinned checkout a page lies in: its path without the page's path in the repository. */
const pinRoot = (pagePath, pin) => pagePath.slice(0, pagePath.length - pin.path.length);

/**
 * The attribution the licence asks for: where the exported data comes from, the hashes of the files
 * it was read from, and the title of the repository's LICENSE at the pin with that file's hash.
 */
export const notice = (pin, sources, licence) => {
  const width = Math.max(...sources.map(([path]) => path.length));
  const title = licence.toString('utf8').split(/\r?\n/).map(line => line.trim()).filter(line => line.length > 0).slice(0, 2);
  return [
    'The files of this folder are exported by tooling/bitperm/export.mjs from BitPerm, part of',
    `${pin.repository} (https://github.com/${pin.repository}), commit ${pin.commit}:`,
    '',
    ...sources.map(([path, bytes]) => `  ${path.padEnd(width)}  sha256 ${sha256(bytes)}`),
    '',
    'They are data read from that page: the building-block registry, the CPU presets, the solver limits,',
    'the test cases, the mappings of the quick patterns and of the route maps the tests build, and raw',
    'evaluation vectors of those mappings. BitPerm is licensed under the LICENSE of the repository root',
    `at that commit (sha256 ${sha256(licence)}):`,
    '',
    ...title.map(line => `  ${line}`),
    ''
  ].join('\n');
};

/** Every fixture file the page yields, by its path relative to the output directory. */
export const exportPage = (pagePath, expect = {}) => {
  const pageBytes = read(pagePath);
  const lines = pageBytes.toString('utf8').split(/\r?\n/);
  const script = engineScript(lines);
  const context = loadEngine(lines, script);
  const live = runInContext('({ BUILDING_BLOCKS, PRESETS, SEARCH_MAX_STATES, SEARCH_TIMEOUT_MS, MAX_SOLVER_ITERATIONS, DEFAULT_BIT_WIDTH })', context);

  const blocks = Object.entries(live.BUILDING_BLOCKS).map(([, block]) => ({
    id: block.id, name: block.name, category: block.category, description: block.description,
    costKey: block.costKey, enableKey: block.enableKey, platforms: block.platforms === undefined ? null : [...block.platforms],
    hasCanRoute: typeof block.canRoute === 'function', hasFindRoutings: typeof block.findRoutings === 'function'
  }));
  const presets = presetOrder(lines, context, live.PRESETS).map(({ id, comment }) => ({
    id, name: live.PRESETS[id].name, comment, allowedWidths: live.PRESETS[id].allowedWidths, costs: live.PRESETS[id].costs, enables: live.PRESETS[id].enables
  }));
  const { tests, defaultCosts } = extractTests(lines);
  for (const [what, actual] of [['blocks', blocks.length], ['presets', presets.length], ['tests', tests.length]])
    if (expect[what] !== undefined && expect[what] !== actual)
      fail(`expected ${expect[what]} ${what}, found ${actual}`);

  const widths = [...new Set(presets.flatMap(preset => preset.allowedWidths))].sort((a, b) => a - b);
  const mappings = [...extractPatterns(lines, context, widths), ...extractRouteMaps(lines, context, tests)];

  const pin = pinOf(pagePath);
  const readmeBytes = read(join(dirname(pagePath), 'README.md'));
  const readmePath = pin.path.includes('/') ? pin.path.slice(0, pin.path.lastIndexOf('/') + 1) + 'README.md' : 'README.md';
  const files = new Map();
  files.set('pin.json', serialize({
    repository: pin.repository, commit: pin.commit,
    files: [{ path: pin.path, sha256: sha256(pageBytes) }, { path: readmePath, sha256: sha256(readmeBytes) }]
  }));
  files.set(NOTICE, notice(pin, [[pin.path, pageBytes], [readmePath, readmeBytes]], read(join(pinRoot(pagePath, pin), 'LICENSE'))));
  files.set('blocks.json', serialize(blocks));
  files.set('presets.json', serialize(presets));
  files.set('limits.json', serialize({
    SEARCH_MAX_STATES: live.SEARCH_MAX_STATES, SEARCH_TIMEOUT_MS: live.SEARCH_TIMEOUT_MS,
    MAX_SOLVER_ITERATIONS: live.MAX_SOLVER_ITERATIONS, DEFAULT_BIT_WIDTH: live.DEFAULT_BIT_WIDTH, defaultCosts
  }));
  files.set('tests.json', serialize(tests.map(({ endLine, ...test }) => test)));
  for (const mapping of mappings) {
    files.set(`mappings/${mapping.name}.json`, serialize(mapping));
    files.set(`vectors/${mapping.name}.json`, serialize(vectorsOf(mapping)));
  }
  const sections = new Set(tests.map(test => test.section)).size;
  const summary = `${blocks.length} blocks, ${presets.length} presets, ${tests.length} tests in ${sections} sections, ${mappings.length} mappings, ${mappings.length} vector files`;
  return { files, summary };
};

/** Every file below a directory, relative to it and '/'-separated. */
const filesBelow = directory => existsSync(directory)
  ? readdirSync(directory, { recursive: true, withFileTypes: true }).filter(entry => entry.isFile())
    .map(entry => relative(directory, join(entry.parentPath, entry.name)).split(sep).join('/'))
  : [];

/** The differences between the files of an export and a directory; empty when they agree. */
export const compare = (files, out) => {
  const shown = path => `${out.replace(/[\\/]+$/, '')}/${path}`;
  const problems = [];
  for (const [path, content] of files) {
    const full = join(out, path);
    if (!existsSync(full))
      problems.push(`${shown(path)} is missing`);
    // A checkout that turns LF into CRLF (Windows) holds the same fixture.
    else if (readFileSync(full, 'utf8').replace(/\r\n/g, '\n') !== content)
      problems.push(`${shown(path)} differs from the pinned page`);
  }
  for (const path of filesBelow(out).sort())
    if (!files.has(path))
      problems.push(`${shown(path)} is not produced by the pinned page`);
  return problems;
};

/**
 * Writes the files of an export and removes the mapping and vector files it no longer produces; no
 * other file is touched, so a wrong --out cannot empty a folder.
 */
const write = (files, out) => {
  for (const path of filesBelow(out))
    if (!files.has(path) && /^(mappings|vectors)\/[^/]+\.json$/.test(path))
      rmSync(join(out, path));
  for (const [path, content] of files) {
    const full = join(out, path);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, content);
  }
};

/** Runs the exporter; returns the exit code. */
export const main = (argv, stdout = process.stdout, stderr = process.stderr) => {
  try {
    const options = parseArguments(argv);
    const { files, summary } = exportPage(options.page, options.expect);
    if (options.check) {
      const problems = compare(files, options.out);
      if (problems.length > 0) {
        stderr.write(problems.map(problem => `bitperm export: ${problem}\n`).join(''));
        return 1;
      }
    } else {
      write(files, options.out);
    }
    stdout.write(`bitperm export: ${summary} ${options.check ? 'checked' : 'written'}\n`);
    return 0;
  } catch (error) {
    if (!(error instanceof ExportError))
      throw error;
    stderr.write(`bitperm export: ${error.message}\n`);
    return 1;
  }
};

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  process.exitCode = main(process.argv.slice(2));
