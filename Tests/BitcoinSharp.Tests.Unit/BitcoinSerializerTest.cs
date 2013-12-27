/*
 * Copyright 2011 Noa Resare
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

using System.IO;
using System.Linq;
using BitCoinSharp.Core;
using BitCoinSharp.Core.Messages;
using FluentAssertions;
using NUnit.Framework;
using Org.BouncyCastle.Utilities.Encoders;

namespace BitCoinSharp.Tests.Unit
{
    [TestFixture]
    public class BitcoinSerializerTest
    {

        // the actual data from https://en.bitcoin.it/wiki/Protocol_specification#verack
        private const string ConstantVersionAck = "f9beb4d976657261636b000000000000000000005DF6E0E2";

        // the actual data from https://en.bitcoin.it/wiki/Protocol_specification#addr
        private readonly byte[] _addrMessage = Hex.Decode("f9beb4d96164647200000000000000001f000000ed52399b01e215104d010000000000000000000000000000000000ffff0a000001208d");

        private byte[] _txMessage = Hex.Decode(
           "F9 BE B4 D9 74 78 00 00  00 00 00 00 00 00 00 00" +
           "02 01 00 00 E2 93 CD BE  01 00 00 00 01 6D BD DB" +
           "08 5B 1D 8A F7 51 84 F0  BC 01 FA D5 8D 12 66 E9" +
           "B6 3B 50 88 19 90 E4 B4  0D 6A EE 36 29 00 00 00" +
           "00 8B 48 30 45 02 21 00  F3 58 1E 19 72 AE 8A C7" +
           "C7 36 7A 7A 25 3B C1 13  52 23 AD B9 A4 68 BB 3A" +
           "59 23 3F 45 BC 57 83 80  02 20 59 AF 01 CA 17 D0" +
           "0E 41 83 7A 1D 58 E9 7A  A3 1B AE 58 4E DE C2 8D" +
           "35 BD 96 92 36 90 91 3B  AE 9A 01 41 04 9C 02 BF" +
           "C9 7E F2 36 CE 6D 8F E5  D9 40 13 C7 21 E9 15 98" +
           "2A CD 2B 12 B6 5D 9B 7D  59 E2 0A 84 20 05 F8 FC" +
           "4E 02 53 2E 87 3D 37 B9  6F 09 D6 D4 51 1A DA 8F" +
           "14 04 2F 46 61 4A 4C 70  C0 F1 4B EF F5 FF FF FF" +
           "FF 02 40 4B 4C 00 00 00  00 00 19 76 A9 14 1A A0" +
           "CD 1C BE A6 E7 45 8A 7A  BA D5 12 A9 D9 EA 1A FB" +
           "22 5E 88 AC 80 FA E9 C7  00 00 00 00 19 76 A9 14" +
           "0E AB 5B EA 43 6A 04 84  CF AB 12 48 5E FD A0 B7" +
           "8B 4E CC 52 88 AC 00 00  00 00");


        [Test]
        public void TestAddress_Something()
        {
            var bitcoinSerializer = new BitcoinSerializer(NetworkParameters.ProdNet());
            
            using (var bais = new MemoryStream(_addrMessage))
            {
                var addressMessage = (AddressMessage)bitcoinSerializer.Deserialize(bais);
                addressMessage.Addresses.Count.Should().Be(1);
                var peerAddress = addressMessage.Addresses.FirstOrDefault();
                peerAddress.Should().NotBeNull();
                peerAddress.Port.Should().Be(8333);
            }
        }

        [Test]
        public void deserialize_block_headers()
        {

            var bitcoinSerializer = new BitcoinSerializer(NetworkParameters.ProdNet());
            using (var bais = new MemoryStream(Hex.Decode("f9beb4d9686561" +
                    "646572730000000000520000005d4fab8101010000006fe28c0ab6f1b372c1a6a246ae6" +
                    "3f74f931e8365e15a089c68d6190000000000982051fd1e4ba744bbbe680e1fee14677b" +
                    "a1a3c3540bf7b1cdb606e857233e0e61bc6649ffff001d01e3629900")))
            {
                var headerMessage = (HeadersMessage)bitcoinSerializer.Deserialize(bais);
                var block = headerMessage.BlockHeaders.FirstOrDefault();
                block.Should().NotBeNull();
                block.HashAsString.Should().Be("00000000839a8e6886ab5951d76f411475428afc90947ee320161bbf18eb6048");
                block.Transactions.Should().BeEmpty();
            }
        }


      

        [Test]
        public void TestVersion()
        {
            var bs = new BitcoinSerializer(NetworkParameters.ProdNet());
            // the actual data from https://en.bitcoin.it/wiki/Protocol_specification#version
            using (var bais = new MemoryStream(Hex.Decode("f9 be b4 d9 76 65 72 73 69 6f 6e 00 00 00 00 00" +
                                                          "64 00 00 00 35 8d 49 32 62 ea 00 00 01 00 00 00" +
                                                          "00 00 00 00 11 b2 d0 50 00 00 00 00 01 00 00 00" +
                                                          "00 00 00 00 00 00 00 00 00 00 00 00 00 00 ff ff" +
                                                          "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00" +
                                                          "00 00 00 00 00 00 00 00 ff ff 00 00 00 00 00 00" +
                                                          "3b 2e b3 5d 8c e6 17 65 0f 2f 53 61 74 6f 73 68" +
                                                          "69 3a 30 2e 37 2e 32 2f c0 3e 03 00")))
            {
                var vm = (VersionMessage)bs.Deserialize(bais);
                Assert.AreEqual(60002, vm.ClientVersion);
                Assert.AreEqual(1355854353, vm.Time);
                Assert.AreEqual(1952535343, vm.BestHeight);
                
            }
        }

        [Test]
        public void TestVerack()
        {
            var bitcoinSerializer = new BitcoinSerializer(NetworkParameters.ProdNet());
            
            using (var memoryStream = new MemoryStream(Hex.Decode(ConstantVersionAck)))
            {
                var message = bitcoinSerializer.Deserialize(memoryStream) as VersionAck;
                message.Should().NotBeNull();
                

            }
        }

        [Test]
        public void TestAddr()
        {
            var bs = new BitcoinSerializer(NetworkParameters.ProdNet());
            // the actual data from https://en.bitcoin.it/wiki/Protocol_specification#addr
            using (var bais = new MemoryStream(Hex.Decode("f9beb4d96164647200000000000000001f000000" +
                                                          "ed52399b01e215104d010000000000000000000000000000000000ffff0a000001208d")))
            {
                var a = (AddressMessage)bs.Deserialize(bais);
                Assert.AreEqual(1, a.Addresses.Count);
                var pa = a.Addresses[0];
                Assert.AreEqual(8333, pa.Port);
                Assert.AreEqual("10.0.0.1", pa.IpAddress.ToString());
            }
        }
    }
}