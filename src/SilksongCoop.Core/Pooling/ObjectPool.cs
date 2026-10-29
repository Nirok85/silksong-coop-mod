using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.Pooling
{
    /// <summary>
    /// Generic object pool for reducing allocations.
    /// </summary>
    public class ObjectPool<T> where T : class, new()
    {
        private readonly Stack<T> _pool = new Stack<T>();
        private readonly Action<T>? _onGet;
        private readonly Action<T>? _onReturn;
        private readonly int _maxSize;

        public int Count => _pool.Count;

        public ObjectPool(int initialSize = 16, int maxSize = 256, Action<T>? onGet = null, Action<T>? onReturn = null)
        {
            _maxSize = maxSize;
            _onGet = onGet;
            _onReturn = onReturn;

            for (int i = 0; i < initialSize; i++)
            {
                _pool.Push(new T());
            }
        }

        public T Get()
        {
            T obj = _pool.Count > 0 ? _pool.Pop() : new T();
            _onGet?.Invoke(obj);
            return obj;
        }

        public void Return(T obj)
        {
            if (obj == null) return;
            if (_pool.Count >= _maxSize) return;
            _onReturn?.Invoke(obj);
            _pool.Push(obj);
        }

        public void Clear()
        {
            _pool.Clear();
        }
    }

    /// <summary>
    /// Array pool helper for byte arrays.
    /// </summary>
    public static class ArrayPoolHelper
    {
        private static readonly System.Buffers.ArrayPool<byte> _bytePool = System.Buffers.ArrayPool<byte>.Shared;

        public static byte[] Rent(int minimumLength) => _bytePool.Rent(minimumLength);
        public static void Return(byte[] array) => _bytePool.Return(array);
    }
}
