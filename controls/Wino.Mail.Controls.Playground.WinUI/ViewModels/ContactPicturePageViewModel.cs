using Wino.Mail.Controls.Playground.Models;

namespace Wino.Mail.Controls.Playground.ViewModels;

public sealed class ContactPicturePageViewModel
{
    public SampleContact Ada { get; } = new("Ada Lovelace", "ada@example.com");

    public SampleContact Grace { get; } = new("Grace Hopper", "grace@example.org");

    public SampleContact NoAddress { get; } = new("Anonymous contributor", string.Empty);
}
