#!/usr/bin/env python3
"""Catch the CS0103 that shipped to main: a field renamed, one call site missed.

No Unity licence and no C# compiler are needed. The check does not attempt to
type-check anything; it exploits one narrow structural fact instead.

A C# identifier that is used *only* as a member-access receiver -- it always
appears as `foo.Something`, and never once anywhere else in its own file --
has no declaration in that file. For a non-partial class that is a compile
error, because a field, local, parameter, foreach variable or pattern variable
must be written somewhere without a trailing dot in order to exist at all:

    readonly Dictionary<...> crossLodHints = ...;   <- `crossLodHints` bare
    var mesh = ...;                                 <- `mesh` bare
    foreach (var e in entries)                      <- `e` bare

That is exactly the shape d57c3c6 left behind when it renamed
accumulatedOverlapHints to crossLodHints and updated two of the three
`.Clear()` call sites: the survivor appeared once in the file, followed by a
dot, and nothing else in the repository declared it.

Deliberate design choices, both in the direction of staying quiet:

* Only camelCase identifiers are considered. Type and namespace receivers are
  PascalCase by convention (`Mathf.Abs`, `AssetDatabase.Contains`), and this
  check has no type table with which to tell a missing type from a present one.
* Any bare occurrence at all -- in any syntactic position -- counts as a
  declaration. Over-approximating "declared" trades recall for precision on
  purpose: a guard that cries wolf gets switched off, and this one is meant to
  survive in CI for years.

Partial classes are the one construct that breaks the premise, since a member
may be declared in a different file. Files declaring a partial type are skipped
and reported, so the exemption stays visible rather than silently eroding
coverage.

Exit codes: 0 clean, 1 findings, 2 bad usage.
"""

import argparse
import pathlib
import re
import sys

# Identifiers that are legal receivers without ever being declared in the file.
# `value` is the implicit property-setter parameter, `args`/`sender`/`e` show up
# as implicit or generated parameters, and the rest are keywords.
IMPLICIT = {
    "value", "base", "this", "args", "sender",
    "var", "new", "return", "throw", "await", "is", "as", "out", "ref", "in",
    "true", "false", "null", "if", "else", "for", "foreach", "while", "do",
    "switch", "case", "default", "break", "continue", "using", "namespace",
    "class", "struct", "interface", "enum", "delegate", "event", "operator",
    "get", "set", "add", "remove", "when", "where", "select", "from", "let",
    "orderby", "group", "into", "join", "on", "equals", "by", "ascending",
    "descending", "yield", "lock", "try", "catch", "finally", "checked",
    "unchecked", "fixed", "unsafe", "sizeof", "typeof", "nameof", "stackalloc",
    # Built-in type keywords are lowercase and are legal receivers of static
    # members (`float.MaxValue`, `char.IsDigit`, `string.Empty`).
    "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int",
    "uint", "long", "ulong", "short", "ushort", "object", "string", "void",
    "nint", "nuint", "dynamic",
}

# Strings (including verbatim and interpolated) and comments, longest-first so
# that block comments win over the `/` that starts them.
_NOISE = re.compile(
    r"""
      /\* .*? \*/            # block comment
    | // [^\n]*              # line comment
    | @" (?: [^"] | "" )* "  # verbatim string
    | \$? " (?: \\. | [^"\\\n] )* "   # regular / interpolated string
    | ' (?: \\. | [^'\\\n] )  '       # char literal
    """,
    re.VERBOSE | re.DOTALL,
)

_IDENT = r"[A-Za-z_][A-Za-z0-9_]*"
# A *root* receiver: an identifier followed by a dot, which is itself not
# preceded by a dot. The lookbehind is what keeps `bounds` in `mesh.bounds.size`
# out of the set -- that is a member being read off another object, not a name
# this file has to declare. Without it the check reports every chained property
# access in the package.
_RECEIVER = re.compile(rf"(?<![.\w])({_IDENT})\s*\.(?!\.)")
_ANY_IDENT = re.compile(rf"\b({_IDENT})\b")
_PARTIAL = re.compile(r"\bpartial\s+(?:class|struct|interface|record)\b")

# A type declaration with an optional base list, e.g.
#   public class LightmapTransferTool : IUvTool
#   sealed class UvToolHub : EditorWindow
_TYPE_DECL = re.compile(
    rf"\b(class|struct|interface|record|enum)\s+({_IDENT})\s*(?:<[^>{{]*>)?\s*(?::\s*([^{{]+))?"
)

# Interfaces from outside the package that carry no members a file must declare.
KNOWN_INTERFACES = {
    "IDisposable", "IEquatable", "IComparable", "IComparer", "IEqualityComparer",
    "IEnumerable", "IEnumerator", "IList", "ICollection", "IReadOnlyList",
    "IReadOnlyCollection", "IDictionary", "ICloneable", "IFormattable",
    "ISerializationCallbackReceiver", "IPreprocessBuildWithReport",
    "IPostprocessBuildWithReport", "IActiveBuildTargetChanged",
}


def package_types(roots):
    """Map every type declared under `roots` to its kind (class/interface/...)."""
    kinds = {}
    for root in roots:
        for path in pathlib.Path(root).rglob("*.cs"):
            code = strip_noise(path.read_text(encoding="utf-8", errors="replace"))
            for kind, name, _bases in _TYPE_DECL.findall(code):
                kinds[name] = kind
    return kinds


def external_base(code: str, kinds) -> str | None:
    """Return the name of a base this file inherits whose members we cannot see.

    Implementing an interface adds no members, so in-package interfaces and
    well-known framework ones are ignored. Anything else in a base list is
    treated as a class whose members are invisible to this check -- Unity's
    EditorWindow contributing `position` being the motivating case.
    """
    for _kind, _name, bases in _TYPE_DECL.findall(code):
        if not bases:
            continue
        for base in bases.split(","):
            base = base.strip().split("<")[0].strip()
            if not base or base == "where":
                continue
            if kinds.get(base) == "interface" or base in KNOWN_INTERFACES:
                continue
            return base
    return None


def strip_noise(text: str) -> str:
    """Blank out comments and literals, preserving newlines so lines still map."""
    def blank(match: re.Match) -> str:
        return re.sub(r"[^\n]", " ", match.group(0))

    return _NOISE.sub(blank, text)


def scan(path: pathlib.Path, kinds):
    """Return (findings, skipped_reason). findings is a list of (line, name)."""
    raw = path.read_text(encoding="utf-8", errors="replace")
    code = strip_noise(raw)

    if _PARTIAL.search(code):
        return [], "declares a partial type"

    base = external_base(code, kinds)
    if base:
        return [], f"inherits from '{base}', whose members are not visible here"

    # Record where each receiver occurrence starts, so the bare-occurrence pass
    # below can recognise the very same occurrences instead of re-deriving them.
    receivers = {}          # name -> first line it is used as a receiver
    receiver_starts = set()
    for match in _RECEIVER.finditer(code):
        name = match.group(1)
        receiver_starts.add(match.start(1))
        if name in IMPLICIT or not name[0].islower():
            continue
        receivers.setdefault(name, code.count("\n", 0, match.start()) + 1)

    if not receivers:
        return [], None

    # Every identifier occurrence that is NOT one of those receiver occurrences.
    # Presence of one is treated as a declaration, whatever its real syntax.
    bare = {match.group(1) for match in _ANY_IDENT.finditer(code)
            if match.start() not in receiver_starts}

    findings = [(line, name) for name, line in sorted(receivers.items(), key=lambda kv: kv[1])
                if name not in bare]
    return findings, None


# Cases the heuristic must get right, kept next to the heuristic so that
# loosening one without noticing fails CI instead of silently ending detection.
# Each entry is (label, source, expected_finding_names).
SELF_TEST_CASES = [
    ("the real d57c3c6 regression: renamed field, one call site left behind", """
namespace P { class C : IThing {
    readonly System.Collections.Generic.Dictionary<int,int> crossLodHints = null;
    void Reset() { crossLodHints.Clear(); accumulatedOverlapHints.Clear(); }
} }
""", {"accumulatedOverlapHints"}),

    ("chained property access is not a receiver", """
namespace P { class C : IThing {
    void M(UnityEngine.Mesh mesh) { var v = mesh.bounds.size.magnitude; }
} }
""", set()),

    ("locals, foreach and pattern variables count as declarations", """
namespace P { class C : IThing {
    void M(System.Collections.Generic.List<object> items) {
        var first = items.Count;
        foreach (var it in items) { var s = it.ToString(); }
        if (items is object o) { var t = o.ToString(); }
    }
} }
""", set()),

    ("built-in type keywords are legal receivers", """
namespace P { class C : IThing {
    void M() { var a = float.MaxValue; var b = string.Empty; var c = char.IsDigit('x'); }
} }
""", set()),

    ("a name only ever mentioned inside a comment or string is not a declaration", """
namespace P { class C : IThing {
    // ghostField.Clear() used to live here
    void M() { var s = "ghostField.Clear()"; ghostField.Clear(); }
} }
""", {"ghostField"}),
]


def self_test() -> int:
    """Verify the heuristic still catches what it exists to catch."""
    import tempfile

    failures = 0
    for label, source, expected in SELF_TEST_CASES:
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "Case.cs").write_text(source, encoding="utf-8")
            # IThing must resolve as an in-package interface, or every case is
            # skipped for inheriting an unknown base and the test proves nothing.
            (root / "IThing.cs").write_text("namespace P { interface IThing { } }",
                                            encoding="utf-8")
            kinds = package_types([str(root)])
            hits, reason = scan(root / "Case.cs", kinds)
            got = {name for _line, name in hits}

        if reason:
            print(f"FAIL {label}: file was skipped ({reason})", file=sys.stderr)
            failures += 1
        elif got != expected:
            print(f"FAIL {label}: expected {sorted(expected) or 'no findings'}, "
                  f"got {sorted(got) or 'no findings'}", file=sys.stderr)
            failures += 1
        else:
            print(f"ok   {label}", file=sys.stderr)

    print(f"self-test: {len(SELF_TEST_CASES) - failures}/{len(SELF_TEST_CASES)} passed",
          file=sys.stderr)
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("roots", nargs="*", default=["Editor", "Tests"],
                        help="directories to scan (default: Editor Tests)")
    parser.add_argument("--list-skipped", action="store_true",
                        help="also report files exempted for declaring a partial type")
    parser.add_argument("--self-test", action="store_true",
                        help="verify the heuristic against its own fixtures and exit")
    opts = parser.parse_args()

    if opts.self_test:
        return self_test()

    kinds = package_types(opts.roots)

    files, findings, skipped = 0, [], []
    for root in opts.roots:
        for path in sorted(pathlib.Path(root).rglob("*.cs")):
            files += 1
            hits, reason = scan(path, kinds)
            if reason:
                skipped.append((path, reason))
            for line, name in hits:
                findings.append((path, line, name))

    for path, line, name in findings:
        # GitHub Actions renders this annotation on the offending line.
        print(f"::error file={path},line={line}::"
              f"'{name}' is used as a member-access receiver but is never "
              f"declared in this file - likely CS0103 (renamed or deleted "
              f"declaration with a surviving call site)")
        print(f"{path}:{line}: undeclared identifier '{name}'", file=sys.stderr)

    if opts.list_skipped:
        for path, reason in skipped:
            print(f"skipped {path}: {reason}", file=sys.stderr)

    print(f"scanned {files} file(s), {len(skipped)} skipped, {len(findings)} finding(s)",
          file=sys.stderr)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
