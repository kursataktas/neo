// Copyright (C) 2015-2024 The Neo Project.
//
// OracleContract.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

#pragma warning disable IDE0051

using Neo.Cryptography;
using Neo.IO;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Neo.SmartContract.Native
{
    /// <summary>
    /// The native Oracle service for NEO system.
    /// </summary>
    public sealed class OracleContract : NativeContract
    {
        private const int MaxUrlLength = 256;
        private const int MaxFilterLength = 128;
        private const int MaxCallbackLength = 32;
        private const int MaxUserDataLength = 512;

        private const byte Prefix_Price = 5;
        private const byte Prefix_RequestId = 9;
        private const byte Prefix_Request = 7;
        private const byte Prefix_IdList = 6;

        [ContractEvent(0, name: "OracleRequest",
            "Id", ContractParameterType.Integer,
            "RequestContract", ContractParameterType.Hash160,
            "Url", ContractParameterType.String,
            "Filter", ContractParameterType.String)]
        [ContractEvent(1, name: "OracleResponse",
            "Id", ContractParameterType.Integer,
            "OriginalTx", ContractParameterType.Hash256)]
        internal OracleContract() : base() { }

        [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.States)]
        private void SetPrice(ApplicationEngine engine, long price)
        {
            if (price <= 0)
                throw new ArgumentOutOfRangeException(nameof(price));
            if (!CheckCommittee(engine)) throw new InvalidOperationException();
            engine.SnapshotCache.GetAndChange(CreateStorageKey(Prefix_Price)).Set(price);
        }

        /// <summary>
        /// Gets the price for an Oracle request.
        /// </summary>
        /// <param name="snapshot">The snapshot used to read data.</param>
        /// <returns>The price for an Oracle request, in the unit of datoshi, 1 datoshi = 1e-8 GAS.</returns>
        [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.ReadStates)]
        public long GetPrice(DataCache snapshot)
        {
            return (long)(BigInteger)snapshot[CreateStorageKey(Prefix_Price)];
        }

        [ContractMethod(RequiredCallFlags = CallFlags.States | CallFlags.AllowCall | CallFlags.AllowNotify)]
        private ContractTask Finish(ApplicationEngine engine)
        {
            if (engine.InvocationStack.Count != 2) throw new InvalidOperationException();
            if (engine.GetInvocationCounter() != 1) throw new InvalidOperationException();
            Transaction tx = (Transaction)engine.ScriptContainer;
            OracleResponse response = tx.GetAttribute<OracleResponse>();
            if (response == null) throw new ArgumentException("Oracle response was not found");
            OracleRequest request = GetRequest(engine.SnapshotCache, response.Id);
            if (request == null) throw new ArgumentException("Oracle request was not found");
            engine.SendNotification(Hash, "OracleResponse", new VM.Types.Array(engine.ReferenceCounter) { response.Id, request.OriginalTxid.ToArray() });
            StackItem userData = BinarySerializer.Deserialize(request.UserData, engine.Limits, engine.ReferenceCounter);
            return engine.CallFromNativeContractAsync(Hash, request.CallbackContract, request.CallbackMethod, request.Url, userData, (int)response.Code, response.Result);
        }

        private UInt256 GetOriginalTxid(ApplicationEngine engine)
        {
            Transaction tx = (Transaction)engine.ScriptContainer;
            OracleResponse response = tx.GetAttribute<OracleResponse>();
            if (response is null) return tx.Hash;
            OracleRequest request = GetRequest(engine.SnapshotCache, response.Id);
            return request.OriginalTxid;
        }

        /// <summary>
        /// Gets a pending request with the specified id.
        /// </summary>
        /// <param name="snapshot">The snapshot used to read data.</param>
        /// <param name="id">The id of the request.</param>
        /// <returns>The pending request. Or <see langword="null"/> if no request with the specified id is found.</returns>
        public OracleRequest GetRequest(DataCache snapshot, ulong id)
        {
            return snapshot.TryGet(CreateStorageKey(Prefix_Request).AddBigEndian(id))?.GetInteroperable<OracleRequest>();
        }

        /// <summary>
        /// Gets all the pending requests.
        /// </summary>
        /// <param name="snapshot">The snapshot used to read data.</param>
        /// <returns>All the pending requests.</returns>
        public IEnumerable<(ulong, OracleRequest)> GetRequests(DataCache snapshot)
        {
            return snapshot.Find(CreateStorageKey(Prefix_Request).ToArray()).Select(p => (BinaryPrimitives.ReadUInt64BigEndian(p.Key.Key.Span[1..]), p.Value.GetInteroperable<OracleRequest>()));
        }

        /// <summary>
        /// Gets the requests with the specified url.
        /// </summary>
        /// <param name="snapshot">The snapshot used to read data.</param>
        /// <param name="url">The url of the requests.</param>
        /// <returns>All the requests with the specified url.</returns>
        public IEnumerable<(ulong, OracleRequest)> GetRequestsByUrl(DataCache snapshot, string url)
        {
            IdList list = snapshot.TryGet(CreateStorageKey(Prefix_IdList).Add(GetUrlHash(url)))?.GetInteroperable<IdList>();
            if (list is null) yield break;
            foreach (ulong id in list)
                yield return (id, snapshot[CreateStorageKey(Prefix_Request).AddBigEndian(id)].GetInteroperable<OracleRequest>());
        }

        private static byte[] GetUrlHash(string url)
        {
            return Crypto.Hash160(Utility.StrictUTF8.GetBytes(url));
        }

        internal override ContractTask InitializeAsync(ApplicationEngine engine, Hardfork? hardfork)
        {
            if (hardfork == ActiveIn)
            {
                engine.SnapshotCache.Add(CreateStorageKey(Prefix_RequestId), new StorageItem(BigInteger.Zero));
                engine.SnapshotCache.Add(CreateStorageKey(Prefix_Price), new StorageItem(0_50000000));
            }
            return ContractTask.CompletedTask;
        }

        internal override async ContractTask PostPersistAsync(ApplicationEngine engine)
        {
            (UInt160 Account, BigInteger GAS)[] nodes = null;
            foreach (Transaction tx in engine.PersistingBlock.Transactions)
            {
                //Filter the response transactions
                OracleResponse response = tx.GetAttribute<OracleResponse>();
                if (response is null) continue;

                //Remove the request from storage
                StorageKey key = CreateStorageKey(Prefix_Request).AddBigEndian(response.Id);
                OracleRequest request = engine.SnapshotCache.TryGet(key)?.GetInteroperable<OracleRequest>();
                if (request == null) continue;
                engine.SnapshotCache.Delete(key);

                //Remove the id from IdList
                key = CreateStorageKey(Prefix_IdList).Add(GetUrlHash(request.Url));
                IdList list = engine.SnapshotCache.GetAndChange(key).GetInteroperable<IdList>();
                if (!list.Remove(response.Id)) throw new InvalidOperationException();
                if (list.Count == 0) engine.SnapshotCache.Delete(key);

                //Mint GAS for oracle nodes
                nodes ??= RoleManagement.GetDesignatedByRole(engine.SnapshotCache, Role.Oracle, engine.PersistingBlock.Index).Select(p => (Contract.CreateSignatureRedeemScript(p).ToScriptHash(), BigInteger.Zero)).ToArray();
                if (nodes.Length > 0)
                {
                    int index = (int)(response.Id % (ulong)nodes.Length);
                    nodes[index].GAS += GetPrice(engine.SnapshotCache);
                }
            }
            if (nodes != null)
            {
                foreach (var (account, gas) in nodes)
                {
                    if (gas.Sign > 0)
                        await GAS.Mint(engine, account, gas, false);
                }
            }
        }

        [ContractMethod(RequiredCallFlags = CallFlags.States | CallFlags.AllowNotify)]
        private async ContractTask Request(ApplicationEngine engine, string url, string filter, string callback, StackItem userData, long gasForResponse /* In the unit of datoshi, 1 datoshi = 1e-8 GAS */)
        {
            //Check the arguments
            if (Utility.StrictUTF8.GetByteCount(url) > MaxUrlLength
                || (filter != null && Utility.StrictUTF8.GetByteCount(filter) > MaxFilterLength)
                || Utility.StrictUTF8.GetByteCount(callback) > MaxCallbackLength || callback.StartsWith('_')
                || gasForResponse < 0_10000000)
                throw new ArgumentException();

            engine.AddFee(GetPrice(engine.SnapshotCache));

            //Mint gas for the response
            engine.AddFee(gasForResponse);
            await GAS.Mint(engine, Hash, gasForResponse, false);

            //Increase the request id
            StorageItem item_id = engine.SnapshotCache.GetAndChange(CreateStorageKey(Prefix_RequestId));
            ulong id = (ulong)(BigInteger)item_id;
            item_id.Add(1);

            //Put the request to storage
            if (ContractManagement.GetContract(engine.SnapshotCache, engine.CallingScriptHash) is null)
                throw new InvalidOperationException();
            engine.SnapshotCache.Add(CreateStorageKey(Prefix_Request).AddBigEndian(id), new StorageItem(new OracleRequest
            {
                OriginalTxid = GetOriginalTxid(engine),
                GasForResponse = gasForResponse,
                Url = url,
                Filter = filter,
                CallbackContract = engine.CallingScriptHash,
                CallbackMethod = callback,
                UserData = BinarySerializer.Serialize(userData, MaxUserDataLength, engine.Limits.MaxStackSize)
            }));

            //Add the id to the IdList
            var list = engine.SnapshotCache.GetAndChange(CreateStorageKey(Prefix_IdList).Add(GetUrlHash(url)), () => new StorageItem(new IdList())).GetInteroperable<IdList>();
            if (list.Count >= 256)
                throw new InvalidOperationException("There are too many pending responses for this url");
            list.Add(id);

            engine.SendNotification(Hash, "OracleRequest", new VM.Types.Array(engine.ReferenceCounter) { id, engine.CallingScriptHash.ToArray(), url, filter ?? StackItem.Null });
        }

        [ContractMethod(CpuFee = 1 << 15)]
        private bool Verify(ApplicationEngine engine)
        {
            Transaction tx = (Transaction)engine.ScriptContainer;
            return tx?.GetAttribute<OracleResponse>() != null;
        }

        private class IdList : InteroperableList<ulong>
        {
            protected override ulong ElementFromStackItem(StackItem item)
            {
                return (ulong)item.GetInteger();
            }

            protected override StackItem ElementToStackItem(ulong element, IReferenceCounter referenceCounter)
            {
                return element;
            }
        }
    }
}
