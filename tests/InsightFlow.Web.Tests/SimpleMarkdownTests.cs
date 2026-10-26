using InsightFlow.Web.Components.Skills;

namespace InsightFlow.Web.Tests;

public sealed class SimpleMarkdownTests
{
    [Fact]
    public void ToHtml_NumberedList_BecomesAnOrderedList() =>
        SimpleMarkdown.ToHtml("1. Restate the question.\n2. Inspect the data.").ShouldBe("<ol><li>Restate the question.</li><li>Inspect the data.</li></ol>");

    [Fact]
    public void ToHtml_ParagraphAndBold_AreRendered() =>
        SimpleMarkdown.ToHtml("You analyse **data** carefully.").ShouldBe("<p>You analyse <strong>data</strong> carefully.</p>");

    [Fact]
    public void ToHtml_HeadingBulletsAndCode_AreRendered()
    {
        var html = SimpleMarkdown.ToHtml("## Rules\n- use `invoice_total`\n- skip drafts\n\n```\nSELECT 1\n```");

        html.ShouldBe("<h2>Rules</h2><ul><li>use <code>invoice_total</code></li><li>skip drafts</li></ul><pre><code>SELECT 1</code></pre>");
    }

    [Fact]
    public void ToHtml_RawHtmlAndScripts_AreEncoded()
    {
        var html = SimpleMarkdown.ToHtml("<script>alert(1)</script> and <img src=x onerror=alert(1)>");

        html.ShouldNotContain("<script");
        html.ShouldNotContain("<img");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void ToHtml_Links_OnlyHttpAndHttpsBecomeLinks()
    {
        SimpleMarkdown.ToHtml("[docs](https://example.com/a)").ShouldContain("<a href=\"https://example.com/a\"");
        SimpleMarkdown.ToHtml("[bad](javascript:alert(1))").ShouldNotContain("<a ");
    }

    [Fact]
    public void ToHtml_Empty_IsEmpty() => SimpleMarkdown.ToHtml(null).ShouldBeEmpty();
}
