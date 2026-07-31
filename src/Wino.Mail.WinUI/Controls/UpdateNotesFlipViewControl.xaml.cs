using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class UpdateNotesFlipViewControl : UserControl
{
    public event EventHandler<int>? SelectedIndexChanged;

    public int SelectedIndex => UpdateFlipView.SelectedIndex;

    public IList<UpdateNoteSection>? Sections
    {
        get { return (IList<UpdateNoteSection>?)GetValue(SectionsProperty); }
        set { SetValue(SectionsProperty, value); }
    }

    public static readonly DependencyProperty SectionsProperty =
        DependencyProperty.Register(nameof(Sections),
            typeof(IList<UpdateNoteSection>),
            typeof(UpdateNotesFlipViewControl),
            new PropertyMetadata(null, OnSectionsChanged));

    public UpdateNotesFlipViewControl()
    {
        InitializeComponent();
    }

    private static void OnSectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UpdateNotesFlipViewControl control)
            control.UpdatePager();
    }

    private void OnFlipViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlipViewPager.SelectedPageIndex = UpdateFlipView.SelectedIndex;
        SelectedIndexChanged?.Invoke(this, UpdateFlipView.SelectedIndex);
        UpdatePager();
    }

    private void OnPipsPagerSelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        UpdateFlipView.SelectedIndex = sender.SelectedPageIndex;
    }

    private void UpdatePager()
    {
        FlipViewPager.NumberOfPages = Sections?.Count ?? 0;
    }
}
