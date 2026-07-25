using IPRO.DataAccess.Repositories;
using IPRO.Email;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class CalendarReminderJob
{
    private readonly IUnitOfWork _uow;
    private readonly IEmailService _email;
    private readonly ILogger<CalendarReminderJob> _logger;

    public CalendarReminderJob(IUnitOfWork uow, IEmailService email, ILogger<CalendarReminderJob> logger)
    {
        _uow = uow; _email = email; _logger = logger;
    }

    public async Task RunAsync()
    {
        var window = DateTime.UtcNow.AddHours(1);
        var events = await _uow.CalendarEvents.FindAsync(e =>
            e.SendReminder && e.StartDate <= window && e.StartDate >= DateTime.UtcNow);

        foreach (var ev in events)
        {
            try
            {
                var agent = await _uow.AgentUsers.GetByIdAsync(ev.AgentUserId);
                if (agent == null) continue;
                var sent = await _email.SendAsync(agent.Email, $"{agent.FirstName} {agent.LastName}",
                    $"Reminder: {ev.Title}",
                    $"<p>This is a reminder for your event: <strong>{ev.Title}</strong></p><p>Starts at: {ev.StartDate:f}</p><p>{ev.Description}</p>");
                if (sent)
                {
                    _logger.LogInformation("Reminder sent for event {Id} to agent {AgentId}", ev.Id, ev.AgentUserId);
                }
                else
                {
                    _logger.LogWarning("Reminder email for event {Id} to agent {AgentId} was not sent", ev.Id, ev.AgentUserId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Calendar reminder for event {Id} failed", ev.Id);
            }
        }
    }
}
