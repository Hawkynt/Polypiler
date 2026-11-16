"""Runs calls of a fixture's functions in CPython and prints what each one did: the oracle of the Python frontend.

Standard input is one JSON object: "module", the path of the fixture, and "calls", a list of [name, arguments]
with every argument {"int": "<digits>"}, {"float": "<float.hex()>"} or {"bool": true|false}. One line is
printed per call: "int <digits>", "float <repr>", "bool True|False", "None", or "raise <exception class>".
"""

import importlib.util
import json
import os
import sys


def value(argument):
    if "int" in argument:
        return int(argument["int"])
    if "float" in argument:
        return float.fromhex(argument["float"])
    return bool(argument["bool"])


def outcome(result):
    if result is None:
        return "None"
    if isinstance(result, bool):
        return "bool " + repr(result)
    if isinstance(result, int):
        return "int " + str(result)
    if isinstance(result, float):
        return "float " + repr(result)
    return type(result).__name__ + " " + repr(result)


def main():
    request = json.loads(sys.stdin.read())
    # The modules a fixture imports stand beside it.
    sys.path.insert(0, os.path.dirname(os.path.abspath(request["module"])))
    spec = importlib.util.spec_from_file_location("fixture", request["module"])
    module = importlib.util.module_from_spec(spec)
    # A dataclass of a module with postponed annotations looks its module up by name, as an import registers it.
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    for name, arguments in request["calls"]:
        try:
            line = outcome(getattr(module, name)(*[value(argument) for argument in arguments]))
        except Exception as error:
            line = "raise " + type(error).__name__
        sys.stdout.write(line + "\n")


if __name__ == "__main__":
    main()
