using MimeKit;
using NodaTime;
using SocklessMail.Api.Calendar;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.Tasks;

namespace SocklessMail.Tests;

/// <summary>Tasks made from messages: the VTODO text, its notes and due dates (no server needed).</summary>
public sealed class TaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static ICalComponent Todo(string text) => ICalComponent.Parse(text).ChildrenNamed("VTODO").Single();

    [Fact]
    public void A_task_due_on_a_day_has_a_date_only_due()
    {
        var text = TaskBuilder.Build("uid-1", "Pay the invoice", null, TaskDue.Parse("2026-11-02"), "<abc@example.test>", Now);
        var todo = Todo(text);

        Assert.Equal("uid-1", todo.Property("UID")!.Value);
        Assert.Equal("Pay the invoice", todo.Property("SUMMARY")!.Text);
        Assert.Equal("20261102", todo.Property("DUE")!.Value);
        Assert.Equal("DATE", todo.Property("DUE")!.Parameter("VALUE"));
        Assert.Equal("NEEDS-ACTION", todo.Property("STATUS")!.Value);
        Assert.Equal("20261030T120000Z", todo.Property("DTSTAMP")!.Value);
        Assert.Equal("abc@example.test", todo.Property(TaskBuilder.MessageIdProperty)!.Text);
        Assert.Null(todo.Property("DESCRIPTION"));
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", text);
    }

    [Fact]
    public void A_task_due_at_a_time_is_stored_in_utc()
    {
        var todo = Todo(TaskBuilder.Build("u", "Call back", null, TaskDue.Parse("2026-11-02T09:30:00+01:00"), null, Now));

        Assert.Equal("20261102T083000Z", todo.Property("DUE")!.Value);
        Assert.Null(todo.Property("DUE")!.Parameter("VALUE"));
        Assert.Null(todo.Property(TaskBuilder.MessageIdProperty));
    }

    [Fact]
    public void No_due_date_means_no_due()
    {
        Assert.Null(TaskDue.Parse(null));
        Assert.Null(TaskDue.Parse(" "));
        Assert.Null(Todo(TaskBuilder.Build("u", "x", null, null, null, Now)).Property("DUE"));
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("2026-13-01")]
    [InlineData("2026-11-02T09:30:00")]
    [InlineData("0001-01-01")]
    public void Unreadable_due_dates_are_refused(string value) =>
        Assert.Equal("due", Assert.Throws<MailboxValidationException>(() => TaskDue.Parse(value)).Field);

    [Fact]
    public void Text_is_escaped_and_control_characters_dropped()
    {
        var todo = Todo(TaskBuilder.Build("u", "Re: a; b, c\\d\u0007", "line 1\r\nline 2\rline 3", null, null, Now));

        Assert.Equal("Re: a; b, c\\d", todo.Property("SUMMARY")!.Text);
        Assert.Equal("line 1\nline 2\nline 3", todo.Property("DESCRIPTION")!.Text);
    }

    [Fact]
    public void Notes_say_who_and_when_in_the_users_zone_then_the_text()
    {
        var message = new MessageText("Hi", new MailboxAddress("Ann Example", "ann@example.test"),
            new DateTimeOffset(2026, 10, 30, 8, 5, 0, TimeSpan.Zero), "Hello\r\nthere\r\n", "<m@x>");
        var zone = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

        Assert.Equal("From: Ann Example <ann@example.test>\nDate: 2026-10-30 09:05\n\nHello\nthere", TaskBuilder.Notes(message, true, zone));
        Assert.Equal("From: Ann Example <ann@example.test>\nDate: 2026-10-30 09:05", TaskBuilder.Notes(message, false, zone));
    }

    [Fact]
    public void Long_text_is_cut_at_a_word()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 1000));
        var shortened = TaskBuilder.Shorten(text, TaskBuilder.MaxTextLength);

        Assert.True(shortened.Length <= TaskBuilder.MaxTextLength);
        Assert.EndsWith("word…", shortened);
        Assert.Equal("short", TaskBuilder.Shorten("short", 10));
    }
}
