using AppKit;

namespace Wino.Presentation.AppKit;

public static class Layout
{
    public static NSStackView Stack(NSUserInterfaceLayoutOrientation orientation, params NSView[] views)
    {
        var stack = new NSStackView { Orientation = orientation, Spacing = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        foreach (var view in views) stack.AddArrangedSubview(view);
        return stack;
    }
    public static void Fill(NSView child, NSView parent, double inset = 0)
    {
        child.TranslatesAutoresizingMaskIntoConstraints = false;
        parent.AddSubview(child);
        NSLayoutConstraint.ActivateConstraints(new[] {
            child.LeadingAnchor.ConstraintEqualTo(parent.LeadingAnchor, (nfloat)inset),
            child.TrailingAnchor.ConstraintEqualTo(parent.TrailingAnchor, (nfloat)-inset),
            child.TopAnchor.ConstraintEqualTo(parent.TopAnchor, (nfloat)inset),
            child.BottomAnchor.ConstraintEqualTo(parent.BottomAnchor, (nfloat)-inset) });
    }
}
