#nullable enable
using System;

namespace Wino.Mail.ViewModels;

/// <summary>
/// A link the renderer already resolved: redirects unwrapped and, when the user's preference asks for it,
/// tracking parameters removed. <see cref="MailRenderingPageViewModel.OpenLinkAsync(ResolvedMailLink)"/> opens
/// <see cref="Address"/> as it is.
/// </summary>
/// <param name="LinkText">The anchor's visible text, when the renderer captured it.</param>
/// <param name="OriginalAddress">The address as written in the message, or null when it equals <paramref name="Address"/>.</param>
public sealed record ResolvedMailLink(Uri Address, string? LinkText = null, int RemovedTrackingParameterCount = 0, Uri? OriginalAddress = null);
