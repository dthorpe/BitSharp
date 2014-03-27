﻿using BitSharp.Common;
using BitSharp.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Storage
{
    public interface IUtxoBuilderStorage : IDisposable
    {
        int TransactionCount { get; }
                
        bool ContainsTransaction(UInt256 txHash);

        bool TryGetTransaction(UInt256 txHash, out UnspentTx unspentTx);

        void AddTransaction(UInt256 txHash, UnspentTx unspentTx);

        bool RemoveTransaction(UInt256 txHash);

        void UpdateTransaction(UInt256 txHash, UnspentTx unspentTx);

        IEnumerable<KeyValuePair<UInt256, UnspentTx>> UnspentTransactions();

        
        int OutputCount { get; }

        bool ContainsOutput(TxOutputKey txOutputKey);

        bool TryGetOutput(TxOutputKey txOutputKey, out TxOutput txOutput);

        void AddOutput(TxOutputKey txOutputKey, TxOutput txOutput);

        bool RemoveOutput(TxOutputKey txOutputKey);
        
        IEnumerable<KeyValuePair<TxOutputKey, TxOutput>> UnspentOutputs();
                
        
        void Flush();

        IUtxoStorage Close(UInt256 blockHash);
    }
}
