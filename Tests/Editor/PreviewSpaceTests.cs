using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PreviewSpaceTests
    {
        [TestCase(1f)]
        [TestCase(-2f)]
        [TestCase(0f)]
        public void RootAndSceneAncestorsDoNotAffectNestedModelPlacement(float sceneScale)
        {
            var ancestor = new GameObject("Scene");
            var root = new GameObject("Model");
            var branch = new GameObject("Branch");
            var child = new GameObject("Part");
            try {
                root.transform.SetParent(ancestor.transform, false);
                branch.transform.SetParent(root.transform, false);
                child.transform.SetParent(branch.transform, false);
                ancestor.transform.SetPositionAndRotation(new Vector3(80, -90, 25), Quaternion.Euler(30, 45, 70));
                ancestor.transform.localScale = new Vector3(sceneScale, 3, .1f);
                root.transform.localPosition = new Vector3(4, 8, 2);
                root.transform.localRotation = Quaternion.Euler(40, 25, 75);
                root.transform.localScale = new Vector3(3, sceneScale, 4);
                branch.transform.localPosition = new Vector3(1, 2, 3);
                branch.transform.localRotation = Quaternion.Euler(10, 20, 30);
                branch.transform.localScale = new Vector3(2, 3, .5f);
                child.transform.localPosition = new Vector3(-4, 0, 2);
                child.transform.localRotation = Quaternion.Euler(40, 25, 15);
                child.transform.localScale = new Vector3(-1, 2, 1);
                var expected = Matrix4x4.TRS(branch.transform.localPosition, branch.transform.localRotation, branch.transform.localScale) *
                    Matrix4x4.TRS(child.transform.localPosition, child.transform.localRotation, child.transform.localScale);
                Assert.AreEqual(expected, PreviewSpace.LocalToRoot(child.transform, root.transform), "retain the model's nested placement, including shear and reflection");
                Assert.AreEqual(Matrix4x4.identity, PreviewSpace.LocalToRoot(root.transform, root.transform));
                Assert.AreEqual(Matrix4x4.identity, PreviewSpace.LocalToRoot(null, root.transform));
                Assert.AreEqual(Matrix4x4.identity, PreviewSpace.LocalToRoot(ancestor.transform, root.transform));
                Assert.AreEqual(new Vector3(3, sceneScale, 4), root.transform.localScale);
            }
            finally { Object.DestroyImmediate(ancestor); }
        }
    }
}
