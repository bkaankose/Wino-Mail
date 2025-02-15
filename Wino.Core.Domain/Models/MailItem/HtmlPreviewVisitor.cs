using System;
using System.Collections.Generic;
using System.IO;
using MimeKit;
using MimeKit.Text;
using MimeKit.Tnef;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Visits a MimeMessage and generates HTML suitable to be rendered by a browser control.
/// </summary>
public class HtmlPreviewVisitor : MimeVisitor
{
    List<MultipartRelated> stack = new List<MultipartRelated>();
    List<MimeEntity> attachments = new List<MimeEntity>();

    readonly string tempDir;

    public string Body { get; set; }

    /// <summary>
    /// Creates a new HtmlPreviewVisitor.
    /// </summary>
    /// <param name="tempDirectory">A temporary directory used for storing image files.</param>
    public HtmlPreviewVisitor(string tempDirectory)
    {
        tempDir = tempDirectory;
    }

    /// <summary>
    /// The list of attachments that were in the MimeMessage.
    /// </summary>
    public IList<MimeEntity> Attachments
    {
        get { return attachments; }
    }

    /// <summary>
    /// The HTML string that can be set on the BrowserControl.
    /// </summary>
    public string HtmlBody
    {
        get { return Body ?? string.Empty; }
    }

    protected override void VisitMultipartAlternative(MultipartAlternative alternative)
    {
        // walk the multipart/alternative children backwards from greatest level of faithfulness to the least faithful
        for (int i = alternative.Count - 1; i >= 0 && Body == null; i--)
            alternative[i].Accept(this);
    }

    protected override void VisitMultipartRelated(MultipartRelated related)
    {
        var root = related.Root;

        // push this multipart/related onto our stack
        stack.Add(related);

        // visit the root document
        root.Accept(this);

        // pop this multipart/related off our stack
        stack.RemoveAt(stack.Count - 1);
    }

    // look up the image based on the img src url within our multipart/related stack
    bool TryGetImage(string url, out MimePart image)
    {
        UriKind kind;
        int index;
        Uri uri;

        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            kind = UriKind.Absolute;
        else if (Uri.IsWellFormedUriString(url, UriKind.Relative))
            kind = UriKind.Relative;
        else
            kind = UriKind.RelativeOrAbsolute;

        try
        {
            uri = new Uri(url, kind);
        }
        catch
        {
            image = null;
            return false;
        }

        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if ((index = stack[i].IndexOf(uri)) == -1)
                continue;

            image = stack[i][index] as MimePart;
            return image != null;
        }

        image = null;

        return false;
    }

    // Save the image to our temp directory and return a "file://" url suitable for
    // the browser control to load.
    // Note: if you'd rather embed the image data into the HTML, you can construct a
    // "data:" url instead.
    string SaveImage(MimePart image)
    {
        using (var memory = new MemoryStream())
        {
            image.Content.DecodeTo(memory);
            var buffer = memory.GetBuffer();
            var length = (int)memory.Length;
            var base64 = Convert.ToBase64String(buffer, 0, length);

            return string.Format("data:{0};base64,{1}", image.ContentType.MimeType, base64);
        }

        //string fileName = url
        //    .Replace(':', '_')
        //    .Replace('\\', '_')
        //    .Replace('/', '_');

        //string path = Path.Combine(tempDir, fileName);

        //if (!File.Exists(path))
        //{
        //    using (var output = File.Create(path))
        //        image.Content.DecodeTo(output);
        //}

        //return "file://" + path.Replace('\\', '/');
    }

    // Replaces <img src=...> urls that refer to images embedded within the message with
    // "file://" urls that the browser control will actually be able to load.
    void HtmlTagCallback(HtmlTagContext ctx, HtmlWriter htmlWriter)
    {
        if (ctx.TagId == HtmlTagId.Image && !ctx.IsEndTag && stack.Count > 0)
        {
            ctx.WriteTag(htmlWriter, false);

            // replace the src attribute with a file:// URL
            foreach (var attribute in ctx.Attributes)
            {
                if (attribute.Id == HtmlAttributeId.Src)
                {
                    MimePart image;
                    string url;

                    if (!TryGetImage(attribute.Value, out image))
                    {
                        htmlWriter.WriteAttribute(attribute);
                        continue;
                    }

                    url = SaveImage(image);

                    htmlWriter.WriteAttributeName(attribute.Name);
                    htmlWriter.WriteAttributeValue(url);
                }
                else
                {
                    htmlWriter.WriteAttribute(attribute);
                }
            }
        }
        else if (ctx.TagId == HtmlTagId.Body && !ctx.IsEndTag)
        {
            ctx.WriteTag(htmlWriter, false);

            // add and/or replace oncontextmenu="return false;"
            foreach (var attribute in ctx.Attributes)
            {
                if (attribute.Name.ToLowerInvariant() == "oncontextmenu")
                    continue;

                htmlWriter.WriteAttribute(attribute);
            }

            htmlWriter.WriteAttribute("oncontextmenu", "return false;");
        }
        else
        {
            if (ctx.TagId == HtmlTagId.Unknown)
            {
                ctx.DeleteTag = true;
                ctx.DeleteEndTag = true;
            }
            else
            {
                ctx.WriteTag(htmlWriter, true);
            }
        }
    }

    protected override void VisitTextPart(TextPart entity)
    {
        TextConverter converter;

        if (Body != null)
        {
            // since we've already found the body, treat this as an attachment
            attachments.Add(entity);
            return;
        }

        if (entity.IsHtml)
        {
            converter = new HtmlToHtml
            {
                HtmlTagCallback = HtmlTagCallback
            };
        }
        else if (entity.IsFlowed)
        {
            var flowed = new FlowedToHtml();
            string delsp;

            if (entity.ContentType.Parameters.TryGetValue("delsp", out delsp))
                flowed.DeleteSpace = delsp.ToLowerInvariant() == "yes";

            converter = flowed;
        }
        else
        {
            converter = new TextToHtml();
        }

        Body = converter.Convert(entity.Text);
    }

    protected override void VisitTnefPart(TnefPart entity)
    {
        // extract any attachments in the MS-TNEF part
        attachments.AddRange(entity.ExtractAttachments());
    }

    protected override void VisitMessagePart(MessagePart entity)
    {
        // treat message/rfc822 parts as attachments
        attachments.Add(entity);
    }

    protected override void VisitMimePart(MimePart entity)
    {
        // realistically, if we've gotten this far, then we can treat this as an attachment
        // even if the IsAttachment property is false.
        attachments.Add(entity);
    }
}
