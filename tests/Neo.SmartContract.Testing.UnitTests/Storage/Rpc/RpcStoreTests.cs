// Copyright (C) 2015-2026 The Neo Project.
//
// RpcStoreTests.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Testing.Storage.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.SmartContract.Testing.UnitTests.Storage
{
    [TestClass]
    public class RpcStoreTests
    {
        [TestMethod]
        public void TestRpcStore()
        {
            using var store = new RpcStore("http://unit.test:20332", new StubRpcHandler(HandleRpcRequest));

            Assert.IsTrue(store.TryGet(StorageKey.Create(7, 0x01, (byte)0x02).ToArray(), out var value));
            CollectionAssert.AreEqual(new byte[] { 0x03, 0x04 }, value);

            var entries = store.Find(StorageKey.Create(7, 0x01).ToArray(), SeekDirection.Forward).ToArray();

            Assert.AreEqual(2, entries.Length);
            CollectionAssert.AreEqual(StorageKey.Create(7, 0x01, (byte)0x02).ToArray(), entries[0].Key);
            CollectionAssert.AreEqual(new byte[] { 0x03, 0x04 }, entries[0].Value);
            CollectionAssert.AreEqual(StorageKey.Create(7, 0x01, (byte)0x03).ToArray(), entries[1].Key);
            CollectionAssert.AreEqual(new byte[] { 0x05, 0x06 }, entries[1].Value);
        }

        [TestMethod]
        public void TestRpcStoreUnknownStorage()
        {
            using var store = new RpcStore("http://unit.test:20332", new StubRpcHandler(request => RpcError(request, -100, "Unknown storage")));

            Assert.IsFalse(store.TryGet(StorageKey.Create(7, 0x01, (byte)0x02).ToArray(), out var value));
            Assert.IsNull(value);
            Assert.AreEqual(0, store.Find(StorageKey.Create(7, 0x01).ToArray(), SeekDirection.Forward).Count());
        }

        [TestMethod]
        public void TestRpcStoreEmptyResponse()
        {
            using var store = new RpcStore("http://unit.test:20332", new StubRpcHandler(_ => null));

            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => store.TryGet(StorageKey.Create(7, 0x01, (byte)0x02).ToArray(), out _));

            Assert.AreEqual("RPC endpoint returned an empty response.", ex.Message);
        }

        private static JObject HandleRpcRequest(JObject request)
        {
            var method = request["method"]?.Value<string>();
            var parameters = request["params"]?.Value<JArray>()?.Values<string>().Select(p => p!).ToArray();

            return method switch
            {
                "getstorage" => HandleGetStorage(request, parameters),
                "findstorage" => HandleFindStorage(request, parameters),
                _ => RpcError(request, -32601, "Method not found")
            };
        }

        private static JObject HandleGetStorage(JObject request, IReadOnlyList<string>? parameters)
        {
            Assert.IsNotNull(parameters);
            Assert.AreEqual("7", parameters![0]);
            Assert.AreEqual(Convert.ToBase64String(new byte[] { 0x01, 0x02 }), parameters[1]);

            return RpcResult(request, Convert.ToBase64String(new byte[] { 0x03, 0x04 }));
        }

        private static JObject HandleFindStorage(JObject request, IReadOnlyList<string>? parameters)
        {
            Assert.IsNotNull(parameters);
            Assert.AreEqual("7", parameters![0]);
            Assert.AreEqual(Convert.ToBase64String(new byte[] { 0x01 }), parameters[1]);

            return parameters[2] switch
            {
                "0" => RpcResult(request, new JObject
                {
                    ["results"] = new JArray
                    {
                        new JObject
                        {
                            ["key"] = Convert.ToBase64String(new byte[] { 0x01, 0x02 }),
                            ["value"] = Convert.ToBase64String(new byte[] { 0x03, 0x04 })
                        }
                    },
                    ["truncated"] = true,
                    ["next"] = 2
                }),
                "2" => RpcResult(request, new JObject
                {
                    ["results"] = new JArray
                    {
                        new JObject
                        {
                            ["key"] = Convert.ToBase64String(new byte[] { 0x01, 0x03 }),
                            ["value"] = Convert.ToBase64String(new byte[] { 0x05, 0x06 })
                        }
                    },
                    ["truncated"] = false
                }),
                _ => RpcError(request, -32602, "Invalid params")
            };
        }

        private static JObject RpcResult(JObject request, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"],
                ["result"] = result
            };
        }

        private static JObject RpcError(JObject request, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"],
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
        }

        private sealed class StubRpcHandler(Func<JObject, JObject?> handler) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var requestContent = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var response = handler(JObject.Parse(requestContent));
                var responseMessage = new HttpResponseMessage(HttpStatusCode.OK);

                if (response is not null)
                {
                    responseMessage.Content = new StringContent(response.ToString(Formatting.None), Encoding.UTF8, "application/json");
                }

                return responseMessage;
            }
        }
    }
}
