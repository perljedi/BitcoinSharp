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
using BitcoinSharp.Core;
using BitcoinSharp.Core.Discovery;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.Store;
using BitcoinSharp.Wallet;

namespace BitcoinSharp.Examples
{
    /// <summary>
    /// RefreshWallet loads a wallet, then processes the block chain to update the transaction pools within it.
    /// </summary>
    public static class RefreshWallet
    {
        static readonly NetworkParameters NetworkParameters = NetworkParameters.TestNet();

        public static void Run(string[] args)
        {

            var wallet = new DefaultWallet(NetworkParameters);

            using (var blockStore = new MemoryBlockStore(NetworkParameters))
            {
                var chain = new BlockChain(NetworkParameters, wallet, blockStore);

                var peerGroup = new PeerGroup(blockStore, NetworkParameters, chain);
                //peerGroup.AddAddress(new PeerAddress(new IPAddress(new byte[]{ 192, 168, 1, 136 }), 18333));
                peerGroup.AddPeerDiscovery(new DnsDiscovery(NetworkParameters));
                Console.WriteLine("AddAddress");
                peerGroup.Start();
                Console.WriteLine("Started");

                // Act
                peerGroup.DownloadBlockChain();

                //peerGroup.Stop();
            }

            //var file = new FileInfo(args[0]);
            //var wallet = Wallet.LoadFromFile(file);
            //Console.WriteLine(wallet.ToString());

            //// Set up the components and link them together.
            //var @params = NetworkParameters.TestNet();
            //using (var blockStore = new MemoryBlockStore(@params))
            //{
            //    var chain = new BlockChain(@params, wallet, blockStore);

            //    var peerGroup = new PeerGroup(blockStore, @params, chain);
            //    peerGroup.AddAddress(new PeerAddress(IPAddress.Loopback));
            //    peerGroup.Start();

            //    wallet.CoinsReceived +=
            //        (sender, e) =>
            //        {
            //            Console.WriteLine();
            //            Console.WriteLine("Received tx " + e.Transaction.HashAsString);
            //            Console.WriteLine(e.Transaction.ToString());
            //        };

            //    // Now download and process the block chain.
            //    peerGroup.DownloadBlockChain();
            //    peerGroup.Stop();
            //}

            //wallet.SaveToFile(file);
            Console.WriteLine();
            Console.WriteLine("Done!");
            Console.WriteLine();
            Console.WriteLine(wallet.ToString());
        }
    }
}