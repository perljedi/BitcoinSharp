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
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using BitcoinSharp.Core.Common.Hashing;
using BitcoinSharp.Core.Exceptions;
using BitcoinSharp.Core.Messages;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.PersistableMessages;
using log4net;

namespace BitcoinSharp.Core
{
    /// <summary>
    /// A Wallet stores keys and a record of transactions that have not yet been spent. Thus, it is capable of
    /// providing transactions on demand that meet a given combined value.
    /// </summary>
    /// <remarks>
    /// The Wallet is read and written from disk, so be sure to follow the Java serialization versioning rules here. We
    /// use the built in Java serialization to avoid the need to pull in a potentially large (code-size) third party
    /// serialization library.<p/>
    /// </remarks>
    [Serializable]
    public class DefaultWallet
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof (DefaultWallet));

        // Algorithm for movement of transactions between pools. Outbound tx = us spending coins. Inbound tx = us
        // receiving coins. If a tx is both inbound and outbound (spend with change) it is considered outbound for the
        // purposes of the explanation below.
        //
        // 1. Outbound tx is created by us: ->pending
        // 2. Outbound tx that was broadcast is accepted into the main chain:
        //     <-pending  and
        //       If there is a change output  ->unspent
        //       If there is no change output ->spent
        // 3. Outbound tx that was broadcast is accepted into a side chain:
        //     ->inactive  (remains in pending).
        // 4. Inbound tx is accepted into the best chain:
        //     ->unspent/spent
        // 5. Inbound tx is accepted into a side chain:
        //     ->inactive
        //     Whilst it's also 'pending' in some sense, in that miners will probably try and incorporate it into the
        //     best chain, we don't mark it as such here. It'll eventually show up after a re-org.
        // 6. Outbound tx that is pending shares inputs with a tx that appears in the main chain:
        //     <-pending ->dead
        //
        // Re-orgs:
        // 1. Tx is present in old chain and not present in new chain
        //       <-unspent/spent  ->pending
        //       These newly inactive transactions will (if they are relevant to us) eventually come back via receive()
        //       as miners resurrect them and re-include into the new best chain.
        // 2. Tx is not present in old chain and is present in new chain
        //       <-inactive  and  ->unspent/spent
        // 3. Tx is present in new chain and shares inputs with a pending transaction, including those that were resurrected
        //    due to point (1)
        //       <-pending ->dead
        //
        // Balance:
        // 1. Sum up all unspent outputs of the transactions in unspent.
        // 2. Subtract the inputs of transactions in pending.
        // 3. If requested, re-add the outputs of pending transactions that are mine. This is the estimated balance.

        /// <summary>
        /// Map of txhash-&gt;Transactions that have not made it into the best chain yet. They are eligible to move there but
        /// are waiting for a miner to send a block on the best chain including them. These transactions inputs count as
        /// spent for the purposes of calculating our balance but their outputs are not available for spending yet. This
        /// means after a spend, our balance can actually go down temporarily before going up again!
        /// </summary>
        internal IDictionary<Sha256Hash, Transaction> Pending { get; private set; }

        /// <summary>
        /// Map of txhash-&gt;Transactions where the Transaction has unspent outputs. These are transactions we can use
        /// to pay other people and so count towards our balance. Transactions only appear in this map if they are part
        /// of the best chain. Transactions we have broadcast that are not confirmed yet appear in pending even though they
        /// may have unspent "change" outputs.
        /// </summary>
        /// <remarks>
        /// Note: for now we will not allow spends of transactions that did not make it into the block chain. The code
        /// that handles this in BitCoin C++ is complicated. Satoshi's code will not allow you to spend unconfirmed coins,
        /// however, it does seem to support dependency resolution entirely within the context of the memory pool so
        /// theoretically you could spend zero-conf coins and all of them would be included together. To simplify we'll
        /// make people wait but it would be a good improvement to resolve this in future.
        /// </remarks>
        internal IDictionary<Sha256Hash, Transaction> Unspent { get; private set; }

        /// <summary>
        /// Map of txhash-&gt;Transactions where the Transactions outputs are all fully spent. They are kept separately so
        /// the time to create a spend does not grow infinitely as wallets become more used. Some of these transactions
        /// may not have appeared in a block yet if they were created by us to spend coins and that spend is still being
        /// worked on by miners.
        /// </summary>
        /// <remarks>
        /// Transactions only appear in this map if they are part of the best chain.
        /// </remarks>
        internal IDictionary<Sha256Hash, Transaction> Spent { get; private set; }

        /// <summary>
        /// An inactive transaction is one that is seen only in a block that is not a part of the best chain. We keep it
        /// around in case a re-org promotes a different chain to be the best. In this case some (not necessarily all)
        /// inactive transactions will be moved out to unspent and spent, and some might be moved in.
        /// </summary>
        /// <remarks>
        /// Note that in the case where a transaction appears in both the best chain and a side chain as well, it is not
        /// placed in this map. It's an error for a transaction to be in both the inactive pool and unspent/spent.
        /// </remarks>
        private readonly IDictionary<Sha256Hash, Transaction> _inactive;

        /// <summary>
        /// A dead transaction is one that's been overridden by a double spend. Such a transaction is pending except it
        /// will never confirm and so should be presented to the user in some unique way - flashing red for example. This
        /// should nearly never happen in normal usage. Dead transactions can be "resurrected" by re-orgs just like any
        /// other. Dead transactions are not in the pending pool.
        /// </summary>
        private readonly IDictionary<Sha256Hash, Transaction> _dead;

        /// <summary>
        /// A list of public/private EC keys owned by this user.
        /// </summary>
        public IList<EcKey> Keychain { get; private set; }

        private readonly NetworkParameters _networkParameters;

        /// <summary>
        /// Creates a new, empty wallet with no keys and no transactions. If you want to restore a wallet from disk instead,
        /// see loadFromFile.
        /// </summary>
        public DefaultWallet(NetworkParameters networkParameters)
        {
            _networkParameters = networkParameters;
            Keychain = new List<EcKey>();
            Unspent = new Dictionary<Sha256Hash, Transaction>();
            Spent = new Dictionary<Sha256Hash, Transaction>();
            _inactive = new Dictionary<Sha256Hash, Transaction>();
            Pending = new Dictionary<Sha256Hash, Transaction>();
            _dead = new Dictionary<Sha256Hash, Transaction>();
        }

        /// <summary>
        /// Uses Java serialization to save the wallet to the given file.
        /// </summary>
        /// <exception cref="IOException"/>
        public void SaveToFile(FileInfo fileInfo)
        {
            lock (this)
            {
                using (var stream = fileInfo.OpenWrite())
                {
                    SaveToFileStream(stream);
                }
            }
        }

        /// <summary>
        /// Uses Java serialization to save the wallet to the given file stream.
        /// </summary>
        /// <exception cref="IOException"/>
        public void SaveToFileStream(FileStream fileStream)
        {
            lock (this)
            {
                var oos = new BinaryFormatter();
                oos.Serialize(fileStream, this);
            }
        }

        /// <summary>
        /// Returns a wallet deserialized from the given file.
        /// </summary>
        /// <exception cref="IOException"/>
        public static DefaultWallet LoadFromFile(FileInfo fileInfo)
        {
            return LoadFromFileStream(fileInfo.OpenRead());
        }

        /// <summary>
        /// Returns a wallet deserialized from the given file input stream.
        /// </summary>
        /// <exception cref="IOException"/>
        public static DefaultWallet LoadFromFileStream(FileStream fileStream)
        {
            var ois = new BinaryFormatter();
            return (DefaultWallet) ois.Deserialize(fileStream);
        }

        /// <summary>
        /// Called by the <see cref="BlockChain"/> when we receive a new block that sends coins to one of our addresses or
        /// spends coins from one of our addresses (note that a single transaction can do both).
        /// </summary>
        /// <remarks>
        /// This is necessary for the internal book-keeping Wallet does. When a transaction is received that sends us
        /// coins it is added to a pool so we can use it later to create spends. When a transaction is received that
        /// consumes outputs they are marked as spent so they won't be used in future.<p/>
        /// A transaction that spends our own coins can be received either because a spend we created was accepted by the
        /// network and thus made it into a block, or because our keys are being shared between multiple instances and
        /// some other node spent the coins instead. We still have to know about that to avoid accidentally trying to
        /// double spend.<p/>
        /// A transaction may be received multiple times if is included into blocks in parallel chains. The blockType
        /// parameter describes whether the containing block is on the main/best chain or whether it's on a presently
        /// inactive side chain. We must still record these transactions and the blocks they appear in because a future
        /// block might change which chain is best causing a reorganize. A re-org can totally change our balance!
        /// </remarks>
        /// <exception cref="VerificationException"/>
        /// <exception cref="ScriptException"/>
        internal void Receive(Transaction transaction, StoredBlock storedBlock, BlockChain.NewBlockType blockType)
        {
            lock (this)
            {
                Receive(transaction, storedBlock, blockType, false);
            }
        }

        /// <exception cref="VerificationException"/>
        /// <exception cref="ScriptException"/>
        private void Receive(Transaction transaction, StoredBlock storedBlock, BlockChain.NewBlockType blockType,
            bool reorg)
        {
            lock (this)
            {
                // Runs in a peer thread.
                var previousBalance = GetBalance();

                var transactionHash = transaction.Hash;

                var bestChain = blockType == BlockChain.NewBlockType.BestChain;
                var sideChain = blockType == BlockChain.NewBlockType.SideChain;

                var valueSentFromMe = transaction.GetValueSentFromMe(this);
                var valueSentToMe = transaction.GetValueSentToMe(this);
                var valueDifference = (long) (valueSentToMe - valueSentFromMe);

                if (!reorg)
                {
                    Log.InfoFormat("Received tx{0} for {1} BTC: {2}", sideChain ? " on a side chain" : "",
                        Utils.BitcoinValueToFriendlyString(valueDifference), transaction.HashAsString);
                }

                // If this transaction is already in the wallet we may need to move it into a different pool. At the very
                // least we need to ensure we're manipulating the canonical object rather than a duplicate.
                Transaction walletTransaction;
                if (Pending.TryGetValue(transactionHash, out walletTransaction))
                {
                    Pending.Remove(transactionHash);
                    Log.Info("  <-pending");
                    // A transaction we created appeared in a block. Probably this is a spend we broadcast that has been
                    // accepted by the network.
                    //
                    // Mark the tx as appearing in this block so we can find it later after a re-org.
                    walletTransaction.AddBlockAppearance(storedBlock);
                    if (bestChain)
                    {
                        if (valueSentToMe.Equals(0))
                        {
                            // There were no change transactions so this tx is fully spent.
                            Log.Info("  ->spent");
                            Debug.Assert(!Spent.ContainsKey(walletTransaction.Hash),
                                "TX in both pending and spent pools");
                            Spent[walletTransaction.Hash] = walletTransaction;
                        }
                        else
                        {
                            // There was change back to us, or this tx was purely a spend back to ourselves (perhaps for
                            // anonymization purposes).
                            Log.Info("  ->unspent");
                            Debug.Assert(!Unspent.ContainsKey(walletTransaction.Hash),
                                "TX in both pending and unspent pools");
                            Unspent[walletTransaction.Hash] = walletTransaction;
                        }
                    }
                    else if (sideChain)
                    {
                        // The transaction was accepted on an inactive side chain, but not yet by the best chain.
                        Log.Info("  ->inactive");
                        // It's OK for this to already be in the inactive pool because there can be multiple independent side
                        // chains in which it appears:
                        //
                        //     b1 --> b2
                        //        \-> b3
                        //        \-> b4 (at this point it's already present in 'inactive'
                        if (_inactive.ContainsKey(walletTransaction.Hash))
                            Log.Info("Saw a transaction be incorporated into multiple independent side chains");
                        _inactive[walletTransaction.Hash] = walletTransaction;
                        // Put it back into the pending pool, because 'pending' means 'waiting to be included in best chain'.
                        Pending[walletTransaction.Hash] = walletTransaction;
                    }
                }
                else
                {
                    if (!reorg)
                    {
                        // Mark the tx as appearing in this block so we can find it later after a re-org.
                        transaction.AddBlockAppearance(storedBlock);
                    }
                    // This TX didn't originate with us. It could be sending us coins and also spending our own coins if keys
                    // are being shared between different wallets.
                    if (sideChain)
                    {
                        Log.Info("  ->inactive");
                        _inactive[transaction.Hash] = transaction;
                    }
                    else if (bestChain)
                    {
                        ProcessTransactionFromBestChain(transaction);
                    }
                }

                Log.InfoFormat("Balance is now: {0}", Utils.BitcoinValueToFriendlyString(GetBalance()));

                // Inform anyone interested that we have new coins. Note: we may be re-entered by the event listener,
                // so we must not make assumptions about our state after this loop returns! For example,
                // the balance we just received might already be spent!
                if (!reorg && bestChain && valueDifference > 0 && CoinsReceived != null)
                {
                    lock (CoinsReceived)
                    {
                        CoinsReceived(this, new WalletCoinsReceivedEventArgs(transaction, previousBalance, GetBalance()));
                    }
                }
            }
        }

        /// <summary>
        /// Handle when a transaction becomes newly active on the best chain, either due to receiving a new block or a
        /// re-org making inactive transactions active.
        /// </summary>
        /// <exception cref="VerificationException"/>
        private void ProcessTransactionFromBestChain(Transaction transaction)
        {
            // This TX may spend our existing outputs even though it was not pending. This can happen in unit
            // tests and if keys are moved between wallets.
            UpdateForSpends(transaction);
            if (!transaction.GetValueSentToMe(this).Equals(0))
            {
                // It's sending us coins.
                Log.Info("  new tx ->unspent");
                Debug.Assert(!Unspent.ContainsKey(transaction.Hash), "TX was received twice");
                Unspent[transaction.Hash] = transaction;
            }
            else
            {
                // It spent some of our coins and did not send us any.
                Log.Info("  new tx ->spent");
                Debug.Assert(!Spent.ContainsKey(transaction.Hash), "TX was received twice");
                Spent[transaction.Hash] = transaction;
            }
        }

        /// <summary>
        /// Updates the wallet by checking if this TX spends any of our outputs. This is not used normally because
        /// when we receive our own spends, we've already marked the outputs as spent previously (during tx creation) so
        /// there's no need to go through and do it again.
        /// </summary>
        /// <exception cref="VerificationException"/>
        private void UpdateForSpends(Transaction transaction)
        {
            // tx is on the best chain by this point.
            foreach (var transactionInput in transaction.TransactionInputs)
            {
                var result = transactionInput.Connect(Unspent, false);
                if (result == TransactionInput.ConnectionResult.NoSuchTx)
                {
                    // Not found in the unspent map. Try again with the spent map.
                    result = transactionInput.Connect(Spent, false);
                    if (result == TransactionInput.ConnectionResult.NoSuchTx)
                    {
                        // Doesn't spend any of our outputs or is coinbase.
                        continue;
                    }
                }
                if (result == TransactionInput.ConnectionResult.AlreadySpent)
                {
                    // Double spend! This must have overridden a pending tx, or the block is bad (contains transactions
                    // that illegally double spend: should never occur if we are connected to an honest node).
                    //
                    // Work backwards like so:
                    //
                    //   A  -> spent by B [pending]
                    //     \-> spent by C [chain]
                    var doubleSpent = transactionInput.Outpoint.FromTransaction; // == A
                    Debug.Assert(doubleSpent != null);
                    var index = transactionInput.Outpoint.Index;
                    var output = doubleSpent.TransactionOutputs[index];
                    var spentBy = output.SpentBy;
                    Debug.Assert(spentBy != null);
                    var connected = spentBy.ParentTransaction;
                    Debug.Assert(connected != null);
                    if (Pending.Remove(connected.Hash))
                    {
                        Log.InfoFormat("Saw double spend from chain override pending tx {0}", connected.HashAsString);
                        Log.Info("  <-pending ->dead");
                        _dead[connected.Hash] = connected;
                        // Now forcibly change the connection.
                        transactionInput.Connect(Unspent, true);
                        // Inform the event listeners of the newly dead tx.
                        if (DeadTransaction != null)
                        {
                            lock (DeadTransaction)
                            {
                                DeadTransaction(this, new WalletDeadTransactionEventArgs(connected, transaction));
                            }
                        }
                    }
                }
                else if (result == TransactionInput.ConnectionResult.Success)
                {
                    // Otherwise we saw a transaction spend our coins, but we didn't try and spend them ourselves yet.
                    // The outputs are already marked as spent by the connect call above, so check if there are any more for
                    // us to use. Move if not.
                    var connected = transactionInput.Outpoint.FromTransaction;
                    MaybeMoveTransactionToSpent(connected, "prevtx");
                }
            }
        }

        /// <summary>
        /// If the transactions outputs are all marked as spent, and it's in the unspent map, move it.
        /// </summary>
        private void MaybeMoveTransactionToSpent(Transaction transaction, String context)
        {
            if (transaction.IsEveryOutputSpent())
            {
                // There's nothing left I can spend in this transaction.
                if (Unspent.Remove(transaction.Hash))
                {
                    if (Log.IsInfoEnabled)
                    {
                        Log.Info("  " + context + " <-unspent");
                        Log.Info("  " + context + " ->spent");
                    }
                    Spent[transaction.Hash] = transaction;
                }
            }
        }

        /// <summary>
        /// This is called on a Peer thread when a block is received that sends some coins to you. Note that this will
        /// also be called when downloading the block chain as the wallet balance catches up so if you don't want that
        /// register the event listener after the chain is downloaded. It's safe to use methods of wallet during the
        /// execution of this callback.
        /// </summary>
        public event EventHandler<WalletCoinsReceivedEventArgs> CoinsReceived;

        /// <summary>
        /// This is called on a Peer thread when a block is received that triggers a block chain re-organization.
        /// </summary>
        /// <remarks>
        /// A re-organize means that the consensus (chain) of the network has diverged and now changed from what we
        /// believed it was previously. Usually this won't matter because the new consensus will include all our old
        /// transactions assuming we are playing by the rules. However it's theoretically possible for our balance to
        /// change in arbitrary ways, most likely, we could lose some money we thought we had.<p/>
        /// It is safe to use methods of wallet whilst inside this callback.<p/>
        /// TODO: Finish this interface.
        /// </remarks>
        public event EventHandler<EventArgs> Reorganized;

        /// <summary>
        /// This is called on a Peer thread when a transaction becomes <i>dead</i>. A dead transaction is one that has
        /// been overridden by a double spend from the network and so will never confirm no matter how long you wait.
        /// </summary>
        /// <remarks>
        /// A dead transaction can occur if somebody is attacking the network, or by accident if keys are being shared.
        /// You can use this event handler to inform the user of the situation. A dead spend will show up in the BitCoin
        /// C++ client of the recipient as 0/unconfirmed forever, so if it was used to purchase something,
        /// the user needs to know their goods will never arrive.
        /// </remarks>
        public event EventHandler<WalletDeadTransactionEventArgs> DeadTransaction;

        /// <summary>
        /// Call this when we have successfully transmitted the send tx to the network, to update the wallet.
        /// </summary>
        internal void ConfirmSend(Transaction transaction)
        {
            lock (this)
            {
                Debug.Assert(!Pending.ContainsKey(transaction.Hash), "confirmSend called on the same transaction twice");
                Log.InfoFormat("confirmSend of {0}", transaction.HashAsString);
                // Mark the outputs of the used transactions as spent, so we don't try and spend it again.
                foreach (var transactionInput in transaction.TransactionInputs)
                {
                    var connectedOutput = transactionInput.Outpoint.ConnectedOutput;
                    var connectedParentTransaction = connectedOutput.ParentTransaction;
                    connectedOutput.MarkAsSpent(transactionInput);
                    MaybeMoveTransactionToSpent(connectedParentTransaction, "spent tx");
                }
                // Add to the pending pool. It'll be moved out once we receive this transaction on the best chain.
                Pending[transaction.Hash] = transaction;
            }
        }

        // This is used only for unit testing, it's an internal API.
        internal enum Pool
        {
            Unspent,
            Spent,
            Pending,
            Inactive,
            Dead,
            All
        }

        internal int GetPoolSize(Pool pool)
        {
            switch (pool)
            {
                case Pool.Unspent:
                    return Unspent.Count;
                case Pool.Spent:
                    return Spent.Count;
                case Pool.Pending:
                    return Pending.Count;
                case Pool.Inactive:
                    return _inactive.Count;
                case Pool.Dead:
                    return _dead.Count;
                case Pool.All:
                    return Unspent.Count + Spent.Count + Pending.Count + _inactive.Count + _dead.Count;
                default:
                    throw new ArgumentOutOfRangeException("pool");
            }
        }

        /// <summary>
        /// Statelessly creates a transaction that sends the given number of nanocoins to address. The change is sent to
        /// the first address in the wallet, so you must have added at least one key.
        /// </summary>
        /// <remarks>
        /// This method is stateless in the sense that calling it twice with the same inputs will result in two
        /// Transaction objects which are equal. The wallet is not updated to track its pending status or to mark the
        /// coins as spent until confirmSend is called on the result.
        /// </remarks>
        internal Transaction CreateSend(Address address, ulong nanocoins)
        {
            lock (this)
            {
                // For now let's just pick the first key in our keychain. In future we might want to do something else to
                // give the user better privacy here, eg in incognito mode.
                Debug.Assert(Keychain.Count > 0, "Can't send value without an address to use for receiving change");
                var first = Keychain[0];
                return CreateSend(address, nanocoins, first.ToAddress(_networkParameters));
            }
        }

        /// <summary>
        /// Sends coins to the given address, via the given <see cref="PeerGroup"/>.
        /// Change is returned to the first key in the wallet.
        /// </summary>
        /// <param name="peerGroup">The peer group to send via.</param>
        /// <param name="toAddress">Which address to send coins to.</param>
        /// <param name="nanocoins">How many nanocoins to send. You can use Utils.toNanoCoins() to calculate this.</param>
        /// <returns>
        /// The <see cref="Transaction"/> that was created or null if there was insufficient balance to send the coins.
        /// </returns>
        /// <exception cref="IOException">If there was a problem broadcasting the transaction.</exception>
        public Transaction SendCoins(PeerGroup peerGroup, Address toAddress, ulong nanocoins)
        {
            lock (this)
            {
                var transaction = CreateSend(toAddress, nanocoins);
                if (transaction == null) // Not enough money! :-(
                    return null;
                if (!peerGroup.BroadcastTransaction(transaction))
                {
                    throw new IOException("Failed to broadcast tx to all connected peers");
                }

                // TODO - retry logic
                ConfirmSend(transaction);
                return transaction;
            }
        }

        /// <summary>
        /// Sends coins to the given address, via the given <see cref="Peer"/>.
        /// Change is returned to the first key in the wallet.
        /// </summary>
        /// <param name="peer">The peer to send via.</param>
        /// <param name="toAddress">Which address to send coins to.</param>
        /// <param name="nanocoins">How many nanocoins to send. You can use Utils.ToNanoCoins() to calculate this.</param>
        /// <returns>The <see cref="Transaction"/> that was created or null if there was insufficient balance to send the coins.</returns>
        /// <exception cref="IOException">If there was a problem broadcasting the transaction.</exception>
        public Transaction SendCoins(Peer peer, Address toAddress, ulong nanocoins)
        {
            lock (this)
            {
                var transaction = CreateSend(toAddress, nanocoins);
                if (transaction == null) // Not enough money! :-(
                    return null;
                peer.BroadcastTransaction(transaction);
                ConfirmSend(transaction);
                return transaction;
            }
        }

        /// <summary>
        /// Creates a transaction that sends $coins.$cents BTC to the given address.
        /// </summary>
        /// <remarks>
        /// IMPORTANT: This method does NOT update the wallet. If you call createSend again you may get two transactions
        /// that spend the same coins. You have to call confirmSend on the created transaction to prevent this,
        /// but that should only occur once the transaction has been accepted by the network. This implies you cannot have
        /// more than one outstanding sending tx at once.
        /// </remarks>
        /// <param name="address">The BitCoin address to send the money to.</param>
        /// <param name="nanocoins">How much currency to send, in nanocoins.</param>
        /// <param name="changeAddress">
        /// Which address to send the change to, in case we can't make exactly the right value from
        /// our coins. This should be an address we own (is in the keychain).
        /// </param>
        /// <returns>
        /// A new <see cref="Transaction"/> or null if we cannot afford this send.
        /// </returns>
        internal Transaction CreateSend(Address address, ulong nanocoins, Address changeAddress)
        {
            lock (this)
            {
                Log.Info("Creating send tx to " + address + " for " +
                         Utils.BitcoinValueToFriendlyString(nanocoins));
                // To send money to somebody else, we need to do gather up transactions with unspent outputs until we have
                // sufficient value. Many coin selection algorithms are possible, we use a simple but suboptimal one.
                // TODO: Sort coins so we use the smallest first, to combat wallet fragmentation and reduce fees.
                var valueGathered = 0UL;
                var gathered = new LinkedList<TransactionOutput>();
                foreach (var transaction in Unspent.Values)
                {
                    foreach (var transactionOutput in transaction.TransactionOutputs)
                    {
                        if (!transactionOutput.IsAvailableForSpending) continue;
                        if (!transactionOutput.IsMine(this)) continue;
                        gathered.AddLast(transactionOutput);
                        valueGathered += transactionOutput.Value;
                    }
                    if (valueGathered >= nanocoins) break;
                }
                // Can we afford this?
                if (valueGathered < nanocoins)
                {
                    Log.Info("Insufficient value in wallet for send, missing " +
                             Utils.BitcoinValueToFriendlyString(nanocoins - valueGathered));
                    // TODO: Should throw an exception here.
                    return null;
                }
                Debug.Assert(gathered.Count > 0);
                var sendTransaction = new Transaction(_networkParameters);
                sendTransaction.AddOutput(new TransactionOutput(_networkParameters, sendTransaction, nanocoins, address));
                var change = (long) (valueGathered - nanocoins);
                if (change > 0)
                {
                    // The value of the inputs is greater than what we want to send. Just like in real life then,
                    // we need to take back some coins ... this is called "change". Add another output that sends the change
                    // back to us.
                    Log.Info("  with " + Utils.BitcoinValueToFriendlyString((ulong) change) + " coins change");
                    sendTransaction.AddOutput(new TransactionOutput(_networkParameters, sendTransaction, (ulong) change,
                        changeAddress));
                }
                foreach (var output in gathered)
                {
                    sendTransaction.AddInput(output);
                }

                // Now sign the inputs, thus proving that we are entitled to redeem the connected outputs.
                sendTransaction.SignInputs(Transaction.SigHash.All, this);
                Log.InfoFormat("  created {0}", sendTransaction.HashAsString);
                return sendTransaction;
            }
        }

        /// <summary>
        /// Adds the given ECKey to the wallet. There is currently no way to delete keys (that would result in coin loss).
        /// </summary>
        public void AddKey(EcKey key)
        {
            lock (this)
            {
                Debug.Assert(!Keychain.Contains(key));
                Keychain.Add(key);
            }
        }

        /// <summary>
        /// Locates a keypair from the keychain given the hash of the public key. This is needed when finding out which
        /// key we need to use to redeem a transaction output.
        /// </summary>
        /// <returns>ECKey object or null if no such key was found.</returns>
        public EcKey FindKeyFromPublicHash(byte[] publicKeyHash)
        {
            lock (this)
            {
                return Keychain.FirstOrDefault(ecKey => ecKey.PublicKeyHash.SequenceEqual(publicKeyHash));
            }
        }

        /// <summary>
        /// Returns true if this wallet contains a public key which hashes to the given hash.
        /// </summary>
        public bool IsPubKeyHashMine(byte[] publicKeyHash)
        {
            lock (this)
            {
                return FindKeyFromPublicHash(publicKeyHash) != null;
            }
        }

        /// <summary>
        /// Locates a keypair from the keychain given the raw public key bytes.
        /// </summary>
        /// <returns>ECKey or null if no such key was found.</returns>
        public EcKey FindKeyFromPublicKey(byte[] pubkey)
        {
            lock (this)
            {
                return Keychain.FirstOrDefault(ecKey => ecKey.PublicKey.SequenceEqual(pubkey));
            }
        }

        /// <summary>
        /// Returns true if this wallet contains a keypair with the given public key.
        /// </summary>
        public bool IsPublicKeyMine(byte[] publicKey)
        {
            lock (this)
            {
                return FindKeyFromPublicKey(publicKey) != null;
            }
        }

        /// <summary>
        /// It's possible to calculate a wallets balance from multiple points of view. This enum selects which
        /// getBalance() should use.
        /// </summary>
        /// <remarks>
        /// Consider a real-world example: you buy a snack costing $5 but you only have a $10 bill. At the start you have
        /// $10 viewed from every possible angle. After you order the snack you hand over your $10 bill. From the
        /// perspective of your wallet you have zero dollars (AVAILABLE). But you know in a few seconds the shopkeeper
        /// will give you back $5 change so most people in practice would say they have $5 (ESTIMATED).
        /// </remarks>
        public enum BalanceType
        {
            /// <summary>
            /// Balance calculated assuming all pending transactions are in fact included into the best chain by miners.
            /// This is the right balance to show in user interfaces.
            /// </summary>
            Estimated,

            /// <summary>
            /// Balance that can be safely used to create new spends. This is all confirmed unspent outputs minus the ones
            /// spent by pending transactions, but not including the outputs of those pending transactions.
            /// </summary>
            Available
        }

        /// <summary>
        /// Returns the available balance of this wallet. See <see cref="BalanceType.Available"/> for details on what this
        /// means.
        /// </summary>
        /// <remarks>
        /// Note: the estimated balance is usually the one you want to show to the end user - however attempting to
        /// actually spend these coins may result in temporary failure. This method returns how much you can safely
        /// provide to <see cref="CreateSend(Address, ulong)"/>.
        /// </remarks>
        public ulong GetBalance()
        {
            lock (this)
            {
                return GetBalance(BalanceType.Available);
            }
        }

        /// <summary>
        /// Returns the balance of this wallet as calculated by the provided balanceType.
        /// </summary>
        public ulong GetBalance(BalanceType balanceType)
        {
            lock (this)
            {
                var available = (from transaction in Unspent.Values
                    from transactionOutput in transaction.TransactionOutputs
                    where transactionOutput.IsMine(this)
                    where transactionOutput.IsAvailableForSpending
                    select transactionOutput).Aggregate(0UL,
                        (current, transactionOutput) => current + transactionOutput.Value);
                if (balanceType == BalanceType.Available)
                    return available;
                Debug.Assert(balanceType == BalanceType.Estimated);
                // Now add back all the pending outputs to assume the transaction goes through.
                return
                    (from transaction in Pending.Values
                        from transactionOutput in transaction.TransactionOutputs
                        where transactionOutput.IsMine(this)
                        select transactionOutput).Aggregate(available,
                            (current, transactionOutput) => current + transactionOutput.Value);
            }
        }

        public override string ToString()
        {
            lock (this)
            {
                var stringBuilder = new StringBuilder();
                stringBuilder.AppendFormat("Wallet containing {0} BTC in:",
                    Utils.BitcoinValueToFriendlyString(GetBalance()))
                    .AppendLine();
                stringBuilder.AppendFormat("  {0} unspent transactions", Unspent.Count).AppendLine();
                stringBuilder.AppendFormat("  {0} spent transactions", Spent.Count).AppendLine();
                stringBuilder.AppendFormat("  {0} pending transactions", Pending.Count).AppendLine();
                stringBuilder.AppendFormat("  {0} inactive transactions", _inactive.Count).AppendLine();
                stringBuilder.AppendFormat("  {0} dead transactions", _dead.Count).AppendLine();
                // Do the keys.
                stringBuilder.AppendLine().AppendLine("Keys:");
                foreach (var key in Keychain)
                {
                    stringBuilder.Append("  addr:");
                    stringBuilder.Append(key.ToAddress(_networkParameters));
                    stringBuilder.Append(" ");
                    stringBuilder.Append(key);
                    stringBuilder.AppendLine();
                }
                // Print the transactions themselves
                if (Unspent.Count > 0)
                {
                    stringBuilder.AppendLine().AppendLine("UNSPENT:");
                    foreach (var tx in Unspent.Values) stringBuilder.Append(tx);
                }
                if (Spent.Count > 0)
                {
                    stringBuilder.AppendLine().AppendLine("SPENT:");
                    foreach (var tx in Spent.Values) stringBuilder.Append(tx);
                }
                if (Pending.Count > 0)
                {
                    stringBuilder.AppendLine().AppendLine("PENDING:");
                    foreach (var tx in Pending.Values) stringBuilder.Append(tx);
                }
                if (_inactive.Count > 0)
                {
                    stringBuilder.AppendLine().AppendLine("INACTIVE:");
                    foreach (var tx in _inactive.Values) stringBuilder.Append(tx);
                }
                if (_dead.Count > 0)
                {
                    stringBuilder.AppendLine().AppendLine("DEAD:");
                    foreach (var tx in _dead.Values) stringBuilder.Append(tx);
                }
                return stringBuilder.ToString();
            }
        }

        /// <summary>
        /// Called by the <see cref="BlockChain"/> when the best chain (representing total work done) has changed. In this case,
        /// we need to go through our transactions and find out if any have become invalid. It's possible for our balance
        /// to go down in this case: money we thought we had can suddenly vanish if the rest of the network agrees it
        /// should be so.
        /// </summary>
        /// <remarks>
        /// The oldBlocks/newBlocks lists are ordered height-wise from top first to bottom last.
        /// </remarks>
        /// <exception cref="VerificationException"/>
        internal void Reorganize(IList<StoredBlock> oldStoredBlocks, IList<StoredBlock> newStoredBlocks)
        {
            lock (this)
            {
                // This runs on any peer thread with the block chain synchronized.
                //
                // The reorganize functionality of the wallet is tested in ChainSplitTests.
                //
                // For each transaction we track which blocks they appeared in. Once a re-org takes place we have to find all
                // transactions in the old branch, all transactions in the new branch and find the difference of those sets.
                //
                // receive() has been called on the block that is triggering the re-org before this is called.

                Log.Info("  Old part of chain (top to bottom):");
                foreach (var oldStoredBlock in oldStoredBlocks)
                    Log.InfoFormat("    {0}", oldStoredBlock.BlockHeader.HashAsString);
                Log.InfoFormat("  New part of chain (top to bottom):");
                foreach (var newStoredBlock in newStoredBlocks)
                    Log.InfoFormat("    {0}", newStoredBlock.BlockHeader.HashAsString);

                // Transactions that appear in the old chain segment.
                IDictionary<Sha256Hash, Transaction> oldChainTransactions = new Dictionary<Sha256Hash, Transaction>();
                // Transactions that appear in the old chain segment and NOT the new chain segment.
                IDictionary<Sha256Hash, Transaction> onlyOldChainTransactions =
                    new Dictionary<Sha256Hash, Transaction>();
                // Transactions that appear in the new chain segment.
                IDictionary<Sha256Hash, Transaction> newChainTransactions = new Dictionary<Sha256Hash, Transaction>();
                // Transactions that don't appear in either the new or the old section, ie, the shared trunk.
                IDictionary<Sha256Hash, Transaction> commonChainTransactions = new Dictionary<Sha256Hash, Transaction>();

                IDictionary<Sha256Hash, Transaction> all = new Dictionary<Sha256Hash, Transaction>();
                foreach (var pair in Unspent.Concat(Spent).Concat(_inactive))
                {
                    all[pair.Key] = pair.Value;
                }
                foreach (var transaction in all.Values)
                {
                    var appearsIn = transaction.AppearsIn;
                    Debug.Assert(appearsIn != null);
                    // If the set of blocks this transaction appears in is disjoint with one of the chain segments it means
                    // the transaction was never incorporated by a miner into that side of the chain.
                    var inOldSection = appearsIn.Any(oldStoredBlocks.Contains) ||
                                       oldStoredBlocks.Any(appearsIn.Contains);
                    var inNewSection = appearsIn.Any(newStoredBlocks.Contains) ||
                                       newStoredBlocks.Any(appearsIn.Contains);
                    var inCommonSection = !inNewSection && !inOldSection;

                    if (inCommonSection)
                    {
                        Debug.Assert(!commonChainTransactions.ContainsKey(transaction.Hash),
                            "Transaction appears twice in common chain segment");
                        commonChainTransactions[transaction.Hash] = transaction;
                    }
                    else
                    {
                        if (inOldSection)
                        {
                            Debug.Assert(!oldChainTransactions.ContainsKey(transaction.Hash),
                                "Transaction appears twice in old chain segment");
                            oldChainTransactions[transaction.Hash] = transaction;
                            if (!inNewSection)
                            {
                                Debug.Assert(!onlyOldChainTransactions.ContainsKey(transaction.Hash),
                                    "Transaction appears twice in only-old map");
                                onlyOldChainTransactions[transaction.Hash] = transaction;
                            }
                        }
                        if (inNewSection)
                        {
                            Debug.Assert(!newChainTransactions.ContainsKey(transaction.Hash),
                                "Transaction appears twice in new chain segment");
                            newChainTransactions[transaction.Hash] = transaction;
                        }
                    }
                }

                // If there is no difference it means we have nothing we need to do and the user does not care.
                var affectedUs = oldChainTransactions.Count != newChainTransactions.Count ||
                                 !oldChainTransactions.All(
                                     item =>
                                     {
                                         Transaction rightValue;
                                         return newChainTransactions.TryGetValue(item.Key, out rightValue) &&
                                                Equals(item.Value, rightValue);
                                     });
                Log.Info(affectedUs ? "Re-org affected our transactions" : "Re-org had no effect on our transactions");
                if (!affectedUs) return;

                // For simplicity we will reprocess every transaction to ensure it's in the right bucket and has the right
                // connections. Attempting to update each one with minimal work is possible but complex and was leading to
                // edge cases that were hard to fix. As re-orgs are rare the amount of work this implies should be manageable
                // unless the user has an enormous wallet. As an optimization fully spent transactions buried deeper than
                // 1000 blocks could be put into yet another bucket which we never touch and assume re-orgs cannot affect.

                foreach (var transaction in onlyOldChainTransactions.Values)
                    Log.InfoFormat("  Only Old: {0}", transaction.HashAsString);
                foreach (var transaction in oldChainTransactions.Values)
                    Log.InfoFormat("  Old: {0}", transaction.HashAsString);
                foreach (var transaction in newChainTransactions.Values)
                    Log.InfoFormat("  New: {0}", transaction.HashAsString);

                // Break all the existing connections.
                foreach (var transaction in all.Values)
                    transaction.DisconnectInputs();
                foreach (var transaction in Pending.Values)
                    transaction.DisconnectInputs();
                // Reconnect the transactions in the common part of the chain.
                foreach (var transaction in commonChainTransactions.Values)
                {
                    var badInput = transaction.ConnectForReorganize(all);
                    Debug.Assert(badInput == null, "Failed to connect " + transaction.HashAsString + ", " + badInput);
                }
                // Recalculate the unspent/spent buckets for the transactions the re-org did not affect.
                Unspent.Clear();
                Spent.Clear();
                _inactive.Clear();
                foreach (var transaction in commonChainTransactions.Values)
                {
                    var unspentOutputs =
                        transaction.TransactionOutputs.Count(
                            transactionOutput => transactionOutput.IsAvailableForSpending);
                    if (unspentOutputs > 0)
                    {
                        Log.InfoFormat("  TX {0}: ->unspent", transaction.HashAsString);
                        Unspent[transaction.Hash] = transaction;
                    }
                    else
                    {
                        Log.InfoFormat("  TX {0}: ->spent", transaction.HashAsString);
                        Spent[transaction.Hash] = transaction;
                    }
                }
                // Now replay the act of receiving the blocks that were previously in a side chain. This will:
                //   - Move any transactions that were pending and are now accepted into the right bucket.
                //   - Connect the newly active transactions.
                foreach (var newStoredBlock in newStoredBlocks.Reverse())
                    // Need bottom-to-top but we get top-to-bottom.
                {
                    Log.InfoFormat("Replaying block {0}", newStoredBlock.BlockHeader.HashAsString);
                    ICollection<Transaction> transactions = new HashSet<Transaction>();
                    foreach (var transaction in newChainTransactions.Values)
                    {
                        if (transaction.AppearsIn.Contains(newStoredBlock))
                        {
                            transactions.Add(transaction);
                            Log.InfoFormat("  containing tx {0}", transaction.HashAsString);
                        }
                    }
                    foreach (var transaction in transactions)
                    {
                        Receive(transaction, newStoredBlock, BlockChain.NewBlockType.BestChain, true);
                    }
                }

                // Find the transactions that didn't make it into the new chain yet. For each input, try to connect it to the
                // transactions that are in {spent,unspent,pending}. Check the status of each input. For inactive
                // transactions that only send us money, we put them into the inactive pool where they sit around waiting for
                // another re-org or re-inclusion into the main chain. For inactive transactions where we spent money we must
                // put them back into the pending pool if we can reconnect them, so we don't create a double spend whilst the
                // network heals itself.
                IDictionary<Sha256Hash, Transaction> pool = new Dictionary<Sha256Hash, Transaction>();
                foreach (var pair in Unspent.Concat(Spent).Concat(Pending))
                {
                    pool[pair.Key] = pair.Value;
                }
                IDictionary<Sha256Hash, Transaction> toReprocess = new Dictionary<Sha256Hash, Transaction>();
                foreach (var pair in onlyOldChainTransactions.Concat(Pending))
                {
                    toReprocess[pair.Key] = pair.Value;
                }
                Log.Info("Reprocessing:");
                // Note, we must reprocess dead transactions first. The reason is that if there is a double spend across
                // chains from our own coins we get a complicated situation:
                //
                // 1) We switch to a new chain (B) that contains a double spend overriding a pending transaction. It goes dead.
                // 2) We switch BACK to the first chain (A). The dead transaction must go pending again.
                // 3) We resurrect the transactions that were in chain (B) and assume the miners will start work on putting them
                //    in to the chain, but it's not possible because it's a double spend. So now that transaction must become
                //    dead instead of pending.
                //
                // This only occurs when we are double spending our own coins.
                foreach (var transaction in _dead.Values.ToList())
                {
                    ReprocessTransactionAfterReorg(pool, transaction);
                }
                foreach (var transaction in toReprocess.Values)
                {
                    ReprocessTransactionAfterReorg(pool, transaction);
                }

                Log.InfoFormat("post-reorg balance is {0}", Utils.BitcoinValueToFriendlyString(GetBalance()));

                // Inform event listeners that a re-org took place.
                if (Reorganized != null)
                {
                    // Synchronize on the event listener as well. This allows a single listener to handle events from
                    // multiple wallets without needing to worry about being thread safe.
                    lock (Reorganized)
                    {
                        Reorganized(this, EventArgs.Empty);
                    }
                }
            }
        }

        private void ReprocessTransactionAfterReorg(IDictionary<Sha256Hash, Transaction> pool, Transaction transaction)
        {
            Log.InfoFormat("  TX {0}", transaction.HashAsString);
            var numInputs = transaction.TransactionInputs.Count;
            var noSuchTransaction = 0;
            var success = 0;
            var isDead = false;
            foreach (var transactionInput in transaction.TransactionInputs)
            {
                if (transactionInput.IsCoinBase)
                {
                    // Input is not in our wallet so there is "no such input tx", bit of an abuse.
                    noSuchTransaction++;
                    continue;
                }
                var result = transactionInput.Connect(pool, false);
                if (result == TransactionInput.ConnectionResult.Success)
                {
                    success++;
                }
                else if (result == TransactionInput.ConnectionResult.NoSuchTx)
                {
                    noSuchTransaction++;
                }
                else if (result == TransactionInput.ConnectionResult.AlreadySpent)
                {
                    isDead = true;
                    // This transaction was replaced by a double spend on the new chain. Did you just reverse
                    // your own transaction? I hope not!!
                    Log.Info("   ->dead, will not confirm now unless there's another re-org");
                    var doubleSpent = transactionInput.GetConnectedOutput(pool);
                    var replacement = doubleSpent.SpentBy.ParentTransaction;
                    _dead[transaction.Hash] = transaction;
                    Pending.Remove(transaction.Hash);
                    // Inform the event listeners of the newly dead tx.
                    if (DeadTransaction != null)
                    {
                        lock (DeadTransaction)
                        {
                            DeadTransaction(this, new WalletDeadTransactionEventArgs(transaction, replacement));
                        }
                    }
                    break;
                }
            }
            if (isDead) return;

            if (noSuchTransaction == numInputs)
            {
                Log.Info("   ->inactive");
                _inactive[transaction.Hash] = transaction;
            }
            else if (success == numInputs - noSuchTransaction)
            {
                // All inputs are either valid for spending or don't come from us. Miners are trying to re-include it.
                Log.Info("   ->pending");
                Pending[transaction.Hash] = transaction;
                _dead.Remove(transaction.Hash);
            }
        }

        /// <summary>
        /// Returns an immutable view of the transactions currently waiting for network confirmations.
        /// </summary>
        public ICollection<Transaction> PendingTransactions
        {
            get { return Pending.Values; }
        }
    }

    /// <summary>
    /// This is called on a Peer thread when a block is received that sends some coins to you. Note that this will
    /// also be called when downloading the block chain as the wallet balance catches up so if you don't want that
    /// register the event listener after the chain is downloaded. It's safe to use methods of wallet during the
    /// execution of this callback.
    /// </summary>
    public class WalletCoinsReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// The transaction which sent us the coins.
        /// </summary>
        public Transaction Transaction { get; private set; }

        /// <summary>
        /// Balance before the coins were received.
        /// </summary>
        public ulong PreviousBalance { get; private set; }

        /// <summary>
        /// Current balance of the wallet.
        /// </summary>
        public ulong NewBalance { get; private set; }

        /// <param name="transaction">The transaction which sent us the coins.</param>
        /// <param name="previousBalance">Balance before the coins were received.</param>
        /// <param name="newBalance">Current balance of the wallet.</param>
        public WalletCoinsReceivedEventArgs(Transaction transaction, ulong previousBalance, ulong newBalance)
        {
            Transaction = transaction;
            PreviousBalance = previousBalance;
            NewBalance = newBalance;
        }
    }

    /// <summary>
    /// This is called on a Peer thread when a transaction becomes <i>dead</i>. A dead transaction is one that has
    /// been overridden by a double spend from the network and so will never confirm no matter how long you wait.
    /// </summary>
    /// <remarks>
    /// A dead transaction can occur if somebody is attacking the network, or by accident if keys are being shared.
    /// You can use this event handler to inform the user of the situation. A dead spend will show up in the BitCoin
    /// C++ client of the recipient as 0/unconfirmed forever, so if it was used to purchase something,
    /// the user needs to know their goods will never arrive.
    /// </remarks>
    public class WalletDeadTransactionEventArgs : EventArgs
    {
        /// <summary>
        /// The transaction that is newly dead.
        /// </summary>
        public Transaction DeadTransaction { get; private set; }

        /// <summary>
        /// The transaction that killed it.
        /// </summary>
        public Transaction ReplacementTransaction { get; private set; }

        /// <param name="deadTransaction">The transaction that is newly dead.</param>
        /// <param name="replacementTransaction">The transaction that killed it.</param>
        public WalletDeadTransactionEventArgs(Transaction deadTransaction, Transaction replacementTransaction)
        {
            DeadTransaction = deadTransaction;
            ReplacementTransaction = replacementTransaction;
        }
    }
}