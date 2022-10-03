using System;

namespace Miku.Core
{
    /// <summary>
    /// Addition tools for message process
    /// </summary>
    public static class MessageTool
    {
        /// <summary>
        /// Apply xor to a specific data with specific key
        /// </summary>
        /// <param name="data"></param>
        /// <param name="key"></param>
        public static void ApplyXor(this ArraySegment<byte> data, Span<byte> key)
        {
            int cnt = data.Count;
            for (int i = 0; i < cnt; i++)
            {
                data[i] ^= key[i % key.Length];
            }
        }
    }
}