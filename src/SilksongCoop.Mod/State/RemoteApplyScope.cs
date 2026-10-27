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

        /// <summary>
        /// Begins a new remote apply scope to prevent sync feedback loops.
        /// </summary>
        /// <returns>A disposable scope object that ends the scope when disposed.</returns>
        public static RemoteApplyScope Begin()
        {
            _depth.Value++;
            return new RemoteApplyScope();
        }

        /// <summary>
        /// Ends the remote apply scope.
        /// </summary>
        public void Dispose()
        {
            if (_depth.Value > 0)
                _depth.Value--;
        }
    }
}
