using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Requests
{
    ///// <summary>
    ///// Encapsulates request to queue and account for synchronizer.
    ///// </summary>
    ///// <param name="AccountId"><inheritdoc/></param>
    ///// <param name="Request"></param>
    ///// <param name="Request">Prepared request for the server.</param>
    ///// <param name="AccountId">Which account to execute this request for.</param>
    public class ServerRequestBundle(Guid accountId, IRequestBase request) : IClientMessage
    {
        public Guid AccountId { get; } = accountId;

        public IRequestBase Request { get; } = request;
    }


    //public record ServerRequestPackage<TUserActionRequestType>(Guid AccountId, TUserActionRequestType Request)
    //    : ServerRequestBundle(AccountId), IClientMessage where TUserActionRequestType : IRequestBase;
}
