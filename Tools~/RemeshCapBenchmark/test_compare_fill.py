import unittest
import numpy as np

from compare_fill import audit, face_key, preserve_source


class CompareFillTests(unittest.TestCase):
    def setUp(self):
        self.p = np.array([[0,0,0],[1,0,0],[0,1,0],[0,0,1]], dtype=np.float32)
        self.ix = np.array([[0,2,1],[0,1,3],[1,2,3]], dtype=np.int32)

    def test_cyclic_reordering_preserves_winding(self):
        self.assertEqual(face_key(self.p[self.ix[0]]), face_key(self.p[np.roll(self.ix[0],1)]))
        self.assertNotEqual(face_key(self.p[self.ix[0]]), face_key(self.p[self.ix[0][::-1]]))

    def test_reorders_library_faces_and_restores_source_prefix(self):
        out = np.array([[2,0,3], [3,1,2], [1,0,2], [1,3,0]])
        p, ix = preserve_source(self.p, self.ix, self.p, out)
        np.testing.assert_array_equal(p, self.p)
        np.testing.assert_array_equal(ix[:len(self.ix)], self.ix)
        self.assertTrue(audit(p,ix,3)['auditedClosed'])

    def test_changed_or_deleted_donor_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'changed or dropped'):
            preserve_source(self.p,self.ix,self.p,self.ix[:-1])
        moved = self.p.copy(); moved[0,0] += .001
        with self.assertRaisesRegex(ValueError, 'changed or dropped'):
            preserve_source(self.p,self.ix,moved,self.ix)

    def test_reversed_donor_is_rejected(self):
        reversed_faces = self.ix.copy(); reversed_faces[0] = reversed_faces[0][::-1]
        with self.assertRaisesRegex(ValueError, 'changed or dropped'):
            preserve_source(self.p,self.ix,self.p,reversed_faces)

    def test_incomplete_audit_never_passes(self):
        faces = np.concatenate((self.ix,[[0,3,2]]))
        report = audit(self.p,faces,3,pair_budget=0)
        self.assertFalse(report['auditComplete'])
        self.assertFalse(report['auditedClosed'])

    def test_contact_with_other_element_is_nonblocking(self):
        p = np.concatenate((self.p,[[-1,.1,.1],[1,.1,.1],[0,.2,.2]])).astype(np.float32)
        faces = np.concatenate((self.ix,[[4,5,6]],[[0,3,2]]))
        report = audit(p,faces,4)
        self.assertEqual(0,report['improperContacts'])
        self.assertGreater(report['excludedOtherElementPairs'],0)

    def test_duplicate_cap_and_wrong_winding_fail(self):
        for added in ([[0,3,2],[0,3,2]], [[2,3,0]]):
            self.assertFalse(audit(self.p,np.concatenate((self.ix,added)),3)['auditedClosed'])


if __name__ == '__main__':
    unittest.main()
