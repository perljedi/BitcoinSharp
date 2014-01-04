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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BitcoinSharp.Core.Common.ExtensionMethods;
using BitcoinSharp.Core.Common.Hashing;
using BitcoinSharp.Core.Common.ValueTypes;
using BitcoinSharp.Core.Exceptions;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.PersistableMessages;

namespace BitcoinSharp.Core.Messages
{
    /// <summary>
    /// A transaction represents the movement of coins from some addresses to some other addresses. It can also represent
    /// the minting of new coins. A Transaction object corresponds to the equivalent in the BitCoin C++ implementation.
    /// </summary>
    /// <remarks>
    /// It implements TWO serialization protocols - the BitCoin proprietary format which is identical to the C++
    /// implementation and is used for reading/writing transactions to the wire and for hashing. It also implements Java
    /// serialization which is used for the wallet. This allows us to easily add extra fields used for our own accounting
    /// or UI purposes.
    /// </remarks>
    [Serializable]
    public class Transaction : AbstractMessage
    {
        // These are serialized in both BitCoin and java serialization.
        private uint _version;
        private List<TransactionInput> _transactionInputs;
        private List<TransactionOutput> _transactionOutputs;
        private uint _lockTime;

        // This is an in memory helper only.
        [NonSerialized] private Sha256Hash _hash;

        internal Transaction(NetworkParameters networkParameters)
            : base(networkParameters)
        {
            _version = 1;
            _transactionInputs = new List<TransactionInput>();
            _transactionOutputs = new List<TransactionOutput>();
            // We don't initialize appearsIn deliberately as it's only useful for transactions stored in the wallet.
        }

        /// <summary>
        /// Creates a transaction from the given serialized bytes, eg, from a block or a tx network message.
        /// </summary>
        /// <exception cref="ProtocolException"/>
        public Transaction(NetworkParameters networkParameters, byte[] payloadBytes)
            : base(networkParameters, payloadBytes, 0)
        {
        }

        /// <summary>
        /// Creates a transaction by reading payload starting from offset bytes in. Length of a transaction is fixed.
        /// </summary>
        /// <exception cref="ProtocolException"/>
        public Transaction(NetworkParameters networkParameters, byte[] payload, int offset)
            : base(networkParameters, payload, offset)
        {
            // inputs/outputs will be created in parse()
        }

        /// <summary>
        /// Returns a read-only list of the inputs of this transaction.
        /// </summary>
        public IList<TransactionInput> TransactionInputs
        {
            get { return _transactionInputs.AsReadOnly(); }
        }

        /// <summary>
        /// Returns a read-only list of the outputs of this transaction.
        /// </summary>
        public IList<TransactionOutput> TransactionOutputs
        {
            get { return _transactionOutputs.AsReadOnly(); }
        }

        /// <summary>
        /// Returns the transaction hash as you see them in the block explorer.
        /// </summary>
        public Sha256Hash Hash
        {
            get
            {
                return _hash ?? (_hash = new Sha256Hash(Utils.ReverseBytes(Utils.DoubleDigest(BitcoinSerialize()))));
            }
        }

        public string HashAsString
        {
            get { return Hash.ToString(); }
        }

        /// <summary>
        /// Calculates the sum of the outputs that are sending coins to a key in the wallet. The flag controls whether to
        /// include spent outputs or not.
        /// </summary>
        internal ulong GetValueSentToMe(DefaultWallet defaultWallet, bool includeSpent)
        {
            // This is tested in WalletTest.
            return
                _transactionOutputs.Where(transactionOutput => transactionOutput.IsMine(defaultWallet))
                    .Where(transactionOutput => includeSpent || transactionOutput.IsAvailableForSpending)
                    .Aggregate(0UL, (current, transactionOutput) => current + transactionOutput.Value);
        }

        /// <summary>
        /// Calculates the sum of the outputs that are sending coins to a key in the wallet.
        /// </summary>
        public ulong GetValueSentToMe(DefaultWallet defaultWallet)
        {
            return GetValueSentToMe(defaultWallet, true);
        }

        /// <summary>
        /// Returns a set of blocks which contain the transaction, or null if this transaction doesn't have that data
        /// because it's not stored in the wallet or because it has never appeared in a block.
        /// </summary>
        internal ICollection<StoredBlock> AppearsIn { get; private set; }

        /// <summary>
        /// Adds the given block to the internal serializable set of blocks in which this transaction appears. This is
        /// used by the wallet to ensure transactions that appear on side chains are recorded properly even though the
        /// block stores do not save the transaction data at all.
        /// </summary>
        internal void AddBlockAppearance(StoredBlock block)
        {
            if (AppearsIn == null)
            {
                AppearsIn = new HashSet<StoredBlock>();
            }
            AppearsIn.Add(block);
        }

        /// <summary>
        /// Calculates the sum of the inputs that are spending coins with keys in the wallet. This requires the
        /// transactions sending coins to those keys to be in the wallet. This method will not attempt to download the
        /// blocks containing the input transactions if the key is in the wallet but the transactions are not.
        /// </summary>
        /// <returns>Sum in nanocoins.</returns>
        /// <exception cref="ScriptException"/>
        public ulong GetValueSentFromMe(DefaultWallet defaultWallet)
        {
            // This is tested in WalletTest.
            return
                _transactionInputs.Select(
                    transactionInput =>
                        (transactionInput.GetConnectedOutput(defaultWallet.Unspent) ??
                         transactionInput.GetConnectedOutput(defaultWallet.Spent)) ??
                        transactionInput.GetConnectedOutput(defaultWallet.Pending))
                    .Where(connected => connected != null)
                    .Where(connected => connected.IsMine(defaultWallet))
                    .Aggregate(0UL, (current, connected) => current + connected.Value);
        }

        internal bool DisconnectInputs()
        {
            return _transactionInputs.Aggregate(false, (current, input) => current | input.Disconnect());
        }

        /// <summary>
        /// Connects all inputs using the provided transactions. If any input cannot be connected returns that input or
        /// null on success.
        /// </summary>
        internal TransactionInput ConnectForReorganize(IDictionary<Sha256Hash, Transaction> transactions)
        {
            return (from transactionInput in _transactionInputs
                where !transactionInput.IsCoinBase
                let result = transactionInput.Connect(transactions, false)
                where result != TransactionInput.ConnectionResult.Success
                where result != TransactionInput.ConnectionResult.NoSuchTx
                select transactionInput).FirstOrDefault();
        }

        /// <returns>true if every output is marked as spent.</returns>
        public bool IsEveryOutputSpent()
        {
            return _transactionOutputs.All(transactionOutput => !transactionOutput.IsAvailableForSpending);
        }

        /// <summary>
        /// These constants are a part of a scriptSig signature on the inputs. They define the details of how a
        /// transaction can be redeemed, specifically, they control how the hash of the transaction is calculated.
        /// </summary>
        /// <remarks>
        /// In the official client, this enum also has another flag, SIGHASH_ANYONECANPAY. In this implementation,
        /// that's kept separate. Only SIGHASH_ALL is actually used in the official client today. The other flags
        /// exist to allow for distributed contracts.
        /// </remarks>
        public enum SigHash
        {
            All, // 1
            None, // 2
            Single, // 3
        }

        /// <exception cref="ProtocolException"/>
        protected override void Parse()
        {
            _version = ReadUint32();
            // First come the inputs.
            var numInputs = ReadVarInt();
            _transactionInputs = new List<TransactionInput>((int) numInputs);
            for (var i = 0UL; i < numInputs; i++)
            {
                var input = new TransactionInput(this.NetworkParameters, this, Bytes, Cursor);
                _transactionInputs.Add(input);
                Cursor += input.MessageSize;
            }
            // Now the outputs
            var numOutputs = ReadVarInt();
            _transactionOutputs = new List<TransactionOutput>((int) numOutputs);
            for (var i = 0UL; i < numOutputs; i++)
            {
                var output = new TransactionOutput(this.NetworkParameters, this, Bytes, Cursor);
                _transactionOutputs.Add(output);
                Cursor += output.MessageSize;
            }
            _lockTime = ReadUint32();
        }

        /// <summary>
        /// A coinbase transaction is one that creates a new coin. They are the first transaction in each block and their
        /// value is determined by a formula that all implementations of BitCoin share. In 2011 the value of a coinbase
        /// transaction is 50 coins, but in future it will be less. A coinbase transaction is defined not only by its
        /// position in a block but by the data in the inputs.
        /// </summary>
        public bool IsCoinBase
        {
            get { return _transactionInputs[0].IsCoinBase; }
        }

        /// <returns>A human readable version of the transaction useful for debugging.</returns>
        public override string ToString()
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.Append("  ");
            stringBuilder.Append(HashAsString);
            stringBuilder.AppendLine();
            if (IsCoinBase)
            {
                string script;
                string script2;
                try
                {
                    script = _transactionInputs[0].ScriptSig.ToString();
                    script2 = _transactionOutputs[0].ScriptPublicKey.ToString();
                }
                catch (ScriptException)
                {
                    script = "???";
                    script2 = "???";
                }
                return "     == COINBASE TXN (scriptSig " + script + ")  (scriptPubKey " + script2 + ")";
            }
            foreach (var transactionInput in _transactionInputs)
            {
                stringBuilder.Append("     ");
                stringBuilder.Append("from ");

                try
                {
                    stringBuilder.Append(transactionInput.ScriptSig.FromAddress);
                }
                catch (Exception e)
                {
                    stringBuilder.Append("[exception: ").Append(e.Message).Append("]");
                    throw;
                }
                stringBuilder.AppendLine();
            }
            foreach (var transactionOutput in _transactionOutputs)
            {
                stringBuilder.Append("       ");
                stringBuilder.Append("to ");
                try
                {
                    var toAddress = new Address(this.NetworkParameters, transactionOutput.ScriptPublicKey.PublicKeyHash);
                    stringBuilder.Append(toAddress);
                    stringBuilder.Append(" ");
                    stringBuilder.Append(Utils.BitcoinValueToFriendlyString(transactionOutput.Value));
                    stringBuilder.Append(" BTC");
                }
                catch (Exception e)
                {
                    stringBuilder.Append("[exception: ").Append(e.Message).Append("]");
                }
                stringBuilder.AppendLine();
            }
            return stringBuilder.ToString();
        }

        /// <summary>
        /// Adds an input to this transaction that imports value from the given output. Note that this input is NOT
        /// complete and after every input is added with addInput() and every output is added with addOutput(),
        /// signInputs() must be called to finalize the transaction and finish the inputs off. Otherwise it won't be
        /// accepted by the network.
        /// </summary>
        // TODO: Rename from
        public void AddInput(TransactionOutput from)
        {
            AddInput(new TransactionInput(this.NetworkParameters, this, from));
        }

        /// <summary>
        /// Adds an input directly, with no checking that it's valid.
        /// </summary>
        public void AddInput(TransactionInput transactionInput)
        {
            _transactionInputs.Add(transactionInput);
        }

        /// <summary>
        /// Adds the given output to this transaction. The output must be completely initialized.
        /// </summary>
        // TODO: Rename to
        public void AddOutput(TransactionOutput to)
        {
            to.ParentTransaction = this;
            _transactionOutputs.Add(to);
        }

        /// <summary>
        /// Once a transaction has some inputs and outputs added, the signatures in the inputs can be calculated. The
        /// signature is over the transaction itself, to prove the redeemer actually created that transaction,
        /// so we have to do this step last.
        /// </summary>
        /// <remarks>
        /// This method is similar to SignatureHash in script.cpp
        /// </remarks>
        /// <param name="hashType">This should always be set to SigHash.ALL currently. Other types are unused. </param>
        /// <param name="defaultWallet">A wallet is required to fetch the keys needed for signing.</param>
        /// <exception cref="ScriptException"/>
        public void SignInputs(SigHash hashType, DefaultWallet defaultWallet)
        {
            Debug.Assert(_transactionInputs.Count > 0);
            Debug.Assert(_transactionOutputs.Count > 0);

            // I don't currently have an easy way to test other modes work, as the official client does not use them.
            Debug.Assert(hashType == SigHash.All);

            // The transaction is signed with the input scripts empty except for the input we are signing. In the case
            // where addInput has been used to set up a new transaction, they are already all empty. The input being signed
            // has to have the connected OUTPUT program in it when the hash is calculated!
            //
            // Note that each input may be claiming an output sent to a different key. So we have to look at the outputs
            // to figure out which key to sign with.

            var signatures = new byte[_transactionInputs.Count][];
            var signingKeys = new EcKey[_transactionInputs.Count];
            for (var i = 0; i < _transactionInputs.Count; i++)
            {
                var transactionInput = _transactionInputs[i];
                Debug.Assert(transactionInput.ScriptBytes.Length == 0, "Attempting to sign a non-fresh transaction");
                // Set the input to the script of its output.
                transactionInput.ScriptBytes = transactionInput.Outpoint.ConnectedPubKeyScript;
                // Find the signing key we'll need to use.
                var connectedPublicKeyHash = transactionInput.Outpoint.ConnectedPubKeyHash;
                var key = defaultWallet.FindKeyFromPublicHash(connectedPublicKeyHash);
                // This assert should never fire. If it does, it means the wallet is inconsistent.
                Debug.Assert(key != null,
                    "Transaction exists in wallet that we cannot redeem: " +
                    Utils.BytesToHexString(connectedPublicKeyHash));
                // Keep the key around for the script creation step below.
                signingKeys[i] = key;
                // The anyoneCanPay feature isn't used at the moment.
                const bool anyoneCanPay = false;
                var hash = HashTransactionForSignature(hashType, anyoneCanPay);
                // Set the script to empty again for the next input.
                transactionInput.ScriptBytes = TransactionInput.EmptyArray;

                // Now sign for the output so we can redeem it. We use the keypair to sign the hash,
                // and then put the resulting signature in the script along with the public key (below).
                using (var byteOutputStream = new MemoryStream())
                {
                    byteOutputStream.Write(key.Sign(hash));
                    byteOutputStream.Write((byte) (((int) hashType + 1) | (0)));
                    signatures[i] = byteOutputStream.ToArray();
                }
            }

            // Now we have calculated each signature, go through and create the scripts. Reminder: the script consists of
            // a signature (over a hash of the transaction) and the complete public key needed to sign for the connected
            // output.
            for (var i = 0; i < _transactionInputs.Count; i++)
            {
                var transactionInput = _transactionInputs[i];
                Debug.Assert(transactionInput.ScriptBytes.Length == 0);
                var signingKey = signingKeys[i];
                transactionInput.ScriptBytes = Script.CreateInputScript(signatures[i], signingKey.PublicKey);
            }

            // Every input is now complete.
        }

        private byte[] HashTransactionForSignature(SigHash sigHash, bool anyoneCanPay)
        {
            using (var outputStream = new MemoryStream())
            {
                BitcoinSerializeToStream(outputStream);
                // We also have to write a hash type.
                var hashType = (uint) sigHash + 1;
                if (anyoneCanPay)
                    hashType |= 0x80;
                Utils.Uint32ToByteStreamLe(hashType, outputStream);
                // Note that this is NOT reversed to ensure it will be signed correctly. If it were to be printed out
                // however then we would expect that it is IS reversed.
                return Utils.DoubleDigest(outputStream.ToArray());
            }
        }

        /// <exception cref="IOException"/>
        public override void BitcoinSerializeToStream(Stream outputStream)
        {
            Utils.Uint32ToByteStreamLe(_version, outputStream);
            outputStream.Write(new VarInt((ulong) _transactionInputs.Count).Encode());
            foreach (var transactionInput in _transactionInputs)
                transactionInput.BitcoinSerializeToStream(outputStream);
            outputStream.Write(new VarInt((ulong) _transactionOutputs.Count).Encode());
            foreach (var transactionOutput in _transactionOutputs)
                transactionOutput.BitcoinSerializeToStream(outputStream);
            Utils.Uint32ToByteStreamLe(_lockTime, outputStream);
        }

        public override bool Equals(object other)
        {
            if (!(other is Transaction)) return false;
            var t = (Transaction) other;

            return t.Hash.Equals(Hash);
        }

        public override int GetHashCode()
        {
            return Hash.GetHashCode();
        }
    }
}