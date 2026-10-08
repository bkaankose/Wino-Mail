#nullable enable

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Implemented by the view model of a page that acts as the root of an application mode.
/// Navigating the inner shell frame to such a page hands its provider's menu to the shell.
/// Pages that do not implement this (detail pages) leave the current menu in place.
/// </summary>
public interface IShellMenuOwner
{
    IShellMenuProvider ShellMenuProvider { get; }
}
