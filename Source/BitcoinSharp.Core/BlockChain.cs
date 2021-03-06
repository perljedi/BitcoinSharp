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
using System.Linq;
using BitcoinSharp.Core.Exceptions;
using BitcoinSharp.Core.Messages;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.PersistableMessages;
using BitcoinSharp.Core.Shared.Interfaces;
using log4net;
using Org.BouncyCastle.Math;


namespace BitcoinSharp.Core
{
    /// <summary>
    ///     A BlockChain holds a series of <see cref="Block" /> objects, links them together, and knows how to verify that the
    ///     chain follows the rules of the <see cref="NetworkParameters" /> for this chain.
    /// </summary>
    /// <remarks>
    ///     A BlockChain requires a <see cref="BitcoinSharp.Wallet.DefaultWallet" /> to receive transactions that it finds during the initial download.
    ///     However,
    ///     if you don't care about this, you can just pass in an empty wallet and nothing bad will happen.<p />
    ///     A newly constructed BlockChain is empty. To fill it up, use a <see cref="Peer" /> object to download the chain from
    ///     the
    ///     network.<p />
    ///     <b>Notes</b><p />
    ///     The 'chain' can actually be a tree although in normal operation it can be thought of as a simple list. In such a
    ///     situation there are multiple stories of the economy competing to become the one true consensus. This can happen
    ///     naturally when two miners solve a block within a few seconds of each other, or it can happen when the chain is
    ///     under attack.<p />
    ///     A reference to the head block of every chain is stored. If you can reach the genesis block by repeatedly walking
    ///     through the prevBlock pointers, then we say this is a full chain. If you cannot reach the genesis block we say it
    ///     is
    ///     an orphan chain.<p />
    ///     Orphan chains can occur when blocks are solved and received during the initial block chain download,
    ///     or if we connect to a peer that doesn't send us blocks in order.
    /// </remarks>
    public class BlockChain
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof (BlockChain));

        /// <summary>
        ///     Keeps a map of block hashes to StoredBlocks.
        /// </summary>
        private readonly IBlockStore _blockStore;

        /// <summary>
        ///     Tracks the top of the best known chain.
        /// </summary>
        /// <remarks>
        ///     Following this one down to the genesis block produces the story of the economy from the creation of BitCoin
        ///     until the present day. The chain head can change if a new set of blocks is received that results in a chain of
        ///     greater work than the one obtained by following this one down. In that case a reorganize is triggered,
        ///     potentially invalidating transactions in our wallet.
        /// </remarks>
        private StoredBlock _chainHead;

        private readonly NetworkParameters _networkParameters;
        private readonly IList<IDefaultWallet> _wallets;

        // Holds blocks that we have received but can't plug into the chain yet, eg because they were created whilst we
        // were downloading the block chain.
        private readonly IList<Block> _unconnectedBlocks = new List<Block>();

        /// <summary>
        ///     Constructs a BlockChain connected to the given wallet and store. To obtain a <see cref="BitcoinSharp.Wallet.DefaultWallet" /> you can
        ///     construct
        ///     one from scratch, or you can deserialize a saved wallet from disk using <see cref="BitcoinSharp.Wallet.DefaultWallet.LoadFromFile" />.
        /// </summary>
        /// <remarks>
        ///     For the store you can use a <see cref="BitcoinSharp.Blockchain.Store.MemoryBlockStore" /> if you don't care about saving the downloaded data, or
        ///     a
        ///     <see cref="BitcoinSharp.Blockchain.Store.BoundedOverheadBlockStore" /> if you'd like to ensure fast start-up the next time you run the program.
        /// </remarks>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        public BlockChain(NetworkParameters networkParameters, IDefaultWallet defaultWallet, IBlockStore blockStore)
            : this(networkParameters, new List<IDefaultWallet>(), blockStore)
        {
            if (defaultWallet != null)
                AddWallet(defaultWallet);
        }

        /// <summary>
        ///     Constructs a BlockChain that has no wallet at all. This is helpful when you don't actually care about sending
        ///     and receiving coins but rather, just want to explore the network data structures.
        /// </summary>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        public BlockChain(NetworkParameters networkParameters, IBlockStore blockStore)
            : this(networkParameters, new List<IDefaultWallet>(), blockStore)
        {
        }

        /// <summary>
        ///     Constructs a BlockChain connected to the given list of wallets and a store.
        /// </summary>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        public BlockChain(NetworkParameters networkParameters, IEnumerable<IDefaultWallet> wallets, IBlockStore blockStore)
        {
            _blockStore = blockStore;
            _chainHead = blockStore.GetChainHead();
            Log.InfoFormat("chain head is:{0}{1}", Environment.NewLine, _chainHead.BlockHeader);
            _networkParameters = networkParameters;
            _wallets = new List<IDefaultWallet>(wallets);
        }

        /// <summary>
        ///     Add a wallet to the BlockChain. Note that the wallet will be unaffected by any blocks received while it
        ///     was not part of this BlockChain. This method is useful if the wallet has just been created, and its keys
        ///     have never been in use, or if the wallet has been loaded along with the BlockChain
        /// </summary>
        public void AddWallet(IDefaultWallet defaultWallet)
        {
            lock (this)
            {
                _wallets.Add(defaultWallet);
            }
        }

        /// <summary>
        ///     Processes a received block and tries to add it to the chain. If there's something wrong with the block an
        ///     exception is thrown. If the block is OK but cannot be connected to the chain at this time, returns false.
        ///     If the block can be connected to the chain, returns true.
        /// </summary>
        /// <exception cref="VerificationException" />
        /// <exception cref="ScriptException" />
        public bool Add(Block block)
        {
            lock (this)
            {
                return Add(block, true);
            }
        }

        // Stat counters.
        private int _statsLastTime = Environment.TickCount;
        private long _statsBlocksAdded;

        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        /// <exception cref="VerificationException" />
        /// <exception cref="ScriptException" />
        private bool Add(Block block, bool tryConnecting)
        {
            lock (this)
            {
                if (Environment.TickCount - _statsLastTime > 1000)
                {
                    // More than a second passed since last stats logging.
                    Log.InfoFormat("{0} blocks per second", _statsBlocksAdded);
                    _statsLastTime = Environment.TickCount;
                    _statsBlocksAdded = 0;
                }
                // We check only the chain head for double adds here to avoid potentially expensive block chain misses.
                if (block.Equals(_chainHead.BlockHeader))
                {
                    // Duplicate add of the block at the top of the chain, can be a natural artifact of the download process.
                    return true;
                }

                // Does this block contain any transactions we might care about? Check this up front before verifying the
                // blocks validity so we can skip the merkle root verification if the contents aren't interesting. This saves
                // a lot of time for big blocks.
                var contentsImportant = false;
                var walletToTransactionMap = new Dictionary<IDefaultWallet, List<Transaction>>();
                if (block.Transactions != null)
                {
                    ScanTransactions(block, walletToTransactionMap);
                    contentsImportant = walletToTransactionMap.Count > 0;
                }

                // Prove the block is internally valid: hash is lower than target, etc. This only checks the block contents
                // if there is a tx sending or receiving coins using an address in one of our wallets. And those transactions
                // are only lightly verified: presence in a valid connecting block is taken as proof of validity. See the
                // article here for more details: http://code.google.com/p/bitcoinj/wiki/SecurityModel
                try
                {
                    block.VerifyHeader();
                    if (contentsImportant)
                        block.VerifyTransactions();
                }
                catch (VerificationException e)
                {
                    Log.Error("Failed to verify block:", e);
                    Log.Error(block.HashAsString);
                    throw;
                }

                // Try linking it to a place in the currently known blocks.
                var previousStoredBlock = _blockStore.Get(block.PreviousBlockHash);

                if (previousStoredBlock == null)
                {
                    // We can't find the previous block. Probably we are still in the process of downloading the chain and a
                    // block was solved whilst we were doing it. We put it to one side and try to connect it later when we
                    // have more blocks.
                    Log.WarnFormat("Block does not connect: {0}", block.HashAsString);
                    _unconnectedBlocks.Add(block);
                    return false;
                }
                // It connects to somewhere on the chain. Not necessarily the top of the best known chain.
                //
                // Create a new StoredBlock from this block. It will throw away the transaction data so when block goes
                // out of scope we will reclaim the used memory.
                var newStoredBlock = previousStoredBlock.Build(block);
                CheckDifficultyTransitions(previousStoredBlock, newStoredBlock);
                _blockStore.Put(newStoredBlock);
                ConnectBlock(newStoredBlock, previousStoredBlock, walletToTransactionMap);

                if (tryConnecting)
                    TryConnectingUnconnected();

                _statsBlocksAdded++;
                return true;
            }
        }

        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        /// <exception cref="VerificationException" />
        private void ConnectBlock(StoredBlock newStoredBlock, StoredBlock previousStoredBlock,
            IEnumerable<KeyValuePair<IDefaultWallet, List<Transaction>>> newTransactions)
        {
            if (previousStoredBlock.Equals(_chainHead))
            {
                // This block connects to the best known block, it is a normal continuation of the system.
                ChainHead = newStoredBlock;
                Log.DebugFormat("Chain is now {0} blocks high", _chainHead.Height);
                if (newTransactions != null)
                    SendTransactionsToWallet(newStoredBlock, NewBlockType.BestChain, newTransactions);
            }
            else
            {
                // This block connects to somewhere other than the top of the best known chain. We treat these differently.
                //
                // Note that we send the transactions to the wallet FIRST, even if we're about to re-organize this block
                // to become the new best chain head. This simplifies handling of the re-org in the Wallet class.
                var haveNewBestChain = newStoredBlock.MoreWorkThan(_chainHead);
                if (haveNewBestChain)
                {
                    Log.Info("Block is causing a re-organize");
                }
                else
                {
                    var splitPoint = FindSplit(newStoredBlock, _chainHead);
                    var splitPointHash = splitPoint != null ? splitPoint.BlockHeader.HashAsString : "?";
                    Log.InfoFormat("Block forks the chain at {0}, but it did not cause a reorganize:{1}{2}",
                        splitPointHash, Environment.NewLine, newStoredBlock);
                }

                // We may not have any transactions if we received only a header. That never happens today but will in
                // future when GetHeaders is used as an optimization.
                if (newTransactions != null)
                {
                    SendTransactionsToWallet(newStoredBlock, NewBlockType.SideChain, newTransactions);
                }

                if (haveNewBestChain)
                    HandleNewBestChain(newStoredBlock);
            }
        }

        /// <summary>
        ///     Called as part of connecting a block when the new block results in a different chain having higher total work.
        /// </summary>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        /// <exception cref="VerificationException" />
        private void HandleNewBestChain(StoredBlock newChainHead)
        {
            // This chain has overtaken the one we currently believe is best. Reorganize is required.
            //
            // Firstly, calculate the block at which the chain diverged. We only need to examine the
            // chain from beyond this block to find differences.
            var splitPoint = FindSplit(newChainHead, _chainHead);
            Log.InfoFormat("Re-organize after split at height {0}", splitPoint.Height);
            Log.InfoFormat("Old chain head: {0}", _chainHead.BlockHeader.HashAsString);
            Log.InfoFormat("New chain head: {0}", newChainHead.BlockHeader.HashAsString);
            Log.InfoFormat("Split at block: {0}", splitPoint.BlockHeader.HashAsString);
            // Then build a list of all blocks in the old part of the chain and the new part.
            var oldBlocks = GetPartialChain(_chainHead, splitPoint);
            var newBlocks = GetPartialChain(newChainHead, splitPoint);
            // Now inform the wallet. This is necessary so the set of currently active transactions (that we can spend)
            // can be updated to take into account the re-organize. We might also have received new coins we didn't have
            // before and our previous spends might have been undone.
            foreach (var wallet in _wallets)
            {
                wallet.Reorganize(oldBlocks, newBlocks);
            }
            // Update the pointer to the best known block.
            ChainHead = newChainHead;
        }

        /// <summary>
        ///     Returns the set of contiguous blocks between 'higher' and 'lower'. Higher is included, lower is not.
        /// </summary>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        private IList<StoredBlock> GetPartialChain(StoredBlock higher, StoredBlock lower)
        {
            Debug.Assert(higher.Height > lower.Height);
            var results = new LinkedList<StoredBlock>();
            var cursor = higher;
            while (true)
            {
                results.AddLast(cursor);
                cursor = cursor.GetPrev(_blockStore);
                Debug.Assert(cursor != null, "Ran off the end of the chain");
                if (cursor.Equals(lower)) break;
            }
            return results.ToList();
        }

        /// <summary>
        ///     Locates the point in the chain at which newStoredBlock and chainHead diverge. Returns null if no split point was
        ///     found (ie they are part of the same chain).
        /// </summary>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        private StoredBlock FindSplit(StoredBlock newChainHead, StoredBlock chainHead)
        {
            var currentChainCursor = chainHead;
            var newChainCursor = newChainHead;
            // Loop until we find the block both chains have in common. Example:
            //
            //    A -> B -> C -> D
            //         \--> E -> F -> G
            //
            // findSplit will return block B. chainHead = D and newChainHead = G.
            while (!currentChainCursor.Equals(newChainCursor))
            {
                if (currentChainCursor.Height > newChainCursor.Height)
                {
                    currentChainCursor = currentChainCursor.GetPrev(_blockStore);
                    Debug.Assert(newChainCursor != null, "Attempt to follow an orphan chain");
                }
                else
                {
                    newChainCursor = newChainCursor.GetPrev(_blockStore);
                    Debug.Assert(currentChainCursor != null, "Attempt to follow an orphan chain");
                }
            }
            return currentChainCursor;
        }

        public enum NewBlockType
        {
            BestChain,
            SideChain
        }

        /// <exception cref="VerificationException" />
        private static void SendTransactionsToWallet(StoredBlock block, NewBlockType blockType,
            IEnumerable<KeyValuePair<IDefaultWallet, List<Transaction>>> newTransactions)
        {
            foreach (var item in newTransactions)
            {
                try
                {
                    foreach (var transaction in item.Value)
                    {
                        item.Key.Receive(transaction, block, blockType);
                    }
                }
                catch (ScriptException e)
                {
                    // We don't want scripts we don't understand to break the block chain so just note that this tx was
                    // not scanned here and continue.
                    Log.WarnFormat("Failed to parse a script: {0}", e);
                }
            }
        }

        /// <summary>
        ///     For each block in unconnectedBlocks, see if we can now fit it on top of the chain and if so, do so.
        /// </summary>
        /// <exception cref="VerificationException" />
        /// <exception cref="ScriptException" />
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        private void TryConnectingUnconnected()
        {
            // For each block in our unconnected list, try and fit it onto the head of the chain. If we succeed remove it
            // from the list and keep going. If we changed the head of the list at the end of the round try again until
            // we can't fit anything else on the top.
            int blocksConnectedThisRound;
            do
            {
                blocksConnectedThisRound = 0;
                foreach (var block in _unconnectedBlocks.ToList())
                {
                    var prev = _blockStore.Get(block.PreviousBlockHash);
                    if (prev == null)
                    {
                        // This is still an unconnected/orphan block.
                        continue;
                    }
                    // Otherwise we can connect it now.
                    // False here ensures we don't recurse infinitely downwards when connecting huge chains.
                    Add(block, false);
                    _unconnectedBlocks.Remove(block);
                    blocksConnectedThisRound++;
                }
                if (blocksConnectedThisRound > 0)
                {
                    Log.InfoFormat("Connected {0} floating blocks.", blocksConnectedThisRound);
                }
            } while (blocksConnectedThisRound > 0);
        }

        /// <summary>
        ///     Throws an exception if the blocks difficulty is not correct.
        /// </summary>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        /// <exception cref="VerificationException" />
        private void CheckDifficultyTransitions(StoredBlock previousStoredBlock, StoredBlock nextStoredBlock)
        {
            var previousBlockHeader = previousStoredBlock.BlockHeader;
            Log.DebugFormat("Previous Block Header: {0}", previousBlockHeader);

            var nextBlockHeader = nextStoredBlock.BlockHeader;
            Log.DebugFormat("Next Block Header: {0}", nextBlockHeader);

            //Check if this is supposed to be a difficulty transition point.
            if ((previousStoredBlock.Height + 1) % _networkParameters.Interval != 0)
            {
                // No ... so check the difficulty didn't actually change.
                if (nextBlockHeader.TargetDifficulty != previousBlockHeader.TargetDifficulty)
                {
                    throw new VerificationException("Unexpected change in difficulty at height " +
                                                    previousStoredBlock.Height +
                                                    ": " + nextBlockHeader.TargetDifficulty.ToString("x") + " vs " +
                                                    previousBlockHeader.TargetDifficulty.ToString("x"));
                }
                //Since this is not a difficulty transition point. return.
                return;
            }

            // We need to find a block far back in the chain. It's OK that this is expensive because it only occurs every
            // two weeks after the initial block chain download.
            var now = Environment.TickCount;
            var cursor = _blockStore.Get(previousBlockHeader.Hash);
            for (var i = 0; i < _networkParameters.Interval - 1; i++)
            {
                if (cursor == null)
                {
                    // This should never happen. If it does, it means we are following an incorrect or busted chain.
                    throw new VerificationException(
                        "Difficulty transition point but we did not find a way back to the genesis block.");
                }
                cursor = _blockStore.Get(cursor.BlockHeader.PreviousBlockHash);
            }
            Log.DebugFormat("Difficulty transition traversal took {0}ms", Environment.TickCount - now);

            var blockIntervalAgo = cursor.BlockHeader;
            var timespan = (int) (previousBlockHeader.TimeSeconds - blockIntervalAgo.TimeSeconds);
            // Limit the adjustment step.
            if (timespan < _networkParameters.TargetTimespan / 4)
                timespan = _networkParameters.TargetTimespan / 4;
            if (timespan > _networkParameters.TargetTimespan * 4)
                timespan = _networkParameters.TargetTimespan * 4;

            var newDifficulty = Utils.DecodeCompactBits(blockIntervalAgo.TargetDifficulty);
            newDifficulty = newDifficulty.Multiply(BigInteger.ValueOf(timespan));
            newDifficulty = newDifficulty.Divide(BigInteger.ValueOf(_networkParameters.TargetTimespan));

            if (newDifficulty.CompareTo(_networkParameters.ProofOfWorkLimit) > 0)
            {
                Log.DebugFormat("Difficulty hit proof of work limit: {0}", newDifficulty.ToString(16));
                newDifficulty = _networkParameters.ProofOfWorkLimit;
            }

            var accuracyBytes = (int) (nextBlockHeader.TargetDifficulty >> 24) - 3;
            var receivedDifficulty = nextBlockHeader.GetDifficultyTargetAsInteger();

            // The calculated difficulty is to a higher precision than received, so reduce here.
            var mask = BigInteger.ValueOf(0xFFFFFF).ShiftLeft(accuracyBytes * 8);
            newDifficulty = newDifficulty.And(mask);

            if (newDifficulty.CompareTo(receivedDifficulty) != 0)
            {
                throw new VerificationException("Network provided difficulty bits do not match what was calculated: " +
                                                "\r\n" + receivedDifficulty.ToString(16) +
                                                " = next block difficulty\r\n" + newDifficulty.ToString(16) +
                                                " = calculated difficulty");
            }
        }

        /// <summary>
        ///     For the transactions in the given block, update the txToWalletMap such that each wallet maps to a list of
        ///     transactions for which it is relevant.
        /// </summary>
        /// <exception cref="VerificationException" />
        private void ScanTransactions(Block block, IDictionary<IDefaultWallet, List<Transaction>> walletToTransactionMap)
        {
            foreach (var transaction in block.Transactions)
            {
                try
                {
                    foreach (var wallet in _wallets)
                    {
                        var shouldReceive =
                            transaction.TransactionOutputs.Where(output => !output.ScriptPublicKey.IsSentToIp)
                                .Any(output => output.IsMine(wallet));

                        // Coinbase transactions don't have anything useful in their inputs (as they create coins out of thin air).
                        if (!shouldReceive && !transaction.IsCoinBase)
                        {
                            foreach (var transactionInput in transaction.TransactionInputs)
                            {
                                var publicKey = transactionInput.ScriptSig.PublicKey;
                                // This is not thread safe as a key could be removed between the call to isPubKeyMine and receive.
                                if (wallet.IsPublicKeyMine(publicKey))
                                {
                                    shouldReceive = true;
                                }
                            }
                        }

                        if (!shouldReceive) continue;
                        List<Transaction> transactions;
                        if (!walletToTransactionMap.TryGetValue(wallet, out transactions))
                        {
                            transactions = new List<Transaction>();
                            walletToTransactionMap[wallet] = transactions;
                        }
                        transactions.Add(transaction);
                    }
                }
                catch (ScriptException e)
                {
                    // We don't want scripts we don't understand to break the block chain so just note that this tx was
                    // not scanned here and continue.
                    Log.Warn("Failed to parse a script: " + e);
                }
            }
        }

        /// <summary>
        ///     Returns the block at the head of the current best chain. This is the block which represents the greatest
        ///     amount of cumulative work done.
        /// </summary>
        /// <exception cref="BitcoinSharp.Blockchain.Store.BlockStoreException" />
        public StoredBlock ChainHead
        {
            get
            {
                lock (this)
                {
                    return _chainHead;
                }
            }
            private set
            {
                _blockStore.SetChainHead(value);
                _chainHead = value;
            }
        }

        /// <summary>
        ///     Returns the most recent unconnected block or null if there are none. This will all have to change.
        /// </summary>
        public Block UnconnectedBlock
        {
            get
            {
                lock (this)
                {
                    return _unconnectedBlocks.LastOrDefault();
                }
            }
        }
    }
}