#!/usr/bin/env python3
"""Reject direct dependencies on concrete tabs in Editor code.

Libraries and tools communicate through contracts and shared services. This is a
source-level guard; runtime module validation additionally resolves declarations.
"""
import argparse
from pathlib import Path
import re
import tempfile


LITERALS_AND_COMMENTS = re.compile(
    r'/\*[\s\S]*?\*/|//[^\r\n]*|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
)
TOOL = re.compile(r'(?:(?:public|internal|private|protected|sealed|abstract|static|partial|new)\s+)*class\s+(\w+)[^{;]*:\s*[^{;]*\bIUvTool\b')
ATTRIBUTES = re.compile(r'(?:\[[^\]]*\]\s*)+$')
SCOPES = re.compile(
    r'\bnamespace\s+([\w.]+)\s*([;{])'
    r'|\b(?:record(?:\s+(?:class|struct))?(?=\s+\w+\s*[({<:;])|class|struct|interface|enum)\s+(\w+)'
    r'|[{}]'
)
USING = re.compile(r'^\s*(?:global\s+)?using\s+([\w.]+)\s*;', re.M)
TOOLS_DIR = 'Tools'


def declarations(code):
    """Return ([(type name, namespace, top-level)], namespaces the file opens).

    Top-level means only namespace scopes enclose the type; a nested type is
    reached through its owner. Braces are tracked as a scope stack so nested
    namespace blocks and file-scoped namespaces both resolve correctly.
    """
    file_namespace, stack, types, namespaces = '', [], [], set()
    for token in SCOPES.finditer(code):
        namespace = '.'.join(part for part in [file_namespace, *(s for s in stack if s)] if part)
        if token.group(1):
            opened = f'{namespace}.{token.group(1)}' if namespace else token.group(1)
            namespaces.add(opened)
            if token.group(2) == ';':
                file_namespace = token.group(1)
            else:
                stack.append(token.group(1))
        elif token.group(3):
            types.append((token.group(3), namespace, all(stack)))
        elif token.group() == '{':
            stack.append(None)
        elif stack:
            stack.pop()
    return types, namespaces


def sees(namespace, file_namespaces, usings):
    """C# name lookup reaches a namespace's types from inside it, its children, or a using directive."""
    return (not namespace or namespace in usings
            or any(f == namespace or f.startswith(namespace + '.') for f in file_namespaces))


def scan(root):
    sources = {
        path: LITERALS_AND_COMMENTS.sub(lambda match: '\n' * match.group().count('\n'), path.read_text(encoding='utf-8-sig'))
        for path in sorted(root.rglob('*.cs'))
    }
    owners = {}
    findings = []
    for path, code in sources.items():
        for declaration in TOOL.finditer(code):
            name = declaration.group(1)
            owners[name] = path
            attributes = ATTRIBUTES.search(code[:declaration.start()])
            if attributes is None or not re.search(r'\bMeshLabTool(?:Attribute)?\b', attributes.group()):
                findings.append((path, code.count('\n', 0, declaration.start()) + 1,
                                 f'{name} needs a MeshLabTool library dependency declaration'))
    for name, owner in owners.items():
        reference = re.compile(r'\b' + re.escape(name) + r'\b')
        for path, code in sources.items():
            if path == owner:
                continue
            for match in reference.finditer(code):
                findings.append((path, code.count('\n', 0, match.start()) + 1,
                                 f'direct dependency on {name}; use a shared library or contract'))
    findings.extend(tool_folder_findings(root, sources, owners))
    return owners, findings


def tool_folder_findings(root, sources, owners):
    """Code outside Tools/ that resolves to a top-level type declared under Tools/."""
    parsed = {path: declarations(code) for path, code in sources.items()}
    in_tools = {path for path in sources if path.relative_to(root).parts[0] == TOOLS_DIR}
    elsewhere = {}
    for path, (types, _) in parsed.items():
        if path not in in_tools:
            for name, namespace, top_level in types:
                if top_level:
                    elsewhere.setdefault(name, set()).add(namespace)
    for owner in sorted(in_tools):
        for name, namespace, top_level in parsed[owner][0]:
            if not top_level or name in owners:
                continue
            reference = re.compile(r'\b' + re.escape(name) + r'\b')
            qualified = re.compile(r'\b' + re.escape(f'{namespace}.{name}') + r'\b') if namespace else None
            for path, code in sources.items():
                if path in in_tools:
                    continue
                types, namespaces = parsed[path]
                usings = set(USING.findall(code))
                # A same-named type the file declares or can see elsewhere wins (or is a compile error).
                shadowed = any(n == name for n, _, _ in types) or any(
                    ns != namespace and sees(ns, namespaces, usings) for ns in elsewhere.get(name, ()))
                if shadowed or not sees(namespace, namespaces, usings):
                    matches = qualified.finditer(code) if qualified else ()
                else:
                    matches = reference.finditer(code)
                for match in matches:
                    yield (path, code.count('\n', 0, match.start()) + 1,
                           f'library code depends on {name} from {TOOLS_DIR}/; move it to the library that owns it')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--editor-root', type=Path, default=Path(__file__).resolve().parents[1] / 'Editor')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tool = root / 'Tab.cs'
            library = root / 'Library.cs'
            tool.write_text('[MeshLabTool("tab")] public class Tab : IUvTool {}', encoding='utf-8')
            library.write_text('class Library { string name = "Tab"; } // Tab\n/* Tab */', encoding='utf-8')
            assert not scan(root)[1], 'Comments and strings must not create dependencies'
            library.write_text('class Library { Tab tool; }', encoding='utf-8')
            assert any('direct dependency' in item[2] for item in scan(root)[1]), 'Concrete tool references must fail'
            tool.write_text('public class Tab : IUvTool {}', encoding='utf-8')
            assert any('declaration' in item[2] for item in scan(root)[1]), 'Undeclared tools must fail'
            for modifiers in ('public partial', 'internal sealed partial', 'private', ''):
                tool.write_text(f'[MeshLabTool("tab")][Obsolete] {modifiers} class Tab :\n BaseClass,\n IUvTool {{}}', encoding='utf-8')
                library.write_text('class Library {}', encoding='utf-8')
                assert 'Tab' in scan(root)[0] and not scan(root)[1], modifiers
                library.write_text('class Library { Tab tool; }', encoding='utf-8')
                assert scan(root)[1], f'Multi-line {modifiers} tool dependencies must fail'
            tools = root / TOOLS_DIR
            tools.mkdir()

            def tool_folder(code):
                library.write_text(code, encoding='utf-8')
                return [item for item in scan(root)[1] if 'from Tools/' in item[2]]

            tool.write_text('[MeshLabTool("tab")] public class Tab : IUvTool { enum Kind { A } }', encoding='utf-8')
            (tools / 'Panel.cs').write_text('namespace N {\n class Panel { struct Result {} }\n}', encoding='utf-8')
            assert not tool_folder('namespace N { class Library { enum Kind { B } Result result; } }'), \
                'Nested types are not tool-folder dependencies'
            assert tool_folder('namespace N { class Library { Panel panel; } }'), 'Library use of a tool-folder type must fail'
            assert not tool_folder('class Library { Panel panel; }'), 'A namespace the file cannot see is not a dependency'
            (tools / 'Other.cs').write_text('namespace N { class Other { Panel panel; } }', encoding='utf-8')
            assert not tool_folder('class Library {}'), 'Tool-folder types may be shared between tool-folder files'
            (tools / 'Intent.cs').write_text('namespace N;\npublic enum Intent { None }\nclass Holder { class Inner {} }', encoding='utf-8')
            assert tool_folder('namespace N { class Library { Intent intent; } }'), 'File-scoped namespace types count as top-level'
            assert not tool_folder('namespace N { class Library { Inner inner; } }'), \
                'A type nested under a file-scoped namespace is not top-level'
            (tools / 'Deep.cs').write_text('namespace A { namespace B { enum Deep { X } } }', encoding='utf-8')
            assert tool_folder('namespace A.B.C { class Library { Deep deep; } }'), 'Types in nested namespace blocks count as top-level'
            assert tool_folder('using A.B;\nclass Library { Deep deep; }'), 'A using directive makes a tool-folder type visible'
            assert tool_folder('class Library { A.B.Deep deep; }'), 'Qualified references must fail'
            assert not tool_folder('namespace A { class Library { Deep deep; } }'), 'A parent namespace does not see child types'
            (tools / 'Settings.cs').write_text('namespace ToolUi { class Settings {} }', encoding='utf-8')
            (root / 'Shared.cs').write_text('namespace Shared { class Settings {} }', encoding='utf-8')
            assert not tool_folder('namespace Shared { class Library { Settings settings; } }'), \
                'A same-named type in another namespace is not a dependency'
            assert not tool_folder('using ToolUi;\nnamespace Shared { class Library { Settings settings; } }'), \
                'An ambiguous simple name is left to the compiler'
            assert tool_folder('namespace Shared { class Library { ToolUi.Settings settings; } }'), \
                'A qualified reference to the tool-folder type still fails'
            (tools / 'Pair.cs').write_text('namespace N { public readonly record struct Pair(int A); }', encoding='utf-8')
            found = tool_folder('namespace N { class Library { Pair pair; } }')
            assert any('Pair from Tools/' in item[2] for item in found), 'Record declarations capture the type name'
        print('tool dependency guard self-test passed')
        return 0
    owners, findings = scan(args.editor_root)
    for path, line, reason in findings:
        print(f'{path}:{line}: {reason}')
    print(f'checked {len(owners)} tool declarations; {len(findings)} dependency violation(s)')
    return 1 if findings else 0


if __name__ == '__main__':
    raise SystemExit(main())
