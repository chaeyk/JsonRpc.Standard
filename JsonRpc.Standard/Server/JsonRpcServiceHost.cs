﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using JsonRpc.Standard.Contracts;
using Newtonsoft.Json.Linq;

namespace JsonRpc.Standard.Server
{
    /// <summary>
    /// Provides options for <see cref="JsonRpcServiceHost"/>.
    /// </summary>
    [Flags]
    public enum JsonRpcServiceHostOptions
    {
        /// <summary>
        /// No options.
        /// </summary>
        None = 0,
        /// <summary>
        /// Makes the response sequence consistent with the request order.
        /// </summary>
        ConsistentResponseSequence,
    }

    internal class JsonRpcServiceHost : IJsonRpcServiceHost
    {

        internal JsonRpcServiceHost(JsonRpcServerContract contract, JsonRpcServiceHostOptions options)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            Propagator = new TransformBlock<Message, ResponseMessage>(
                (Func<Message, Task<ResponseMessage>>) ReaderAction,
                new ExecutionDataflowBlockOptions
                {
                    EnsureOrdered = (options & JsonRpcServiceHostOptions.ConsistentResponseSequence) ==
                                    JsonRpcServiceHostOptions.ConsistentResponseSequence
                });
            // Drain null responses generated by RpcMethodEntryPoint.
            Propagator.LinkTo(DataflowBlock.NullTarget<ResponseMessage>(), m => m == null);
            Contract = contract;
            Options = options;
        }

        protected IPropagatorBlock<Message, ResponseMessage> Propagator { get; }

        internal ISession Session { get; set; }

        internal JsonRpcServerContract Contract { get; }

        internal JsonRpcServiceHostOptions Options { get; }

        public IServiceFactory ServiceFactory { get; set; }

        internal IJsonRpcMethodBinder MethodBinder { get; set; }

        /// <inheritdoc />
        public IDisposable Attach(ISourceBlock<Message> source, ITargetBlock<ResponseMessage> target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            var d1 = source.LinkTo(Propagator, new DataflowLinkOptions {PropagateCompletion = true},
                m => m is GeneralRequestMessage);
            var d2 = Propagator.LinkTo(target, new DataflowLinkOptions {PropagateCompletion = true},
                m => m != null);
            return Utility.CombineDisposable(d1, d2);
        }

        private Task<ResponseMessage> ReaderAction(Message message)
        {
            var ct = CancellationToken.None;
            if (ct.IsCancellationRequested) return Task.FromCanceled<ResponseMessage>(ct);
            var request = message as GeneralRequestMessage;
            if (request == null) return Task.FromResult<ResponseMessage>(null);
            // TODO provides a way to cancel the request from inside JsonRpcService.
            var context = new RequestContext(this, Session, request, ct);
            return RpcMethodEntryPoint(context);
        }

        private async Task<ResponseMessage> RpcMethodEntryPoint(RequestContext context)
        {
            var request = context.Request as RequestMessage;
            JsonRpcMethod method = null;
            try
            {
                if (Contract.Methods.TryGetValue(context.Request.Method, out var candidates))
                    method = MethodBinder.TryBindToMethod(candidates, context);
                else
                    return new ResponseMessage(request.Id, new ResponseError(JsonRpcErrorCode.MethodNotFound,
                        $"Method \"{request.Method}\" is not found."));
            }
            catch (AmbiguousMatchException)
            {
                if (request != null)
                    return new ResponseMessage(request.Id, new ResponseError(JsonRpcErrorCode.InvalidRequest,
                        $"Invocation of method \"{request.Method}\" is ambiguous."));
                return null;
            }
            if (method == null)
            {
                if (request != null)
                    return new ResponseMessage(request.Id, new ResponseError(JsonRpcErrorCode.MethodNotFound,
                        $"Cannot find method \"{request.Method}\" with matching signature."));
                return null;
            }
            object[] args;
            object result;
            try
            {
                args = method.UnmarshalArguments(context.Request);
            }
            catch (ArgumentException ex)
            {
                // Signature not match. This is not likely to happen. Still there might be problem with binder.
                if (request != null)
                    return new ResponseMessage(request.Id,
                        new ResponseError(JsonRpcErrorCode.InvalidParams, ex.Message));
                return null;
            }
            catch (Exception ex)
            {
                if (request != null)
                    return new ResponseMessage(request.Id, ResponseError.FromException(ex));
                return null;
            }
            try
            {
                result = await method.Invoker.InvokeAsync(context, args).ConfigureAwait(false);
            }
            catch (TargetInvocationException ex)
            {
                if (request != null)
                    return new ResponseMessage(request.Id, ResponseError.FromException(ex.InnerException));
                return null;
            }
            catch (Exception ex)
            {
                if (request != null)
                    return new ResponseMessage(request.Id, ResponseError.FromException(ex));
                return null;
            }
            if (request != null)
            {
                // We need a response.
                if (result == null)
                    return new ResponseMessage(request.Id, JValue.CreateNull());
                if (result is ResponseError error)
                    return new ResponseMessage(request.Id, error);
                return new ResponseMessage(request.Id, method.ReturnParameter.Converter.ValueToJson(result));
            }
            // Otherwise, we do not send anything.
            return null;
        }
    }
}
