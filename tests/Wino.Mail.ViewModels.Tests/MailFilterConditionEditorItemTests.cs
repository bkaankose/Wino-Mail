using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class MailFilterConditionEditorItemTests
{
    [Fact]
    public void ChangingToHasAttachments_ResolvesAChoiceAndValue()
    {
        var item = new MailFilterConditionEditorItem
        {
            FieldOptions = [],
            OperatorOptions =
            [
                new(MailFilterConditionOperator.Contains, "Contains"),
                new(MailFilterConditionOperator.Equals, "Equals")
            ],
            SelectedField = new(MailFilterConditionField.Subject, "Subject"),
            SelectedOperator = new(MailFilterConditionOperator.Contains, "Contains"),
            Value = "old value"
        };

        item.SelectedField = new(MailFilterConditionField.HasAttachments, "Has attachments");

        item.SelectedChoice.Should().NotBeNull();
        item.SelectedChoice!.Value.Should().Be(bool.TrueString);
        item.Value.Should().Be(bool.TrueString);
        item.SelectedOperator!.Value.Should().Be(MailFilterConditionOperator.Equals);
    }
}
