import collections
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import numpy as np

from analyze import NearestTriangles, cap_groups, scan, voxel_samples


class CapAuditTests(unittest.TestCase):
    def test_existing_source_crossings_fail_solid_gate_despite_clean_cap(self):
        tetra = np.array([[0,0,0],[2,0,0],[0,2,0],[0,0,2]], dtype='f4')
        p = np.concatenate((tetra, tetra+.5, tetra+10)).astype('f4')
        faces = np.array([[0,2,1],[0,1,3],[1,2,3],[2,0,3]], dtype='u4')
        ix = np.concatenate((faces, faces+4, faces+8)).astype('u4')
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name, positions, indices in (('source',p[:8],ix[:8]), ('capped',p,ix)):
                with (root/(name+'.bin')).open('wb') as stream:
                    np.array([len(positions),indices.size],dtype='<u4').tofile(stream)
                    positions.astype('<f4').tofile(stream)
                    indices.astype('<u4').tofile(stream)
            subprocess.run([sys.executable,str(Path(__file__).with_name('analyze.py')),
                            '--source',str(root/'source.bin'),'--capped',str(root/'capped.bin'),
                            '--output',str(root/'report.json')],check=True,capture_output=True)
            report = json.loads((root/'report.json').read_text())
        self.assertTrue(report['topology']['closedManifold'])
        self.assertTrue(report['capAccepted'])
        self.assertGreater(report['summary']['pairCounts']['source-source']['crossing'],0)
        self.assertFalse(report['solidCandidateAccepted'])

    def test_closed_cube_cap_has_no_geometric_intersections(self):
        p=np.array([[-1,-1,-1],[1,-1,-1],[1,1,-1],[-1,1,-1],[-1,-1,1],[1,-1,1],[1,1,1],[-1,1,1]],dtype='f4')
        ix=np.array([[0,2,1],[0,3,2],[4,5,6],[4,6,7],[0,1,5],[0,5,4],
                     [1,2,6],[1,6,5],[2,3,7],[2,7,6],[3,0,4],[3,4,7]],dtype='u4')
        result=scan(p,ix,10)
        self.assertEqual([],result['pairs'])
        self.assertTrue(result['summary']['capGeometryAccepted'])
        self.assertEqual(1,result['summary']['capComponents'])

    def test_nested_independent_caps_are_rejected(self):
        # An outer full patch covers an existing inner island and its own cap.
        p=np.array([[-2,-2,0],[2,-2,0],[2,2,0],[-2,2,0],[-.5,-.5,0],[.5,-.5,0],[.5,.5,0],[-.5,.5,0]],dtype='f4')
        ix=np.array([[4,5,6],[4,6,7],[0,2,1],[0,3,2],[4,6,5],[4,7,6]],dtype='u4')
        result=scan(p,ix,2)
        roles=collections.Counter(p['role'] for p in result['pairs'])
        self.assertGreater(roles['source-cap'],0)
        self.assertGreater(roles['cap-cap'],0)
        self.assertFalse(result['summary']['capGeometryAccepted'])
        self.assertEqual(2,result['summary']['capComponents'])

    def test_cap_groups_do_not_join_at_one_shared_vertex(self):
        ix=np.array([[0,1,2],[0,2,3],[0,4,5]],dtype='u4')
        self.assertEqual([0,0,1],cap_groups(ix,0).tolist())

    def test_degenerate_cap_is_rejected_even_without_pair_intersections(self):
        p=np.array([[0,0,0],[1,0,0],[0,1,0],[2,0,0]],dtype='f4')
        result=scan(p,np.array([[0,1,2],[0,1,3]],dtype='u4'),1)
        self.assertEqual(1,result['summary']['degenerateCapFaces'])
        self.assertFalse(result['summary']['capGeometryAccepted'])

    def test_duplicate_sampling_counts_groups_and_incidences_separately(self):
        p=np.array([[0,0,0],[1,0,0],[0,1,0]],dtype='f4')
        ix=np.array([[0,1,2],[0,2,1]],dtype='u4')
        points,rows,counts=voxel_samples(p,ix)
        self.assertEqual(1,len(points)); self.assertEqual(1,len(rows))
        self.assertEqual(1,counts['duplicateFaceGroups'])
        self.assertEqual(2,counts['duplicateFaceIncidences'])
        self.assertEqual(1,counts['duplicateExcessFaces'])

    def test_nearest_triangle_interior_edge_and_degenerate_witness(self):
        triangles=np.array([[[0,0,0],[2,0,0],[0,2,0]],[[3,0,0],[3,0,0],[3,0,0]],[[0,0,2],[2,0,2],[2,0,2]]],dtype=float)
        nearest=NearestTriangles(triangles)
        np.testing.assert_allclose(nearest.distances(np.array([.5,.5,1])),[1,7.5,1.25])
        np.testing.assert_allclose(nearest.distances(np.array([3,0,0])),[1,0,5])


if __name__=='__main__':
    unittest.main()
