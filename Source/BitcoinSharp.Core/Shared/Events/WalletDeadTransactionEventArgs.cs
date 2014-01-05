using System;
using BitcoinSharp.Core.Messages;

namespace BitcoinSharp.Core.Shared.Events
{
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