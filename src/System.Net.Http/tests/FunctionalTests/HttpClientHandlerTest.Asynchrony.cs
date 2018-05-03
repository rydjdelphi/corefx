// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Test.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Http.Functional.Tests
{
    public abstract class HttpClientHandler_Asynchrony_Test : HttpClientTestBase
    {
        public static IEnumerable<object[]> ResponseHeadersRead_SynchronizationContextNotUsedByHandler_MemberData() =>
            from responseHeadersRead in new[] { false, true }
            from contentMode in Enum.GetValues(typeof(LoopbackServer.ContentMode)).Cast<LoopbackServer.ContentMode>()
            select new object[] { responseHeadersRead, contentMode };

        [Theory]
        [MemberData(nameof(ResponseHeadersRead_SynchronizationContextNotUsedByHandler_MemberData))]
        public async Task ResponseHeadersRead_SynchronizationContextNotUsedByHandler(bool responseHeadersRead, LoopbackServer.ContentMode contentMode)
        {
            await Task.Run(async delegate // escape xunit's sync ctx
            {
                await LoopbackServer.CreateClientAndServerAsync(uri =>
                {
                    return Task.Run(() => // allow client and server to run concurrently even though this is all synchronous/blocking
                    {
                        var sc = new TrackingSynchronizationContext();
                        SynchronizationContext.SetSynchronizationContext(sc);

                        using (HttpClient client = CreateHttpClient())
                        {
                            if (responseHeadersRead)
                            {
                                using (HttpResponseMessage resp = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                                using (Stream respStream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                                {
                                    byte[] buffer = new byte[0x1000];
                                    while (respStream.ReadAsync(buffer, 0, buffer.Length).GetAwaiter().GetResult() > 0);
                                }
                            }
                            else
                            {
                                client.GetStringAsync(uri).GetAwaiter().GetResult();
                            }
                        }

                        Assert.True(sc.CallStacks.Count == 0, "Sync Ctx used: " + string.Join(Environment.NewLine + Environment.NewLine, sc.CallStacks));
                    });
                }, async server =>
                {
                    await server.AcceptConnectionAsync(async connection =>
                    {
                        await connection.ReadRequestHeaderAsync();
                        await connection.Writer.WriteAsync(
                            LoopbackServer.GetContentModeResponse(
                                contentMode,
                                string.Concat(Enumerable.Repeat('s', 10_000)),
                                connectionClose: true));
                    });
                }, new LoopbackServer.Options { StreamWrapper = s => new DribbleStream(s) });
            });
        }

        private sealed class TrackingSynchronizationContext : SynchronizationContext
        {
            public readonly List<string> CallStacks = new List<string>();

            public override void OperationStarted() => CallStacks.Add(Environment.StackTrace);
            public override void OperationCompleted() => CallStacks.Add(Environment.StackTrace);

            public override void Post(SendOrPostCallback d, object state)
            {
                CallStacks.Add(Environment.StackTrace);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    SetSynchronizationContext(this);
                    d(state);
                });
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                CallStacks.Add(Environment.StackTrace);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    SynchronizationContext orig = SynchronizationContext.Current;
                    try
                    {
                        SetSynchronizationContext(this);
                        d(state);
                    }
                    finally
                    {
                        SetSynchronizationContext(orig);
                    }
                });
            }
        }
    }
}
