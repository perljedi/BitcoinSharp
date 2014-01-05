namespace BitcoinSharp.Core.Shared.Enums
{
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
}