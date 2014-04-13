﻿using BitSharp.Common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Core.Wallet
{
    public class WalletEntry
    {
        private readonly IImmutableList<MonitoredWalletAddress> addresses;
        private readonly EnumWalletEntryType type;
        private readonly ChainPosition chainPosition;
        private readonly UInt64 value;

        public WalletEntry(IImmutableList<MonitoredWalletAddress> addresses, EnumWalletEntryType type, ChainPosition chainPosition, UInt64 value)
        {
            this.addresses = addresses;
            this.type = type;
            this.chainPosition = chainPosition;
            this.value = value;
        }

        public IImmutableList<MonitoredWalletAddress> Addresses { get { return this.addresses; } }

        public EnumWalletEntryType Type { get { return this.type; } }

        public ChainPosition ChainPosition { get { return this.chainPosition; } }

        public UInt64 Value { get { return this.value; } }
    }
}
