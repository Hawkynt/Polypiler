"""An encoder of value images written apart from Polypiler's codec, on Python's struct and
int.to_bytes, to check tests/fixtures/verify/images/conformance.json independently.

Usage: python3 encode.py <conformance.json>
Prints one line per row, '<name> ok' or '<name> expected <hex> encoded <hex>', and exits 1 when a
row differs."""

import json
import struct
import sys

FIXED = {"u8": "<B", "u16": "<H", "u32": "<I", "u64": "<Q", "i8": "<b", "i16": "<h", "i32": "<i", "i64": "<q", "char16": "<H"}


class Reader:
    def __init__(self, text):
        self.text = text.replace(" ", "")
        self.at = 0

    def name(self):
        start = self.at
        while self.at < len(self.text) and (self.text[self.at].isalnum() or self.text[self.at] == "_"):
            self.at += 1
        return self.text[start:self.at]

    def take(self, char):
        assert self.text[self.at] == char, f"{char!r} expected at {self.at} in {self.text!r}"
        self.at += 1

    def more(self, separator):
        if self.text[self.at] == separator:
            self.at += 1
            return True
        return False

    def type(self):
        name = self.name()
        if name in ("seq", "opt", "enum"):
            self.take("<")
            inner = self.type()
            self.take(">")
            return (name, inner)
        if name in ("tuple", "union"):
            self.take("<")
            items = [self.type()]
            while self.more("," if name == "tuple" else "|"):
                items.append(self.type())
            self.take(">")
            return (name, items)
        if name == "record":
            self.take("{")
            fields = []
            while True:
                field = self.name()
                self.take(":")
                fields.append((field, self.type()))
                if not self.more(","):
                    break
            self.take("}")
            return ("record", fields)
        return (name,)


def counted(data):
    return struct.pack("<I", len(data)) + data


def integer(value):
    return int(value)


def encode(t, v):
    kind = t[0]
    if kind in FIXED:
        return struct.pack(FIXED[kind], integer(v))
    if kind == "enum":
        return encode(t[1], v)
    if kind == "bool":
        return struct.pack("<?", v)
    if kind in ("f32", "f64"):
        bits = int(v, 16)
        return struct.pack("<I" if kind == "f32" else "<Q", bits)
    if kind == "bigint":
        n = integer(v)
        if n == 0:
            return counted(b"")
        length = 1
        while not -(1 << (8 * length - 1)) <= n < (1 << (8 * length - 1)):
            length += 1
        return counted(n.to_bytes(length, "little", signed=True))
    if kind == "bytes":
        return counted(bytes.fromhex(v))
    if kind == "utf8":
        return counted(v.encode("utf-8"))
    if kind == "utf16":
        units = v.encode("utf-16-le", "surrogatepass")
        return struct.pack("<I", len(units) // 2) + units
    if kind == "seq":
        return struct.pack("<I", len(v)) + b"".join(encode(t[1], item) for item in v)
    if kind == "opt":
        return b"\x00" if v is None else b"\x01" + encode(t[1], v["some"])
    if kind == "record":
        return b"".join(encode(field_type, v[field]) for field, field_type in t[1])
    if kind == "tuple":
        return b"".join(encode(item_type, item) for item_type, item in zip(t[1], v))
    if kind == "union":
        alternative = t[1][v["tag"]]
        return bytes([v["tag"]]) + (b"" if alternative == ("null",) else encode(alternative, v["value"]))
    raise ValueError(f"unknown image type {kind!r}")


def main(path):
    with open(path, encoding="utf-8") as file:
        rows = json.load(file)["rows"]
    differences = 0
    for row in rows:
        encoded = encode(Reader(row["type"]).type(), row["value"]).hex()
        if encoded == row["hex"]:
            print(f"{row['name']} ok")
        else:
            differences += 1
            print(f"{row['name']} expected {row['hex']} encoded {encoded}")
    print(f"{len(rows)} rows, {differences} difference(s)")
    return 1 if differences else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
