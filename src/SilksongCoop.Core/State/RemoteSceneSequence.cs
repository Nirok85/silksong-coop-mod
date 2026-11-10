namespace SilksongCoop.Core.State
{
    /// <summary>Orders reliable scene departure signals against the transform stream.</summary>
    public struct RemoteSceneSequence
    {
        private uint _generation;
        private uint _transformTick;
        private bool _hasGeneration;
        public bool HasTransform { get; private set; }
        public uint Generation => _generation;

        public bool AcceptDeparture(uint generation)
        {
            if (_hasGeneration && !RevisionLedger.IsNewer(generation, _generation))
                return false;
            _hasGeneration = true;
            _generation = generation;
            HasTransform = false;
            return true;
        }

        public bool AcceptTransform(uint generation, uint tick)
        {
            if (_hasGeneration && generation != _generation &&
                !RevisionLedger.IsNewer(generation, _generation))
                return false;
            if (_hasGeneration && generation == _generation && HasTransform &&
                !RevisionLedger.IsNewer(tick, _transformTick))
                return false;

            _hasGeneration = true;
            _generation = generation;
            _transformTick = tick;
            HasTransform = true;
            return true;
        }
    }
}
