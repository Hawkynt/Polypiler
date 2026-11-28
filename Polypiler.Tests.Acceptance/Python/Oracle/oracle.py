"""The differential oracle of the Python frontend's own tokenizer, parser and compile-time checks.

For each request on standard input it reports what CPython itself decides of a source file, one JSON
object per line. "P <minor> <path>" asks ast.parse(bytes, feature_version=(3, minor)) whether the
file parses and, for the newest version, compile(bytes, path, "exec") whether it compiles, with the
digest of the tree; "C <minor> <path>" does the same for the file's text given as a str, compiling at
every version; "T <minor> <path>" asks for the tree itself; "S <minor> <path>" asks of every str
constant of the file, each given as a source of its own. The tree is written as the frontend's
PyDump writes its own: the fields and positions of ast.dump(tree, include_attributes=True), every
value spelled so that both sides agree. Nothing is imported or run.
"""

import ast
import hashlib
import json
import sys
import warnings

POSITIONS = ("lineno", "col_offset", "end_lineno", "end_col_offset")


def text(value):
    if any(0xD800 <= ord(c) <= 0xDFFF for c in value):
        return "cp:[" + ",".join(str(ord(c)) for c in value) + "]"
    return json.dumps(value, ensure_ascii=True)


def write(out, value):
    if value is None:
        out.append("None")
    elif isinstance(value, ast.AST):
        out.append(type(value).__name__ + "(")
        first = True
        for name in value._fields:
            if not first:
                out.append(",")
            first = False
            out.append(name + "=")
            write(out, getattr(value, name, None))
        if all(hasattr(value, a) for a in POSITIONS) and value.lineno is not None:
            out.append("@%d:%d:%d:%d" % tuple(getattr(value, a) for a in POSITIONS))
        out.append(")")
    elif isinstance(value, list):
        out.append("[")
        for i, item in enumerate(value):
            if i:
                out.append(",")
            write(out, item)
        out.append("]")
    elif isinstance(value, bool):
        out.append("True" if value else "False")
    elif isinstance(value, int):
        out.append(str(value))
    elif isinstance(value, float):
        out.append(value.hex())
    elif isinstance(value, complex):
        out.append("complex(%s,%s)" % (value.real.hex(), value.imag.hex()))
    elif isinstance(value, str):
        out.append(text(value))
    elif isinstance(value, bytes):
        out.append("b:" + value.hex())
    elif value is Ellipsis:
        out.append("...")
    else:
        raise TypeError(type(value).__name__)


def canonical(tree):
    out = []
    write(out, tree)
    return "".join(out)


def error(exception):
    return "%s: %s" % (type(exception).__name__, getattr(exception, "msg", None) or str(exception))


def verdict(source, path, minor, newest, kind):
    result = {"minor": minor}
    try:
        tree = ast.parse(source, path, feature_version=(3, minor))
        result["parsed"] = True
    except (SyntaxError, ValueError, MemoryError, RecursionError, UnicodeError) as exception:
        tree = None
        result["parsed"] = False
        result["error"] = error(exception)
    if tree is not None:
        dump = canonical(tree)
        if kind == "T":
            result["tree"] = dump
        result["digest"] = hashlib.sha256(dump.encode("utf-8")).hexdigest()
        if minor == newest or kind == "C":
            try:
                compile(source, path, "exec", dont_inherit=True)
                result["compiled"] = True
            except (SyntaxError, ValueError, MemoryError, RecursionError, UnicodeError) as exception:
                result["compiled"] = False
                result["error"] = error(exception)
    return result


def snippets(path):
    """The str constants of a file that are no docstrings: the sources CPython's tests compile."""
    try:
        with open(path, "rb") as f:
            tree = ast.parse(f.read(), path)
    except (SyntaxError, ValueError, MemoryError, RecursionError, UnicodeError):
        return []
    found = []
    seen = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str) and node.value not in seen:
            value = node.value
            if any(0xD800 <= ord(c) <= 0xDFFF for c in value) or "\0" in value:
                continue
            seen.add(value)
            found.append(value)
    return found


def main():
    sys.setrecursionlimit(100000)
    warnings.simplefilter("ignore")
    newest = sys.version_info[1]
    for line in sys.stdin:
        kind, minor, path = line.rstrip("\n").split(" ", 2)
        minor = int(minor)
        if kind == "S":
            # Every str constant of the file as a source of its own, given to ast.parse as a str.
            for source in snippets(path):
                result = verdict(source, "<snippet>", minor, newest, kind)
                result["path"] = path
                result["source"] = source
                sys.stdout.write(json.dumps(result) + "\n")
            sys.stdout.flush()
            continue
        with open(path, "rb") as f:
            source = f.read()
        if kind == "C":
            # A case: the file's UTF-8 text given as a str, whose encoding declaration CPython ignores.
            source = source.decode("utf-8")
        result = verdict(source, path, minor, newest, kind)
        result["path"] = path
        sys.stdout.write(json.dumps(result) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
