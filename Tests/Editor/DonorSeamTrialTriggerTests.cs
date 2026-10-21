using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class DonorSeamTrialTriggerTests
    {
        static TransferUvQuality Quality(int degenerate = 0, int pairs = 0, double worst = 1)
            => new TransferUvQuality { degenerateFaces = degenerate, overlapPairs = pairs, worstAnisotropy = worst };

        /// <summary>Zero-area 3D faces are degenerate in every layout; alone they no longer start the trial.</summary>
        [Test]
        public void ZeroAreaGeometryAloneDoesNotStartTheTrial()
        {
            Assert.IsFalse(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(degenerate: 3), 3));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(degenerate: 4), 3),
                "A collapsed UV face beyond the zero-area geometry still starts it.");
        }

        /// <summary>Stretch, overlap and collapsed UVs start the trial as before.</summary>
        [Test]
        public void UvDefectsStillStartTheTrial()
        {
            Assert.IsFalse(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(worst: 4), 0));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(worst: 4.01), 0));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(pairs: 1), 0));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(degenerate: 1), 0));
        }
    }
}
