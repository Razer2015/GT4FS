using System;
using System.IO;

namespace GT.Shared {
    public class OffsetStreamDecorator : Stream {
        private readonly Stream _stream;
        private readonly long _offset;

        public OffsetStreamDecorator(Stream stream) {
            this._stream = stream;
            this._offset = stream.Position;
        }

        #region override methods and properties pertaining to the file position/length to transform the file positon using the instance's offset

        public override long Length {
            get { return _stream.Length - _offset; }
        }

        public override void SetLength(long value) {
            _stream.SetLength(value + _offset);
        }

        public override long Position {
            get { return _stream.Position - this._offset; }
            set { _stream.Position = value + this._offset; }
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _stream.CanWrite;

        #endregion

        #region override all other methods and properties as simple pass-through calls to the decorated instance.

        public override IAsyncResult BeginRead(byte[] array, int offset, int numBytes, AsyncCallback userCallback, object stateObject) {
            return _stream.BeginRead(array, offset, numBytes, userCallback, stateObject);
        }

        public override IAsyncResult BeginWrite(byte[] array, int offset, int numBytes, AsyncCallback userCallback, object stateObject) {
            return _stream.BeginWrite(array, offset, numBytes, userCallback, stateObject);
        }

        public override void Flush() {
            _stream?.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            int retValue = _stream.Read(buffer, offset, count);

            for (int a = 0; a < buffer.Length; a++) {
                buffer[a] = (byte)(buffer[a] - (byte)_offset);
            }

            return retValue;
        }

        public override long Seek(long offset, SeekOrigin origin) {
            return _stream.Seek(_offset + offset, origin);
        }

        public override void Write(byte[] buffer, int offset, int count) {
            byte[] changedBytes = new byte[buffer.Length];

            int index = 0;
            foreach (byte b in buffer) {
                changedBytes[index] = (byte)(b + (byte)_offset);
                index++;
            }

            _stream.Write(changedBytes, offset, count);
        }

        #endregion
    }
}
