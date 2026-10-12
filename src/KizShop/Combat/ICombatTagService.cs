namespace KizShop.Combat
{
    public interface ICombatTagService
    {
        void Tag(ulong steamId1, ulong steamId2);
        bool IsTagged(ulong steamId, out int secondsLeft);
        void Clear(ulong steamId);
    }
}
