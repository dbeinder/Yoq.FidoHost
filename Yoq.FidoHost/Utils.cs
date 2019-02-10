using System;
using System.IO;

namespace Yoq.FidoHost
{
    internal class ByteArrayBuilder
    {
        private MemoryStream _stream = new MemoryStream();

        public long Length => _stream.Length;

        public void Clear() => _stream = new MemoryStream();

        public void Append(byte value) => _stream.WriteByte(value);

        public void Append(byte[] values) => _stream.Write(values, 0, values.Length);

        public void AppendZerosTill(int finalLength)
        {
            if (_stream.Length == finalLength) return;
            if (_stream.Length > finalLength)
                throw new ArgumentException($"builder is already larger than {nameof(finalLength)}");
            Append(new byte[finalLength - _stream.Length]);
        }

        public byte[] GetBytes() => _stream.ToArray();
    }
}