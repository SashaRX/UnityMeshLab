import json
from pathlib import Path
import struct
import tempfile
import unittest

import numpy as np

from failure_capture import export, read_failure


def capture_bytes():
    metadata = {'stage': 'Simplify', 'node': 'Тестовая модель', 'reason': 'invalid topology',
                'settingsJson': '{"shell":true}', 'resolution': 128, 'initialFlags': 2, 'resultFlags': 2}
    text = json.dumps(metadata, ensure_ascii=False).encode('utf-8')
    size = len(text)
    prefix = bytearray()
    while size >= 128:
        prefix.append((size & 127) | 128)
        size >>= 7
    prefix.append(size)
    points = np.array([[-0., 0., 0.], [1., 0., 0.], [0., 1., 0.]], dtype='<f4')
    source = np.array([[0, 1, 2]], dtype='<u4')
    rejected = np.array([[0, 1, 2], [0, 2, 1]], dtype='<u4')
    def mesh(indices):
        return b'\x01'+struct.pack('<II',len(points),indices.size)+points.tobytes()+indices.tobytes()
    # Raw is explicitly absent; it must not become a fabricated copy of input.
    binary = struct.pack('<II',0x524D4C42,2)+prefix+text+mesh(source)+b'\x00'+mesh(rejected)
    return binary, metadata, points, source, rejected


class FailureCaptureTests(unittest.TestCase):
    def test_cross_language_layout_and_exact_geometry_export(self):
        binary, metadata, points, source, rejected = capture_bytes()
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path = root/'failure.bin'
            path.write_bytes(binary)
            report = export(path, root/'out')
            self.assertEqual(metadata, report['metadata'])
            self.assertIsNone(report['meshes']['raw'])
            self.assertFalse((root/'out/raw.bin').exists())
            self.assertEqual(1, report['meshes']['input']['topology']['duplicateFaces'])
            with np.load(root/'out/input.npz') as mesh:
                self.assertEqual(points.tobytes(), mesh['positions'].tobytes())
                np.testing.assert_array_equal(rejected, mesh['indices'])
            self.assertEqual(struct.pack('<II',3,3)+points.tobytes()+source.tobytes(),
                             (root/'out/source.bin').read_bytes())
            self.assertEqual(binary, path.read_bytes())

    def test_truncated_and_legacy_captures_are_rejected(self):
        binary, *_ = capture_bytes()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'failure.bin'
            for invalid in (binary[:-1], binary[:8], struct.pack('<II',0x524D4C42,1)+binary[8:]):
                path.write_bytes(invalid)
                with self.assertRaises(ValueError):
                    read_failure(path)

    def test_invalid_indices_are_not_exported(self):
        binary, *_ = capture_bytes()
        binary = binary[:-4]+struct.pack('<I',99)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'failure.bin'
            path.write_bytes(binary)
            with self.assertRaisesRegex(ValueError, 'Invalid captured'):
                export(path, Path(folder)/'out')
            self.assertFalse((Path(folder)/'out').exists())

    def test_existing_output_is_preserved_instead_of_mixing_capture_stages(self):
        binary, *_ = capture_bytes()
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path = root/'failure.bin'
            path.write_bytes(binary)
            output = root/'out'
            output.mkdir()
            (output/'raw.npz').write_bytes(b'previous geometry')
            with self.assertRaisesRegex(ValueError, 'must be empty'):
                export(path, output)
            self.assertEqual(b'previous geometry', (output/'raw.npz').read_bytes())


if __name__ == '__main__':
    unittest.main()
