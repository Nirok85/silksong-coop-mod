using System;
using System.Threading;

namespace SilksongCoop.Mod.State
{
    /// <summary>
    /// Scoped guard for applying remote state locally without triggering
    /// outgoing sync (prevents feedback loops). Replaces static bool flags.
    /// Usage: using (RemoteApplyScope.Begin()) { ... }
    /// </summary>
    public sealed class RemoteApplyScope : IDisposable
    {
        private static readonly AsyncLocal<int> _depth = new AsyncLocal<int>();

        public static bool IsActive => _depth.Value > 0;

        private RemoteApplyScope() { }

        public static RemoteApplyScope Begin()
        {
            _depth.Value++;
            return new RemoteApplyScope();
        }

        public void Dispose()
        {
            if (_depth.Value > 0)
                _depth.Value--;
        }
    }
}
