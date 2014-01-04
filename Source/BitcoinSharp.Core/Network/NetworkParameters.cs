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
using System.Diagnostics;
using System.IO;
using BitcoinSharp.Core.Common.ExtensionMethods;
using BitcoinSharp.Core.Messages;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities.Encoders;

namespace BitcoinSharp.Core.Network
{
    /// <summary>
    /// NetworkParameters contains the data needed for working with an instantiation of a BitCoin chain.
    /// </summary>
    /// <remarks>
    /// Currently there are only two, the production chain and the test chain. But in future as BitCoin
    /// evolves there may be more. You can create your own as long as they don't conflict.
    /// </remarks>
    [Serializable]
    public class NetworkParameters
    {
        /// <summary>
        /// The protocol version this library implements. A value of 31800 means 0.3.18.00.
        /// </summary>
        //public const uint ProtocolVersion = 31800;
        //TODO: move this someplace common.
        public const uint ProtocolVersion = 70001;
        

        // TODO: Seed nodes and checkpoint values should be here as well.

        /// <summary>
        /// Genesis block for this chain.
        /// </summary>
        /// <remarks>
        /// The first block in every chain is a well known constant shared between all BitCoin implementations. For a
        /// block to be valid, it must be eventually possible to work backwards to the genesis block by following the
        /// prevBlockHash pointers in the block headers.<p/>
        /// The genesis blocks for both test and prod networks contain the timestamp of when they were created,
        /// and a message in the coinbase transaction. It says, <i>"The Times 03/Jan/2009 Chancellor on brink of second bailout for banks"</i>.
        /// </remarks>
        public Block GenesisBlock { get; private set; }

        /// <summary>
        /// What the easiest allowable proof of work should be.
        /// </summary>
        public BigInteger ProofOfWorkLimit { get; set; }

        /// <summary>
        /// Default TCP port on which to connect to nodes.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// The header bytes that identify the start of a packet on this network.
        /// </summary>
        public uint PacketMagic { get; private set; }

        /// <summary>
        /// First byte of a base58 encoded address. See <see cref="Address"/>
        /// </summary>
        public int AddressHeader { get; private set; }

        /// <summary>
        /// First byte of a base58 encoded dumped private key. See <see cref="DumpedPrivateKey"/>.
        /// </summary>
        public int DumpedPrivateKeyHeader { get; private set; }

        /// <summary>
        /// How many blocks pass between difficulty adjustment periods. BitCoin standardises this to be 2015.
        /// </summary>
        public int Interval { get; private set; }

        /// <summary>
        /// How much time in seconds is supposed to pass between "interval" blocks. If the actual elapsed time is
        /// significantly different from this value, the network difficulty formula will produce a different value. Both
        /// test and production BitCoin networks use 2 weeks (1209600 seconds).
        /// </summary>
        public int TargetTimespan { get; private set; }

        public String[] DnsSeeds { get; private set; }

        private static Block CreateGenesis(NetworkParameters networkParameters)
        {
            var genesisBlock = new Block(networkParameters);
            var transaction = new Transaction(networkParameters);
            // A script containing the difficulty bits and the following message:
            //   "The Times 03/Jan/2009 Chancellor on brink of second bailout for banks"
            var bytes = Hex.Decode("04ffff001d0104455468652054696d65732030332f4a616e2f32303039204368616e63656c6c6f72206f6e206272696e6b206f66207365636f6e64206261696c6f757420666f722062616e6b73");
            transaction.AddInput(new TransactionInput(networkParameters, transaction, bytes));
            using (var scriptPubKeyBytes = new MemoryStream())
            {
                Script.WriteBytes(scriptPubKeyBytes,Hex.Decode("04678afdb0fe5548271967f1a67130b7105cd6a828e03909a67962e0ea1f61deb649f6bc3f4cef38c4f35504e51ec112de5c384df7ba0b8d578a4c702b6bf11d5f"));
                scriptPubKeyBytes.Write(Script.OpCheckSig);
                transaction.AddOutput(new TransactionOutput(networkParameters, transaction, scriptPubKeyBytes.ToArray()));
            }
            genesisBlock.AddTransaction(transaction);
            return genesisBlock;
        }

        // TODO: Place these elsewhere?
        private const int _targetTimespan = 14 * 24 * 60 * 60; // 2 weeks per difficulty cycle, on average.
        private const int _targetSpacing = 10 * 60; // 10 minutes per block.
        private const int _interval = _targetTimespan / _targetSpacing;

        /// <summary>
        /// Sets up the given NetworkParameters with testnet values.
        /// </summary>
        private static NetworkParameters CreateTestNet(NetworkParameters networkParameters)
        {
            // Genesis hash is 0000000224b1593e3ff16a0e3b61285bbc393a39f78c8aa48c456142671f7110
            // The proof of work limit has to start with 00, as otherwise the value will be interpreted as negative.
            networkParameters.ProofOfWorkLimit = new BigInteger("0000000fffffffffffffffffffffffffffffffffffffffffffffffffffffffff", 16);
            networkParameters.Port = 18333;
            networkParameters.PacketMagic = 0x0b110907;
            networkParameters.AddressHeader = 111;
            networkParameters.DumpedPrivateKeyHeader = 239;
            networkParameters.Interval = _interval;
            networkParameters.TargetTimespan = _targetTimespan;
            networkParameters.GenesisBlock = CreateGenesis(networkParameters);
            //TODO: Should this use SystemTime.UnixTime()?
            networkParameters.GenesisBlock.TimeSeconds = 1296688602;
            networkParameters.GenesisBlock.TargetDifficulty = 0x1d07fff8;
            networkParameters.GenesisBlock.Nonce = 384568319;
            networkParameters.DnsSeeds = new[]
            {
                "testnet-seed.bitcoin.petertodd.org",
                "testnet-seed.bluematt.me"
            };
            var genesisHash = networkParameters.GenesisBlock.HashAsString;
            Debug.Assert(genesisHash.Equals("00000007199508e34a9ff81e6ec0c477a4cccff2a4767a8eee39c11db367b008"),genesisHash);
            return networkParameters;
        }

        /// <summary>
        /// The test chain created by Gavin.
        /// </summary>
        public static NetworkParameters TestNet()
        {
            var networkParameters = new NetworkParameters();
            return CreateTestNet(networkParameters);
        }

        /// <summary>
        /// The primary BitCoin chain created by Satoshi.
        /// </summary>
        public static NetworkParameters ProdNet()
        {
            var networkParameters = new NetworkParameters
            {
                ProofOfWorkLimit = new BigInteger("00000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffff", 16),
                Port = 8333,
                PacketMagic = 0xf9beb4d9,
                AddressHeader = 0,
                DumpedPrivateKeyHeader = 128,
                Interval = _interval,
                TargetTimespan = _targetTimespan,
                
            };
            networkParameters.GenesisBlock = CreateGenesis(networkParameters);
            networkParameters.GenesisBlock.TargetDifficulty = 0x1d00ffff;
            networkParameters.GenesisBlock.TimeSeconds = 1231006505;
            networkParameters.GenesisBlock.Nonce = 2083236893;
            networkParameters.DnsSeeds = new[]
            {
                //Well known discovery host names on the production network.
                "seed.bitcoin.sipa.be",
                "dnsseed.bluematt.me",
                "dnsseed.bitcoin.dashjr.org",
                "bitseed.xf2.org"
            };
            var genesisHash = networkParameters.GenesisBlock.HashAsString;
            //TODO: If the genesis block must be created why don't we throw if it's invalid?
            Debug.Assert(genesisHash.Equals("000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f"),
                genesisHash);
            return networkParameters;
        }

        /// <summary>
        /// Returns a testnet params modified to allow any difficulty target.
        /// </summary>
        public static NetworkParameters UnitTests()
        {
            var networkParameters = new NetworkParameters();
            networkParameters = CreateTestNet(networkParameters);
            networkParameters.ProofOfWorkLimit =
                new BigInteger("00ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", 16);
            networkParameters.GenesisBlock.TargetDifficulty = Block.EasiestDifficultyTarget;
            networkParameters.Interval = 10;
            networkParameters.TargetTimespan = 200000000; // 6 years. Just a very big number.
            return networkParameters;
        }
    }
}