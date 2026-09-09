using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine.UIElements;

namespace AgentForUnity.Editor.UI
{
    internal static class AgentMarkdownRenderer
    {
        private static readonly Regex HeadingPattern = new Regex("^(#{1,3})\\s+(.+?)\\s*$", RegexOptions.Compiled);
        private static readonly Regex OrderedListPattern = new Regex("^(\\d+)\\.\\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex UnorderedListPattern = new Regex("^[-*+]\\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex LinkPattern = new Regex("\\[([^\\]]+)\\]\\(([^)]+)\\)", RegexOptions.Compiled);
        private static readonly Regex StandaloneLinkPattern = new Regex("^\\[([^\\]]+)\\]\\(([^)]+)\\)$", RegexOptions.Compiled);
        private static readonly Regex InlineCodePattern = new Regex("`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex BoldPattern = new Regex("(\\*\\*|__)(.+?)\\1", RegexOptions.Compiled);
        private static readonly Regex ItalicPattern = new Regex("(?<!\\*)\\*([^*]+)\\*(?!\\*)|(?<!_)_([^_]+)_(?!_)", RegexOptions.Compiled);

        internal static void Render(VisualElement host, string markdown, Action<string> onLinkClicked = null)
        {
            host.Clear();
            var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var index = 0;

            while (index < lines.Length)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                {
                    index++;
                    continue;
                }

                if (IsFence(lines[index]))
                {
                    AddCodeBlock(host, lines, ref index);
                    continue;
                }

                var heading = HeadingPattern.Match(lines[index]);
                if (heading.Success)
                {
                    var label = RichLabel(heading.Groups[2].Value, "afu-markdown__heading", "afu-markdown__heading--h" + heading.Groups[1].Value.Length);
                    host.Add(label);
                    index++;
                    continue;
                }

                if (IsHorizontalRule(lines[index]))
                {
                    var rule = new VisualElement();
                    rule.AddToClassList("afu-markdown__rule");
                    host.Add(rule);
                    index++;
                    continue;
                }

                if (lines[index].StartsWith(">", StringComparison.Ordinal))
                {
                    AddQuote(host, lines, ref index);
                    continue;
                }

                if (OrderedListPattern.IsMatch(lines[index]) || UnorderedListPattern.IsMatch(lines[index]))
                {
                    AddList(host, lines, ref index, onLinkClicked);
                    continue;
                }

                AddParagraph(host, lines, ref index, onLinkClicked);
            }
        }

        private static void AddCodeBlock(VisualElement host, IReadOnlyList<string> lines, ref int index)
        {
            var openingFence = lines[index].Trim();
            var language = openingFence.Length > 3 ? openingFence.Substring(3).Trim() : string.Empty;
            index++;
            var codeLines = new List<string>();
            while (index < lines.Count && !IsFence(lines[index]))
            {
                codeLines.Add(lines[index]);
                index++;
            }

            if (index < lines.Count)
            {
                index++;
            }

            var block = new VisualElement();
            block.AddToClassList("afu-markdown__code-block");
            if (!string.IsNullOrEmpty(language))
            {
                var caption = new Label(language);
                ConfigureTextLabel(caption);
                caption.AddToClassList("afu-markdown__code-language");
                block.Add(caption);
            }

            var code = new Label(AddSoftBreakOpportunities(string.Join("\n", codeLines)));
            code.enableRichText = false;
            ConfigureTextLabel(code);
            code.AddToClassList("afu-markdown__code");
            block.Add(code);
            host.Add(block);
        }

        private static void AddQuote(VisualElement host, IReadOnlyList<string> lines, ref int index)
        {
            var quoteLines = new List<string>();
            while (index < lines.Count && lines[index].StartsWith(">", StringComparison.Ordinal))
            {
                var value = lines[index].Substring(1);
                quoteLines.Add(value.StartsWith(" ", StringComparison.Ordinal) ? value.Substring(1) : value);
                index++;
            }

            var quote = new VisualElement();
            quote.AddToClassList("afu-markdown__quote");
            quote.Add(RichLabel(string.Join("\n", quoteLines), "afu-markdown__quote-text"));
            host.Add(quote);
        }

        private static void AddList(
            VisualElement host,
            IReadOnlyList<string> lines,
            ref int index,
            Action<string> onLinkClicked)
        {
            var list = new VisualElement();
            list.AddToClassList("afu-markdown__list");
            while (index < lines.Count)
            {
                var ordered = OrderedListPattern.Match(lines[index]);
                var unordered = UnorderedListPattern.Match(lines[index]);
                if (!ordered.Success && !unordered.Success)
                {
                    break;
                }

                var item = new VisualElement();
                item.AddToClassList("afu-markdown__list-item");
                var marker = new Label(ordered.Success ? ordered.Groups[1].Value + "." : "-");
                marker.AddToClassList("afu-markdown__list-marker");
                item.Add(marker);
                var itemText = ordered.Success ? ordered.Groups[2].Value : unordered.Groups[1].Value;
                var link = StandaloneLinkPattern.Match(itemText.Trim());
                if (link.Success && onLinkClicked != null)
                {
                    item.Add(LinkButton(link.Groups[1].Value, link.Groups[2].Value, onLinkClicked));
                }
                else if (onLinkClicked != null && LinkPattern.IsMatch(itemText))
                {
                    item.Add(InlineContent(itemText, onLinkClicked, "afu-markdown__list-text"));
                }
                else
                {
                    item.Add(RichLabel(itemText, "afu-markdown__list-text"));
                }
                list.Add(item);
                index++;
            }

            host.Add(list);
        }

        private static void AddParagraph(
            VisualElement host,
            IReadOnlyList<string> lines,
            ref int index,
            Action<string> onLinkClicked)
        {
            var paragraphLines = new List<string>();
            while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
            {
                if (paragraphLines.Count > 0 && (IsFence(lines[index]) || HeadingPattern.IsMatch(lines[index]) ||
                                                 IsHorizontalRule(lines[index]) || lines[index].StartsWith(">", StringComparison.Ordinal) ||
                                                 OrderedListPattern.IsMatch(lines[index]) || UnorderedListPattern.IsMatch(lines[index])))
                {
                    break;
                }

                paragraphLines.Add(lines[index]);
                index++;
            }

            var paragraph = string.Join("\n", paragraphLines);
            var link = StandaloneLinkPattern.Match(paragraph.Trim());
            if (link.Success && onLinkClicked != null)
            {
                host.Add(LinkButton(link.Groups[1].Value, link.Groups[2].Value, onLinkClicked));
            }
            else if (onLinkClicked != null && LinkPattern.IsMatch(paragraph))
            {
                host.Add(InlineContent(paragraph, onLinkClicked, "afu-markdown__paragraph"));
            }
            else
            {
                host.Add(RichLabel(paragraph, "afu-markdown__paragraph"));
            }
        }

        private static VisualElement InlineContent(
            string value,
            Action<string> onLinkClicked,
            params string[] classNames)
        {
            var content = new VisualElement();
            content.AddToClassList("afu-markdown__inline");
            foreach (var className in classNames)
            {
                content.AddToClassList(className);
            }

            var lines = (value ?? string.Empty).Split('\n');
            foreach (var lineValue in lines)
            {
                var line = new VisualElement();
                line.AddToClassList("afu-markdown__inline-line");
                var start = 0;
                foreach (Match match in LinkPattern.Matches(lineValue))
                {
                    if (match.Index > start)
                    {
                        line.Add(InlineLabel(lineValue.Substring(start, match.Index - start)));
                    }

                    var button = LinkButton(match.Groups[1].Value, match.Groups[2].Value, onLinkClicked);
                    button.AddToClassList("afu-markdown__inline-link");
                    line.Add(button);
                    start = match.Index + match.Length;
                }

                if (start < lineValue.Length)
                {
                    line.Add(InlineLabel(lineValue.Substring(start)));
                }

                content.Add(line);
            }

            return content;
        }

        private static Label InlineLabel(string value)
        {
            var label = new Label
            {
                enableRichText = true,
                text = ToRichText(AddSoftBreakOpportunities(value))
            };
            label.AddToClassList("afu-markdown__inline-text");
            label.focusable = true;
            label.selection.isSelectable = true;
            return label;
        }

        private static Label RichLabel(string value, params string[] classNames)
        {
            var label = new Label { enableRichText = true, text = ToRichText(AddSoftBreakOpportunities(value)) };
            ConfigureTextLabel(label);
            foreach (var className in classNames)
            {
                label.AddToClassList(className);
            }

            return label;
        }

        private static void ConfigureTextLabel(Label label)
        {
            label.style.minWidth = 0;
            label.style.width = Length.Percent(100f);
            label.style.maxWidth = Length.Percent(100f);
            label.style.flexShrink = 1f;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.focusable = true;
            label.selection.isSelectable = true;
        }

        private static Button LinkButton(string text, string target, Action<string> onLinkClicked)
        {
            var button = new Button(() => onLinkClicked(target)) { text = text };
            button.AddToClassList("afu-markdown__link");
            return button;
        }

        private static string ToRichText(string value)
        {
            var text = Escape(value ?? string.Empty);
            text = LinkPattern.Replace(text, "<color=#5AA9E6><u>$1</u></color>");
            text = InlineCodePattern.Replace(text, "<color=#D19A66><b>$1</b></color>");
            text = BoldPattern.Replace(text, "<b>$2</b>");
            return ItalicPattern.Replace(text, match => "<i>" + (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) + "</i>");
        }

        private static string Escape(string value)
        {
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string AddSoftBreakOpportunities(string value)
        {
            const int MaximumUnbrokenCharacters = 48;
            var result = new StringBuilder(value?.Length ?? 0);
            var unbrokenCharacters = 0;
            foreach (var character in value ?? string.Empty)
            {
                result.Append(character);
                if (char.IsWhiteSpace(character))
                {
                    unbrokenCharacters = 0;
                    continue;
                }

                unbrokenCharacters++;
                if (character == '/' || character == '\\' || character == '-' || character == '_' ||
                    character == '.' || character == '?' || character == '&' || character == '=' || character == ':')
                {
                    result.Append("\u200B");
                    unbrokenCharacters = 0;
                }
                else if (unbrokenCharacters >= MaximumUnbrokenCharacters)
                {
                    result.Append("\u200B");
                    unbrokenCharacters = 0;
                }
            }

            return result.ToString();
        }

        private static bool IsFence(string value)
        {
            return value.TrimStart().StartsWith("```", StringComparison.Ordinal);
        }

        private static bool IsHorizontalRule(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length >= 3 && (trimmed.Trim('-').Length == 0 || trimmed.Trim('*').Length == 0 || trimmed.Trim('_').Length == 0);
        }
    }
}
