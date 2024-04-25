using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using MoreLinq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Requests.Bundles;

namespace Wino.Core.Integration
{
    public abstract class BaseMailIntegrator<TNativeRequestType>
    {
        /// <summary>
        /// How many items per single HTTP call can be modified.
        /// </summary>
        public abstract uint BatchModificationSize { get; }

        /// <summary>
        /// How many items must be downloaded per folder when the folder is first synchronized.
        /// </summary>
        public abstract uint InitialMessageDownloadCountPerFolder { get; }

        /// <summary>
        /// Creates a batched HttpBundle without a response for a collection of MailItem.
        /// </summary>
        /// <param name="batchChangeRequest">Generated batch request.</param>
        /// <param name="action">An action to get the native request from the MailItem.</param>
        /// <returns>Collection of http bundle that contains batch and native request.</returns>
        public IEnumerable<IRequestBundle<TNativeRequestType>> CreateBatchedHttpBundleFromGroup(
            IBatchChangeRequest batchChangeRequest,
            Func<IEnumerable<IRequest>, TNativeRequestType> action)
        {
            if (batchChangeRequest.Items == null) yield break;

            var groupedItems = batchChangeRequest.Items.Batch((int)BatchModificationSize);

            foreach (var group in groupedItems)
                yield return new HttpRequestBundle<TNativeRequestType>(action(group), batchChangeRequest);
        }

        public IEnumerable<IRequestBundle<TNativeRequestType>> CreateBatchedHttpBundle(
            IBatchChangeRequest batchChangeRequest,
            Func<IRequest, TNativeRequestType> action)
        {
            if (batchChangeRequest.Items == null) yield break;

            var groupedItems = batchChangeRequest.Items.Batch((int)BatchModificationSize);

            foreach (var group in groupedItems)
                foreach (var item in group)
                    yield return new HttpRequestBundle<TNativeRequestType>(action(item), item);

            yield break;
        }

        /// <summary>
        /// Creates a single HttpBundle without a response for a collection of MailItem.
        /// </summary>
        /// <param name="batchChangeRequest">Batch request</param>
        /// <param name="action">An action to get the native request from the MailItem</param>
        /// <returns>Collection of http bundle that contains batch and native request.</returns>
        public IEnumerable<IRequestBundle<TNativeRequestType>> CreateHttpBundle(
            IBatchChangeRequest batchChangeRequest,
            Func<IRequest, TNativeRequestType> action)
        {
            if (batchChangeRequest.Items == null) yield break;

            foreach (var item in batchChangeRequest.Items)
                yield return new HttpRequestBundle<TNativeRequestType>(action(item), batchChangeRequest);
        }

        public IEnumerable<IRequestBundle<TNativeRequestType>> CreateHttpBundle<TResponseType>(
            IBatchChangeRequest batchChangeRequest,
            Func<IRequest, TNativeRequestType> action)
        {
            if (batchChangeRequest.Items == null) yield break;

            foreach (var item in batchChangeRequest.Items)
                yield return new HttpRequestBundle<TNativeRequestType, TResponseType>(action(item), item);
        }

        /// <summary>
        /// Creates HttpBundle with TResponse of expected response type from the http call for each of the items in the batch.
        /// </summary>
        /// <typeparam name="TResponse">Expected http response type after the call.</typeparam>
        /// <param name="batchChangeRequest">Generated batch request.</param>
        /// <param name="action">An action to get the native request from the MailItem.</param>
        /// <returns>Collection of http bundle that contains batch and native request.</returns>
        public IEnumerable<IRequestBundle<TNativeRequestType>> CreateHttpBundleWithResponse<TResponse>(
            IBatchChangeRequest batchChangeRequest,
            Func<IRequest, TNativeRequestType> action)
        {
            if (batchChangeRequest.Items == null) yield break;

            foreach (var item in batchChangeRequest.Items)
                yield return new HttpRequestBundle<TNativeRequestType, TResponse>(action(item), batchChangeRequest);
        }

        public IEnumerable<IRequestBundle<TNativeRequestType>> CreateHttpBundleWithResponse<TResponse>(
            IRequestBase item,
            Func<IRequestBase, TNativeRequestType> action)
        {
            yield return new HttpRequestBundle<TNativeRequestType, TResponse>(action(item), item);
        }

        /// <summary>
        /// Creates a batched HttpBundle with TResponse of expected response type from the http call for each of the items in the batch.
        /// Func will be executed for each item separately in the batch request.
        /// </summary>
        /// <typeparam name="TResponse">Expected http response type after the call.</typeparam>
        /// <param name="batchChangeRequest">Generated batch request.</param>
        /// <param name="action">An action to get the native request from the MailItem.</param>
        /// <returns>Collection of http bundle that contains batch and native request.</returns>
        public IEnumerable<IRequestBundle<TNativeRequestType>> CreateBatchedHttpBundle<TResponse>(
            IBatchChangeRequest batchChangeRequest,
            Func<IRequest, TNativeRequestType> action)
        {
            if (batchChangeRequest.Items == null) yield break;

            var groupedItems = batchChangeRequest.Items.Batch((int)BatchModificationSize);

            foreach (var group in groupedItems)
                foreach (var item in group)
                    yield return new HttpRequestBundle<TNativeRequestType, TResponse>(action(item), item);

            yield break;
        }

        public IEnumerable<IRequestBundle<ImapRequest>> CreateTaskBundle(Func<ImapClient, Task> value, IRequestBase request)
        {
            var imapreq = new ImapRequest(value, request);

            return [new ImapRequestBundle(imapreq, request)];
        }
    }
}
