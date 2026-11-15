"""Parses Python source with the standard ast module and prints the tree as JSON.

The source is read from standard input as UTF-8 and only parsed: it is never compiled, imported or
executed. Wire format 1: one JSON object with "format", "python" and either "module" (the tree) or
"error" (the syntax error). A node is an object whose "_" names its class; "line", "col", "endLine"
and "endCol" give its range, columns counted in characters from 1. Every field of the node follows
under its own name. A constant carries "kind" (int, float, bool, none, str, bytes, complex,
ellipsis) and "value" as text: an int in decimal digits of any size, a float as float.hex(). A str
that holds a surrogate code point carries "codePoints", every code point in order, and its "value" has
U+FFFD in the place of each surrogate, which not every JSON reader accepts alone; one that holds a lone
high surrogate right before a lone low one also carries "surrogatePair": true.
"""

import ast
import json
import sys

FORMAT = 1

# The nodes whose "value" is a constant; match statements arrived with Python 3.10.
CONSTANTS = (ast.Constant, getattr(ast, "MatchSingleton", ast.Constant))


def column(lines, line, offset):
    if line is None or offset is None:
        return None
    if 1 <= line <= len(lines):
        return len(lines[line - 1][:offset].decode("utf-8", "replace")) + 1
    return offset + 1


def constant(value):
    if value is None:
        return "none", None
    if isinstance(value, bool):
        return "bool", "True" if value else "False"
    if isinstance(value, int):
        return "int", str(value)
    if isinstance(value, float):
        return "float", value.hex()
    if isinstance(value, complex):
        return "complex", repr(value)
    if isinstance(value, str):
        return "str", value
    if isinstance(value, bytes):
        return "bytes", value.hex()
    return "ellipsis", None


def surrogate(character):
    return 0xD800 <= ord(character) <= 0xDFFF


def surrogate_pair(text):
    return any(0xD800 <= ord(high) <= 0xDBFF and 0xDC00 <= ord(low) <= 0xDFFF for high, low in zip(text, text[1:]))


def node(value, lines):
    if isinstance(value, list):
        return [node(item, lines) for item in value]
    if not isinstance(value, ast.AST):
        return value
    result = {"_": type(value).__name__}
    if hasattr(value, "lineno"):
        result["line"] = value.lineno
        result["col"] = column(lines, value.lineno, value.col_offset)
        result["endLine"] = value.end_lineno
        result["endCol"] = column(lines, value.end_lineno, value.end_col_offset)
    for name, field in ast.iter_fields(value):
        if isinstance(value, CONSTANTS) and name == "value":
            result["kind"], result["value"] = constant(field)
            if isinstance(field, str) and any(surrogate(character) for character in field):
                result["value"] = "".join("\ufffd" if surrogate(character) else character for character in field)
                result["codePoints"] = [ord(character) for character in field]
                if surrogate_pair(field):
                    result["surrogatePair"] = True
        elif name not in ("type_comment", "kind"):
            result[name] = node(field, lines)
    return result


def main():
    data = sys.stdin.buffer.read()
    filename = sys.argv[1] if len(sys.argv) > 1 else "<source>"
    version = ".".join(str(part) for part in sys.version_info[:3])
    output = {"format": FORMAT, "python": version}
    info = sys.float_info
    if info.mant_dig != 53 or info.max_exp != 1024 or info.radix != 2:
        output["error"] = {"type": "SystemError", "msg": "float is not IEEE 754 binary64", "line": 1, "col": 1}
    else:
        try:
            tree = ast.parse(data, filename=filename)
            output["module"] = node(tree, data.split(b"\n"))
        except SyntaxError as error:
            output["error"] = {
                "type": type(error).__name__,
                "msg": error.msg,
                "line": error.lineno or 1,
                "col": error.offset or 1,
            }
        except ValueError as error:
            output["error"] = {"type": "ValueError", "msg": str(error), "line": 1, "col": 1}
    sys.stdout.write(json.dumps(output, separators=(",", ":"), ensure_ascii=True))
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
