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
using System.Collections.Generic;
using System.IO;
using System.Text;
using BitCoinSharp.Core.Common.ExtensionMethods;
using BitCoinSharp.Core.Common.ValueTypes;
using BitCoinSharp.Core.Exceptions;
using BitCoinSharp.Core.Network;

namespace BitCoinSharp.Core.Messages
{
    [Serializable]
    public class AddressMessage : AbstractMessage
    {
        private const ulong MaxAddresses = 1024;

        internal IList<PeerAddress> Addresses { get; private set; }

        /// <exception cref="ProtocolException" />
        internal AddressMessage(NetworkParameters networkParameters, byte[] payload, int offset)
            : base(networkParameters, payload, offset)
        {
        }

        /// <exception cref="ProtocolException" />
        internal AddressMessage(NetworkParameters networkParameters, byte[] payload)
            : base(networkParameters, payload, 0)
        {
        }

        /// <exception cref="ProtocolException" />
        protected override void Parse()
        {
            var numAddresses = ReadVarInt();
            // Guard against ultra large messages that will crash us.
            if (numAddresses > MaxAddresses)
                throw new ProtocolException("ipAddress message too large.");
            Addresses = new List<PeerAddress>((int) numAddresses);
            for (var i = 0UL; i < numAddresses; i++)
            {
                var peerAddress = new PeerAddress(NetworkParameters, Bytes, Cursor, ProtocolVersion);
                Addresses.Add(peerAddress);
                Cursor += peerAddress.MessageSize;
            }
        }

        public override void BitcoinSerializeToStream(Stream outputStream)
        {
            outputStream.Write(new VarInt((ulong) Addresses.Count).Encode());
            foreach (var peerAddress in Addresses)
            {
                peerAddress.BitcoinSerializeToStream(outputStream);
            }
        }

        public override string ToString()
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.Append("addr: ");
            foreach (var peerAddress in Addresses)
            {
                stringBuilder.Append((object) peerAddress);
                stringBuilder.Append(" ");
            }
            return stringBuilder.ToString();
        }
    }
}