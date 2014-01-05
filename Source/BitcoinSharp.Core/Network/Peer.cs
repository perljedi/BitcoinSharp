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
using System.Threading;
using BitcoinSharp.Core.Common.Hashing;
using BitcoinSharp.Core.Exceptions;
using BitcoinSharp.Core.Messages;
using BitcoinSharp.Core.Model;
using log4net;

namespace BitcoinSharp.Core.Network
{
    /// <summary>
    /// A Peer handles the high level communication with a BitCoin node.
    /// </summary>
    /// <remarks>
    /// After making the connection with Connect(), call Run() to start the message handling loop.
    /// </remarks>
    public class Peer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof (Peer));

        private NetworkConnection _connection;
        private readonly NetworkParameters _networkParameters;
        // Whether the peer loop is supposed to be running or not. Set to false during shutdown so the peer loop
        // knows to quit when the socket goes away.
        private bool _running;
        private readonly BlockChain _blockChain;

        // Used to notify clients when the initial block chain download is finished.
        // When we want to download a block or transaction from a peer, the InventoryItem is put here whilst waiting for
        // the response. Synchronized on itself.
        private readonly IList<GetDataFuture<Block>> _pendingGetBlockFutures;

        private readonly uint _bestHeight;

        private readonly PeerAddress _peerAddress;

        /// <summary>
        /// Construct a peer that handles the given network connection and reads/writes from the given block chain. Note that
        /// communication won't occur until you call Connect().
        /// </summary>
        /// <param name="bestHeight">Our current best chain height, to facilitate downloading.</param>
        public Peer(NetworkParameters networkParameters, PeerAddress peerAddress, uint bestHeight, BlockChain blockChain)
        {
            _networkParameters = networkParameters;
            _peerAddress = peerAddress;
            _bestHeight = bestHeight;
            _blockChain = blockChain;
            _pendingGetBlockFutures = new List<GetDataFuture<Block>>();
        }

        /// <summary>
        /// Construct a peer that handles the given network connection and reads/writes from the given block chain. Note that
        /// communication won't occur until you call connect().
        /// </summary>
        public Peer(NetworkParameters networkParameters, PeerAddress peerAddress, BlockChain blockChain)
            : this(networkParameters, peerAddress, 0, blockChain)
        {
        }

        /// <summary>
        /// Called on a Peer thread when a block is received.
        /// </summary>
        /// <remarks>
        /// The block may have transactions or may be a header only once getheaders is implemented.
        /// </remarks>
        public event EventHandler<BlocksDownloadedEventArgs> BlocksDownloaded;

        /// <summary>
        /// Called when a download is started with the initial number of blocks to be downloaded.
        /// </summary>
        public event EventHandler<ChainDownloadStartedEventArgs> ChainDownloadStarted;

        public override string ToString()
        {
            return "Peer(" + _peerAddress.IpAddress + ":" + _peerAddress.Port + ")";
        }

        /// <summary>
        /// Connects to the peer.
        /// </summary>
        /// <exception cref="PeerException">When there is a temporary problem with the peer and we should retry later.</exception>
        public void Connect()
        {
            lock (this)
            {
                try
                {
                    Log.DebugFormat("Creating connection to peer: {0} on network: {1} at blockchain height: {2}", _peerAddress, _networkParameters, _bestHeight);
                    _connection = new NetworkConnection(_peerAddress, _networkParameters, _bestHeight);
                    Log.Info("=========================================================");
                    Log.Info("=========================================================");
                    Log.Info("WE HAVE CONNECTION!");
                    Log.Info("=========================================================");
                    Log.Info("=========================================================");
                }
                catch (IOException ex)
                {
                    throw new PeerException(ex);
                }
                catch (ProtocolException ex)
                {
                    throw new PeerException(ex);
                }
            }
        }

        // For testing
        internal NetworkConnection Connection
        {
            set { _connection = value; }
        }

        /// <summary>
        /// Runs in the peers network loop and manages communication with the peer.
        /// </summary>
        /// <remarks>
        /// Connect() must be called first.
        /// </remarks>
        /// <exception cref="PeerException">When there is a temporary problem with the peer and we should retry later.</exception>
        public void Run()
        {
            // This should be called in the network loop thread for this peer
            if (_connection == null)
                throw new Exception("please call connect() first");

            _running = true;

            try
            {
                while (true)
                {
                    var m = _connection.ReadMessage();
                    var inv = m as InventoryMessage;
                    if (inv != null)
                    {
                        ProcessInv(inv);
                    }
                    else
                    {
                        var block = m as Block;
                        if (block != null)
                        {
                            ProcessBlock(block);
                        }
                        else if (m is AddressMessage)
                        {
                            // We don't care about addresses of the network right now. But in future,
                            // we should save them in the wallet so we don't put too much load on the seed nodes and can
                            // properly explore the network.
                        }
                        else
                        {
                            // TODO: Handle the other messages we can receive.
                            Log.WarnFormat("Received unhandled message: {0}", m);
                        }
                    }
                }
            }
            catch (IOException e)
            {
                if (!_running)
                {
                    // This exception was expected because we are tearing down the socket as part of quitting.
                    Log.Info("Shutting down peer loop");
                }
                else
                {
                    Disconnect();
                    throw new PeerException(e);
                }
            }
            catch (ProtocolException e)
            {
                Disconnect();
                throw new PeerException(e);
            }
            catch (Exception e)
            {
                Disconnect();
                Log.Error("unexpected exception in peer loop", e);
                throw;
            }

            Disconnect();
        }

        /// <exception cref="IOException"/>
        private void ProcessBlock(Block m)
        {
            // This should called in the network loop thread for this peer
            try
            {
                // Was this block requested by getblock?
                lock (_pendingGetBlockFutures)
                {
                    for (var i = 0; i < _pendingGetBlockFutures.Count; i++)
                    {
                        var f = _pendingGetBlockFutures[i];
                        if (f.InventoryItem.Hash.Equals(m.Hash))
                        {
                            // Yes, it was. So pass it through the future.
                            f.SetResult(m);
                            // Blocks explicitly requested don't get sent to the block chain.
                            _pendingGetBlockFutures.RemoveAt(i);
                            return;
                        }
                    }
                }
                // Otherwise it's a block sent to us because the peer thought we needed it, so add it to the block chain.
                // This call will synchronize on blockChain.
                if (_blockChain.Add(m))
                {
                    // The block was successfully linked into the chain. Notify the user of our progress.
                    if (BlocksDownloaded != null)
                    {
                        BlocksDownloaded(this, new BlocksDownloadedEventArgs(m, GetPeerBlocksToGet()));
                    }
                }
                else
                {
                    // This block is unconnected - we don't know how to get from it back to the genesis block yet. That
                    // must mean that there are blocks we are missing, so do another getblocks with a new block locator
                    // to ask the peer to send them to us. This can happen during the initial block chain download where
                    // the peer will only send us 500 at a time and then sends us the head block expecting us to request
                    // the others.

                    // TODO: Should actually request root of orphan chain here.
                    BlockChainDownload(m.Hash);
                }
            }
            catch (VerificationException e)
            {
                // We don't want verification failures to kill the thread.
                Log.Warn("Block verification failed", e);
            }
            catch (ScriptException e)
            {
                // We don't want script failures to kill the thread.
                Log.Warn("Script exception", e);
            }
        }

        /// <exception cref="IOException"/>
        private void ProcessInv(InventoryMessage inv)
        {
            // This should be called in the network loop thread for this peer

            // The peer told us about some blocks or transactions they have. For now we only care about blocks.
            // Note that as we don't actually want to store the entire block chain or even the headers of the block
            // chain, we may end up requesting blocks we already requested before. This shouldn't (in theory) happen
            // enough to be a problem.
            var topBlock = _blockChain.UnconnectedBlock;
            var topHash = (topBlock != null ? topBlock.Hash : null);
            var items = inv.Items;
            if (items.Count == 1 && items[0].Type == InventoryItem.ItemType.Block && topHash != null &&
                items[0].Hash.Equals(topHash))
            {
                // An inv with a single hash containing our most recent unconnected block is a special inv,
                // it's kind of like a tickle from the peer telling us that it's time to download more blocks to catch up to
                // the block chain. We could just ignore this and treat it as a regular inv but then we'd download the head
                // block over and over again after each batch of 500 blocks, which is wasteful.
                BlockChainDownload(topHash);
                return;
            }
            var getdata = new GetDataMessage(_networkParameters);
            var dirty = false;
            foreach (var item in items)
            {
                if (item.Type != InventoryItem.ItemType.Block) continue;
                getdata.AddItem(item);
                dirty = true;
            }
            // No blocks to download. This probably contained transactions instead, but right now we can't prove they are
            // valid so we don't bother downloading transactions that aren't in blocks yet.
            if (!dirty)
                return;
            // This will cause us to receive a bunch of block messages.
            _connection.WriteMessage(getdata);
        }

        /// <summary>
        /// Asks the connected peer for the block of the given hash, and returns a Future representing the answer.
        /// If you want the block right away and don't mind waiting for it, just call .get() on the result. Your thread
        /// will block until the peer answers. You can also use the Future object to wait with a timeout, or just check
        /// whether it's done later.
        /// </summary>
        /// <param name="blockHash">Hash of the block you were requesting.</param>
        /// <exception cref="IOException"/>
        public IAsyncResult BeginGetBlock(Sha256Hash blockHash, AsyncCallback callback, object state)
        {
            var getData = new GetDataMessage(_networkParameters);
            var inventoryItem = new InventoryItem(InventoryItem.ItemType.Block, blockHash);
            getData.AddItem(inventoryItem);
            var future = new GetDataFuture<Block>(inventoryItem, callback, state);
            // Add to the list of things we're waiting for. It's important this come before the network send to avoid
            // race conditions.
            lock (_pendingGetBlockFutures)
            {
                _pendingGetBlockFutures.Add(future);
            }
            _connection.WriteMessage(getData);
            return future;
        }

        public Block EndGetBlock(IAsyncResult asyncResult)
        {
            return ((GetDataFuture<Block>) asyncResult).Get();
        }

        // A GetDataFuture wraps the result of a getblock or (in future) getTransaction so the owner of the object can
        // decide whether to wait forever, wait for a short while or check later after doing other work.
        private class GetDataFuture<T> : IAsyncResult
        {
            private readonly InventoryItem _inventoryItem;
            private readonly AsyncCallback _callback;
            private readonly object _state;
            
            private readonly ManualResetEvent _waitHandle = new ManualResetEvent(false);
            private T _result;

            internal GetDataFuture(InventoryItem inventoryItem, AsyncCallback callback, object state)
            {
                _inventoryItem = inventoryItem;
                _callback = callback;
                _state = state;
            }

            public bool IsCompleted
            {
                get { return !Equals(_result, default(T)); }
            }

            public WaitHandle AsyncWaitHandle
            {
                get
                {
                    return _waitHandle;
                }
            }

            public object AsyncState
            {
                get { return _state; }
            }

            public bool CompletedSynchronously
            {
                get { return false; }
            }

            internal T Get()
            {
                _waitHandle.WaitOne();
                Debug.Assert(!Equals(_result, default(T)));
                return _result;
            }

            internal InventoryItem InventoryItem
            {
                get { return _inventoryItem; }
            }

            /// <summary>
            /// Called by the Peer when the result has arrived. Completes the task.
            /// </summary>
            internal void SetResult(T result)
            {
                // This should be called in the network loop thread for this peer
                _result = result;
                
                if (_callback != null)
                {
                    _callback(this);
                }
                _waitHandle.Set();
            }
        }

        /// <summary>
        /// Send the given Transaction, ie, make a payment with BitCoins. To create a transaction you can broadcast, use
        /// a <see cref="BitcoinSharp.Wallet.DefaultWallet"/>. After the broadcast completes, confirm the send using the wallet confirmSend() method.
        /// </summary>
        /// <exception cref="IOException"/>
        public void BroadcastTransaction(Transaction transaction)
        {
            _connection.WriteMessage(transaction);
        }

        /// <exception cref="IOException"/>
        // TODO: Figure out a better name for toHash
        private void BlockChainDownload(Sha256Hash toHash)
        {
            // This may run in ANY thread.

            // The block chain download process is a bit complicated. Basically, we start with zero or more blocks in a
            // chain that we have from a previous session. We want to catch up to the head of the chain BUT we don't know
            // where that chain is up to or even if the top block we have is even still in the chain - we
            // might have got ourselves onto a fork that was later resolved by the network.
            //
            // To solve this, we send the peer a block locator which is just a list of block hashes. It contains the
            // blocks we know about, but not all of them, just enough of them so the peer can figure out if we did end up
            // on a fork and if so, what the earliest still valid block we know about is likely to be.
            //
            // Once it has decided which blocks we need, it will send us an inv with up to 500 block messages. We may
            // have some of them already if we already have a block chain and just need to catch up. Once we request the
            // last block, if there are still more to come it sends us an "inv" containing only the hash of the head
            // block.
            //
            // That causes us to download the head block but then we find (in processBlock) that we can't connect
            // it to the chain yet because we don't have the intermediate blocks. So we rerun this function building a
            // new block locator describing where we're up to.
            //
            // The getblocks with the new locator gets us another inv with another bunch of blocks. We download them once
            // again. This time when the peer sends us an inv with the head block, we already have it so we won't download
            // it again - but we recognize this case as special and call back into blockChainDownload to continue the
            // process.
            //
            // So this is a complicated process but it has the advantage that we can download a chain of enormous length
            // in a relatively stateless manner and with constant/bounded memory usage.
            Log.InfoFormat("blockChainDownload({0})", toHash);

            // TODO: Block locators should be abstracted out rather than special cased here.
            var blockLocator = new LinkedList<Sha256Hash>();
            // We don't do the exponential thinning here, so if we get onto a fork of the chain we will end up
            // re-downloading the whole thing again.
            blockLocator.AddLast(_networkParameters.GenesisBlock.Hash);
            var topBlock = _blockChain.ChainHead.BlockHeader;
            if (!topBlock.Equals(_networkParameters.GenesisBlock))
                blockLocator.AddFirst(topBlock.Hash);
            var message = new GetBlocksMessage(_networkParameters, blockLocator.ToList(), toHash);
            _connection.WriteMessage(message);
        }

        /// <summary>
        /// Starts an asynchronous download of the block chain. The chain download is deemed to be complete once we've
        /// downloaded the same number of blocks that the peer advertised having in its version handshake message.
        /// </summary>
        /// <exception cref="IOException"/>
        public void StartBlockChainDownload()
        {
            // TODO: peer might still have blocks that we don't have, and even have a heavier
            // chain even if the chain block count is lower.
            if (GetPeerBlocksToGet() > 0)
            {
                if (ChainDownloadStarted != null)
                {
                    ChainDownloadStarted(this, new ChainDownloadStartedEventArgs(GetPeerBlocksToGet()));
                }

                // When we just want as many blocks as possible, we can set the target hash to zero.
                BlockChainDownload(Sha256Hash.ZeroHash);
            }
        }

        private int GetPeerBlocksToGet()
        {
            // Chain will overflow signed int blocks in ~41,000 years.
            var chainHeight = _connection.VersionMessage.BestHeight;
            if (chainHeight <= 0)
            {
                // This should not happen because we shouldn't have given the user a Peer that is to another client-mode
                // node. If that happens it means the user overrode us somewhere.
                return -1;
            }
            var blocksToGet = (int) (chainHeight - _blockChain.ChainHead.Height);
            return blocksToGet;
        }

        /// <summary>
        /// Terminates the network connection and stops the message handling loop.
        /// </summary>
        public void Disconnect()
        {
            lock (this)
            {
                _running = false;
                try
                {
                    // This is the correct way to stop an IO bound loop
                    if (_connection != null)
                        _connection.Shutdown();
                }
                catch (IOException)
                {
                    // Don't care about this.
                }
            }
        }
    }

    /// <summary>
    /// Called on a Peer thread when a block is received.
    /// </summary>
    /// <remarks>
    /// The block may have transactions or may be a header only once getheaders is implemented.
    /// </remarks>
    public class BlocksDownloadedEventArgs : EventArgs
    {
        /// <summary>
        /// The downloaded block.
        /// </summary>
        public Block Block { get; private set; }

        /// <summary>
        /// The number of blocks left to download.
        /// </summary>
        public int BlocksLeft { get; private set; }

        public BlocksDownloadedEventArgs(Block block, int blocksLeft)
        {
            Block = block;
            BlocksLeft = blocksLeft;
        }
    }

    /// <summary>
    /// Called when a download is started with the initial number of blocks to be downloaded.
    /// </summary>
    public class ChainDownloadStartedEventArgs : EventArgs
    {
        /// <summary>
        /// The number of blocks left to download.
        /// </summary>
        public int BlocksLeft { get; private set; }

        public ChainDownloadStartedEventArgs(int blocksLeft)
        {
            BlocksLeft = blocksLeft;
        }
    }
}