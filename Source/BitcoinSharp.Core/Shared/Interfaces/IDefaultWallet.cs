using System;
using System.Collections.Generic;
using System.IO;
using BitcoinSharp.Core.Common.Hashing;
using BitcoinSharp.Core.Messages;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.PersistableMessages;
using BitcoinSharp.Core.Shared.Enums;
using BitcoinSharp.Core.Shared.Events;

namespace BitcoinSharp.Core.Shared.Interfaces
{
    public interface IDefaultWallet
    {
        /// <summary>
        /// Map of txhash-&gt;Transactions that have not made it into the best chain yet. They are eligible to move there but
        /// are waiting for a miner to send a block on the best chain including them. These transactions inputs count as
        /// spent for the purposes of calculating our balance but their outputs are not available for spending yet. This
        /// means after a spend, our balance can actually go down temporarily before going up again!
        /// </summary>

        IDictionary<Sha256Hash, Transaction> Pending { get; }

        /// <summary>
        /// Map of txhash-&gt;Transactions where the Transaction has unspent outputs. These are transactions we can use
        /// to pay other people and so count towards our balance. Transactions only appear in this map if they are part
        /// of the best chain. Transactions we have broadcast that are not confirmed yet appear in pending even though they
        /// may have unspent "change" outputs.
        /// </summary>
        /// <remarks>
        /// Note: for now we will not allow spends of transactions that did not make it into the block chain. The code
        /// that handles this in BitCoin C++ is complicated. Satoshi's code will not allow you to spend unconfirmed coins,
        /// however, it does seem to support dependency resolution entirely within the context of the memory WalletPool so
        /// theoretically you could spend zero-conf coins and all of them would be included together. To simplify we'll
        /// make people wait but it would be a good improvement to resolve this in future.
        /// </remarks>
        IDictionary<Sha256Hash, Transaction> Unspent { get; }


        /// <summary>
        /// Map of txhash-&gt;Transactions where the Transactions outputs are all fully spent. They are kept separately so
        /// the time to create a spend does not grow infinitely as wallets become more used. Some of these transactions
        /// may not have appeared in a block yet if they were created by us to spend coins and that spend is still being
        /// worked on by miners.
        /// </summary>
        /// <remarks>
        /// Transactions only appear in this map if they are part of the best chain.
        /// </remarks>
        IDictionary<Sha256Hash, Transaction> Spent { get; }

        /// <summary>
        /// A list of public/private EC keys owned by this user.
        /// </summary>
        IList<EcKey> Keychain { get; }

        /// <summary>
        /// Returns an immutable view of the transactions currently waiting for network confirmations.
        /// </summary>
        ICollection<Transaction> PendingTransactions { get; }

        /// <summary>
        /// Uses Java serialization to save the wallet to the given file.
        /// </summary>
        /// <exception cref="System.IO.IOException"/>
        void SaveToFile(FileInfo fileInfo);

        /// <summary>
        /// Uses Java serialization to save the wallet to the given file stream.
        /// </summary>
        /// <exception cref="System.IO.IOException"/>
        void SaveToFileStream(FileStream fileStream);

        /// <summary>
        /// This is called on a Peer thread when a block is received that sends some coins to you. Note that this will
        /// also be called when downloading the block chain as the wallet balance catches up so if you don't want that
        /// register the event listener after the chain is downloaded. It's safe to use methods of wallet during the
        /// execution of this callback.
        /// </summary>
        event EventHandler<WalletCoinsReceivedEventArgs> CoinsReceived;


        /// <summary>
        /// Called by the <see cref="BitcoinSharp.Core.BlockChain"/> when the best chain (representing total work done) has changed. In this case,
        /// we need to go through our transactions and find out if any have become invalid. It's possible for our balance
        /// to go down in this case: money we thought we had can suddenly vanish if the rest of the network agrees it
        /// should be so.
        /// </summary>
        /// <remarks>
        /// The oldBlocks/newBlocks lists are ordered height-wise from top first to bottom last.
        /// </remarks>
        /// <exception cref="BitcoinSharp.Core.Exceptions.VerificationException"/>
        void Reorganize(IList<StoredBlock> oldStoredBlocks, IList<StoredBlock> newStoredBlocks);

        /// <summary>
        /// Called by the <see cref="BitcoinSharp.Core.BlockChain"/> when we receive a new block that sends coins to one of our addresses or
        /// spends coins from one of our addresses (note that a single transaction can do both).
        /// </summary>
        /// <remarks>
        /// This is necessary for the internal book-keeping Wallet does. When a transaction is received that sends us
        /// coins it is added to a WalletPool so we can use it later to create spends. When a transaction is received that
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
        /// <exception cref="BitcoinSharp.Core.Exceptions.VerificationException"/>
        /// <exception cref="BitcoinSharp.Core.Exceptions.ScriptException"/>
        void Receive(Transaction transaction, StoredBlock storedBlock, BlockChain.NewBlockType blockType);

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
        event EventHandler<EventArgs> Reorganized;

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
        event EventHandler<WalletDeadTransactionEventArgs> DeadTransaction;

        /// <summary>
        /// Statelessly creates a transaction that sends the given number of nanocoins to address. The change is sent to
        /// the first address in the wallet, so you must have added at least one key.
        /// </summary>
        /// <remarks>
        /// This method is stateless in the sense that calling it twice with the same inputs will result in two
        /// Transaction objects which are equal. The wallet is not updated to track its pending status or to mark the
        /// coins as spent until confirmSend is called on the result.
        /// </remarks>
        Transaction CreateSend(Address address, ulong nanocoins);


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
        /// A new <see cref="BitcoinSharp.Core.Messages.Transaction"/> or null if we cannot afford this send.
        /// </returns>
        Transaction CreateSend(Address address, ulong nanocoins, Address changeAddress);


        /// <summary>
        /// Call this when we have successfully transmitted the send tx to the network, to update the wallet.
        /// </summary>
        void ConfirmSend(Transaction transaction);

        /// <summary>
        /// Sends coins to the given address, via the given <see cref="BitcoinSharp.Core.Network.PeerGroup"/>.
        /// Change is returned to the first key in the wallet.
        /// </summary>
        /// <param name="peerGroup">The peer group to send via.</param>
        /// <param name="toAddress">Which address to send coins to.</param>
        /// <param name="nanocoins">How many nanocoins to send. You can use Utils.toNanoCoins() to calculate this.</param>
        /// <returns>
        /// The <see cref="BitcoinSharp.Core.Messages.Transaction"/> that was created or null if there was insufficient balance to send the coins.
        /// </returns>
        /// <exception cref="System.IO.IOException">If there was a problem broadcasting the transaction.</exception>
        Transaction SendCoins(PeerGroup peerGroup, Address toAddress, ulong nanocoins);

        /// <summary>
        /// Sends coins to the given address, via the given <see cref="BitcoinSharp.Core.Network.Peer"/>.
        /// Change is returned to the first key in the wallet.
        /// </summary>
        /// <param name="peer">The peer to send via.</param>
        /// <param name="toAddress">Which address to send coins to.</param>
        /// <param name="nanocoins">How many nanocoins to send. You can use Utils.ToNanoCoins() to calculate this.</param>
        /// <returns>The <see cref="BitcoinSharp.Core.Messages.Transaction"/> that was created or null if there was insufficient balance to send the coins.</returns>
        /// <exception cref="System.IO.IOException">If there was a problem broadcasting the transaction.</exception>
        Transaction SendCoins(Peer peer, Address toAddress, ulong nanocoins);

        /// <summary>
        /// Adds the given ECKey to the wallet. There is currently no way to delete keys (that would result in coin loss).
        /// </summary>
        void AddKey(EcKey key);

        /// <summary>
        /// Locates a keypair from the keychain given the hash of the public key. This is needed when finding out which
        /// key we need to use to redeem a transaction output.
        /// </summary>
        /// <returns>ECKey object or null if no such key was found.</returns>
        EcKey FindKeyFromPublicHash(byte[] publicKeyHash);

        /// <summary>
        /// Returns true if this wallet contains a public key which hashes to the given hash.
        /// </summary>
        bool IsPubKeyHashMine(byte[] publicKeyHash);

        /// <summary>
        /// Locates a keypair from the keychain given the raw public key bytes.
        /// </summary>
        /// <returns>ECKey or null if no such key was found.</returns>
        EcKey FindKeyFromPublicKey(byte[] pubkey);

        /// <summary>
        /// Returns true if this wallet contains a keypair with the given public key.
        /// </summary>
        bool IsPublicKeyMine(byte[] publicKey);

        /// <summary>
        /// Returns the available balance of this wallet. See <see cref="DefaultWallet.BalanceType.Available"/> for details on what this
        /// means.
        /// </summary>
        /// <remarks>
        /// Note: the estimated balance is usually the one you want to show to the end user - however attempting to
        /// actually spend these coins may result in temporary failure. This method returns how much you can safely
        /// provide to <see cref="DefaultWallet.CreateSend(BitcoinSharp.Core.Address,ulong)"/>.
        /// </remarks>
        ulong GetBalance();

        /// <summary>
        /// Returns the balance of this wallet as calculated by the provided balanceType.
        /// </summary>
        ulong GetBalance(BalanceType balanceType);

        string ToString();



        // This is used only for unit testing, it's an internal API.
        int GetPoolSize(WalletPool walletPool);
    }
}