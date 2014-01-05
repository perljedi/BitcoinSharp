using System;
using BitcoinSharp.Core.Messages;

namespace BitcoinSharp.Core.Shared.Events
{
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
}