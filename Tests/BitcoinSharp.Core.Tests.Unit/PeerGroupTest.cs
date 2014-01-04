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
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.Store;
using NUnit.Framework;

namespace BitcoinSharp.Tests.Unit
{
    [TestFixture]
    public class PeerGroupTest
    {
        private static readonly NetworkParameters _params = NetworkParameters.UnitTests();

        private DefaultWallet _defaultWallet;
        private IBlockStore _blockStore;
        private PeerGroup _peerGroup;

        [SetUp]
        public void SetUp()
        {
            _defaultWallet = new DefaultWallet(_params);
            _blockStore = new MemoryBlockStore(_params);
            var chain = new BlockChain(_params, _defaultWallet, _blockStore);
            _peerGroup = new PeerGroup(_blockStore, _params, chain);
        }
    }
}