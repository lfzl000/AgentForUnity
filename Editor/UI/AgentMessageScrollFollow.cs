namespace AgentForUnity.Editor.UI
{
    internal static class AgentMessageScrollFollow
    {
        internal const float NearBottomThreshold = 64f;

        internal static float MaximumOffset(float contentHeight, float viewportHeight)
        {
            var maximum = contentHeight - viewportHeight;
            return maximum > 0f ? maximum : 0f;
        }

        internal static bool IsNearBottom(
            float scrollOffset,
            float contentHeight,
            float viewportHeight,
            float threshold = NearBottomThreshold)
        {
            return MaximumOffset(contentHeight, viewportHeight) - scrollOffset <= threshold;
        }

        internal static bool ResolveFollow(bool currentlyFollowing, bool isNearBottom, bool userInitiated)
        {
            if (isNearBottom)
            {
                return true;
            }

            return userInitiated ? false : currentlyFollowing;
        }
    }
}
