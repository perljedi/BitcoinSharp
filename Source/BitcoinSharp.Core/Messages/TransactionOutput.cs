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
using System.Diagnostics;
using System.IO;
using BitcoinSharp.Core.Common.ExtensionMethods;
using BitcoinSharp.Core.Common.ValueTypes;
using BitcoinSharp.Core.Exceptions;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.Shared.Interfaces;
using log4net;

namespace BitcoinSharp.Core.Messages
{
    /// <summary>
    /// A TransactionOutput message contains a scriptPubKey that controls who is able to spend its value. It is a sub-part
    /// of the Transaction message.
    /// </summary>
    [Serializable]
    public class TransactionOutput : AbstractMessage
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof (TransactionOutput));

        // A transaction output has some value and a script used for authenticating that the redeemer is allowed to spend
        // this output.
        private byte[] _scriptBytes;

        // The script bytes are parsed and turned into a Script on demand.
        [NonSerialized] private Script _scriptPublicKey;

        // These fields are Java serialized but not BitCoin serialized. They are used for tracking purposes in our wallet
        // only. If set to true, this output is counted towards our balance. If false and spentBy is null the tx output
        // was owned by us and was sent to somebody else. If false and spentBy is true it means this output was owned by
        // us and used in one of our own transactions (eg, because it is a change output).
        private bool _availableForSpending;

        // A reference to the transaction which holds this output.
        public Transaction ParentTransaction { get; set; }

        /// <summary>
        /// Deserializes a transaction output message. This is usually part of a transaction message.
        /// </summary>
        /// <exception cref="ProtocolException"/>
        public TransactionOutput(NetworkParameters networkParameters, Transaction parentTransaction, byte[] payload,
            int offset)
            : base(networkParameters, payload, offset)
        {
            ParentTransaction = parentTransaction;
            _availableForSpending = true;
        }

        public TransactionOutput(NetworkParameters networkParameters, Transaction parentTransaction, ulong value,
            Address to)
            : base(networkParameters)
        {
            Value = value;
            _scriptBytes = Script.CreateOutputScript(to);
            ParentTransaction = parentTransaction;
            _availableForSpending = true;
        }

        /// <summary>
        /// Used only in creation of the genesis blocks and in unit tests.
        /// </summary>
        public TransactionOutput(NetworkParameters networkParameters, Transaction parentTransaction,
            byte[] scriptBytes)
            : base(networkParameters)
        {
            _scriptBytes = scriptBytes;
            Value = Utils.ToNanoCoins(50, 0);
            ParentTransaction = parentTransaction;
            _availableForSpending = true;
        }

        /// <exception cref="ScriptException"/>
        public Script ScriptPublicKey
        {
            get
            {
                return _scriptPublicKey ??
                       (_scriptPublicKey = new Script(NetworkParameters, _scriptBytes, 0, _scriptBytes.Length));
            }
        }

        /// <exception cref="ProtocolException"/>
        protected override void Parse()
        {
            Value = ReadUint64();
            var scriptLength = (int) ReadVarInt();
            _scriptBytes = ReadBytes(scriptLength);
        }

        /// <exception cref="IOException"/>
        public override void BitcoinSerializeToStream(Stream outputStream)
        {
            Debug.Assert(_scriptBytes != null);
            Utils.Uint64ToByteStreamLe(Value, outputStream);
            // TODO: Move script serialization into the Script class, where it belongs.
            outputStream.Write(new VarInt((ulong) _scriptBytes.Length).Encode());
            outputStream.Write(_scriptBytes);
        }

        /// <summary>
        /// Returns the value of this output in nanocoins. This is the amount of currency that the destination address
        /// receives.
        /// </summary>
        public ulong Value { get; private set; }

        internal int Index
        {
            get
            {
                Debug.Assert(ParentTransaction != null);
                for (var i = 0; i < ParentTransaction.TransactionOutputs.Count; i++)
                {
                    if (ParentTransaction.TransactionOutputs[i] == this)
                        return i;
                }
                // Should never happen.
                throw new Exception("Output linked to wrong parent transaction?");
            }
        }

        /// <summary>
        /// Sets this objects availableToSpend flag to false and the spentBy pointer to the given input.
        /// If the input is null, it means this output was signed over to somebody else rather than one of our own keys.
        /// </summary>
        public void MarkAsSpent(TransactionInput input)
        {
            Debug.Assert(_availableForSpending);
            _availableForSpending = false;
            SpentBy = input;
        }

        internal void MarkAsUnspent()
        {
            _availableForSpending = true;
            SpentBy = null;
        }

        public bool IsAvailableForSpending
        {
            get { return _availableForSpending; }
        }

        public byte[] ScriptBytes
        {
            get { return _scriptBytes; }
        }

        /// <summary>
        /// Returns true if this output is to an address we have the keys for in the wallet.
        /// </summary>
        public bool IsMine(IDefaultWallet defaultWallet)
        {
            try
            {
                var publicKeyHash = ScriptPublicKey.PublicKeyHash;
                return defaultWallet.IsPubKeyHashMine(publicKeyHash);
            }
            catch (ScriptException e)
            {
                Log.ErrorFormat("Could not parse tx output script: {0}", e);
                return false;
            }
        }

        /// <summary>
        /// Returns a human readable debug string.
        /// </summary>
        public override string ToString()
        {
            return "TxOut of " + Utils.BitcoinValueToFriendlyString(Value) + " to " + ScriptPublicKey.ToAddress +
                   " script:" + ScriptPublicKey;
        }

        /// <summary>
        /// Returns the connected input.
        /// </summary>
        public TransactionInput SpentBy { get; private set; }
    }
}