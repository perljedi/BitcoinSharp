using System;
using BitcoinSharp.Core;
using BitcoinSharp.Core.Discovery;
using BitcoinSharp.Core.Network;
using BitcoinSharp.Core.Store;
using NUnit.Framework;

namespace BitcoinSharp.Tests.Integration
{
    public class PeerGroup_should
    {
        readonly NetworkParameters _networkParameters = NetworkParameters.TestNet();

        [SetUp]
        public void SetUp()
        {
        }

        [Test, Ignore]
        public void connect_to_other_peers()
        {
            
            //Arrange
            var wallet = new Wallet(_networkParameters);
           
            using (var blockStore = new MemoryBlockStore(_networkParameters))
            {
                var chain = new BlockChain(_networkParameters, wallet, blockStore);

                var peerGroup = new PeerGroup(blockStore, _networkParameters, chain);

                //peerGroup.AddAddress(new PeerAddress(IPAddress.Loopback));
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
