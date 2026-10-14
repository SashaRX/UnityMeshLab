"""Validate compatible query reports and summarize their actual paired timings."""
import argparse
import json
import statistics
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reports', type=Path, nargs='+')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    measurements = {}
    bake_checks = []
    expected = {'analytic-layers', 'analytic-tiny', 'analytic-unit', 'analytic-large',
                'analytic-offset', 'analytic-capacity-overflow', 'Cafe_Table_A_01',
                'Modern_Kitchen_A', 'kamaz_Off'}
    modes = {'ray', 'ray-facing', 'ray-target', 'nearest', 'nearest-normal'}
    exact = 0
    for path in args.reports:
        report = json.loads(path.read_text(encoding='utf-8-sig'))
        assert report['backend'] == 'candidates'
        assert len(report['models']) == 9 and {m['name'] for m in report['models']} == expected
        for model in report['models']:
            cases = model['measurements']
            assert len(cases) == 10
            assert {(m['mode'], m['threads']) for m in cases} == {(m, t) for m in modes for t in (1, 8)}
            for case in cases:
                assert case['queries'] > 0 and case['exactResults'] == case['queries']
                assert all(case[name] == 0 for name in ('hitMismatch', 'faceMismatch', 'uvMismatch', 'parameterMismatch'))
                exact += case['queries']
                key = (model['name'], case['mode'], case['threads'])
                measurements.setdefault(key, []).append(case)
        for bake in report.get('bakes', []):
            assert bake['allMapBytesAndCountersExact'] and bake['rayQueries'] > 0
            assert bake['controlSha256'] == bake['candidateSha256']
            bake_checks.append({'report': str(path), **bake})
    rows = []
    for (model, mode, threads), cases in sorted(measurements.items()):
        assert len({case['queries'] for case in cases}) == 1
        before = statistics.median(ms for case in cases for ms in case['managedMs'])
        after = statistics.median(ms for case in cases for ms in case['nativeMs'])
        rows.append({'model': model, 'mode': mode, 'threads': threads, 'controlMs': before,
                     'candidateMs': after, 'speedup': before / after, 'processes': len(cases),
                     'samplesPerArm': sum(len(case['managedMs']) for case in cases),
                     'exactQueriesPerProcess': cases[0]['queries'],
                     'candidateFallbacks': [case['candidateFallbacks'] for case in cases]})
    args.output.write_text(json.dumps({'reports': [str(p) for p in args.reports],
                                      'exactQueryCases': exact, 'rows': rows, 'bakes': bake_checks},
                                     indent=2), encoding='utf-8')
    for row in rows:
        if row['threads'] == 8 and row['model'] in {'Cafe_Table_A_01', 'Modern_Kitchen_A', 'kamaz_Off'}:
            print(f"{row['model']} {row['mode']}: {row['controlMs']:.3f} -> {row['candidateMs']:.3f} ms ({row['speedup']:.2f}x)")
    print(f'{exact} query cases match every result bit; {len(bake_checks)} exact whole-bake checks.')


if __name__ == '__main__':
    main()
