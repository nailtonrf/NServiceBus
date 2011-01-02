﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Transactions;
using log4net;
using NServiceBus.Unicast.Queuing;
using NServiceBus.Unicast.Transport;

namespace NServiceBus.Gateway
{
    public class HttpRequestHandler
    {
        private const int maximumBytesToRead = 100000;
        private readonly string inputQueue;
        private ISendMessages messageSender;
        private string destinationQueue;

        public HttpRequestHandler(string inputQueue, ISendMessages sender, string queue)
        {
            this.inputQueue = inputQueue;
            messageSender = sender;
            destinationQueue = queue;
        }

        public void Handle(HttpListenerContext ctx)
        {
            try
            {
                if (ctx.Request.ContentLength64 > 4 * 1024 * 1024)
                {
                    CloseResponseAndWarn(ctx, "Cannot accept messages larger than 4MB.", 413);
                    return;
                }

                string hash = ctx.Request.Headers[Headers.ContentMd5Key];
                if (hash == null)
                {
                    CloseResponseAndWarn(ctx, "Required header '" + Headers.ContentMd5Key + "' missing.", 400);
                    return;
                }

                var callInfo = GetCallInfo(ctx);

                switch(callInfo.Type)
                {
                    case CallType.Submit: HandleSubmit(ctx, callInfo); break;
                    case CallType.Ack: HandleAck(ctx, callInfo); break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error", ex);
                CloseResponseAndWarn(ctx, "Unexpected server error", 502);
            }

            Logger.Info("Http request processing complete.");
        }

        private void HandleAck(HttpListenerContext ctx, CallInfo callInfo)
        {
            var msg = new TransportMessage { ReturnAddress = inputQueue };

            using (var scope = new TransactionScope(TransactionScopeOption.Required, new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted }))
            {
                var p = new Persistence
                {
                    ConnectionString =
                        @"Data Source=UDIDAHANMOBILE2\SQLEXPRESS;Initial Catalog=model;Integrated Security=True"
                };

                msg.Body = p.AckMessage(callInfo.ClientId, Convert.FromBase64String(callInfo.MD5));

                //check to see if this is a gateway from another site
                if (ctx.Request.Headers["NServiceBus.Gateway"] != null)
                    HeaderMapper.Map(ctx.Request.Headers, msg);
                else
                {
                    msg.MessageIntent = MessageIntentEnum.Send;
                    msg.Recoverable = true;
                    msg.Headers = new Dictionary<string, string>();
                }

                if (ctx.Request.Headers[Headers.FromKey] != null)
                    msg.Headers.Add(NServiceBus.Headers.HttpFrom, ctx.Request.Headers[Headers.FromKey]);

                if (msg.Headers.ContainsKey(HeaderMapper.RouteTo))
                    messageSender.Send(msg, msg.Headers[HeaderMapper.RouteTo]);
                else
                    messageSender.Send(msg, destinationQueue);

                scope.Complete();
            }

            ReportSuccess(ctx);
        }

        private void HandleSubmit(HttpListenerContext ctx, CallInfo callInfo)
        {
            string hash = ctx.Request.Headers[Headers.ContentMd5Key];

            byte[] buffer = GetBuffer(ctx);
            string myHash = Hasher.Hash(buffer);

            if (myHash != hash)
            {
                CloseResponseAndWarn(ctx, "MD5 hash received does not match hash calculated on server. Consider resubmitting.", 412);
                return;
            }

            using (var scope = new TransactionScope(TransactionScopeOption.Required, new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted }))
            {
                var p = new Persistence
                            {
                                ConnectionString =
                                    @"Data Source=UDIDAHANMOBILE2\SQLEXPRESS;Initial Catalog=model;Integrated Security=True"
                            };
                p.InsertMessage(DateTime.UtcNow, callInfo.ClientId, Convert.FromBase64String(callInfo.MD5), buffer, "headers");

                scope.Complete();
            }

            ReportSuccess(ctx);
        }

        private void ReportSuccess(HttpListenerContext ctx)
        {
            Logger.Debug("Sending HTTP 200 response.");

            ctx.Response.StatusCode = 200;
            ctx.Response.StatusDescription = "OK";

            ctx.Response.Close(Encoding.ASCII.GetBytes("<html><body>" + ctx.Response.StatusDescription + "</body></html>"), false);
        }

        private byte[] GetBuffer(HttpListenerContext ctx)
        {
            var length = (int)ctx.Request.ContentLength64;
            var buffer = new byte[length];

            int numBytesToRead = length;
            int numBytesRead = 0;
            while (numBytesToRead > 0)
            {
                int n = ctx.Request.InputStream.Read(
                    buffer, 
                    numBytesRead, 
                    numBytesToRead < maximumBytesToRead ? numBytesToRead : maximumBytesToRead);
                    
                if (n == 0)
                    break;

                numBytesRead += n;
                numBytesToRead -= n;
            }
            return buffer;
        }

        private CallInfo GetCallInfo(HttpListenerContext ctx)
        {
            CallType type;
            var callTypeHeader = HeaderMapper.NServiceBus + HeaderMapper.CallType;
            string callType = ctx.Request.Headers[callTypeHeader];
            if (!Enum.TryParse(callType, out type))
            {
                CloseResponseAndWarn(ctx, "Required header '" + callTypeHeader + "' missing.", 400);
                return null;
            }

            var clientIdHeader = HeaderMapper.NServiceBus + HeaderMapper.Id;
            var clientId = ctx.Request.Headers[clientIdHeader];
            if (clientId == null)
            {
                CloseResponseAndWarn(ctx, "Required header '" + clientIdHeader + "' missing.", 400);
                return null;
            }

            return new CallInfo
                       {
                           ClientId = ctx.Request.Headers[HeaderMapper.NServiceBus + HeaderMapper.Id],
                           MD5 = ctx.Request.Headers[Headers.ContentMd5Key],
                           Type = type
                       };
        }

        private static void CloseResponseAndWarn(HttpListenerContext ctx, string warning, int statusCode)
        {
            try
            {
                Logger.WarnFormat("Cannot process HTTP request from {0}. Reason: {1}.", ctx.Request.RemoteEndPoint, warning);
                ctx.Response.StatusCode = statusCode;
                ctx.Response.StatusDescription = warning;

                ctx.Response.Close(Encoding.ASCII.GetBytes("<html><body>" + warning + "</body></html>"), false);
            }
            catch (Exception e)
            {
                Logger.Warn("Could not return warning to client.", e);
            }
        }

        private static readonly ILog Logger = LogManager.GetLogger("NServiceBus.Gateway");
    }
}
