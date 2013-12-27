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

using System.Collections.Generic;
using System.IO;
using BitCoinSharp.Core.Exceptions;
using BitCoinSharp.Core.Model;
using BitCoinSharp.Core.IO;

namespace BitCoinSharp.Core.Messages
{
    /// <summary>
    /// Abstract super class of classes with list based payload, i.e. InventoryMessage and GetDataMessage.
    /// </summary>
    public abstract class ListMessage : Message
    {
        // For some reason the compiler complains if this is inside InventoryItem

        private const ulong MaxInventoryItems = 50000;

        /// <exception cref="ProtocolException"/>
        protected ListMessage(NetworkParameters networkParameters, byte[] bytes)
            : base(networkParameters, bytes, 0)
        {
        }

        protected ListMessage(NetworkParameters networkParameters)
            : base(networkParameters)
        {
            Items = new List<InventoryItem>();
        }

        public IList<InventoryItem> Items { get; private set; }

        public void AddItem(InventoryItem item)
        {
            Items.Add(item);
        }

        /// <exception cref="ProtocolException"/>
        protected override void Parse()
        {
            // An inv is vector<CInv> where CInv is int+hash. The int is either 1 or 2 for tx or block.
            var arrayLength = ReadVarInt();
            if (arrayLength > MaxInventoryItems)
                throw new ProtocolException("Too many items in INV message: " + arrayLength);
            Items = new List<InventoryItem>((int) arrayLength);
            for (var i = 0UL; i < arrayLength; i++)
            {
                if (Cursor + 4 + 32 > Bytes.Length)
                {
                    throw new ProtocolException("Ran off the end of the INV");
                }
                var typeCode = ReadUint32();
                InventoryItem.ItemType type;
                // See ppszTypeName in net.h
                switch (typeCode)
                {
                    case 0:
                        type = InventoryItem.ItemType.Error;
                        break;
                    case 1:
                        type = InventoryItem.ItemType.Transaction;
                        break;
                    case 2:
                        type = InventoryItem.ItemType.Block;
                        break;
                    default:
                        throw new ProtocolException("Unknown CInv type: " + typeCode);
                }
                var item = new InventoryItem(type, ReadHash());
                Items.Add(item);
            }
            Bytes = null;
        }

        /// <exception cref="IOException"/>
        public override void BitcoinSerializeToStream(Stream outputStream)
        {
            outputStream.Write(new VarInt((ulong) Items.Count).Encode());
            foreach (var inventoryItem in Items)
            {
                // Write out the type code.
                Utils.Uint32ToByteStreamLe((uint) inventoryItem.Type, outputStream);
                // And now the hash.
                outputStream.Write(Utils.ReverseBytes(inventoryItem.Hash.Bytes));
            }
        }
    }
}