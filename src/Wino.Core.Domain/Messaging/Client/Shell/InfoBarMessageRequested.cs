using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.Client.Shell;

/// <summary>
/// For displaying right sliding notification message in shell.
/// </summary>
/// <param name="Severity">Severity of notification.</param>
/// <param name="Title">Title of the message.</param>
/// <param name="Message">Message content.</param>
public record InfoBarMessageRequested(InfoBarMessageType Severity,
                                      string Title,
                                      string Message,
                                      string ActionButtonTitle = "",
                                      Action Action = null);
