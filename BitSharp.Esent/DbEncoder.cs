﻿using BitSharp.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Esent
{
    public static class DbEncoder
    {
        public static byte[] EncodeUInt256(UInt256 value)
        {
            return value.ToByteArrayBE();
        }

        public static UInt256 DecodeUInt256(byte[] value)
        {
            return UInt256.FromByteArrayBE(value);
        }
    }
}
