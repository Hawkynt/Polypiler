"""A module the modules fixture imports: a constant, a function with a default and a class. Written for this repository."""

OFFSET = 7


def scale(x: int, factor: int = 2) -> int:
    return x * factor


class Limits:
    def __init__(self, low: int):
        self.low = low
        self.high = low + 10
