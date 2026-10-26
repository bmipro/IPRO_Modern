-- 592: which domain rows will need their adviser's TXT record (_ipro-verify) once 592 is live? Read-only: it
-- changes nothing. Meant for just before the release (afterwards a row already proven by its record still
-- shows here).
--
-- At the release every row made before 15 October 2026 whose www name was connected (bound, or its certificate
-- in place) counts as proven, and nothing changes for it. Every other row -- added on or after 15 October, or
-- never connected -- is connected only once its adviser has published the record shown on My Website. Until
-- then it is not shown as anyone's site. The owner's own 2026-10-15 test (www.411trades.com on michaeltran) is
-- expected here.
SELECT d.Id AS row_id, d.DomainName, d.AgentUserId, u.UserName, d.CreatedAt, d.DnsStatus, d.AzureBindingStatus, d.SslStatus,
       CASE WHEN d.CreatedAt >= '2026-10-15 00:00:00' THEN 'added on or after 15 Oct 2026'
            ELSE 'never connected' END AS why
FROM AgentDomains d
LEFT JOIN AgentUsers u ON u.Id = d.AgentUserId
WHERE NOT (d.CreatedAt < '2026-10-15 00:00:00'
           AND (d.AzureBindingStatus = 'Bound' OR d.SslStatus IN ('Bound', 'SslBound')))
ORDER BY d.CreatedAt, d.Id;
