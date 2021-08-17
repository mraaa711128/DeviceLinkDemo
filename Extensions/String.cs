using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Utility.Extensions;

namespace DeviceLink.Extensions
{
    public static class String
    {
        public static string ToPrintOutString(this string origin)
        {
            if (string.IsNullOrEmpty(origin)) { throw new ArgumentNullException("this", "Caller shall not be null or empty."); } 
            var result = "";
            foreach (var chr in origin.ToCharArray())
            {
                result += chr.ToPrintOutString();
            }
            return result;
        }

        public static string ToFixLengthString(this string origin, int length)
        {
            if (string.IsNullOrEmpty(origin)) { throw new ArgumentNullException("this", "Caller shall not be null or empty."); }
            if (length <= 0) { throw new ArgumentException("length", "Length shall be greater than zero."); }
            if (origin.Length >= length) { return origin.Substring(0, length); }
            return origin.PadLeft(length);
        }

        public static IEnumerable<string> Split(this string origin, int chunkSize) {
            if (string.IsNullOrEmpty(origin)) { throw new ArgumentNullException("this", "Caller shall not be null or empty."); }
            if (chunkSize <= 0) { throw new ArgumentException("chunkSize", "ChunkSize shall be greater than zero."); }
            return Enumerable.Range(0, origin.Length / chunkSize)
                .Select(i => origin.Substring(i * chunkSize, chunkSize));
        }

        public static string Repeat(this string origin, int count) {
            if (origin.IsNullOrEmpty()) { throw new ArgumentNullException(); }
            return string.Concat(Enumerable.Repeat(origin, count));
        }
    }
}
