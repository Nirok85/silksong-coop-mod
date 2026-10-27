namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Specifies the reliability and ordering guarantees for network packet delivery.
    /// </summary>
    public enum DeliveryMethod : byte
    {
        Unreliable = 0,
        Sequenced = 1,
        ReliableOrdered = 2,
        ReliableUnordered = 3
    }
}
