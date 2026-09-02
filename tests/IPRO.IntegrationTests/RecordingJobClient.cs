using System.Collections.Generic;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace IPRO.IntegrationTests;

// A Hangfire client that records instead of queueing, for controllers that enqueue one-off jobs
// (CampaignsController enqueues DripCampaignJob.RunEnrollmentAsync at enrolment -- TODO 448).
// Tests read Created to assert WHAT was enqueued, without a Hangfire storage behind it.
public sealed class RecordingJobClient : IBackgroundJobClient
{
    public List<Job> Created { get; } = new();

    public string Create(Job job, IState state)
    {
        Created.Add(job);
        return Created.Count.ToString();
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
