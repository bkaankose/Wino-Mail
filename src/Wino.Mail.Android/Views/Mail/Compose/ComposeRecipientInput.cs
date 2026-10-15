using Android.Content;
using Android.Runtime;
using Android.Util;
using AndroidX.AppCompat.Widget;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The text field of a recipient row. Its suggestions come from the shared recipient service, not from the widget's own
/// filtering: the list may show only while <see cref="ShowsSuggestions"/> (set when the service answered, cleared as soon
/// as the text changes), so AutoCompleteTextView neither filters stale items nor closes the list the page opened.
/// </summary>
[Register("com.winomail.android.ComposeRecipientInput")]
public sealed class ComposeRecipientInput : AppCompatAutoCompleteTextView
{
    public ComposeRecipientInput(Context context) : base(context) { }
    public ComposeRecipientInput(Context context, IAttributeSet? attrs) : base(context, attrs) { }
    private ComposeRecipientInput(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public bool ShowsSuggestions { get; set; }

    public override bool EnoughToFilter() => ShowsSuggestions;

    /// <summary>
    /// A single-line field asks for the whole row; in the chip flow it is only as wide as its text (at least its minimum
    /// width), so it stays on the same line as the last chip while there is room.
    /// </summary>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        if (MeasureSpec.GetMode(widthMeasureSpec) == global::Android.Views.MeasureSpecMode.AtMost && Paint is { } paint)
        {
            int available = MeasureSpec.GetSize(widthMeasureSpec);
            int slack = Resources?.GetDimensionPixelSize(Resource.Dimension.wino_compose_input_text_slack) ?? 0;
            int text = (int)Math.Ceiling(paint.MeasureText(Text ?? string.Empty)) + PaddingLeft + PaddingRight + slack;
            int width = Math.Min(available, Math.Max(MinWidth, text));
            widthMeasureSpec = MeasureSpec.MakeMeasureSpec(width, global::Android.Views.MeasureSpecMode.Exactly);
        }
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}
