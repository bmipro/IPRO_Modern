# Background Jobs + Email — Audit Report
[Agent 4 of 5. Verbatim; pending my verification.]

## CRITICAL
### 1. Drip campaigns bypass the consent gate end to end
`DripCampaignJob.cs:30-38`, `NewsLetterDispatcher.cs:206-262`, `EmailConsentService.cs:17-24`
DispatchDripStepAsync is the ONLY outbound path that never calls IsSuppressed. Four compounding gaps: (a) EmailChannel enum has Newsletter/ECard/ELetter/Poll/DidYouKnow — NO drip member, so the check is unrepresentable; (b) due-enrollment query filters only Status==Active && NextSendAt<=now && IsActive — no opt-out; (c) EnrollClientsAsync (CampaignsController.cs:429-434) filters only AgentUserId+email, so an unsubscribed client can be freshly enrolled; (d) SuppressAllAsync (EmailPreferencesController.cs:146-166) never touches DripCampaignEnrollments. Escape hatches don't close it: drip unsubscribe cancels ONE enrollment (NewsletterController.cs:285-297) not the client; RecordDripStepEventAsync (NewsLetterService.cs:279-331) has no spamreport/unsubscribe case. Most plausible in-product source of the shared-IP spam complaints.
FIX: add EmailChannel.DripCampaign; IsSuppressed per enrollment in the job (→Cancelled); filter opted-out in EnrollClientsAsync; cancel enrollments in SuppressAllAsync; add spamreport/unsubscribe cases.

## HIGH
### 2. Three jobs send first, persist "already sent" marker once at end → duplicate on Hangfire retry
`TrialReminderJob.cs:72,81` · `OverdueInvoiceReminderJob.cs:64,72` · `DripCampaignJob.cs:70-83,93`
All three mutate idempotency marker in memory and SaveChangesAsync ONCE after the loop, outside the per-item try. Default AutomaticRetry (up to 10x). Terminal save fails → every already-sent item is in the "not sent" window again → re-sent. Exactly the bug DidYouKnowEmailDispatchJob.cs:51-63 documents fixing for itself; never applied to neighbours.
FIX: persist marker per item (as DYK does), or claim-before-send with conditional ExecuteUpdateAsync.

### 3. A send stranded in "Sending" is permanent, invisible, loses every per-recipient result
`NewsLetterDispatcher.cs:62-64,112-119,142` · `ECardDispatcher.cs:48-49,135` · `ELetterDispatcher.cs:32-33,116` · `PollDispatcher.cs:35-37,133`
Each flips parent to Sending, buffers ALL per-recipient status in memory, commits once at end. Jobs select only Status==Scheduled. Nothing anywhere moves a row out of Sending. Worker recycles mid-blast (several deploys/day) → send stuck Sending forever, recipients stay Queued (stats read "0 sent"), only recovery = manual reset which RE-RUNS DispatchSendAsync creating a SECOND full recipient set → double-send.
FIX: persist recipient outcomes incrementally (batched); stale-Sending sweep that resumes from un-Sent rows or fails loudly.

### 4. Spam complaints/unsubscribes never suppress the recipient outside the newsletter
`EmailDeliveryTracker.cs:40-49,58` · `NewsLetterService.cs:219-231`
Map sends spamreport→Outcome.Failed (stamps row only), no unsubscribe/group_unsubscribe case → Ignored/discarded. Covers ecards/eletters/polls/DYK. Newsletter path sets only IsNewsletterSubscribed=false, never EmailOptOutAt (which gates Newsletter channel alone). Client marks newsletter spam → next month birthday ecard + eletter both pass IsSuppressed and land. More complaints on shared IP.
FIX: route spamreport/unsubscribe/group_unsubscribe from every recipient table through one suppression call setting EmailOptOutAt.

## MEDIUM
### 5. DYK treats transport failures as definitive, drops email permanently
`DidYouKnowEmailDispatchJob.cs:158-173` · `SendGridEmailService.cs:83-87`
Comment justifies retiring on failure because "SendGrid answered" — false: catch-all returns Failed for socket timeout/DNS/TLS where SendGrid never answered (msg may have been accepted). 429 burst or blip → drains 100/min into Failed with SentAtUtc set, no retry.
FIX: distinguish answered-and-rejected (4xx→retire) from unreachable/429/5xx (leave claimed for sweep), attempt counter.

### 6. SendGrid webhook has no per-event isolation
`NewsletterController.cs:563-601` — event loop no try/catch. One poisoned event (FK-cascaded row, deadlock) 500s the action → every later event unprocessed, SendGrid retries whole batch, hits same event, drops all 1000 after retry budget. Same isolation already fixed in dispatch jobs, missing on ingest. FIX: wrap loop body try/catch, log index, continue, return 200.

### 7. Drip step send failures silent, enrollment advances anyway
`NewsLetterDispatcher.cs:253-259` · `DripCampaignJob.cs:70-72` — DispatchDripStepAsync records Failed, returns normally (never throws); caller unconditionally clears LastError and NextStepIndex++. Client silently misses middle of sequence, LastError blanked. FIX: return EmailSendResult; on failure leave index, record LastError, bounded retry.

### 8. Drip enrollment marked Failed is terminal, never retried
`DripCampaignJob.cs:85-90` — only write of Failed, no read/UI/recovery. Transient timeout ends campaign permanently, count silently shrinks. FIX: attempt counter, fail terminally after N; surface on Details.

### 9. Poll audience re-implements stricter consent than EmailConsentService
`PollDispatcher.cs:176-191 (180)` vs `EmailConsentService.cs:77` ("Nothing else may re-implement this test"). GetAudienceClientsAsync filters IsNewsletterSubscribed in SQL, but IsSuppressed applies that flag to Newsletter only → poll to "All clients" silently excludes never-opted-in clients BEFORE the suppression count, log reports "0 suppressed". FIX: drop IsNewsletterSubscribed from poll query, let IsSuppressed(Poll) be the gate.

### 10. Testimonial requests bypass consent, carry no List-Unsubscribe
`TestimonialsController.cs:142-181` — sends to client.Email, no IsSuppressed, no listUnsubscribeUrl, no channel. Breaks the promise EmailPreferencesController.cs:190-192 makes. Agent-initiated one-at-a-time (lower blast radius). FIX: add EmailChannel.Testimonial, gate, attach preferences URL.

## LOW
### 11. Webhook signature verified but timestamp never checked for freshness
`NewsletterController.cs:503-505,525-545` — no timestamp-vs-now comparison; captured payload valid indefinitely. Bounded (ApplyTimestamps write-once) but replaying a bounce batch flips live rows to Failed. FIX: reject outside a few minutes' tolerance.

## Overall (agent's words)
Dispatch layer absorbed several rounds of hard-won fixes; DidYouKnowEmailDispatchJob is now a correct claim-before-send queue and EmailConsentService is the right single-authority shape. Problem: fixes stayed local to the file that got burned. Claim-before-send, per-item isolation, and the consent gate each exist only where an incident forced them, absent from structurally identical neighbours: three jobs still send-then-persist (duplicate on retry); four dispatchers buffer whole blast behind unrecoverable Sending; webhook ingest lacks the per-event isolation its own dispatch jobs have. Most serious = drip campaigns entirely outside the consent system (EmailChannel has no member for them, so no reviewer notices) — plus no spam-report handling and unsubscribe scoped to one enrollment → most plausible source of the shared-IP reputation problem. Fix drip first.
