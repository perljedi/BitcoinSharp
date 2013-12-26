using System;
using System.Net;
using BitCoinSharp;
using BitCoinSharp.Discovery;
using BitCoinSharp.Store;
using NUnit.Framework;

namespace BitcoinSharp.Tests.Integration
{
    public class PeerGroup_should
    {
        readonly NetworkParameters _networkParameters = NetworkParameters.ProdNet();

        [SetUp]
        public void SetUp()
        {
        }

        [Test]
        public void connect_to_other_peers()
        {
            //Arrange
            var wallet = new Wallet(_networkParameters);
           
            using (var blockStore = new MemoryBlockStore(_networkParameters))
            {
                var chain = new BlockChain(_networkParameters, wallet, blockStore);

                var peerGroup = new PeerGroup(blockStore, _networkParameters, chain);
                
                //peerGroup.AddAddress(new PeerAddress(new IPAddress(new byte[]{ 192, 168, 1, 136 }), 18333));
                peerGroup.AddPeerDiscovery(new DnsDiscovery(_networkParameters));
                Console.WriteLine("AddAddress");
                peerGroup.Start();
                Console.WriteLine("Started");

                // Act
                peerGroup.DownloadBlockChain();

                //peerGroup.Stop();
            }

        }
    }
}
