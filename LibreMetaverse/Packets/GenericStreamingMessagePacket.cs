using System;

namespace OpenMetaverse.Packets {
    public sealed class GenericStreamingMessagePacket : Packet {
        public sealed class MethodDataBlock : PacketBlock {
            public ushort Method;

            public override int Length { get { return sizeof(ushort); } }

            public MethodDataBlock() {
            }

            public MethodDataBlock(byte[] bytes, ref int i) {
                FromBytes(bytes, ref i);
            }

            public override void FromBytes(byte[] bytes, ref int i) {
                try {
                    Method = (ushort) (bytes[i++] + (bytes[i++] << 8));
                } catch(Exception) {
                    throw new MalformedDataException();
                }
            }

            public override void ToBytes(byte[] bytes, ref int i) {
                bytes[i++] = (byte) (Method % 256);
                bytes[i++] = (byte) ((Method >> 8) % 256);
            }
        }

        public sealed class DataBlock : PacketBlock {
            public byte[] Data;

            public override int Length {
                get {
                    int length = 0;
                    if(Data != null) { length += Data.Length; }

                    return length;
                }
            }

            public DataBlock() {
            }

            public DataBlock(byte[] bytes, ref int i) {
                FromBytes(bytes, ref i);
            }

            public override void FromBytes(byte[] bytes, ref int i) {
                try {
                    var length = bytes[i++];
                    Data = new byte[length];
                    Buffer.BlockCopy(bytes, i, Data, 0, length);
                    i += length;
                } catch(Exception) {
                    throw new MalformedDataException();
                }
            }

            public override void ToBytes(byte[] bytes, ref int i) {
                bytes[i++] = (byte) Data.Length;
                Buffer.BlockCopy(Data, 0, bytes, i, Data.Length);
                i += Data.Length;
            }
        }

        public override int Length {
            get {
                int length = 8;
                length += MethodData.Length;
                length += Data.Length;
                return length;
            }
        }

        public MethodDataBlock MethodData;
        public DataBlock Data;

        public GenericStreamingMessagePacket() {
            Type = PacketType.ObjectAnimation;
            Header = new Header();
            Header.Frequency = PacketFrequency.High;
            Header.ID = 31;
            Header.Reliable = true;
            MethodData = new MethodDataBlock();
            Data = null;
        }

        public GenericStreamingMessagePacket(byte[] bytes, ref int i) : this() {
            int packetEnd = bytes.Length - 1;
            FromBytes(bytes, ref i, ref packetEnd, null);
        }

        override public void FromBytes(byte[] bytes, ref int i, ref int packetEnd, byte[] zeroBuffer) {
            Header.FromBytes(bytes, ref i, ref packetEnd);
            if(Header.Zerocoded && zeroBuffer != null) {
                packetEnd = Helpers.ZeroDecode(bytes, packetEnd + 1, zeroBuffer) - 1;
                bytes = zeroBuffer;
            }

            MethodData.FromBytes(bytes, ref i);
            Data = new DataBlock();
            Data.FromBytes(bytes, ref i);
        }

        public GenericStreamingMessagePacket(Header head, byte[] bytes, ref int i) : this() {
            int packetEnd = bytes.Length - 1;
            FromBytes(head, bytes, ref i, ref packetEnd);
        }

        override public void FromBytes(Header header, byte[] bytes, ref int i, ref int packetEnd) {
            Header = header;
            MethodData.FromBytes(bytes, ref i);
            Data = new DataBlock();
            Data.FromBytes(bytes, ref i);
        }

        public override byte[] ToBytes() {
            int length = 7;
            length += MethodData.Length;
            length += Data.Length;
            if(Header.AckList != null && Header.AckList.Length > 0) { length += Header.AckList.Length * 4 + 1; }

            byte[] bytes = new byte[length];
            int i = 0;
            Header.ToBytes(bytes, ref i);
            MethodData.ToBytes(bytes, ref i);
            Data.ToBytes(bytes, ref i);

            if(Header.AckList != null && Header.AckList.Length > 0) { Header.AcksToBytes(bytes, ref i); }

            return bytes;
        }

        public override byte[][] ToBytesMultiple() {
            return new byte[][] {ToBytes()};
        }
    }
}