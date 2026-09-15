using AgentForUnity.Editor.UI;
using NUnit.Framework;

namespace AgentForUnity.Editor.Tests
{
    internal sealed class AgentMessageScrollFollowTests
    {
        [Test]
        public void MaximumOffset_WhenContentFits_ReturnsZero()
        {
            Assert.That(AgentMessageScrollFollow.MaximumOffset(400f, 400f), Is.EqualTo(0f));
            Assert.That(AgentMessageScrollFollow.MaximumOffset(200f, 400f), Is.EqualTo(0f));
        }

        [Test]
        public void MaximumOffset_WhenContentOverflows_ReturnsRemainingHeight()
        {
            Assert.That(AgentMessageScrollFollow.MaximumOffset(1000f, 400f), Is.EqualTo(600f));
        }

        [Test]
        public void IsNearBottom_WhenAtOrNearMaximum_ReturnsTrue()
        {
            Assert.That(AgentMessageScrollFollow.IsNearBottom(600f, 1000f, 400f), Is.True);
            Assert.That(AgentMessageScrollFollow.IsNearBottom(560f, 1000f, 400f), Is.True);
            Assert.That(AgentMessageScrollFollow.IsNearBottom(0f, 400f, 400f), Is.True);
        }

        [Test]
        public void IsNearBottom_WhenScrolledAway_ReturnsFalse()
        {
            Assert.That(AgentMessageScrollFollow.IsNearBottom(0f, 1000f, 400f), Is.False);
            Assert.That(AgentMessageScrollFollow.IsNearBottom(500f, 1000f, 400f), Is.False);
            Assert.That(
                AgentMessageScrollFollow.IsNearBottom(
                    600f - AgentMessageScrollFollow.NearBottomThreshold - 1f,
                    1000f,
                    400f),
                Is.False);
        }

        [Test]
        public void ResolveFollow_UserScrollAwayPausesAndReturnResumes()
        {
            Assert.That(AgentMessageScrollFollow.ResolveFollow(true, false, true), Is.False);
            Assert.That(AgentMessageScrollFollow.ResolveFollow(false, true, true), Is.True);
        }

        [Test]
        public void ResolveFollow_ContentGrowthDoesNotPauseFollow()
        {
            Assert.That(AgentMessageScrollFollow.ResolveFollow(true, false, false), Is.True);
            Assert.That(AgentMessageScrollFollow.ResolveFollow(false, false, false), Is.False);
        }
    }
}
