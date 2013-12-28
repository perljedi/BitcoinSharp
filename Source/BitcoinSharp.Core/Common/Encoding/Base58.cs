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
using System.Linq;
using System.Text;
using BitCoinSharp.Core.Exceptions;
using Org.BouncyCastle.Math;

namespace BitCoinSharp.Core.Common.Encoding
{
    /// <summary>
    ///     A custom form of base58 is used to encode BitCoin addresses. Note that this is not the same base58 as used by
    ///     Flickr, which you may see reference to around the internet.
    /// </summary>
    /// <remarks>
    ///     Satoshi says: why base-58 instead of standard base-64 encoding?<p />
    ///     <ul>
    ///         <li>
    ///             Don't want 0OIl characters that look the same in some fonts and
    ///             could be used to create visually identical looking account numbers.
    ///         </li>
    ///         <li>A string with non-alphanumeric characters is not as easily accepted as an account number.</li>
    ///         <li>E-mail usually won't line-break if there's no punctuation to break at.</li>
    ///         <li>Double clicking selects the whole number as one word if it's all alphanumeric.</li>
    ///     </ul>
    /// </remarks>
    public static class Base58
    {
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        private static readonly BigInteger Base = BigInteger.ValueOf(58);

        public static string Encode(byte[] input)
        {
            // TODO: This could be a lot more efficient.
            var bigInteger = new BigInteger(1, input);
            var stringBuilder = new StringBuilder();
            while (bigInteger.CompareTo(Base) >= 0)
            {
                var mod = bigInteger.Mod(Base);
                stringBuilder.Insert(0, new[] {Alphabet[mod.IntValue]});
                bigInteger = bigInteger.Subtract(mod).Divide(Base);
            }
            stringBuilder.Insert(0, new[] {Alphabet[bigInteger.IntValue]});
            // Convert leading zeros too.
            foreach (var anInput in input)
            {
                if (anInput == 0)
                    stringBuilder.Insert(0, new[] {Alphabet[0]});
                else
                    break;
            }
            return stringBuilder.ToString();
        }

        /// <exception cref="AddressFormatException" />
        public static byte[] Decode(string input)
        {
            var bytes = DecodeToBigInteger(input).ToByteArray();
            // We may have got one more byte than we wanted, if the high bit of the next-to-last byte was not zero. This
            // is because BigIntegers are represented with twos-compliment notation, thus if the high bit of the last
            // byte happens to be 1 another 8 zero bits will be added to ensure the number parses as positive. Detect
            // that case here and chop it off.
            var stripSignByte = bytes.Length > 1 && bytes[0] == 0 && bytes[1] >= 0x80;
            // Count the leading zeros, if any.
            var leadingZeros = 0;
            for (var i = 0; input[i] == Alphabet[0]; i++)
            {
                leadingZeros++;
            }
            // Now cut/pad correctly. Java 6 has a convenience for this, but Android can't use it.
            var temp = new byte[bytes.Length - (stripSignByte ? 1 : 0) + leadingZeros];
            Array.Copy(bytes, stripSignByte ? 1 : 0, temp, leadingZeros, temp.Length - leadingZeros);
            return temp;
        }

        /// <exception cref="AddressFormatException" />
        public static BigInteger DecodeToBigInteger(string input)
        {
            var bigInteger = BigInteger.ValueOf(0);
            // Work backwards through the string.
            for (var i = input.Length - 1; i >= 0; i--)
            {
                var alphaIndex = Alphabet.IndexOf(input[i]);
                if (alphaIndex == -1)
                {
                    throw new AddressFormatException("Illegal character " + input[i] + " at " + i);
                }
                bigInteger = bigInteger.Add(BigInteger.ValueOf(alphaIndex).Multiply(Base.Pow(input.Length - 1 - i)));
            }
            return bigInteger;
        }

        /// <summary>
        ///     Uses the checksum in the last 4 bytes of the decoded data to verify the rest are correct. The checksum is
        ///     removed from the returned data.
        /// </summary>
        /// <exception cref="AddressFormatException">If the input is not base 58 or the checksum does not validate.</exception>
        public static byte[] DecodeChecked(string input)
        {
            var temp = Decode(input);
            if (temp.Length < 4)
                throw new AddressFormatException("Input too short");
            var checksum = new byte[4];
            Array.Copy(temp, temp.Length - 4, checksum, 0, 4);
            var bytes = new byte[temp.Length - 4];
            Array.Copy(temp, 0, bytes, 0, temp.Length - 4);
            temp = Utils.DoubleDigest(bytes);
            var hash = new byte[4];
            Array.Copy(temp, 0, hash, 0, 4);
            if (!hash.SequenceEqual(checksum))
                throw new AddressFormatException("Checksum does not validate");
            return bytes;
        }
    }
}