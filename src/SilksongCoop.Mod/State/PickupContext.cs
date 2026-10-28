using System.Threading;

namespace SilksongCoop.Mod.State
{
    /// <summary>
    /// Ambient data of the physical currency drop currently being collected. The currency
    /// hook reads it while <c>CurrencyObjectBase.DoCollect</c> runs, and clears it afterwards.
    /// </summary>
    public static class PickupContext
    {
        private struct Claim
        {
            public ulong Key;
            public float X;
            public float Y;
        }

        private static readonly AsyncLocal<Claim> Current = new AsyncLocal<Claim>();

        public static ulong CurrentKey => Current.Value.Key;
        public static float CurrentX => Current.Value.X;
        public static float CurrentY => Current.Value.Y;

        public static void Begin(ulong key, float x, float y)
        {
            Current.Value = new Claim { Key = key, X = x, Y = y };
        }

        public static void End()
        {
            Current.Value = default;
        }
    }
}
