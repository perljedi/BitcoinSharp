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

using BitcoinSharp.Core;
using BitcoinSharp.Core.Messages;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.Store;
using NUnit.Framework;

namespace BitcoinSharp.Tests.Unit
{
    [TestFixture]
    public class WalletTest
    {
        private static readonly NetworkParameters Params = NetworkParameters.UnitTests();

        private Address _myAddress;
        private DefaultWallet _defaultWallet;
        private IBlockStore _blockStore;

        [SetUp]
        public void SetUp()
        {
            var myKey = new EcKey();
            _myAddress = myKey.ToAddress(Params);
            _defaultWallet = new DefaultWallet(Params);
            _defaultWallet.AddKey(myKey);
            _blockStore = new MemoryBlockStore(Params);
        }

        [TearDown]
        public void TearDown()
        {
            _blockStore.Dispose();
        }

        [Test]
        public void BasicSpending()
        {
            // We'll set up a wallet that receives a coin, then sends a coin of lesser value and keeps the change.
            var v1 = Utils.ToNanoCoins(1, 0);
            var t1 = TestUtils.CreateFakeTx(Params, v1, _myAddress);

            _defaultWallet.Receive(t1, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(v1, _defaultWallet.GetBalance());
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.Unspent));
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.All));

            var k2 = new EcKey();
            var v2 = Utils.ToNanoCoins(0, 50);
            var t2 = _defaultWallet.CreateSend(k2.ToAddress(Params), v2);
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.Unspent));
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.All));

            // Do some basic sanity checks.
            Assert.AreEqual(1, t2.TransactionInputs.Count);
            Assert.AreEqual(_myAddress, t2.TransactionInputs[0].ScriptSig.FromAddress);

            // We have NOT proven that the signature is correct!

            _defaultWallet.ConfirmSend(t2);
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.Pending));
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.Spent));
            Assert.AreEqual(2, _defaultWallet.GetPoolSize(DefaultWallet.Pool.All));
        }

        [Test]
        public void SideChain()
        {
            // The wallet receives a coin on the main chain, then on a side chain. Only main chain counts towards balance.
            var v1 = Utils.ToNanoCoins(1, 0);
            var t1 = TestUtils.CreateFakeTx(Params, v1, _myAddress);

            _defaultWallet.Receive(t1, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(v1, _defaultWallet.GetBalance());
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.Unspent));
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.All));

            var v2 = Utils.ToNanoCoins(0, 50);
            var t2 = TestUtils.CreateFakeTx(Params, v2, _myAddress);
            _defaultWallet.Receive(t2, null, BlockChain.NewBlockType.SideChain);
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.Inactive));
            Assert.AreEqual(2, _defaultWallet.GetPoolSize(DefaultWallet.Pool.All));

            Assert.AreEqual(v1, _defaultWallet.GetBalance());
        }

        [Test]
        public void Listener()
        {
            var fakeTx = TestUtils.CreateFakeTx(Params, Utils.ToNanoCoins(1, 0), _myAddress);
            var didRun = false;
            _defaultWallet.CoinsReceived +=
                (sender, e) =>
                {
                    Assert.IsTrue(e.PreviousBalance.Equals(0));
                    Assert.IsTrue(e.NewBalance.Equals(Utils.ToNanoCoins(1, 0)));
                    Assert.AreEqual(e.Transaction, fakeTx);
                    Assert.AreEqual(sender, _defaultWallet);
                    didRun = true;
                };
            _defaultWallet.Receive(fakeTx, null, BlockChain.NewBlockType.BestChain);
            Assert.IsTrue(didRun);
        }

        [Test]
        public void Balance()
        {
            // Receive 5 coins then half a coin.
            var v1 = Utils.ToNanoCoins(5, 0);
            var v2 = Utils.ToNanoCoins(0, 50);
            var t1 = TestUtils.CreateFakeTx(Params, v1, _myAddress);
            var t2 = TestUtils.CreateFakeTx(Params, v2, _myAddress);
            var b1 = TestUtils.CreateFakeBlock(Params, _blockStore, t1).StoredBlock;
            var b2 = TestUtils.CreateFakeBlock(Params, _blockStore, t2).StoredBlock;
            var expected = Utils.ToNanoCoins(5, 50);
            _defaultWallet.Receive(t1, b1, BlockChain.NewBlockType.BestChain);
            _defaultWallet.Receive(t2, b2, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(expected, _defaultWallet.GetBalance());

            // Now spend one coin.
            var v3 = Utils.ToNanoCoins(1, 0);
            var spend = _defaultWallet.CreateSend(new EcKey().ToAddress(Params), v3);
            _defaultWallet.ConfirmSend(spend);

            // Available and estimated balances should not be the same. We don't check the exact available balance here
            // because it depends on the coin selection algorithm.
            Assert.AreEqual(Utils.ToNanoCoins(4, 50), _defaultWallet.GetBalance(DefaultWallet.BalanceType.Estimated));
            Assert.IsFalse(_defaultWallet.GetBalance(DefaultWallet.BalanceType.Available).Equals(
                _defaultWallet.GetBalance(DefaultWallet.BalanceType.Estimated)));

            // Now confirm the transaction by including it into a block.
            var b3 = TestUtils.CreateFakeBlock(Params, _blockStore, spend).StoredBlock;
            _defaultWallet.Receive(spend, b3, BlockChain.NewBlockType.BestChain);

            // Change is confirmed. We started with 5.50 so we should have 4.50 left.
            var v4 = Utils.ToNanoCoins(4, 50);
            Assert.AreEqual(v4, _defaultWallet.GetBalance(DefaultWallet.BalanceType.Available));
        }

        // Intuitively you'd expect to be able to create a transaction with identical inputs and outputs and get an
        // identical result to the official client. However the signatures are not deterministic - signing the same data
        // with the same key twice gives two different outputs. So we cannot prove bit-for-bit compatibility in this test
        // suite.
        [Test]
        public void BlockChainCatchup()
        {
            var tx1 = TestUtils.CreateFakeTx(Params, Utils.ToNanoCoins(1, 0), _myAddress);
            var b1 = TestUtils.CreateFakeBlock(Params, _blockStore, tx1).StoredBlock;
            _defaultWallet.Receive(tx1, b1, BlockChain.NewBlockType.BestChain);
            // Send 0.10 to somebody else.
            var send1 = _defaultWallet.CreateSend(new EcKey().ToAddress(Params), Utils.ToNanoCoins(0, 10), _myAddress);
            // Pretend it makes it into the block chain, our wallet state is cleared but we still have the keys, and we
            // want to get back to our previous state. We can do this by just not confirming the transaction as
            // createSend is stateless.
            var b2 = TestUtils.CreateFakeBlock(Params, _blockStore, send1).StoredBlock;
            _defaultWallet.Receive(send1, b2, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(Utils.BitcoinValueToFriendlyString(_defaultWallet.GetBalance()), "0.90");
            // And we do it again after the catch-up.
            var send2 = _defaultWallet.CreateSend(new EcKey().ToAddress(Params), Utils.ToNanoCoins(0, 10), _myAddress);
            // What we'd really like to do is prove the official client would accept it .... no such luck unfortunately.
            _defaultWallet.ConfirmSend(send2);
            var b3 = TestUtils.CreateFakeBlock(Params, _blockStore, send2).StoredBlock;
            _defaultWallet.Receive(send2, b3, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(Utils.BitcoinValueToFriendlyString(_defaultWallet.GetBalance()), "0.80");
        }

        [Test]
        public void Balances()
        {
            var nanos = Utils.ToNanoCoins(1, 0);
            var tx1 = TestUtils.CreateFakeTx(Params, nanos, _myAddress);
            _defaultWallet.Receive(tx1, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(nanos, tx1.GetValueSentToMe(_defaultWallet, true));
            // Send 0.10 to somebody else.
            var send1 = _defaultWallet.CreateSend(new EcKey().ToAddress(Params), Utils.ToNanoCoins(0, 10), _myAddress);
            // Re-serialize.
            var send2 = new Transaction(Params, send1.BitcoinSerialize());
            Assert.AreEqual(nanos, send2.GetValueSentFromMe(_defaultWallet));
        }

        [Test]
        public void Transactions()
        {
            // This test covers a _bug in which Transaction.getValueSentFromMe was calculating incorrectly.
            var tx = TestUtils.CreateFakeTx(Params, Utils.ToNanoCoins(1, 0), _myAddress);
            // Now add another output (ie, change) that goes to some other address.
            var someOtherGuy = new EcKey().ToAddress(Params);
            var output = new TransactionOutput(Params, tx, Utils.ToNanoCoins(0, 5), someOtherGuy);
            tx.AddOutput(output);
            // Note that tx is no longer valid: it spends more than it imports. However checking transactions balance
            // correctly isn't possible in SPV mode because value is a property of outputs not inputs. Without all
            // transactions you can't check they add up.
            _defaultWallet.Receive(tx, null, BlockChain.NewBlockType.BestChain);
            // Now the other guy creates a transaction which spends that change.
            var tx2 = new Transaction(Params);
            tx2.AddInput(output);
            tx2.AddOutput(new TransactionOutput(Params, tx2, Utils.ToNanoCoins(0, 5), _myAddress));
            // tx2 doesn't send any coins from us, even though the output is in the wallet.
            Assert.AreEqual(Utils.ToNanoCoins(0, 0), tx2.GetValueSentFromMe(_defaultWallet));
        }

        [Test]
        public void Bounce()
        {
            // This test covers _bug 64 (False double spends). Check that if we create a spend and it's immediately sent
            // back to us, this isn't considered as a double spend.
            var coin1 = Utils.ToNanoCoins(1, 0);
            var coinHalf = Utils.ToNanoCoins(0, 50);
            // Start by giving us 1 coin.
            var inbound1 = TestUtils.CreateFakeTx(Params, coin1, _myAddress);
            _defaultWallet.Receive(inbound1, null, BlockChain.NewBlockType.BestChain);
            // Send half to some other guy. Sending only half then waiting for a confirm is important to ensure the tx is
            // in the unspent pool, not pending or spent.
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.Unspent));
            Assert.AreEqual(1, _defaultWallet.GetPoolSize(DefaultWallet.Pool.All));
            var someOtherGuy = new EcKey().ToAddress(Params);
            var outbound1 = _defaultWallet.CreateSend(someOtherGuy, coinHalf);
            _defaultWallet.ConfirmSend(outbound1);
            _defaultWallet.Receive(outbound1, null, BlockChain.NewBlockType.BestChain);
            // That other guy gives us the coins right back.
            var inbound2 = new Transaction(Params);
            inbound2.AddOutput(new TransactionOutput(Params, inbound2, coinHalf, _myAddress));
            inbound2.AddInput(outbound1.TransactionOutputs[0]);
            _defaultWallet.Receive(inbound2, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(coin1, _defaultWallet.GetBalance());
        }

        [Test]
        public void FinneyAttack()
        {
            // A Finney attack is where a miner includes a transaction spending coins to themselves but does not
            // broadcast it. When they find a solved block, they hold it back temporarily whilst they buy something with
            // those same coins. After purchasing, they broadcast the block thus reversing the transaction. It can be
            // done by any miner for products that can be bought at a chosen time and very quickly (as every second you
            // withhold your block means somebody else might find it first, invalidating your work).
            //
            // Test that we handle ourselves performing the attack correctly: a double spend on the chain moves
            // transactions from pending to dead.
            //
            // Note that the other way around, where a pending transaction sending us coins becomes dead,
            // isn't tested because today BitCoinJ only learns about such transactions when they appear in the chain.
            Transaction eventDead = null;
            Transaction eventReplacement = null;
            _defaultWallet.DeadTransaction +=
                (sender, e) =>
                {
                    eventDead = e.DeadTransaction;
                    eventReplacement = e.ReplacementTransaction;
                };

            // Receive 1 BTC.
            var nanos = Utils.ToNanoCoins(1, 0);
            var t1 = TestUtils.CreateFakeTx(Params, nanos, _myAddress);
            _defaultWallet.Receive(t1, null, BlockChain.NewBlockType.BestChain);
            // Create a send to a merchant.
            var send1 = _defaultWallet.CreateSend(new EcKey().ToAddress(Params), Utils.ToNanoCoins(0, 50));
            // Create a double spend.
            var send2 = _defaultWallet.CreateSend(new EcKey().ToAddress(Params), Utils.ToNanoCoins(0, 50));
            // Broadcast send1.
            _defaultWallet.ConfirmSend(send1);
            // Receive a block that overrides it.
            _defaultWallet.Receive(send2, null, BlockChain.NewBlockType.BestChain);
            Assert.AreEqual(send1, eventDead);
            Assert.AreEqual(send2, eventReplacement);
        }
    }
}