"""Compare private paired exports exactly, then report median stage timings.

No fixtures or generated data belong in the repository. Pass the immutable
baseline models directory and one or more complete models directories per arm.
"""
import argparse
import hashlib
import json
import re
import statistics
import struct
import xml.etree.ElementTree as ET
from pathlib import Path

# The three paired fixtures; the test's TransferDiagnostics cases are ignored without user settings.
MODEL_CASE = re.compile(r'FrozenForwardReverseComparison\(.Assets/(JunctionFixtures|TransferBenchmarkInputs|KamazFixtures)/')


def assert_same_values(actual, expected, float32=False, path=''):
    # Reports contain millions of ancestry fields. Compare leaves directly
    # instead of constructing two additional full canonical byte buffers.
    assert type(actual) is type(expected), f'{path}: changed value type'
    if isinstance(actual, dict):
        assert actual.keys() == expected.keys(), f'{path}: changed fields'
        for key in actual:
            assert_same_values(actual[key], expected[key], float32, f'{path}.{key}')
    elif isinstance(actual, list):
        assert len(actual) == len(expected), f'{path}: changed array length'
        for index in range(len(actual)):
            assert_same_values(actual[index], expected[index], float32, f'{path}[{index}]')
    elif isinstance(actual, float):
        encoding = '<f' if float32 else '<d'
        assert struct.pack(encoding, actual) == struct.pack(encoding, expected), f'{path}: changed numeric bits'
    else:
        assert actual == expected, f'{path}: changed value'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def stable(root):
    return {p.relative_to(root): p for p in root.rglob('*.json')
            if p.name.endswith('.layout.json') or p.name == 'report.json' or p.name.startswith('quality_LOD')}


def seconds(mode, stage):
    return sum(s['milliseconds'] for s in mode['performance']['samples'] if s['stage'] == stage) / 1000


def counts(mode, stage):
    return sum(s['count'] for s in mode['performance']['samples'] if s['stage'] == stage)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--control', type=Path, nargs='+', required=True)
    parser.add_argument('--candidate', type=Path, nargs='+', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--require-query-backend', action='store_true')
    args = parser.parse_args()
    expected = stable(args.baseline)
    assert len(expected) == 120, 'Expected the complete three-model export set.'
    controls = {p.relative_to(args.control[0]): read(p) for p in args.control[0].rglob('comparison.json')}
    assert len(controls) == 3
    assert {Path(c['asset']).stem for c in controls.values()} == {'Cafe_Table_A_01', 'Modern_Kitchen_A', 'kamaz_Off'}
    formatting = {}
    tests = []
    work_reference = None
    refusal_reference = None
    samples = {'control': [], 'candidate': []}
    for arm, directories in (('control', args.control), ('candidate', args.candidate)):
        for root in directories:
            results = ET.parse(root.parent / 'tests.xml').getroot()
            model_cases = [case for case in results.iter('test-case')
                           if MODEL_CASE.search(case.attrib.get('fullname', ''))]
            assert results.attrib['failed'] == '0' and len(model_cases) == 3
            assert all(case.attrib['result'] == 'Passed' for case in model_cases), root
            tests.append({'directory': str(root.parent), **{key: results.attrib[key]
                          for key in ('total', 'passed', 'failed', 'skipped')}})
            log = (root.parent / 'unity.log').read_text(encoding='utf-8-sig')
            work = re.findall(r'Final family packing: [0-9]+ ms, (.+)', log)
            refused = re.findall(r'Footprint trials: (.+)', log)
            if work_reference is None:
                work_reference, refusal_reference = work, refused
                assert len(work_reference) == 4
            assert work == work_reference and refused == refusal_reference, f'{root}: changed logical work or packing decisions'
            files = stable(root)
            assert files.keys() == expected.keys(), f'{root}: incomplete export set'
            different = []
            for key, path in files.items():
                if hashlib.sha256(path.read_bytes()).digest() != hashlib.sha256(expected[key].read_bytes()).digest():
                    assert_same_values(read(path), read(expected[key]), key.name.endswith('.layout.json'), f'{root}: {key}')
                    different.append(str(key))
            formatting[str(root)] = different
            comparisons = {p.relative_to(root): read(p) for p in root.rglob('comparison.json')}
            assert comparisons.keys() == controls.keys(), f'{root}: incomplete comparison set'
            for key, comparison in comparisons.items():
                reference = controls[key]
                assert comparison['originalMeshesUnchanged']
                for field in ('asset', 'density', 'boundaryToleranceTexels', 'padding', 'borderPadding', 'rotation',
                              'axisAlignment', 'junctionCuts', 'inputKeys', 'inputFrames', 'inputHashes'):
                    assert comparison[field] == reference[field], (root, field)
                assert [m['mode'] for m in comparison['modes']] == ['forward', 'reverse-exclusive', 'reverse-overlap']
                for mode in comparison['modes']:
                    assert mode['completed'] and not mode['error'], (root, mode['mode'])
                    ref_mode = next(m for m in reference['modes'] if m['mode'] == mode['mode'])
                    assert (mode['atlasWidth'], mode['atlasHeight']) == (ref_mode['atlasWidth'], ref_mode['atlasHeight'])
                    for stage in ('Native chart placement', 'Footprint candidate search', 'Footprint placement audits'):
                        assert counts(mode, stage) == counts(ref_mode, stage), (root, stage)
                    if arm == 'candidate' and mode['mode'] == 'reverse-overlap':
                        assert counts(mode, 'Native footprint search snapshots') == counts(mode, 'Footprint candidate search')
                    if mode['mode'] != 'forward':
                        if args.require_query_backend:
                            native_queries = counts(mode, 'Embree centre queries')
                            scenes = counts(mode, 'Embree scenes')
                            buffers = counts(mode, 'Native query buffer allocations')
                            if arm == 'control' or Path(comparison['asset']).stem == 'Cafe_Table_A_01':
                                assert native_queries == scenes == buffers == 0, (root, 'expected managed backend')
                            else:
                                assert scenes == buffers > 0 and native_queries > 4096 * buffers, (root, 'native reuse witness missing')
                                assert counts(mode, 'Reused centre hits') > 0
                                assert counts(mode, 'Embree failed batches') == 0
                        samples[arm].append({'model': Path(comparison['asset']).stem, 'mode': mode['mode'],
                            'reverse': seconds(mode, 'Reverse total'), 'search': seconds(mode, 'Footprint candidate search'),
                            'snapshots': seconds(mode, 'Native footprint search snapshots'),
                            'workflow': seconds(mode, 'Reverse workflow total'),
                            'projection': seconds(mode, 'Face projection'),
                            'embree_queries': counts(mode, 'Embree centre queries'),
                            'embree_fallbacks': counts(mode, 'Embree capacity fallback queries'),
                            'reused_centres': counts(mode, 'Reused centre hits'),
                            'query_scenes': counts(mode, 'Embree scenes'),
                            'query_buffer_allocations': counts(mode, 'Native query buffer allocations')})
    rows = []
    for model in sorted({s['model'] for s in samples['control']}):
        for mode in ('reverse-exclusive', 'reverse-overlap'):
            row = {'model': model, 'mode': mode}
            for arm in samples:
                selected = [s for s in samples[arm] if s['model'] == model and s['mode'] == mode]
                for stage in ('reverse', 'search', 'snapshots', 'workflow', 'projection', 'embree_queries',
                              'embree_fallbacks', 'reused_centres', 'query_scenes', 'query_buffer_allocations'):
                    row[f'{arm}_{stage}'] = statistics.median(s[stage] for s in selected)
                row[f'{arm}_runs'] = len(selected)
            rows.append(row)
    summary = {'rows': rows, 'exact_binary_value_files_per_run': len(expected), 'json_formatting_differences': formatting,
               'tests': tests, 'identical_work_diagnostics': work_reference, 'identical_refused_orders': refusal_reference}
    args.output.write_text(json.dumps(summary, indent=2), encoding='utf-8')
    for row in rows:
        print(f"{row['model']} {row['mode']}: reverse {row['control_reverse']:.2f} -> {row['candidate_reverse']:.2f}s; "
              f"search {row['control_search']:.2f} -> {row['candidate_search']:.2f}s")
    print('All 120 exports per run have identical binary numeric values; inputs, atlas dimensions and audit counts match.')


if __name__ == '__main__':
    main()
