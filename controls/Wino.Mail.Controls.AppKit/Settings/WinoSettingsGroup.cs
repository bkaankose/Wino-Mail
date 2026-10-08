using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// A titled run of settings cards, the Windows section header (13pt semibold, 22pt above and 6pt
/// below) followed by cards stacked with 4pt spacing. Without a title it is only the card stack.
/// </summary>
public sealed class WinoSettingsGroup : NSView
{
    private readonly NSStackView _rows;
    private readonly NSTextField _title;
    private readonly NSTextField _footer;
    private readonly NSView _titleWrapper;
    private readonly NSView _footerWrapper;

    public WinoSettingsGroup(string? title = null, string? footer = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _title = WinoStyle.Label(title, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);
        _footer = WinoStyle.Label(footer, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);

        _rows = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = (nfloat)WinoSettingsStyle.CardSpacing,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var outer = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        // The page stack already adds 4pt between sections; the wrappers add the rest of 22 above / 6 below.
        _titleWrapper = Inset(_title, 18, 1, 2);
        _footerWrapper = Inset(_footer, 4, 1, 0);
        _titleWrapper.Hidden = string.IsNullOrEmpty(title);
        _footerWrapper.Hidden = string.IsNullOrEmpty(footer);
        outer.AddArrangedSubview(_titleWrapper);
        outer.AddArrangedSubview(_rows);
        outer.AddArrangedSubview(_footerWrapper);
        _rows.WidthAnchor.ConstraintEqualTo(outer.WidthAnchor).Active = true;
        _titleWrapper.WidthAnchor.ConstraintEqualTo(outer.WidthAnchor).Active = true;
        _footerWrapper.WidthAnchor.ConstraintEqualTo(outer.WidthAnchor).Active = true;
        WinoLayout.Fill(outer, this);
    }

    /// <summary>The card stack, for callers that need to hide or inspect it.</summary>
    public NSView Surface => _rows;

    public int RowCount => _rows.ArrangedSubviews.Length;

    private static NSView Inset(NSTextField label, double top, double leading, double bottom)
    {
        var wrapper = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, wrapper, top, leading, bottom, 0);
        return wrapper;
    }

    public string? Footer
    {
        get => _footer.StringValue;
        set { _footer.StringValue = value ?? string.Empty; _footerWrapper.Hidden = string.IsNullOrEmpty(value); }
    }

    public string? Title
    {
        get => _title.StringValue;
        set { _title.StringValue = value ?? string.Empty; _titleWrapper.Hidden = string.IsNullOrEmpty(value); }
    }

    /// <summary>Removes every row, for groups rebuilt from a collection.</summary>
    public void Clear()
    {
        foreach (var view in _rows.ArrangedSubviews)
        {
            _rows.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
    }

    public void Add(NSView row)
    {
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        _rows.AddArrangedSubview(row);
        row.WidthAnchor.ConstraintEqualTo(_rows.WidthAnchor).Active = true;
    }

    public void AddRange(params NSView[] rows)
    {
        foreach (var row in rows) Add(row);
    }
}
