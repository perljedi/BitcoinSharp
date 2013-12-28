using System.Collections.Generic;
using System.IO;
using BitCoinSharp.Core.Common.ExtensionMethods;
using BitCoinSharp.Core.Common.ValueTypes;
using BitCoinSharp.Core.Exceptions;
using BitCoinSharp.Core.Network;
using log4net;

namespace BitCoinSharp.Core.Messages
{
    public class HeadersMessage : AbstractMessage
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(HeadersMessage));

        // The main client will never send us more than this number of headers.
        public static ulong MaxHeaders = 2000;

        public List<Block> BlockHeaders;
        
        public HeadersMessage(NetworkParameters @params, byte[] payload) :
            base(@params, payload, 0)
        {
        }

        public HeadersMessage(NetworkParameters @params, Block headers)
            : base(@params)
        {
            BlockHeaders = new List<Block>() { headers };
        }

        public override void BitcoinSerializeToStream(Stream outputStream)
        {
            outputStream.Write(new VarInt((ulong)BlockHeaders.Count).Encode());
            foreach (var blockHeader in BlockHeaders)
            {
                if (blockHeader.Transactions == null)
                {
                    blockHeader.BitcoinSerializeToStream(outputStream);
                }
                else
                {
                    blockHeader.CloneAsHeader().BitcoinSerializeToStream(outputStream);
                }
                outputStream.Write(0);
            }
        }


        protected override void Parse()
        {
            var numHeaders = base.ReadVarInt();
            if (numHeaders > MaxHeaders)
            {
                throw new ProtocolException("Too many headers: got " + numHeaders + " which is larger than " + MaxHeaders);
            }

            BlockHeaders = new List<Block>();

            for (var i = 0UL; i < numHeaders; ++i)
            {
                // Read 80 bytes of the header and one more byte for the transaction list, which is always a 00 because the
                // transaction list is empty.
                var blockHeaderBytes = base.ReadBytes(81);
                if (blockHeaderBytes[80] != 0)
                {
                    throw new ProtocolException("Block header does not end with a null byte");
                }
                var newBlockHeader = new Block(this.NetworkParameters, blockHeaderBytes);
                BlockHeaders.Add(newBlockHeader);
            }
            if (!Log.IsDebugEnabled) return;
            foreach (var blockHeader in BlockHeaders)
            {
                Log.Debug(blockHeader);
            }
        }
    }
}