namespace SilksongCoop.Core.Networking
{
    public enum DeliveryMethod : byte
    {
        Unreliable = 0,
        Sequenced = 1,
        ReliableOrdered = 2,
        ReliableUnordered = 3
    }
}
