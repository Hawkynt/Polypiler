// Derives BitPerm's route map of every exported mapping with the page's own code (issue #268): the
// solver of the pinned page turns its mapping state into `routeMap[dest]`, the source bit of each
// output or -1, before it searches. That derivation is cut from the page and run in node over the files
// of tests/fixtures/bitperm/mappings; the result is what Polypiler.Permutation's BitMapping.ToRouteMap
// must give for the same files.
//
//   node tooling/bitperm/routemap.mjs --page <index.html> --mappings <dir> [--check <file>]
//
// The cut runs from the line `const w = bitWidth;` of `const solvePermutation = useMemo(` up to the
// line `});` that closes `const routeMap = mapping.map(`; it runs in a context that holds the page's
// engine region, which defines the TYPE_ and BIT_ constants it reads. Without --check the route maps
// are written to standard output; with --check they are compared with the file and a difference fails
// the run.

import { readdirSync, readFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { Script, createContext } from 'node:vm';
import { engineScript, serialize } from './export.mjs';

const PAGE_NAME = 'BitPerm/index.html';
const SOLVER = /^\s*const solvePermutation = useMemo\(\(\) => \{\s*$/;
const START = 'const w = bitWidth;';
const ROUTE_MAP = /^\s*const routeMap = mapping\.map\(/;
const END = '});';

class RouteMapError extends Error {}

const fail = reason => { throw new RouteMapError(reason); };

/** The command line as an object; an unknown or incomplete option is an error. */
export const parseArguments = argv => {
  const options = {};
  const value = index => argv[index + 1] ?? fail(`${argv[index]} needs a value`);
  for (let index = 0; index < argv.length; ++index) {
    switch (argv[index]) {
      case '--page': options.page = value(index++); break;
      case '--mappings': options.mappings = value(index++); break;
      case '--check': options.check = value(index++); break;
      default: fail(`unknown argument '${argv[index]}'`);
    }
  }
  if (options.page === undefined || options.mappings === undefined)
    fail('usage: routemap.mjs --page <index.html> --mappings <dir> [--check <file>]');
  return options;
};

const read = path => {
  try {
    return readFileSync(path, 'utf8');
  } catch {
    return fail(`cannot read ${path}`);
  }
};

/** The page's route-map derivation as the source of a function `(mapping, bitWidth) => routeMap`, with its first page line. */
export const routeMapSource = lines => {
  const solver = lines.findIndex(line => SOLVER.test(line));
  if (solver < 0)
    fail("'const solvePermutation = useMemo' not found");
  const start = lines.findIndex((line, at) => at > solver && line.trim() === START);
  if (start < 0)
    fail(`'${START}' not found in the solver`);
  const routeMap = lines.findIndex((line, at) => at > start && ROUTE_MAP.test(line));
  if (routeMap < 0)
    fail("'const routeMap = mapping.map(' not found in the solver");
  const end = lines.findIndex((line, at) => at > routeMap && line.trim() === END);
  if (end < 0)
    fail("the end of 'const routeMap = mapping.map(' not found");
  return { code: ['(mapping, bitWidth) => {', ...lines.slice(start, end + 1), 'return routeMap; }'].join('\n'), line: start + 1 };
};

/** The page's derivation as a function, run next to the engine region in a fresh context. */
export const loadDerivation = lines => {
  const engine = engineScript(lines);
  const quiet = () => {};
  const context = createContext({ console: { log: quiet, warn: quiet, error: quiet, info: quiet } });
  new Script(engine.code, { filename: PAGE_NAME, lineOffset: engine.lineOffset }).runInContext(context);
  const derivation = routeMapSource(lines);
  // The wrapper's first line stands one line before the cut.
  return new Script(derivation.code, { filename: PAGE_NAME, lineOffset: derivation.line - 2 }).runInContext(context);
};

/** The route map of every mapping file of a directory, by file name without extension, in name order. */
export const routeMaps = (pagePath, directory) => {
  const derive = loadDerivation(read(pagePath).split(/\r?\n/));
  const names = readdirSync(directory).filter(name => name.endsWith('.json')).sort();
  const maps = {};
  for (const name of names) {
    const fixture = JSON.parse(read(join(directory, name)));
    maps[basename(name, '.json')] = derive(fixture.mapping, fixture.width);
  }
  return maps;
};

/** The file the route maps are kept in: where they come from and the maps themselves. */
export const document = maps => serialize({
  producedBy: 'node tooling/bitperm/routemap.mjs --page <pinned BitPerm/index.html> --mappings tests/fixtures/bitperm/mappings',
  routeMaps: maps
});

const main = argv => {
  const options = parseArguments(argv);
  const text = document(routeMaps(options.page, options.mappings));
  if (options.check === undefined) {
    process.stdout.write(text);
    return 0;
  }
  if (read(options.check).replace(/\r\n/g, '\n') !== text) {
    process.stderr.write(`bitperm routemap: ${options.check} differs from the pinned page\n`);
    return 1;
  }
  process.stdout.write(`${Object.keys(JSON.parse(text).routeMaps).length} route maps agree with ${options.check}\n`);
  return 0;
};

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    if (!(error instanceof RouteMapError) && !(error?.constructor?.name === 'ExportError'))
      throw error;
    process.stderr.write(`bitperm routemap: ${error.message}\n`);
    process.exitCode = 1;
  }
}
