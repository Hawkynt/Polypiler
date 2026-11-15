using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// #453: classes without bases, dataclasses, named tuples, enums, <c>None</c> and optionals, and
/// exceptions with <c>try</c> - the scenarios of <c>fe-py-types.feature</c>, the rows of the test
/// design the <c>types</c> fixture does not already hold to CPython, and every refusal with its code.
/// </summary>
[TestFixture]
public sealed class TypeLoweringTests {

  private const string Plane = "from dataclasses import dataclass\n\n@dataclass\nclass Plane:\n    width: int\n    height: int\n    pixels: bytes\n\n"
    + "    def at(self, x: int, y: int) -> int:\n        return self.pixels[y * self.width + x]\n\n"
    + "def f(x: int, y: int) -> int:\n    return Plane(3, 2, bytes([10, 20, 30, 40, 50, 60])).at(x, y)\n";

  /// <summary>A dataclass with a method is a class with a receiver function; its pixels are bytes, which an IR class holds, where a list is no field it can have.</summary>
  [Test]
  public void Dataclass_WithAMethod_IsAClassWithAReceiverFunction() {
    var module = PythonSnippet.Module(Plane);
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("f", [2L, 1L])), Is.EqualTo("int 60"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("f", [0L, 2L])), Is.EqualTo("raise IndexError"));
    Assert.That(module.Types.OfType<IrClassType>().Single(type => type.Name == "Plane").Fields.Select(field => field.Name), Is.EqualTo(new[] { "width", "height", "pixels" }));
    Assert.That(module.Functions.Select(function => function.Name), Does.Contain("Plane.at").And.Contain("Plane.__init__"));
  }

  [Test]
  public void ListAttribute_IsRefused()
    => Assert.That(PythonSnippet.Refusals(Plane.Replace("pixels: bytes", "pixels: list[int]", System.StringComparison.Ordinal).Replace("bytes([", "([", System.StringComparison.Ordinal)),
      Has.Some.Contains("error PLY1840: 'the attribute pixels of Plane of the type list[int]' is outside the first Python subset"));

  [TestCase("@dataclass\nclass P:\n    x: int\n    y: int\n", 1L, "bool True")]
  [TestCase("@dataclass\nclass P:\n    x: int\n    y: int\n", 3L, "bool False")]
  [TestCase("class P:\n    def __init__(self, x: int, y: int):\n        self.x = x\n        self.y = y\n", 1L, "bool False")]
  public void DataclassEquality_IsStructuralAndPlainClassEqualityIsIdentity(string definition, long a, string expected)
    => Assert.That(PythonSnippet.Run($"from dataclasses import dataclass\n\n{definition}\n\ndef f(a: int) -> bool:\n    return P(a, 2) == P(1, 2)\n", "f", a), Is.EqualTo(expected));

  [TestCase(1L, "int 20")]
  [TestCase(2L, "int 32")]
  public void Enum_IsAnEnumerationAndMatchSwitchesOnIt(long m, string expected)
    => Assert.That(PythonSnippet.Run("from enum import IntEnum\n\nclass Method(IntEnum):\n    YADIF = 0\n    BWDIF = 1\n    GREEDY_HM = 2\n\n"
      + "def f(m: Method) -> int:\n    match m:\n        case Method.YADIF:\n            return 10\n        case Method.BWDIF:\n            return 20\n        case _:\n            return 30 + m\n\n"
      + "def g(m: int) -> int:\n    return f(Method(m))\n", "g", m), Is.EqualTo(expected));

  [TestCase("-1 if a is None else a", true, "int -1")]
  [TestCase("-1 if a is None else a", false, "int 0")]
  [TestCase("a if a else -1", false, "int -1")]
  [TestCase("a + 1", true, "raise TypeError")]
  [TestCase("a + 1", false, "int 1")]
  public void None_IsAnExplicitAbsentValueDistinctFromZero(string expression, bool none, string expected)
    => Assert.That(PythonSnippet.Run($"def f(a: int | None) -> int:\n    return {expression}\n\ndef g(none: bool) -> int:\n    return f(None if none else 0)\n", "g", none), Is.EqualTo(expected));

  [TestCase(6L, 3L, "int 2")]
  [TestCase(1L, 0L, "int -1")]
  [TestCase(-1L, 1L, "raise ValueError")]
  public void Exceptions_AreRaisedAndCaughtByClassIncludingSubclasses(long a, long b, string expected)
    => Assert.That(PythonSnippet.Run("def f(a: int, b: int) -> int:\n    try:\n        if a < 0:\n            raise ValueError(\"negative\")\n        return a // b\n    except ArithmeticError:\n        return -1\n", "f", a, b),
      Is.EqualTo(expected));

  /// <summary>The optional hints in every spelling: <c>Optional[T]</c>, <c>T | None</c>, <c>None | T</c>, <c>Union[T, None]</c>, a forward reference.</summary>
  [TestCase("Optional[int]")]
  [TestCase("int | None")]
  [TestCase("None | int")]
  [TestCase("Union[int, None]")]
  [TestCase("typing.Optional[int]")]
  public void OptionalHints_AreOptionals(string hint)
    => Assert.That(PythonSnippet.Run($"import typing\nfrom typing import Optional, Union\n\ndef f(a: {hint}) -> int:\n    return 0 if a is None else a\n\ndef g(n: int) -> int:\n    return f(n) + f(None)\n", "g", 4L),
      Is.EqualTo("int 4"));

  [Test]
  public void OptionalObjects_AreReferencesThatMayBeNull() {
    const string source = "from typing import Optional\n\nclass Node:\n    def __init__(self, value: int, next: 'Optional[Node]'):\n        self.value = value\n        self.next = next\n\n"
      + "def f(n: int) -> int:\n    head: Optional[Node] = None\n    for i in range(n):\n        head = Node(i, head)\n    total = 0\n    while head is not None:\n        total = total * 10 + head.value\n        head = head.next\n    return total\n\n"
      + "def g(n: int) -> int:\n    node = Node(n, None)\n    return node.next.value\n";
    Assert.That(PythonSnippet.Run(source, "f", 4L), Is.EqualTo("int 3210"));
    Assert.That(PythonSnippet.Run(source, "g", 4L), Is.EqualTo("raise AttributeError"));
  }

  [Test]
  public void IntEnumAndEnum_CompareAsPythonCompares() {
    const string source = "from enum import Enum, IntEnum\n\nclass M(IntEnum):\n    A = 1\n    B = -2\n\nclass C(Enum):\n    X = 1\n\n"
      + "def f(n: int) -> int:\n    m = M(n)\n    return int(m == 1) + int(M.A < M.B) * 2 + int(C.X != 1) * 4 + (-m) * 10 + int(bool(m)) * 1000 + len(m.name) * 10000\n";
    Assert.That(PythonSnippet.Run(source, "f", 1L), Is.EqualTo("int 10995"));
    Assert.That(PythonSnippet.Run(source, "f", 7L), Is.EqualTo("raise ValueError"));
  }

  [Test]
  public void IsInstance_TestsClassesAndKnowsTheStaticTypes() {
    const string source = "class P:\n    pass\n\ndef f(n: int) -> int:\n    p = P()\n    try:\n        if n:\n            raise KeyError('k')\n        return int(isinstance(True, int)) + int(isinstance(1.5, (int, float))) * 2 + int(isinstance(p, P)) * 4 + int(isinstance('a', bytes)) * 8\n"
      + "    except Exception as e:\n        return 100 + int(isinstance(e, LookupError)) + int(isinstance(e, ValueError)) * 2\n";
    Assert.That(PythonSnippet.Run(source, "f", 0L), Is.EqualTo("int 7"));
    Assert.That(PythonSnippet.Run(source, "f", 1L), Is.EqualTo("int 101"));
  }

  [Test]
  public void ExceptionMessages_AreTheirStr() {
    const string source = "class Mine(LookupError):\n    pass\n\ndef f(n: int) -> str:\n    try:\n        if n == 1:\n            raise Mine('mine')\n        if n == 2:\n            raise LookupError('look')\n        raise AttributeError\n"
      + "    except (ValueError, LookupError) as e:\n        return str(e)\n    except BaseException:\n        return 'base'\n";
    Assert.That(PythonSnippet.Run(source, "f", 1L), Is.EqualTo("String mine"));
    Assert.That(PythonSnippet.Run(source, "f", 2L), Is.EqualTo("String look"));
    Assert.That(PythonSnippet.Run(source, "f", 3L), Is.EqualTo("String base"));
  }

  [Test]
  public void FinallyWithoutHandlers_RunsOnEveryExit() {
    const string source = "def f(n: int) -> int:\n    total = 0\n    while True:\n        try:\n            total += 1\n            if total > n:\n                break\n        finally:\n            total += 10\n    return total\n";
    Assert.That(PythonSnippet.Run(source, "f", 1L), Is.EqualTo("int 22"));
  }

  [Test]
  public void ClassesOfAnotherKindOfDataclass_KeepTheirFields() {
    const string source = "from dataclasses import dataclass\nimport dataclasses\n\n@dataclasses.dataclass(slots=True, repr=True)\nclass A:\n    a: int\n    b: float = 1.5\n    c: bool = True\n\n"
      + "def f(n: int) -> float:\n    x = A(n)\n    y = A(b=2.0, a=n, c=False)\n    return x.a + x.b + y.b + int(x.c) + int(y.c)\n";
    Assert.That(PythonSnippet.Run(source, "f", 1L), Is.EqualTo("float 5.5"));
  }

  [TestCase("class A:\n    pass\n\nclass B(A):\n    pass\n", "error PLY1840: 'the base class A of B' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #762")]
  [TestCase("from typing import Generic, TypeVar\nT = TypeVar('T')\n\nclass Box(Generic[T]):\n    pass\n", "error PLY1841: generic declaration 'Generic[T]' is not supported; generic classes and functions are imported by #472")]
  [TestCase("class Box[T]:\n    pass\n", "error PLY1841: generic declaration 'class Box[…]' is not supported")]
  [TestCase("class A:\n    def f(self) -> int:\n        return super().f()\n", "error PLY1840: 'super()' is outside the first Python subset")]
  [TestCase("class A:\n    @classmethod\n    def f(cls) -> int:\n        return 1\n", "error PLY1840: 'the decorator classmethod of A.f'")]
  [TestCase("class A:\n    def __getattr__(self, name: str) -> int:\n        return 1\n", "error PLY1840: 'the operator method __getattr__ of A' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #751")]
  [TestCase("class A:\n    def __add__(self, other: 'A') -> 'A':\n        return self\n", "it is imported by #474")]
  [TestCase("from dataclasses import dataclass, field\n\n@dataclass\nclass P:\n    x: list[int] = field(default_factory=list)\n", "error PLY1840: 'field(…) in P, a default made by a factory'")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass P:\n    x: int\n\n    def __init__(self):\n        self.x = 1\n", "error PLY1840: 'a dataclass P with its own __init__'")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass P(Exception):\n    x: int\n", "error PLY1840: 'the base class Exception of the dataclass P'")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass(order=True)\nclass P:\n    x: int\n", "error PLY1840: '@dataclass(order=True)'")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass(frozen=1)\nclass P:\n    x: int\n", "error PLY1840: '@dataclass(frozen=…) of a value other than True or False'")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass(True)\nclass P:\n    x: int\n", "error PLY1840: 'positional arguments of @dataclass'")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass(init=False)\nclass P:\n    x: int\n", "error PLY1840: '@dataclass(init=False)'")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass P:\n    x: int = 1\n    y: int\n", "the field y without a default after one with a default in P, which raises TypeError")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass P:\n    x: int\n    x: int\n", "the field x of P declared twice")]
  [TestCase("from dataclasses import dataclass\nfrom typing import ClassVar\n\n@dataclass\nclass P:\n    x: ClassVar[int] = 1\n", "the class attribute x of P")]
  [TestCase("from dataclasses import dataclass\n\n@dataclass\nclass P:\n    x: int\n    y = 2\n", "the class attribute y = 2 of P")]
  [TestCase("def deco(c: int) -> int:\n    return c\n\n@deco\nclass P:\n    pass\n", "error PLY1840: 'the decorator deco of P'")]
  [TestCase("class A:\n    count = 0\n", "error PLY1840: 'the class attribute count = 0 of A'")]
  [TestCase("class A(metaclass=type):\n    pass\n", "it is imported by #751")]
  [TestCase("class A(ValueError, KeyError):\n    pass\n", "error PLY1840: 'multiple inheritance of A' is outside the first Python subset (dataclasses, simple final classes, enums); it is imported by #762")]
  [TestCase("from typing import Protocol\n\nclass A(Protocol):\n    pass\n", "it is imported by #473")]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n\n    def m(self) -> int:\n        return 1\n", "error PLY1840: 'the method m of the enum E'")]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    B = 1\n", "error PLY1840: 'the alias B of another member of E'")]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 1\n    A = 2\n", "the member A of E given twice")]
  [TestCase("from enum import Enum\n\nclass E(Enum):\n    A = 'a'\n", "whose value is no integer literal")]
  [TestCase("class A:\n    def m() -> int:\n        return 1\n", "error PLY1840: 'the method m of A without its receiver'")]
  [TestCase("class A:\n    @property\n    def p(self, x: int) -> int:\n        return x\n", "error PLY1840: 'the method p of A without its receiver'")]
  [TestCase("class A:\n    def m(self) -> int:\n        return 1\n\n    def m(self) -> int:\n        return 2\n", "error PLY1840: 'the method m of A defined twice'")]
  [TestCase("class A:\n    pass\n\nclass A:\n    pass\n", "error PLY1840: 'redefinition of A'")]
  [TestCase("class A:\n    def m(self, *args: int) -> int:\n        return 1\n", "error PLY1830: '*args of m' is not supported for functions")]
  [TestCase("class A:\n    def __init__(self):\n        self.m = 1\n\n    def m(self) -> int:\n        return 1\n", "error PLY1840: 'an attribute of A named as one of its methods'")]
  [TestCase("class E(ValueError):\n    def __init__(self):\n        pass\n", "error PLY1843: exception construct 'an exception class with members: E' is not supported")]
  public void ClassFeatureOutsideTheSubset_IsRefusedWithItsCode(string source, string diagnostic)
    => Assert.That(PythonSnippet.Refusals(source + "\ndef f() -> int:\n    return 0\n"), Has.Some.Contains(diagnostic));

  /// <summary>Test design 14 to 18: dynamic attributes, dict and set use, exception constructs and misuse of classes, refused where they stand.</summary>
  [TestCase("    c.extra = 1\n", "error PLY1844: attribute 'extra' of class 'C' is created dynamically")]
  [TestCase("    setattr(c, 'a', 1)\n", "error PLY1844: attribute 'setattr' of class 'c' is created dynamically")]
  [TestCase("    c.missing\n", "error PLY1844: attribute 'missing' of class 'C' is created dynamically")]
  [TestCase("    c.missing()\n", "error PLY1844: attribute 'missing' of class 'C' is created dynamically")]
  [TestCase("    del c.a\n", "error PLY1844: attribute 'a' of class 'c' is created dynamically")]
  [TestCase("    x = c.__dict__\n", "error PLY1844: attribute '__dict__' of class 'c' is created dynamically")]
  [TestCase("    x = {**{1: 2}}\n", "error PLY1842: dict or set operation '**{1: 2} in a display' is not supported")]
  [TestCase("    for x in {1, 2}:\n        pass\n", "error PLY1842: dict or set operation 'iteration over a set, whose order Python leaves to the hash,' is not supported")]
  [TestCase("    x = frozenset()\n", "error PLY1842: dict or set operation 'frozenset()' is not supported")]
  [TestCase("    c.setdefault(1, 0)\n", "error PLY1842: dict or set operation 'setdefault' is not supported")]
  [TestCase("    f.a = 2\n", "error PLY1840: 'assignment to the field a of the frozen F, which raises FrozenInstanceError,'")]
  [TestCase("    raise ValueError('a') from None\n", "error PLY1843: exception construct 'raise … from' is not supported")]
  [TestCase("    raise\n", "error PLY1843: exception construct 'raise outside an except clause' is not supported")]
  [TestCase("    raise 5\n", "error PLY1843: exception construct 'raise of a value of the type int, which raises TypeError,' is not supported")]
  [TestCase("    raise C\n", "error PLY1843: exception construct 'raise C, which is no exception' is not supported")]
  [TestCase("    raise ValueError('a', 'b')\n", "error PLY1843: exception construct 'an exception made with other arguments than a message")]
  [TestCase("    e = ValueError(1)\n", "error PLY1843: exception construct 'an exception whose argument is no str' is not supported")]
  [TestCase("    e = ValueError(x='a')\n", "error PLY1843: exception construct 'an exception made with other arguments than a message")]
  [TestCase("    try:\n        pass\n    except 5:\n        pass\n", "error PLY1843: exception construct 'except 5, which names no exception class' is not supported")]
  [TestCase("    try:\n        pass\n    except C:\n        pass\n", "error PLY1843: exception construct 'except C, which names no exception class' is not supported")]
  [TestCase("    try:\n        pass\n    finally:\n        return 1\n", "error PLY1843: exception construct 'return in finally' is not supported")]
  [TestCase("    for i in range(2):\n        try:\n            pass\n        finally:\n            continue\n", "error PLY1843: exception construct 'continue in finally' is not supported")]
  [TestCase("    try:\n        x = 1\n    except ValueError as e:\n        x = len(e.args)\n", "error PLY1843: exception construct 'args of an exception' is not supported")]
  [TestCase("    x = E.A.foo\n", "error PLY1840: 'foo of a member of an enum'")]
  [TestCase("    x = C.attribute\n", "error PLY1840: 'C.attribute, a class attribute,'")]
  [TestCase("    c.m\n", "error PLY1832: the function value 'c.m' escapes")]
  [TestCase("    x = E(1, 2)\n", "error PLY1802: 'E() of other than one value'")]
  [TestCase("    x = E(1.5)\n", "error PLY1802: 'E() of a value of the type float, which raises ValueError,'")]
  [TestCase("    C.m(c)\n", "error PLY1840: 'C.m, a class attribute,'")]
  [TestCase("    x = make().s(1)\n", "error PLY1802: 'the static method s called through a value that has to be evaluated'")]
  [TestCase("    o: int | None = None\n    x = o == 5\n", "error PLY1802: '== of the optional value of the type int | None; test it with 'is None' first'")]
  [TestCase("    x = None < 1\n", "error PLY1802: '< of None, which raises TypeError,'")]
  [TestCase("    x = make() is None\n", "error PLY1802: 'is None of a value of the type C, which is never None,'")]
  [TestCase("    x = 1 if E.A else 0\n", "error PLY1802: 'the truth of a value of the type E'")]
  [TestCase("    -E.A\n", "error PLY1802: '- of a value of the type E, which raises TypeError,'")]
  [TestCase("    E.A + 1\n", "error PLY1802: '+ of the types E and int, which raises TypeError,'")]
  [TestCase("    x = c == 1\n", "error PLY1802: '== of the types C and int'")]
  [TestCase("    x = isinstance(c, 'x')\n", "error PLY1802: 'isinstance against 'x''")]
  [TestCase("    x = isinstance(1, dict)\n", "error PLY1802: 'isinstance of a value of the type int against dict'")]
  [TestCase("    x = isinstance(make(), C)\n", "bool True")]
  [TestCase("    x = isinstance(len([1]), int)\n", "error PLY1802: 'isinstance of len([1]), which has to be evaluated'")]
  [TestCase("    c[0]\n", "error PLY1802: 'c[0] of an object of C, which has no __getitem__, and raises TypeError'")]
  [TestCase("    x = len(c)\n", "error PLY1802: 'len of a value of the type C, which raises TypeError,'")]
  [TestCase("    c.a.b\n", "error PLY1802: 'the attribute b of a value of the type int, which raises AttributeError,'")]
  [TestCase("    x = c.unknown\n", "error PLY1844: attribute 'unknown' of class 'C' is created dynamically")]
  [TestCase("    x = make(1)\n", "error PLY1830: 'more positional arguments than make takes")]
  public void ObjectMisuse_IsRefusedWithItsCode(string body, string diagnostic) {
    const string classes = "from dataclasses import dataclass\nfrom enum import Enum\n\nclass C:\n    def __init__(self):\n        self.a = 1\n\n    def m(self) -> int:\n        return 1\n\n    @staticmethod\n    def s(n: int) -> int:\n        return n\n\n"
      + "@dataclass(frozen=True)\nclass F:\n    a: int\n\nclass E(Enum):\n    A = 1\n\ndef make() -> C:\n    return C()\n\n";
    var source = classes + "def f() -> int:\n    c = C()\n    f = F(1)\n" + body + "    return 0\n";
    if (diagnostic.StartsWith("bool", System.StringComparison.Ordinal))
      Assert.That(PythonSnippet.Module(source), Is.Not.Null);
    else
      Assert.That(PythonSnippet.Refusals(source), Has.Some.Contains(diagnostic));
  }

  [Test]
  public void YieldInsideATryWithHandlers_IsRefused()
    => Assert.That(PythonSnippet.Refusals("from typing import Iterator\n\ndef g(n: int) -> Iterator[int]:\n    try:\n        yield n\n    except ValueError:\n        pass\n\ndef f(n: int) -> int:\n    return sum(g(n))\n"),
      Has.Some.Contains("error PLY1832: the function value 'g' escapes, or 'g' is a generator or coroutine").And.Contains("(a yield inside a try with except: #760)"));

  [Test]
  public void UnsupportedFieldHint_IsRefused()
    => Assert.That(PythonSnippet.Refusals("from dataclasses import dataclass\n\n@dataclass\nclass P:\n    x: dict\n\ndef f() -> int:\n    return 0\n"),
      Has.Some.Contains("error PLY1801: the type hint 'dict' of the field 'x' of 'P' is not supported"));

  [Test]
  public void AttributeOfAnUnknownType_IsRefused()
    => Assert.That(PythonSnippet.Refusals("class C:\n    def __init__(self):\n        self.a = []\n\ndef f() -> int:\n    return len(C().a)\n"),
      Has.Some.Contains("error PLY1801: the type of the attribute 'a' of 'C' is not known"));

  private const string Kinds = "from dataclasses import dataclass\nfrom enum import Enum, IntEnum\nfrom typing import Optional\n\n"
    + "class Color(Enum):\n    RED = 1\n\nclass Level(IntEnum):\n    LOW = 1\n    HIGH = 2\n\n"
    + "class Box:\n    def __init__(self, n: int):\n        self.n = n\n\n    def __len__(self) -> int:\n        return self.n\n\n"
    + "@dataclass\nclass P:\n    x: int\n\ndef pick() -> Color:\n    return Color.RED\n\n";

  /// <summary>Values held where another kind is expected, truth, and equality across optionals, enums and None.</summary>
  [TestCase("x: int = Level.HIGH\n    return x", 0L, "int 2")]
  [TestCase("return 1 if Box(a) else 0", 0L, "int 0")]
  [TestCase("return 1 if Box(a) else 0", 2L, "int 1")]
  [TestCase("e: Optional[ValueError] = None if a == 0 else ValueError()\n    return 1 if e else 0", 0L, "int 0")]
  [TestCase("e: Optional[ValueError] = None if a == 0 else ValueError()\n    return 1 if e else 0", 1L, "int 1")]
  [TestCase("o: Optional[int] = None if a == 0 else a\n    return int(o == None) * 10 + int(o != None)", 0L, "int 10")]
  [TestCase("o: Optional[int] = None if a == 0 else a\n    return int(o == None) * 10 + int(o != None)", 3L, "int 1")]
  [TestCase("e1: Optional[ValueError] = None\n    e2: Optional[ValueError] = None if a == 0 else ValueError()\n    return int(e1 == e2)", 0L, "int 1")]
  [TestCase("p1: Optional[P] = P(a) if a > 0 else None\n    p2: Optional[P] = P(a) if a < 5 else None\n    return int(p1 == p2)", 1L, "int 1")]
  [TestCase("p1: Optional[P] = P(a) if a > 0 else None\n    p2: Optional[P] = P(a) if a < 5 else None\n    return int(p1 == p2)", 7L, "int 0")]
  [TestCase("return int(None == None) * 10 + int(a == None)", 0L, "int 10")]
  [TestCase("x: list[int] = [] if a else []\n    return len(x)", 1L, "int 0")]
  [TestCase("o: Optional[int] = None if a == 0 else a\n    b: int = o\n    return b", 0L, "None")]
  [TestCase("x: int = 'a'\n    return x", 0L, "String a")]
  public void KindsMeeting_FollowPython(string body, long argument, string expected)
    => Assert.That(PythonSnippet.Run($"{Kinds}def f(a: int) -> int:\n    {body}\n", "f", argument), Is.EqualTo(expected));

  [TestCase("o: Optional[int] = None if a == 0 else a\n    return int(o == 1)", "error PLY1802: '== of the optional value of the type int | None; test it with 'is None' first'")]
  [TestCase("return int(pick() == Level.LOW)", "error PLY1802: '== of Color and Level, which is always False,'")]
  [TestCase("return int(reversed([1]) == reversed([1]))", "error PLY1802: '== of the type Iterator[int]'")]
  [TestCase("return 1 if a else 'x'", "error PLY1803: ''x'' gives a value of the type str where one of the type int is held")]
  [TestCase("return -'a'", "error PLY1802: '- of a value of the type str, which raises TypeError,'")]
  public void KindsMeetingOutsideTheSubset_AreRefused(string body, string diagnostic)
    => Assert.That(PythonSnippet.Refusals($"{Kinds}def f(a: int) -> int:\n    {body}\n"), Has.Some.Contains(diagnostic));

}
