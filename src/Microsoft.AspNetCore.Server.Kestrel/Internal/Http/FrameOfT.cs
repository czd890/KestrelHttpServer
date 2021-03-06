// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Http
{
    public class Frame<TContext> : Frame
    {
        private readonly IHttpApplication<TContext> _application;

        public Frame(IHttpApplication<TContext> application,
                     ConnectionContext context)
            : base(context)
        {
            _application = application;
        }

        /// <summary>
        /// Primary loop which consumes socket input, parses it for protocol framing, and invokes the
        /// application delegate for as long as the socket is intended to remain open.
        /// The resulting Task from this loop is preserved in a field which is used when the server needs
        /// to drain and close all currently active connections.
        /// </summary>
        public override async Task RequestProcessingAsync()
        {
            try
            {
                while (!_requestProcessingStopping)
                {
                    ConnectionControl.SetTimeout(_keepAliveMilliseconds, TimeoutAction.CloseConnection);

                    while (!_requestProcessingStopping && TakeStartLine(SocketInput) != RequestLineStatus.Done)
                    {
                        if (SocketInput.CheckFinOrThrow())
                        {
                            // We need to attempt to consume start lines and headers even after
                            // SocketInput.RemoteIntakeFin is set to true to ensure we don't close a
                            // connection without giving the application a chance to respond to a request
                            // sent immediately before the a FIN from the client.
                            var requestLineStatus = TakeStartLine(SocketInput);

                            if (requestLineStatus == RequestLineStatus.Empty)
                            {
                                return;
                            }

                            if (requestLineStatus != RequestLineStatus.Done)
                            {
                                RejectRequest(RequestRejectionReason.InvalidRequestLine, requestLineStatus.ToString());
                            }

                            break;
                        }

                        await SocketInput;
                    }

                    InitializeHeaders();

                    while (!_requestProcessingStopping && !TakeMessageHeaders(SocketInput, FrameRequestHeaders))
                    {
                        if (SocketInput.CheckFinOrThrow())
                        {
                            // We need to attempt to consume start lines and headers even after
                            // SocketInput.RemoteIntakeFin is set to true to ensure we don't close a
                            // connection without giving the application a chance to respond to a request
                            // sent immediately before the a FIN from the client.
                            if (!TakeMessageHeaders(SocketInput, FrameRequestHeaders))
                            {
                                RejectRequest(RequestRejectionReason.MalformedRequestInvalidHeaders);
                            }

                            break;
                        }

                        await SocketInput;
                    }

                    if (!_requestProcessingStopping)
                    {
                        var messageBody = MessageBody.For(_httpVersion, FrameRequestHeaders, this);
                        _keepAlive = messageBody.RequestKeepAlive;
                        _upgrade = messageBody.RequestUpgrade;

                        InitializeStreams(messageBody);

                        var context = _application.CreateContext(this);
                        try
                        {
                            try
                            {
                                await _application.ProcessRequestAsync(context).ConfigureAwait(false);
                                VerifyResponseContentLength();
                            }
                            catch (Exception ex)
                            {
                                ReportApplicationError(ex);

                                if (ex is BadHttpRequestException)
                                {
                                    throw;
                                }
                            }
                            finally
                            {
                                // Trigger OnStarting if it hasn't been called yet and the app hasn't
                                // already failed. If an OnStarting callback throws we can go through
                                // our normal error handling in ProduceEnd.
                                // https://github.com/aspnet/KestrelHttpServer/issues/43
                                if (!HasResponseStarted && _applicationException == null && _onStarting != null)
                                {
                                    await FireOnStarting();
                                }

                                PauseStreams();

                                if (_onCompleted != null)
                                {
                                    await FireOnCompleted();
                                }
                            }

                            // If _requestAbort is set, the connection has already been closed.
                            if (Volatile.Read(ref _requestAborted) == 0)
                            {
                                ResumeStreams();

                                if (_keepAlive)
                                {
                                    // Finish reading the request body in case the app did not.
                                    await messageBody.Consume();
                                }

                                // ProduceEnd() must be called before _application.DisposeContext(), to ensure
                                // HttpContext.Response.StatusCode is correctly set when
                                // IHttpContextFactory.Dispose(HttpContext) is called.
                                await ProduceEnd();
                            }
                            else if (!HasResponseStarted)
                            {
                                // If the request was aborted and no response was sent, there's no
                                // meaningful status code to log.
                                StatusCode = 0;
                            }
                        }
                        catch (BadHttpRequestException ex)
                        {
                            // Handle BadHttpRequestException thrown during app execution or remaining message body consumption.
                            // This has to be caught here so StatusCode is set properly before disposing the HttpContext
                            // (DisposeContext logs StatusCode).
                            SetBadRequestState(ex);
                        }
                        finally
                        {
                            _application.DisposeContext(context, _applicationException);
                        }
                    }

                    StopStreams();

                    if (!_keepAlive)
                    {
                        // End the connection for non keep alive as data incoming may have been thrown off
                        return;
                    }

                    // Don't reset frame state if we're exiting the loop. This avoids losing request rejection
                    // information (for 4xx response), and prevents ObjectDisposedException on HTTPS (ODEs
                    // will be thrown if PrepareRequest is not null and references objects disposed on connection
                    // close - see https://github.com/aspnet/KestrelHttpServer/issues/1103#issuecomment-250237677).
                    if (!_requestProcessingStopping)
                    {
                        Reset();
                    }
                }
            }
            catch (BadHttpRequestException ex)
            {
                // Handle BadHttpRequestException thrown during request line or header parsing.
                // SetBadRequestState logs the error.
                SetBadRequestState(ex);
            }
            catch (Exception ex)
            {
                Log.LogWarning(0, ex, "Connection processing ended abnormally");
            }
            finally
            {
                try
                {
                    // If _requestAborted is set, the connection has already been closed.
                    if (Volatile.Read(ref _requestAborted) == 0)
                    {
                        await TryProduceInvalidRequestResponse();
                        ConnectionControl.End(ProduceEndType.SocketShutdown);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning(0, ex, "Connection shutdown abnormally");
                }
            }
        }
    }
}
