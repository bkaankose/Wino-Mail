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
            child.TrailingAnchor.ConstraintEqualTo(parent.TrailingAnchor, (nfloat)(-inset)),
            child.TopAnchor.ConstraintEqualTo(parent.TopAnchor, (nfloat)inset),
            child.BottomAnchor.ConstraintEqualTo(parent.BottomAnchor, (nfloat)(-inset)) });
    }
}

/// <summary>Layout helpers under a name that does not collide with NSView.Layout() inside view subclasses.</summary>
public static class WinoLayout
{
    public static void Fill(NSView child, NSView parent, double inset = 0) => Layout.Fill(child, parent, inset);

    public static void Fill(NSView child, NSView parent, double top, double leading, double bottom, double trailing)
    {
        child.TranslatesAutoresizingMaskIntoConstraints = false;
        parent.AddSubview(child);
        NSLayoutConstraint.ActivateConstraints(new[] {
            child.LeadingAnchor.ConstraintEqualTo(parent.LeadingAnchor, (nfloat)leading),
            child.TrailingAnchor.ConstraintEqualTo(parent.TrailingAnchor, (nfloat)(-trailing)),
            child.TopAnchor.ConstraintEqualTo(parent.TopAnchor, (nfloat)top),
            child.BottomAnchor.ConstraintEqualTo(parent.BottomAnchor, (nfloat)(-bottom)) });
    }

    public static NSStackView VStack(double spacing, params NSView[] views)
    {
        var stack = Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, views);
        stack.Spacing = (nfloat)spacing;
        stack.Alignment = NSLayoutAttribute.Leading;
        return stack;
    }

    public static NSStackView HStack(double spacing, params NSView[] views)
    {
        var stack = Layout.Stack(NSUserInterfaceLayoutOrientation.Horizontal, views);
        stack.Spacing = (nfloat)spacing;
        stack.Alignment = NSLayoutAttribute.CenterY;
        return stack;
    }

    public static T Size<T>(T view, double width = -1, double height = -1) where T : NSView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        if (width >= 0) view.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        if (height >= 0) view.HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;
        return view;
    }

    /// <summary>Flexible space for stack views.</summary>
    public static NSView Spacer()
    {
        var view = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        view.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        view.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        return view;
    }
}
