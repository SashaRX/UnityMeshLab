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
TYPE = re.compile(r'\b(?:class|struct|interface|enum|record)\s+(\w+)')
TOOLS_DIR = 'Tools'


def top_level_types(code):
    """Types declared directly in a namespace (or the file); nested types are reached through their owner."""
    for declaration in TYPE.finditer(code):
        prefix = code[:declaration.start()]
        if prefix.count('{') - prefix.count('}') <= 1:
            yield declaration.group(1)


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
    in_tools = {path for path in sources if path.relative_to(root).parts[0] == TOOLS_DIR}
    tool_private = {name: path for path in in_tools for name in top_level_types(sources[path]) if name not in owners}
    for name, owner in tool_private.items():
        reference = re.compile(r'\b' + re.escape(name) + r'\b')
        for path, code in sources.items():
            if path in in_tools:
                continue
            for match in reference.finditer(code):
                findings.append((path, code.count('\n', 0, match.start()) + 1,
                                 f'library code depends on {name} from {TOOLS_DIR}/; move it to the library that owns it'))
    return owners, findings


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
            (root / TOOLS_DIR).mkdir()
            tool.write_text('[MeshLabTool("tab")] public class Tab : IUvTool { enum Kind { A } }', encoding='utf-8')
            panel = root / TOOLS_DIR / 'Panel.cs'
            panel.write_text('namespace N {\n class Panel { struct Result {} }\n}', encoding='utf-8')
            library.write_text('class Library { enum Kind { B } Result result; }', encoding='utf-8')
            assert not scan(root)[1], 'Nested and same-named types are not tool-folder dependencies'
            library.write_text('class Library { Panel panel; }', encoding='utf-8')
            assert any('from Tools/' in item[2] for item in scan(root)[1]), 'Library use of a tool-folder type must fail'
            (root / TOOLS_DIR / 'Other.cs').write_text('class Other { Panel panel; }', encoding='utf-8')
            library.write_text('class Library {}', encoding='utf-8')
            assert not scan(root)[1], 'Tool-folder types may be shared between tool-folder files'
            (root / TOOLS_DIR / 'Intent.cs').write_text('namespace N;\n[Flags] public enum Intent { None }', encoding='utf-8')
            library.write_text('class Library { Intent intent; }', encoding='utf-8')
            assert any('Intent from Tools/' in item[2] for item in scan(root)[1]), 'File-scoped namespace types count as top-level'
        print('tool dependency guard self-test passed')
        return 0
    owners, findings = scan(args.editor_root)
    for path, line, reason in findings:
        print(f'{path}:{line}: {reason}')
    print(f'checked {len(owners)} tool declarations; {len(findings)} dependency violation(s)')
    return 1 if findings else 0


if __name__ == '__main__':
    raise SystemExit(main())
