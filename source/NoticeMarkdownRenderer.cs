using System;
using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Block = Markdig.Syntax.Block;

namespace DeepSeekHarnessLauncher
{
    internal static class NoticeMarkdownRenderer
    {
        private static readonly MarkdownPipeline Pipeline = BuildPipeline();

        private static MarkdownPipeline BuildPipeline()
        {
            var builder = new MarkdownPipelineBuilder().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);
            builder.BlockParsers.RemoveAll(parser => parser is HtmlBlockParser);
            return builder.Build();
        }

        internal static RichTextBlock Create(string markdown)
        {
            var text = new RichTextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            foreach (Block block in Markdown.Parse(markdown ?? String.Empty, Pipeline)) AddBlock(text, block, String.Empty);
            if (text.Blocks.Count > 0) text.Blocks[text.Blocks.Count - 1].Margin = new Thickness(0);
            return text;
        }

        private static void AddBlock(RichTextBlock text, Block block, string prefix)
        {
            if (block is ListBlock list)
            {
                int index = Int32.TryParse(list.OrderedStart, out int start) ? start : 1;
                foreach (Block child in list)
                {
                    string marker = list.IsOrdered ? (index++).ToString() + ". " : "\u2022 ";
                    if (child is ContainerBlock item)
                        foreach (Block entry in item) AddBlock(text, entry, prefix + marker);
                }
                return;
            }
            if (block is ContainerBlock container)
            {
                foreach (Block child in container) AddBlock(text, child, prefix);
                return;
            }
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            if (prefix.Length > 0) paragraph.Inlines.Add(new Run { Text = prefix });
            if (block is HeadingBlock heading)
            {
                paragraph.FontSize = heading.Level <= 2 ? 18 : 15;
                paragraph.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            if (block is CodeBlock code)
            {
                paragraph.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
                paragraph.Inlines.Add(new Run { Text = code.Lines.ToString() });
            }
            else if (block is LeafBlock leaf && leaf.Inline != null) AddInlines(paragraph.Inlines, leaf.Inline);
            else if (block is ThematicBreakBlock) paragraph.Inlines.Add(new Run { Text = "---" });
            else return;
            text.Blocks.Add(paragraph);
        }

        private static void AddInlines(InlineCollection target, ContainerInline source)
        {
            var scopes = new Stack<(string Tag, InlineCollection Parent)>();
            foreach (Markdig.Syntax.Inlines.Inline inline in source)
            {
                switch (inline)
                {
                    case LiteralInline literal: target.Add(new Run { Text = literal.Content.ToString() }); break;
                    case LineBreakInline: target.Add(new LineBreak()); break;
                    case CodeInline code: target.Add(new Run { Text = code.Content, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") }); break;
                    case EmphasisInline emphasis:
                        Span span = emphasis.DelimiterChar == '~'
                            ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
                            : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                        AddInlines(span.Inlines, emphasis); target.Add(span); break;
                    case HtmlInline html:
                        string tag = html.Tag;
                        if ((tag == "</u>" || tag == "</span>") && scopes.Count > 0 && scopes.Peek().Tag == tag)
                            target = scopes.Pop().Parent;
                        else if (TryFormattingTag(tag, out Span format, out string closeTag))
                        {
                            target.Add(format); scopes.Push((closeTag, target)); target = format.Inlines;
                        }
                        else target.Add(new Run { Text = tag });
                        break;
                    case LinkInline link:
                        // Remote images and link targets never execute inside a notice body.
                        var label = new Span(); AddInlines(label.Inlines, link); target.Add(label); break;
                    case ContainerInline nested: AddInlines(target, nested); break;
                }
            }
        }

        // Only toolbar-generated text formatting is interpreted. All other HTML remains inert text.
        private static bool TryFormattingTag(string tag, out Span span, out string closeTag)
        {
            span = null; closeTag = null;
            if (tag == "<u>") { span = new Underline(); closeTag = "</u>"; return true; }
            foreach (int size in new[] { 12, 14, 16, 18, 20, 24, 28, 32 })
                if (tag == "<span style=\"font-size:" + size + "px\">")
                {
                    span = new Span { FontSize = size }; closeTag = "</span>"; return true;
                }
            return false;
        }
    }
}
