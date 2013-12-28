/*
 * Copyright 2011 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;
using System.Net;
using BitCoinSharp.Core.Common.DatesAndTimes;
using BitCoinSharp.Core.Common.ExtensionMethods;
using BitCoinSharp.Core.Exceptions;
using BitCoinSharp.Core.Network;
using Org.BouncyCastle.Math;

namespace BitCoinSharp.Core.Messages
{
    /// <summary>
    /// A PeerAddress holds an IP ipAddress and port number representing the network location of
    /// a peer in the BitCoin P2P network. It exists primarily for serialization purposes.
    /// </summary>
    [Serializable]
    public class PeerAddress : AbstractMessage
    {
        internal IPAddress IpAddress { get; private set; }
        internal int Port { get; private set; }
        private ulong _services;
        private uint _time;

        /// <summary>
        /// Construct a peer ipAddress from a serialized payload.
        /// </summary>
        /// <exception cref="ProtocolException"/>
        public PeerAddress(NetworkParameters networkParameters, byte[] payload, int offset, uint protocolVersion)
            : base(networkParameters, payload, offset, protocolVersion)
        {
        }

        /// <summary>
        /// Construct a peer ipAddress from a memorized or hardcoded ipAddress.
        /// </summary>
        public PeerAddress(IPAddress ipAddress, int port, uint protocolVersion)
        {
            IpAddress = ipAddress;
            Port = port;
            ProtocolVersion = protocolVersion;
            _services = 0;
        }

        public PeerAddress(IPAddress ipAddress, int port)
            : this(ipAddress, port, NetworkParameters.ProtocolVersion)
        {
        }

        public PeerAddress(IPAddress ipAddress)
            : this(ipAddress, 0)
        {
        }

        public PeerAddress(IPEndPoint addr)
            : this(addr.Address, addr.Port)
        {
        }

        /// <exception cref="IOException"/>
        public override void BitcoinSerializeToStream(Stream outputStream)
        {
            if (ProtocolVersion >= 31402)
            {
                var secs = SystemTime.UnixNow();
                Utils.Uint32ToByteStreamLe((uint) secs, outputStream);
            }
            Utils.Uint64ToByteStreamLe(_services, outputStream); // nServices.
            // Java does not provide any utility to map an IPv4 ipAddress into IPv6 space, so we have to do it by hand.
            var ipBytes = IpAddress.GetAddressBytes();
            if (ipBytes.Length == 4)
            {
                var ipV6Address = new byte[16];
                Array.Copy(ipBytes, 0, ipV6Address, 12, 4);
                ipV6Address[10] = 0xFF;
                ipV6Address[11] = 0xFF;
                ipBytes = ipV6Address;
            }
            outputStream.Write(ipBytes);
            // And write out the port. Unlike the rest of the protocol, ipAddress and port is in big endian byte order.
            outputStream.Write((byte) (Port >> 8));
            outputStream.Write((byte) Port);
        }

        protected override void Parse()
        {
            // Format of a serialized ipAddress:
            //   uint32 timestamp
            //   uint64 services   (flags determining what the node can do)
            //   16 bytes IP ipAddress
            //   2 bytes port num
            _time = ProtocolVersion > 31402 ? ReadUint32() : uint.MaxValue;
            _services = ReadUint64();
            var addrBytes = ReadBytes(16);
            if (new BigInteger(addrBytes, 0, 12).Equals(BigInteger.ValueOf(0xFFFF)))
            {
                var newBytes = new byte[4];
                Array.Copy(addrBytes, 12, newBytes, 0, 4);
                addrBytes = newBytes;
            }
            IpAddress = new IPAddress(addrBytes);
            Port = (Bytes[Cursor++] << 8) | Bytes[Cursor++];
        }

        public override string ToString()
        {
            return "[" + IpAddress + "]:" + Port;
        }
    }
}