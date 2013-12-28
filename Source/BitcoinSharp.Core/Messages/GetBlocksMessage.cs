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
using BitcoinSharp.Core.Common.ExtensionMethods;
using BitcoinSharp.Core.Common.Hashing;
using BitcoinSharp.Core.Common.ValueTypes;
using BitcoinSharp.Core.Network;

namespace BitcoinSharp.Core.Messages
{
    [Serializable]
    public class GetBlocksMessage : AbstractMessage
    {
        private readonly IList<Sha256Hash> _locator;
        private readonly Sha256Hash _stopHash;

        public GetBlocksMessage(NetworkParameters networkParameters, IList<Sha256Hash> locator, Sha256Hash stopHash)
            : base(networkParameters)
        {
            _locator = locator;
            _stopHash = stopHash;
        }

        protected override void Parse()
        {
        }

        public IList<Sha256Hash> Locator
        {
            get { return _locator; }
        }

        public Sha256Hash StopHash
        {
            get { return _stopHash; }
        }

        public override string ToString()
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.Append("getblocks: ");
            foreach (var hash in _locator)
            {
                stringBuilder.Append((object) hash);
                stringBuilder.Append(" ");
            }
            return stringBuilder.ToString();
        }

        public override byte[] BitcoinSerialize()
        {
            using (var outputMemoryStream = new MemoryStream())
            {
                // Version, for some reason.
                Utils.Uint32ToByteStreamLe(NetworkParameters.ProtocolVersion, outputMemoryStream);
                // Then a vector of block hashes. This is actually a "block locator", a set of block
                // identifiers that spans the entire chain with exponentially increasing gaps between
                // them, until we end up at the genesis block. See CBlockLocator::Set()
                outputMemoryStream.Write(new VarInt((ulong) _locator.Count).Encode());
                foreach (var hash in _locator)
                {
                    // Have to reverse as wire format is little endian.
                    outputMemoryStream.Write(Utils.ReverseBytes(hash.Bytes));
                }
                // Next, a block ID to stop at.
                outputMemoryStream.Write(_stopHash.Bytes);
                return outputMemoryStream.ToArray();
            }
        }
    }
}