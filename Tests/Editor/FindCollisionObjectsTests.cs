// FindCollisionObjectsTests.cs — collision nodes found by the naming rule, each by its own
// name, down to grandchildren: "Column" is no collision, a _COL under a group of any name is.
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FindCollisionObjectsTests
    {
        GameObject root;

        [TearDown]
        public void TearDown() { if (root) Object.DestroyImmediate(root); }

        static Transform Child(Transform parent, string name)
        {
            var node = new GameObject(name).transform;
            node.SetParent(parent, false);
            return node;
        }

        [Test]
        public void FindsCollisionByItsOwnNameDownToGrandchildren()
        {
            root = new GameObject("SM_University_Inside_Column_U");
            Child(root.transform, "University_Inside_Column_Concrete_U_LOD0");
            Child(root.transform, "University_Inside_Column_U_Wood_COL");
            var hulls = Child(root.transform, "Seat_COL");
            Child(hulls, "Seat_COL_Hull0");
            var group = Child(root.transform, "Inside_Column");
            Child(group, "Pillar_COL");
            Child(group, "Pillar_LOD0");
            Child(root.transform, "Chair_COLOR");

            var names = MeshHygieneUtility.FindCollisionObjects(root.transform).Select(o => o.name).ToArray();

            CollectionAssert.AreEqual(new[] { "University_Inside_Column_U_Wood_COL", "Seat_COL", "Seat_COL_Hull0", "Pillar_COL" }, names);
        }
    }
}
