using UIKit;

namespace Wino.Presentation.UIKit;

/// <summary>Auto Layout helpers, the UIKit twin of the AppKit <c>Layout</c> class.</summary>
public static class Layout
{
    public static UIStackView Stack(UILayoutConstraintAxis axis, params UIView[] views)
    {
        var stack = new UIStackView { Axis = axis, Spacing = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        foreach (var view in views) stack.AddArrangedSubview(view);
        return stack;
    }

    public static void Fill(UIView child, UIView parent, double inset = 0)
        => WinoLayout.Fill(child, parent, inset, inset, inset, inset);
}

/// <summary>
/// Layout helpers under the same names as the AppKit <c>WinoLayout</c>: fill a parent, vertical and horizontal stacks,
/// fixed sizes and flexible spacers. Stack insets use the stack's layout margins (<see cref="Insets"/>), the twin of
/// NSStackView.EdgeInsets.
/// </summary>
public static class WinoLayout
{
    public static void Fill(UIView child, UIView parent, double inset = 0) => Fill(child, parent, inset, inset, inset, inset);

    public static void Fill(UIView child, UIView parent, double top, double leading, double bottom, double trailing)
    {
        child.TranslatesAutoresizingMaskIntoConstraints = false;
        parent.AddSubview(child);
        NSLayoutConstraint.ActivateConstraints(
        [
            child.LeadingAnchor.ConstraintEqualTo(parent.LeadingAnchor, (nfloat)leading),
            child.TrailingAnchor.ConstraintEqualTo(parent.TrailingAnchor, (nfloat)(-trailing)),
            child.TopAnchor.ConstraintEqualTo(parent.TopAnchor, (nfloat)top),
            child.BottomAnchor.ConstraintEqualTo(parent.BottomAnchor, (nfloat)(-bottom))
        ]);
    }

    /// <summary>A vertical stack with leading alignment.</summary>
    public static UIStackView VStack(double spacing, params UIView[] views)
    {
        var stack = Layout.Stack(UILayoutConstraintAxis.Vertical, views);
        stack.Spacing = (nfloat)spacing;
        stack.Alignment = UIStackViewAlignment.Leading;
        return stack;
    }

    /// <summary>A horizontal stack with centred alignment.</summary>
    public static UIStackView HStack(double spacing, params UIView[] views)
    {
        var stack = Layout.Stack(UILayoutConstraintAxis.Horizontal, views);
        stack.Spacing = (nfloat)spacing;
        stack.Alignment = UIStackViewAlignment.Center;
        return stack;
    }

    /// <summary>Insets a stack's arranged views (NSStackView.EdgeInsets on AppKit).</summary>
    public static T Insets<T>(T stack, double top, double leading, double bottom, double trailing) where T : UIStackView
    {
        stack.LayoutMarginsRelativeArrangement = true;
        stack.DirectionalLayoutMargins = new NSDirectionalEdgeInsets((nfloat)top, (nfloat)leading, (nfloat)bottom, (nfloat)trailing);
        return stack;
    }

    public static T Size<T>(T view, double width = -1, double height = -1) where T : UIView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        if (width >= 0) view.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        if (height >= 0) view.HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;
        return view;
    }

    /// <summary>Flexible space for stack views.</summary>
    public static UIView Spacer()
    {
        var view = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        view.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        view.SetContentHuggingPriority(1, UILayoutConstraintAxis.Vertical);
        view.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Horizontal);
        return view;
    }

    /// <summary>Sets hugging and compression resistance on one axis in one call.</summary>
    public static T Priorities<T>(T view, UILayoutConstraintAxis axis, float hugging, float compression) where T : UIView
    {
        view.SetContentHuggingPriority(hugging, axis);
        view.SetContentCompressionResistancePriority(compression, axis);
        return view;
    }
}
