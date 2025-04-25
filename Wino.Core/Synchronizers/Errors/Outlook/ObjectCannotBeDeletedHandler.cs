using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Errors;

namespace Wino.Core.Synchronizers.Errors.Outlook;

internal class ObjectCannotBeDeletedHandler : ISynchronizerErrorHandler
{
    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.ErrorMessage.Contains("ObjectCannotBeDeleted");
    }

    public Task HandleAsync(SynchronizerErrorContext error)
    {
        // Handle the error here, e.g., log it or notify the user
        Console.WriteLine($"Error: {error.ErrorMessage}");
        return Task.CompletedTask;
    }
}
